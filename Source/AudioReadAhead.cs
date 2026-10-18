#if RW17
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using Verse;
using Waylith.Audio;
using Waylith.Rendering;

namespace RimWorldLoadingOptimizer
{
    public static class AudioReadAhead
    {
        private const string Feature = "sound read-ahead";
        private const long CapBytes = 256L << 20;
        private const int Untouched = 0, Opening = 1, Ready = 2, Gone = 3;

        private static readonly object gate = new object();
        private static Func<string, IPrefetchedAsset> original;
        private static Func<string, IPrefetchedAsset> wrapper;
        private static string[] paths;
        private static Dictionary<string, int> indexOf;
        private static int[] state;
        private static IPrefetchedAsset[] results;
        private static long[] openTicks;
        private static int floor, releaseCursor, finished;
        private static long held, maxHeld;
        private static bool stop;
        private static Thread thread;
        private static long installedAt, workerDoneAt, firstMainAt;
        private static int opened, failed, takenReady, takenWaited, ownOpens, released, leftovers, otherPaths;
        private static int workerTakenReady, workerTakenWaited, workerOwnOpens;
        private static long workerSavedTicks;
        private static long workerTicks, savedTicks, waitTicks, ownTicks, decodedBytes;
        private static int compared, mismatches;
        private static PropertyInfo[] openedFlags;
        private static PropertyInfo openedSound;

