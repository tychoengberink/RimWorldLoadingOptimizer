#if RW17
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace RimWorldLoadingOptimizer
{
    // Makes a fixed list of items ahead on worker threads, in list order, while at most a byte budget waits to be taken.
    // A caller takes an item by its key exactly once; anything it does not take is handed back to the release callback.
    // The list order is only a guess of the order the caller asks in: when the budget is full, items before the furthest
    // one the main thread took are released to make room, and the caller makes those itself if it asks for them later.
    internal sealed class OrderedReadAhead<T> where T : class
    {
        private const int Untouched = 0, Making = 1, Ready = 2, Gone = 3;

        private readonly object gate = new object();
        private readonly string[] keys;
        private readonly Dictionary<string, int> indexOf;
        private readonly int[] state;
        private readonly T[] results;
        private readonly long[] makeTicks;
        private readonly Func<string, T> make;
        private readonly Func<T, long> size;
        private readonly Action<T> release;
        private readonly long cap;
        private int floor, remaining, running;
        private long held;
        private bool stop;

        public int Made, Failed, TakenReady, TakenWaited, OtherTaken, Dropped, Leftovers;
        public long WorkerTicks, WaitTicks, SavedTicks, Bytes, MaxHeld, DoneAt;

        public OrderedReadAhead(IList<string> list, Func<string, T> make, Func<T, long> size, Action<T> release, long cap)
        {
            var order = new List<string>(list.Count);
            indexOf = new Dictionary<string, int>(list.Count, StringComparer.Ordinal);
            foreach (var key in list)
            {
                if (key != null && !indexOf.ContainsKey(key))
                {
                    indexOf[key] = order.Count;
                    order.Add(key);
                }
            }
            keys = order.ToArray();
            state = new int[keys.Length];
            results = new T[keys.Length];
            makeTicks = new long[keys.Length];
            remaining = keys.Length;
            this.make = make;
            this.size = size;
            this.release = release;
            this.cap = cap;
        }

        public int Count => keys.Length;

        public int Remaining
        {
            get
            {
                lock (gate)
                {
                    return remaining;
                }
            }
        }

        public bool Contains(string key) => key != null && indexOf.ContainsKey(key);

        public void Start(int threads, string name)
        {
            running = threads;
            for (var t = 0; t < threads; t++)
            {
                new Thread(Run) { IsBackground = true, Name = name, Priority = ThreadPriority.BelowNormal }.Start();
            }
        }

        private void Run()
        {
            var drop = new List<T>();
            while (true)
            {
                int i;
                lock (gate)
                {
                    while (!stop && held >= cap && !DropPassedLocked(drop))
                    {
                        Monitor.Wait(gate);
                    }
                    if (stop)
                    {
                        break;
                    }
                    i = NextLocked();
                    if (i < 0)
                    {
                        break;
                    }
                    state[i] = Making;
                }
                ReleaseAll(drop);
                var start = Stopwatch.GetTimestamp();
                T result;
                try
                {
                    result = make(keys[i]);
                }
                catch (Exception)
                {
                    result = null;
                }
                var ticks = Stopwatch.GetTimestamp() - start;
                var late = false;
                lock (gate)
                {
                    makeTicks[i] = ticks;
                    WorkerTicks += ticks;
                    if (result == null)
                    {
                        Failed++;
                    }
                    else
                    {
                        Made++;
                        var bytes = size(result);
                        Bytes += bytes;
                        if (stop)
                        {
                            late = true;
                            Leftovers++;
                        }
                        else
                        {
                            results[i] = result;
                            held += bytes;
                            MaxHeld = Math.Max(MaxHeld, held);
                        }
                    }
                    state[i] = Ready;
                    Monitor.PulseAll(gate);
                }
                if (late)
                {
                    release(result);
                }
            }
            ReleaseAll(drop);
            if (Interlocked.Decrement(ref running) == 0)
            {
                DoneAt = Stopwatch.GetTimestamp();
            }
        }

        // The first untouched item from the furthest one the main thread took, else the first untouched one before it.
        private int NextLocked()
        {
            for (var i = floor; i < keys.Length; i++)
            {
                if (state[i] == Untouched)
                {
                    return i;
                }
            }
            for (var i = 0; i < floor && i < keys.Length; i++)
            {
                if (state[i] == Untouched)
                {
                    return i;
                }
            }
            return -1;
        }

        private bool DropPassedLocked(List<T> drop)
        {
            var any = false;
            for (var i = 0; i < floor && i < keys.Length; i++)
            {
                if (state[i] == Ready && results[i] != null)
                {
                    drop.Add(results[i]);
                    held -= size(results[i]);
                    results[i] = null;
                    state[i] = Gone;
                    remaining--;
                    Dropped++;
                    any = true;
                }
            }
            return any;
        }

        // True with the item when it was made ahead; false when the caller has to make it itself (not listed, failed, dropped or taken before).
        public bool TryTake(string key, bool main, out T value)
        {
            value = null;
            if (key == null || !indexOf.TryGetValue(key, out var i))
            {
                return false;
            }
            lock (gate)
            {
                var now = Stopwatch.GetTimestamp();
                if (main)
                {
                    floor = Math.Max(floor, i + 1);
                }
                var waited = state[i] == Making;
                while (state[i] == Making)
                {
                    Monitor.Wait(gate);
                }
                if (state[i] != Gone)
                {
                    remaining--;
                }
                if (state[i] == Ready && results[i] != null)
                {
                    value = results[i];
                    results[i] = null;
                    held -= size(value);
                    if (main)
                    {
                        var waitedTicks = Stopwatch.GetTimestamp() - now;
                        if (waited)
                        {
                            TakenWaited++;
                            WaitTicks += waitedTicks;
                            SavedTicks += Math.Max(0, makeTicks[i] - waitedTicks);
                        }
                        else
                        {
                            TakenReady++;
                            SavedTicks += makeTicks[i];
                        }
                    }
                    else
                    {
                        OtherTaken++;
                    }
                }
                state[i] = Gone;
                Monitor.PulseAll(gate);
                return value != null;
            }
        }

        public void Stop()
        {
            var unused = new List<T>();
            lock (gate)
            {
                stop = true;
                for (var i = 0; i < keys.Length; i++)
                {
                    if (state[i] == Ready && results[i] != null)
                    {
                        unused.Add(results[i]);
                        held -= size(results[i]);
                        results[i] = null;
                        Leftovers++;
                    }
                    if (state[i] != Making)
                    {
                        state[i] = Gone;
                    }
                }
                Monitor.PulseAll(gate);
            }
            ReleaseAll(unused);
        }

        private void ReleaseAll(List<T> items)
        {
            foreach (var item in items)
            {
                try
                {
                    release(item);
                }
                catch (Exception)
                {
                }
            }
            items.Clear();
        }

        public string Summary(string what)
        {
            double S(long ticks) => ticks / (double)Stopwatch.Frequency;
            lock (gate)
            {
                return $"{Made} of {keys.Length} {what} made ahead on workers in {S(WorkerTicks):F1} s ({Bytes / 1048576.0:F0} MB, at most {MaxHeld / 1048576.0:F0} MB waiting, {Failed} left to the game after an error or a missing file); "
                    + $"main thread took {TakenReady} ready and {TakenWaited} after waiting {S(WaitTicks):F1} s; {OtherTaken} taken on other threads; {Dropped} released to make room, {Leftovers} never asked for; "
                    + $"estimated main-thread time saved {S(SavedTicks):F1} s";
            }
        }
    }
}
#endif
