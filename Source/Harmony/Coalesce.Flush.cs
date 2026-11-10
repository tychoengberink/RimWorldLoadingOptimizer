#if RW17
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;

namespace RimWorldLoadingOptimizer
{
    public static partial class Coalesce
    {
        private static void FromOtherThread(Window w)
        {
            if (!Monitor.TryEnter(locker, 10000))
            {
                RLOLog.Warning(
                    "Harmony rebuild coalescing: another thread read patch state while a merged rebuild was pending "
                        + "and the patch lock stayed busy; it reads the stored state as is."
                );
                return;
            }
            try
            {
                Interlocked.Increment(ref crossThread);
                Flush(w, "patch state read from another thread");
            }
            finally
            {
                Monitor.Exit(locker);
            }
        }

        private static void Flush(Window w, string reason)
        {
            lock (locker)
            {
                if (w.Chains.Count == 0)
                {
                    return;
                }
                var start = Stopwatch.GetTimestamp();
                var chains = w.Chains.ToList();
                var order = w.Steps.ToList();
                w.Chains.Clear();
                w.Steps.Clear();
                w.ByOriginal.Clear();
                lock (pending)
                {
                    foreach (var chain in chains)
                    {
                        pending.Remove(chain.Original);
                    }
                    pendingCount = pending.Count;
                }
                lock (flushReasons)
                {
                    flushReasons.TryGetValue(reason, out var n);
                    flushReasons[reason] = n + 1;
                }
                var wasFlushing = flushing;
                flushing = true;
                try
                {
                    if (!Optimistic(chains))
                    {
                        Replay(w, chains, order);
                    }
                    else if (DevSwitches.Verify)
                    {
                        VerifyIntermediates(chains);
                    }
                }
                finally
                {
                    flushing = wasFlushing;
                    Interlocked.Add(ref flushTicks, Stopwatch.GetTimestamp() - start);
                }
            }
        }

        private static bool Optimistic(List<Chain> chains)
        {
            foreach (var chain in chains)
            {
                var last = chain.Steps[chain.Steps.Count - 1];
                byte[] current;
                lock (state)
                {
                    state.TryGetValue(chain.Original, out current);
                }
                if (current != last.Bytes)
                {
                    chain.Superseded = true;
                    Interlocked.Increment(ref superseded);
                    continue;
                }
                chain.Touched = true;
                if (Build(last) != null)
                {
                    return false;
                }
                Interlocked.Increment(ref builds);
                Interlocked.Add(ref merged, chain.Steps.Count - 1);
            }
            return true;
        }

        private static void Replay(Window w, List<Chain> chains, List<Step> order)
        {
            Interlocked.Increment(ref replays);
            var skip = new HashSet<MethodBase>(chains.Where(c => c.Superseded).Select(c => c.Original));
            foreach (var chain in chains.Where(c => !c.Superseded))
            {
                Restore(chain);
            }
            foreach (var step in order)
            {
                if (skip.Contains(step.Original))
                {
                    continue;
                }
                var error = Build(step);
                if (error != null)
                {
                    Interlocked.Increment(ref failures);
                    w.Failure = error;
                    return;
                }
            }
        }

        private static Exception Build(Step step)
        {
            var original = step.Original;
            var replacement = step.Built;
            if (replacement == null)
            {
                try
                {
                    replacement = (MethodInfo)generate(step.Config, false);
                }
                catch (Exception e)
                {
                    return Wrap(e, original);
                }
                if (replacement == null)
                {
                    return Wrap(
                        new MissingMethodException("Cannot create replacement for " + original.FullDescription()),
                        original
                    );
                }
                step.Built = replacement;
            }
            try
            {
                detour(null, original, replacement);
            }
            catch (Exception e)
            {
                return Wrap((Exception)createException(null, e, step.Instructions), original);
            }
            try
            {
                update(null, original, replacement, step.Bytes);
            }
            catch (Exception e)
            {
                return Wrap(e, original);
            }
            return null;
        }

        private static Exception Wrap(Exception e, MethodBase original) =>
            e is HarmonyException
                ? e
                : (Exception)
                    wrapException.Invoke(
                        new object[] { "Patching exception in method " + original.FullDescription(), e }
                    );

        private static void Restore(Chain chain)
        {
            lock (state)
            {
                if (chain.HadState)
                {
                    state[chain.Original] = chain.PreBytes;
                }
                else
                {
                    state.Remove(chain.Original);
                }
            }
            if (!chain.Touched)
            {
                return;
            }
            try
            {
                if (chain.PreDetour != null && chain.PreApplied)
                {
                    detour(null, chain.Original, (MethodBase)detourTarget.GetValue(chain.PreDetour));
                    return;
                }
                lock (detours)
                {
                    var current = detours[chain.Original];
                    if (current != null && current != chain.PreDetour)
                    {
                        ((IDisposable)current).Dispose();
                    }
                    if (chain.PreDetour == null)
                    {
                        detours.Remove(chain.Original);
                    }
                    else
                    {
                        detours[chain.Original] = chain.PreDetour;
                    }
                }
            }
            catch (Exception e)
            {
                RLOLog.Warning(
                    $"Harmony rebuild coalescing: could not restore {chain.Original.FullDescription()} before replaying a failed patch: {e.Message}"
                );
            }
        }

        private static void VerifyIntermediates(List<Chain> chains)
        {
            foreach (var chain in chains.Where(c => !c.Superseded))
            {
                byte[] current;
                lock (state)
                {
                    state.TryGetValue(chain.Original, out current);
                }
                if (current != chain.Steps[chain.Steps.Count - 1].Bytes)
                {
                    mismatches.Report(
                        $"stored patch state of {chain.Original.FullDescription()} is not the last sequential state"
                    );
                }
                for (var i = 0; i < chain.Steps.Count - 1; i++)
                {
                    Interlocked.Increment(ref verifyChecked);
                    try
                    {
                        var replacement = (MethodInfo)generate(chain.Steps[i].Config, false);
                        if (replacement == null)
                        {
                            mismatches.Report(
                                $"intermediate rebuild {i + 1}/{chain.Steps.Count} of {chain.Original.FullDescription()} returned no method"
                            );
                        }
                        else if (platform != null && compile != null)
                        {
                            compile.Invoke(platform, new object[] { replacement });
                        }
                    }
                    catch (Exception e)
                    {
                        mismatches.Report(
                            $"intermediate rebuild {i + 1}/{chain.Steps.Count} of {chain.Original.FullDescription()} fails although the merged one works: {(e as TargetInvocationException)?.InnerException?.Message ?? e.Message}"
                        );
                    }
                }
            }
        }
    }
}
#endif