        public static void Install()
        {
            if (PlayDataLoader.Loaded)
            {
                return;
            }
            if (AudioBackend.Current?.IsInitialized != true)
            {
                Log.Message($"[RLO] {Feature}: the audio system is not running yet; skipped");
                return;
            }
            var openForPrefetch = AccessTools.Method(typeof(AudioClip), "OpenForPrefetch");
            var current = AssetPrefetcher.AudioLoader;
            if (current == null || openForPrefetch == null || current.Method != openForPrefetch || current.Target != null)
            {
                Log.Message($"[RLO] {Feature}: the game's sound loader was replaced by other code; skipped");
                return;
            }
            var prefetcher = typeof(AssetPrefetcher);
            var slotType = AccessTools.Inner(prefetcher, "Slot");
            var gateObject = AccessTools.Field(prefetcher, "Gate")?.GetValue(null);
            var ordered = AccessTools.Field(prefetcher, "Ordered")?.GetValue(null) as IList;
            var pathField = slotType == null ? null : AccessTools.Field(slotType, "Path");
            var kindField = slotType == null ? null : AccessTools.Field(slotType, "Kind");
            var stateField = slotType == null ? null : AccessTools.Field(slotType, "State");
            var openedType = AccessTools.Inner(typeof(AudioClip), "Opened");
            if (gateObject == null || ordered == null || pathField?.FieldType != typeof(string) || kindField?.FieldType.IsEnum != true || stateField?.FieldType.IsEnum != true
                || !Enum.IsDefined(kindField.FieldType, "Audio") || !Enum.IsDefined(stateField.FieldType, "Queued") || openedType == null)
            {
                Log.Message($"[RLO] {Feature}: the game's asset prefetcher is not the version this was built for; skipped");
                return;
            }
            var loaderType = typeof(ModContentLoader<AudioClip>);
            if (!Fingerprint.Matches("Sound read-ahead",
                Fingerprint.Known(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.LoadModContent)), "4FD7CF066764C53F"),
                Fingerprint.Known(AccessTools.Method(typeof(ModContentPack), "ReloadContentInt"), "449DFDF625FE3FAC"),
                Fingerprint.Known(AccessTools.Method(typeof(ModContentHolder<AudioClip>), nameof(ModContentHolder<AudioClip>.ReloadAll)), "FAFE99A32F614121"),
                Fingerprint.Known(AccessTools.Method(loaderType, nameof(ModContentLoader<AudioClip>.LoadItem)), "564880D9F2751A13"),
                Fingerprint.Known(AccessTools.EnumeratorMoveNext(AccessTools.Method(loaderType, nameof(ModContentLoader<AudioClip>.LoadAllForMod))), "00DBB5662C88C818"),
                Fingerprint.Known(AccessTools.Method(typeof(AudioClip), nameof(AudioClip.LoadFromFile)), "388C47436D6C44AD"),
                Fingerprint.Known(openForPrefetch, "80A34DCBF94CC177"),
                Fingerprint.Known(AccessTools.Method(typeof(AudioClip), "Open"), "40AE3817CE1013B4", "44EF9117FF80BCEC", "67E9B5D3BA5AAF10"),
                Fingerprint.Known(AccessTools.Method(prefetcher, "Enqueue"), "D61923B0BE9AC58C"),
                Fingerprint.Known(AccessTools.Method(prefetcher, nameof(AssetPrefetcher.TryTakeAudio)), "D867DEE606810A20"),
                Fingerprint.Known(AccessTools.Method(prefetcher, "TryClaim"), "408F3EEEAB082546"),
                Fingerprint.Known(AccessTools.Method(prefetcher, "DropSkippedBefore"), "F2DAE941E6B2B559"),
                Fingerprint.Known(AccessTools.Method(prefetcher, "DropUnclaimed"), "3E4CFF7EE2472101"),
                Fingerprint.Known(AccessTools.Method(prefetcher, "Process"), "095288888022A955"),
                Fingerprint.Known(AccessTools.Method(prefetcher, "WorkerLoop"), "289CC98C049C81FA"),
                Fingerprint.Known(AccessTools.Method(prefetcher, nameof(AssetPrefetcher.Drain)), "2B3346E5AD94B9FF")))
            {
                return;
            }
            var audioKind = Enum.Parse(kindField.FieldType, "Audio");
            var queued = Enum.Parse(stateField.FieldType, "Queued");
            original = current;
            wrapper = Wrap;
            AssetPrefetcher.AudioLoader = wrapper;
            var snapshot = new List<string>();
            lock (gateObject)
            {
                foreach (var slot in ordered)
                {
                    if (audioKind.Equals(kindField.GetValue(slot)) && queued.Equals(stateField.GetValue(slot)))
                    {
                        snapshot.Add((string)pathField.GetValue(slot));
                    }
                }
            }
            if (snapshot.Count == 0)
            {
                AssetPrefetcher.AudioLoader = original;
                Log.Message($"[RLO] {Feature}: no mod sounds are waiting to be opened; skipped");
                return;
            }
            if (RLOMod.Verify)
            {
                PrepareCompare(openedType);
            }
            var map = new Dictionary<string, int>(snapshot.Count, StringComparer.Ordinal);
            for (var i = 0; i < snapshot.Count; i++)
            {
                map[snapshot[i]] = i;
            }
            lock (gate)
            {
                paths = snapshot.ToArray();
                state = new int[paths.Length];
                results = new IPrefetchedAsset[paths.Length];
                openTicks = new long[paths.Length];
                indexOf = map;
            }
            installedAt = Stopwatch.GetTimestamp();
            thread = new Thread(Run) { IsBackground = true, Name = "RLO sound read-ahead" };
            thread.Start();
            LongEventHandler.ExecuteWhenFinished(Finish);
            MainMenu.Then(Finish);
            LeanFixes.Reports.Add(Stats);
        }

        private static void Run()
        {
            var i = 0;
            while (true)
            {
                lock (gate)
                {
                    while (!stop && held >= CapBytes)
                    {
                        Monitor.Wait(gate);
                    }
                    if (stop)
                    {
                        break;
                    }
                    i = Math.Max(i, floor);
                    while (i < paths.Length && state[i] != Untouched)
                    {
                        i++;
                    }
                    if (i >= paths.Length)
                    {
                        break;
                    }
                    state[i] = Opening;
                }
                var start = Stopwatch.GetTimestamp();
                IPrefetchedAsset result = null;
                try
                {
                    result = original(paths[i]);
                }
                catch (Exception)
                {
                    result = null;
                }
                var ticks = Stopwatch.GetTimestamp() - start;
                var late = false;
                lock (gate)
                {
                    openTicks[i] = ticks;
                    workerTicks += ticks;
                    if (result == null)
                    {
                        failed++;
                        state[i] = Ready;
                    }
                    else
                    {
                        opened++;
                        decodedBytes += result.Bytes;
                        late = i < floor;
                        if (late)
                        {
                            released++;
                            state[i] = Gone;
                        }
                        else
                        {
                            results[i] = result;
                            held += result.Bytes;
                            maxHeld = Math.Max(maxHeld, held);
                            state[i] = Ready;
                        }
                    }
                    Monitor.PulseAll(gate);
                }
                if (late)
                {
                    Release(result);
                }
            }
            workerDoneAt = Stopwatch.GetTimestamp();
        }

        private static IPrefetchedAsset Wrap(string path)
        {
            var main = UnityData.IsInMainThread;
            IPrefetchedAsset result = null;
            List<IPrefetchedAsset> unused = null;
            lock (gate)
            {
                if (indexOf == null || !indexOf.TryGetValue(path, out var i))
                {
                    otherPaths++;
                }
                else
                {
                    var now = Stopwatch.GetTimestamp();
                    if (main)
                    {
                        if (firstMainAt == 0)
                        {
                            firstMainAt = now;
                        }
                        floor = Math.Max(floor, i + 1);
                        for (; releaseCursor < i; releaseCursor++)
                        {
                            if (state[releaseCursor] == Ready && results[releaseCursor] != null)
                            {
                                (unused ??= new List<IPrefetchedAsset>()).Add(TakeLocked(releaseCursor));
                                released++;
                            }
                        }
                    }
                    var waited = state[i] == Opening;
                    while (state[i] == Opening)
                    {
                        Monitor.Wait(gate);
                    }
                    if (state[i] == Ready && results[i] != null)
                    {
                        result = TakeLocked(i);
                        if (main)
                        {
                            var waitedTicks = Stopwatch.GetTimestamp() - now;
                            if (waited)
                            {
                                takenWaited++;
                                waitTicks += waitedTicks;
                                savedTicks += Math.Max(0, openTicks[i] - waitedTicks);
                            }
                            else
                            {
                                takenReady++;
                                savedTicks += openTicks[i];
                            }
                        }
                        else
                        {
                            var waitedTicks = Stopwatch.GetTimestamp() - now;
                            if (waited)
                            {
                                workerTakenWaited++;
                                workerSavedTicks += Math.Max(0, openTicks[i] - waitedTicks);
                            }
                            else
                            {
                                workerTakenReady++;
                                workerSavedTicks += openTicks[i];
                            }
                        }
                    }
                    state[i] = Gone;
                    Monitor.PulseAll(gate);
                }
            }
            if (unused != null)
            {
                unused.ForEach(Release);
            }
            if (result == null)
            {
                if (!main)
                {
                    Interlocked.Increment(ref workerOwnOpens);
                    return original(path);
                }
                var start = Stopwatch.GetTimestamp();
                try
                {
                    return original(path);
                }
                finally
                {
                    Interlocked.Increment(ref ownOpens);
                    Interlocked.Add(ref ownTicks, Stopwatch.GetTimestamp() - start);
                }
            }
            if (RLOMod.Verify)
            {
                Compare(path, result);
            }
            return result;
        }

        private static IPrefetchedAsset TakeLocked(int i)
        {
            var result = results[i];
            results[i] = null;
            held -= result.Bytes;
            state[i] = Gone;
            return result;
        }

        private static void Release(IPrefetchedAsset asset)
        {
            try
            {
                asset.Release();
            }
            catch (Exception e)
            {
                Log.Warning($"[RLO] {Feature}: could not release an unused sound: {e.Message}");
            }
        }

        private static void Finish()
        {
            if (Interlocked.Exchange(ref finished, 1) != 0)
            {
                return;
            }
            lock (gate)
            {
                stop = true;
                Monitor.PulseAll(gate);
            }
            thread?.Join();
            var unused = new List<IPrefetchedAsset>();
            lock (gate)
            {
                for (var i = 0; i < paths.Length; i++)
                {
                    if (state[i] == Ready && results[i] != null)
                    {
                        unused.Add(TakeLocked(i));
                    }
                }
                leftovers = unused.Count;
            }
            unused.ForEach(Release);
            if (AssetPrefetcher.AudioLoader == wrapper)
            {
                AssetPrefetcher.AudioLoader = original;
            }
            else
            {
                Log.Message($"[RLO] {Feature}: other code replaced the game's sound loader during loading; it was left as it is");
            }
            if (RLOMod.Verify)
            {
                Log.Message($"[RLO] {Feature} verify: {compared} compared, {mismatches} mismatches");
            }
        }

        private static void Compare(string path, IPrefetchedAsset ahead)
        {
            IPrefetchedAsset fresh = null;
            string difference;
            try
            {
                fresh = original(path);
                difference = Difference(ahead, fresh);
            }
            catch (Exception e)
            {
                difference = "a fresh open threw " + e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                if (fresh != null)
                {
                    Release(fresh);
                }
            }
            lock (gate)
            {
                compared++;
                if (difference == null)
                {
                    return;
                }
                mismatches++;
                if (mismatches <= 5)
                {
                    Log.Warning($"[RLO] {Feature} verify mismatch: {path}: {difference}");
                }
            }
        }

        // Also used by the game sound read-ahead, which opens the same kind of sound.
        internal static void PrepareCompare(Type openedType)
        {
            if (openedFlags != null)
            {
                return;
            }
            openedSound = AccessTools.Property(openedType, "Sound");
            openedFlags = new[] { "Streamed", "Packed", "BakedMono", "ForceToMono", "Bytes" }.Select(name => AccessTools.Property(openedType, name)).ToArray();
        }

        internal static string Difference(IPrefetchedAsset a, IPrefetchedAsset b)
        {
            if (a.GetType() != b.GetType())
            {
                return $"type {a.GetType().Name} vs {b.GetType().Name}";
            }
            foreach (var property in openedFlags)
            {
                var x = property?.GetValue(a);
                var y = property?.GetValue(b);
                if (property == null || !Equals(x, y))
                {
                    return $"{property?.Name ?? "a property"} {x} vs {y}";
                }
            }
            if (!(openedSound?.GetValue(a) is IAudioSound sa) || !(openedSound.GetValue(b) is IAudioSound sb))
            {
                return "no sound object";
            }
            if (sa.LengthFrames != sb.LengthFrames || sa.Channels != sb.Channels || sa.Frequency != sb.Frequency || sa.IsStream != sb.IsStream)
            {
                return $"sound {sa.LengthFrames}x{sa.Channels}@{sa.Frequency} stream {sa.IsStream} vs {sb.LengthFrames}x{sb.Channels}@{sb.Frequency} stream {sb.IsStream}";
            }
            if (sa.IsStream || (bool)openedFlags[0].GetValue(a))
            {
                return null;
            }
            var count = sa.LengthFrames * sa.Channels;
            if (count <= 0 || count > int.MaxValue)
            {
                return null;
            }
            var pa = new float[count];
            var pb = new float[count];
            var ra = sa.TryReadPcm(pa, 0);
            var rb = sb.TryReadPcm(pb, 0);
            if (ra != rb)
            {
                return $"PCM readable {ra} vs {rb}";
            }
            return pa.AsSpan().SequenceEqual(pb) ? null : "PCM samples differ";
        }

        public static void LogClipHash()
        {
            var streamed = AccessTools.Field(typeof(AudioClip), "streamed");
            var text = new StringBuilder();
            var clips = 0;
            var mods = 0;
            foreach (var mod in LoadedModManager.RunningModsListForReading)
            {
                var list = mod.GetContentHolder<AudioClip>()?.contentList;
                if (list == null || list.Count == 0)
                {
                    continue;
                }
                mods++;
                foreach (var pair in list)
                {
                    clips++;
                    var clip = pair.Value;
                    text.Append(mod.PackageId).Append('|').Append(pair.Key).Append('|');
                    if (clip == null)
                    {
                        text.Append("null\n");
                        continue;
                    }
                    text.Append(clip.name).Append('|').Append(clip.length.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                        .Append(clip.channels).Append('|').Append(clip.frequency).Append('|').Append(streamed?.GetValue(clip)).Append('\n');
                }
            }
            string hash;
            using (var sha = SHA256.Create())
            {
                hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())), 0, 8).Replace("-", "");
            }
            Log.Message($"[RLO] {Feature} verify: mod sound list hash {hash} ({clips} sounds in {mods} mods)");
        }

        private static string Stats()
        {
            if (paths == null)
            {
                return $"{Feature}: off";
            }
            double S(long ticks) => ticks / (double)Stopwatch.Frequency;
            string At(long stamp) => stamp == 0 ? "never" : $"{S(stamp - installedAt):F1} s";
            lock (gate)
            {
                return $"{Feature}: {opened} of {paths.Length} mod sounds opened ahead on a worker in {S(workerTicks):F1} s ({decodedBytes / 1048576.0:F0} MB decoded, at most {maxHeld / 1048576.0:F0} MB waiting, {failed} left to the game after an error); "
                    + $"main thread took {takenReady} ready and {takenWaited} after waiting {S(waitTicks):F1} s, opened {ownOpens} itself in {S(ownTicks):F1} s; "
                    + $"the game's prefetch threads took {workerTakenReady} ready and {workerTakenWaited} after waiting, opened {workerOwnOpens} themselves (worker time handed over {S(workerSavedTicks):F1} s); {otherPaths} sounds outside the snapshot; {released + leftovers} unused released; "
                    + $"worker done at {At(workerDoneAt)}, main thread's first sound at {At(firstMainAt)}; estimated main-thread time saved {S(savedTicks):F1} s";
            }
        }
    }
}
#endif
