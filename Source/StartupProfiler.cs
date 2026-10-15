using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class StartupProfiler
    {
        private static string ModOf(Type type)
        {
            foreach (var mod in LoadedModManager.RunningModsListForReading)
            {
                if (mod.assemblies.loadedAssemblies.Contains(type.Assembly))
                {
                    return mod.Name;
                }
            }
            return type.Assembly.GetName().Name;
        }

        [ThreadStatic] private static Stack<KeyValuePair<string, long>> open;

        private static readonly Dictionary<string, long[]> totals = new Dictionary<string, long[]>();
        private static readonly List<string> timeline = new List<string>();
        private static readonly HashSet<string> timelineSeen = new HashSet<string>();
        private static readonly DateTime firstUse = DateTime.Now;

        private static DateTime ProcessStart => Measurement.ProcessStart ?? firstUse;
        private static readonly object gate = new object();
        private static bool active = true;

        [ThreadStatic] private static Stack<long> ctorStarts;

        private static readonly Dictionary<Type, long> ctorTicks = new Dictionary<Type, long>();

        private static readonly List<KeyValuePair<double, string>> spans = new List<KeyValuePair<double, string>>();
        private static bool menuReached;

        private static double Now => (DateTime.Now - ProcessStart).TotalSeconds;

        public static void Install(Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(RuntimeHelpers), nameof(RuntimeHelpers.RunClassConstructor), new[] { typeof(RuntimeTypeHandle) }),
                    prefix: new HarmonyMethod(typeof(StartupProfiler), nameof(CtorStart)),
                    finalizer: new HarmonyMethod(typeof(StartupProfiler), nameof(CtorEnd)));
            }
            catch (Exception e)
            {
                Log.Warning($"[RLO] static constructor timing unavailable: {e.Message}");
            }
            if (Environment.GetEnvironmentVariable("LEANMEMORY_PROFILE") == "1")
            {
                var updateWrapper = AccessTools.Method(typeof(Harmony).Assembly.GetType("HarmonyLib.PatchFunctions"), "UpdateWrapper");
                if (updateWrapper != null)
                {
                    harmony.Patch(updateWrapper, prefix: new HarmonyMethod(typeof(StartupProfiler), nameof(PatchStart)), finalizer: new HarmonyMethod(typeof(StartupProfiler), nameof(PatchEnd)));
                }
                harmony.Patch(AccessTools.Method(typeof(DeepProfiler), nameof(DeepProfiler.Start)), prefix: new HarmonyMethod(typeof(StartupProfiler), nameof(OnStart)));
                harmony.Patch(AccessTools.Method(typeof(DeepProfiler), nameof(DeepProfiler.End)), prefix: new HarmonyMethod(typeof(StartupProfiler), nameof(OnEnd)));
                MainMenu.Then(OnMainMenu);
                harmony.Patch(AccessTools.Method(typeof(Root_Entry), nameof(Root_Entry.Update)), prefix: new HarmonyMethod(typeof(StartupProfiler), nameof(FrameStart)), finalizer: new HarmonyMethod(typeof(StartupProfiler), nameof(FrameEnd)));
                harmony.Patch(AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.QueueLongEvent), new[] { typeof(Action), typeof(string), typeof(bool), typeof(Action<Exception>), typeof(bool), typeof(bool), typeof(Action) }), prefix: new HarmonyMethod(typeof(StartupProfiler), nameof(WrapLongEvent)));
            }
        }

        public static void CtorStart()
        {
            (ctorStarts ?? (ctorStarts = new Stack<long>())).Push(Stopwatch.GetTimestamp());
        }

        public static void CtorEnd(RuntimeTypeHandle type, Exception __exception)
        {
            if (ctorStarts == null || ctorStarts.Count == 0)
            {
                return;
            }
            var started = ctorStarts.Pop();
            if (active)
            {
                var elapsed = Stopwatch.GetTimestamp() - started;
                var ctorType = Type.GetTypeFromHandle(type);
                lock (gate)
                {
                    ctorTicks.TryGetValue(ctorType, out var sum);
                    ctorTicks[ctorType] = sum + elapsed;
                }
            }
        }

        public static void OnStart(string label)
        {
            if (!active)
            {
                return;
            }
            var stack = open ?? (open = new Stack<KeyValuePair<string, long>>());
            if (stack.Count <= 2 && timeline.Count < 5000 && label != null && !label.StartsWith("Loading ", StringComparison.Ordinal) && !label.StartsWith("ParseValueAndReturnDef", StringComparison.Ordinal) && !label.StartsWith("Reload ", StringComparison.Ordinal))
            {
                lock (gate)
                {
                    if (timelineSeen.Add(label))
                    {
                        timeline.Add($"{Now,7:F1}s d{stack.Count} {label}");
                    }
                }
            }
            stack.Push(new KeyValuePair<string, long>(label ?? "(unnamed)", Stopwatch.GetTimestamp()));
        }

        public static void OnEnd()
        {
            if (!active || open == null || open.Count == 0)
            {
                return;
            }
            var entry = open.Pop();
            var elapsed = Stopwatch.GetTimestamp() - entry.Value;
            var seconds = elapsed / (double)Stopwatch.Frequency;
            if (seconds >= 0.25 && !menuReached && (open.Count <= 3 || entry.Key.StartsWith("LongEvent ", StringComparison.Ordinal) || entry.Key.Contains(" -> ")))
            {
                var end = Now;
                var thread = UnityData.IsInMainThread ? "main" : "t" + Thread.CurrentThread.ManagedThreadId;
                lock (gate)
                {
                    spans.Add(new KeyValuePair<double, string>(end - seconds, $"{end - seconds,7:F1} +{seconds,6:F2} {thread,-5} d{open.Count} {entry.Key}"));
                }
            }
            lock (gate)
            {
                if (!totals.TryGetValue(entry.Key, out var total))
                {
                    totals[entry.Key] = total = new long[2];
                }
                total[0] += elapsed;
                total[1]++;
            }
        }

        [ThreadStatic] private static int patchDepth;
        [ThreadStatic] private static long patchStarted;
        private static long patchTicksMain;
        private static long patchTicksOther;
        private static int patchCalls;

        public static void PatchStart()
        {
            if (patchDepth++ == 0)
            {
                patchStarted = Stopwatch.GetTimestamp();
            }
        }

        public static void PatchEnd(Exception __exception)
        {
            if (patchDepth > 0 && --patchDepth == 0 && !menuReached)
            {
                var elapsed = Stopwatch.GetTimestamp() - patchStarted;
                if (UnityData.IsInMainThread)
                {
                    Interlocked.Add(ref patchTicksMain, elapsed);
                }
                else
                {
                    Interlocked.Add(ref patchTicksOther, elapsed);
                }
                Interlocked.Increment(ref patchCalls);
            }
        }

        private static long frameStart;
        private static readonly FieldInfo currentEventField = AccessTools.Field(typeof(LongEventHandler), "currentEvent");
        private static readonly FieldInfo eventTextField = currentEventField == null ? null : AccessTools.Field(currentEventField.FieldType, "eventText");

        private static string CurrentEventText()
        {
            var current = currentEventField?.GetValue(null);
            return current == null ? "-" : (eventTextField?.GetValue(current) as string ?? "?");
        }

        public static void FrameStart()
        {
            frameStart = Stopwatch.GetTimestamp();
        }

        public static void FrameEnd(Exception __exception)
        {
            if (!menuReached && frameStart != 0)
            {
                var seconds = (Stopwatch.GetTimestamp() - frameStart) / (double)Stopwatch.Frequency;
                if (seconds >= 0.25)
                {
                    var end = Now;
                    lock (gate)
                    {
                        spans.Add(new KeyValuePair<double, string>(end - seconds, $"{end - seconds,7:F1} +{seconds,6:F2} frame    {CurrentEventText()}"));
                    }
                }
            }
        }

        public static void WrapLongEvent(ref Action action, string textKey)
        {
            if (menuReached || action == null)
            {
                return;
            }
            var inner = action;
            var label = $"LongEvent {textKey}: {inner.Method.DeclaringType} -> {inner.Method.Name}";
            action = () =>
            {
                OnStart(label);
                try
                {
                    inner();
                }
                finally
                {
                    OnEnd();
                }
            };
        }

        public static void OnMainMenu()
        {
            if (menuReached)
            {
                return;
            }
            menuReached = true;
            active = false;
            List<string> rows;
            lock (gate)
            {
                rows = spans.OrderBy(r => r.Key).Select(r => r.Value).ToList();
            }
            Log.Message($"[RLO] Harmony patch calls before menu: {patchCalls}, main thread {patchTicksMain / (double)Stopwatch.Frequency:F2} s, other threads {patchTicksOther / (double)Stopwatch.Frequency:F2} s");
            Log.Message($"[RLO] wall spans (start, +seconds, thread, depth):\n  " + string.Join("\n  ", rows));
        }

        private static void Diagnostic(string name, Action report)
        {
            try
            {
                report();
            }
            catch (Exception e)
            {
                Log.Warning($"[RLO] {name} failed: {e}");
            }
        }

        public static void Report()
        {
            List<KeyValuePair<string, long[]>> rows;
            lock (gate)
            {
                rows = totals.OrderByDescending(r => r.Value[0]).Take(30).ToList();
            }
            string Part(Func<string> part)
            {
                try
                {
                    return part();
                }
                catch
                {
                    return "n/a";
                }
            }
            var parts = new List<Func<string>>
            {
                () => $"category cache: {CategoryCache.Hits} hits, {CategoryCache.Builds} builds",
                () => $"recipe index: {RecipeIndex.Served} defs served ({RecipeIndex.WarmServed} in Alpha Implants' warm-up without a new check), {RecipeIndex.Rebuilds} rebuilds, {RecipeIndex.Mismatches} verify mismatches",
                () => $"texture lookups: {TextureLookup.Hits} hits, {TextureLookup.CachedMisses} cached misses, {TextureLookup.SkippedScans} holder scans skipped, {TextureLookup.Builds} builds, {TextureLookup.PatchedMethods} methods, {TextureLookup.Mismatches} verify mismatches",
                () => $"DDS files recovered from PNG: {DdsFallback.Recovered}",
#if !NATIVE_ASSETS
                DdsPrefetch.Stats,
#endif
                Fingerprint.Stats,
#if RW17
                () => $"runtime: JIT {System.Runtime.JitInfo.GetCompiledMethodCount()} methods in {System.Runtime.JitInfo.GetCompilationTime().TotalSeconds:F1} s, GC collections {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} (gen0/1/2), GC pauses {GC.GetTotalPauseDuration().TotalSeconds:F1} s, {AppDomain.CurrentDomain.GetAssemblies().Length} assemblies loaded",
#endif
                AssemblySafety.Stats,
                TypeIndex.Stats,
                RrssRecipes.Stats,
                LunarInlining.Stats,
                WtmPatchScan.Stats,
                ThinkKeys.Stats,
                ModuleCache.Stats,
                HarmonyIdentityMap.Stats,
                LeanFixes.Stats,
                GagarinFast.Stats,
                GagarinAsync.Stats,
#if !NATIVE_ASSETS
                () => $"audio waits yielded: {AudioStreaming.YieldedWaits}",
#endif
                () => $"def XML release: {XmlRelease.Skipped} references skipped, {XmlRelease.Cleared} cleared, XML Extensions document released: {XmlRelease.ReleasedXmlExtensionsDocument}; {MonoHeap.Stats()}",
            };
            var sb = new StringBuilder("[RLO] startup report, one line per feature:");
            foreach (var line in parts.SelectMany(p => Part(p).Split('\n')))
            {
                sb.Append("\n[RLO] ").Append(line);
            }
            if (rows.Count > 0)
            {
                sb.Append("\n[RLO] startup profile (inclusive seconds, calls):");
            }
            foreach (var row in rows)
            {
                sb.Append($"\n  {row.Value[0] / (double)Stopwatch.Frequency,8:F2}  x{row.Value[1],-6} {row.Key}");
            }
            List<KeyValuePair<string, double>> actions;
            lock (gate)
            {
                actions = totals.Where(r => r.Key.Contains(" -> ")).GroupBy(r => r.Key.Substring(0, r.Key.IndexOf(" -> ", StringComparison.Ordinal))).Select(g => new KeyValuePair<string, double>($"{g.Key} (x{g.Sum(r => r.Value[1])})", g.Sum(r => r.Value[0]) / (double)Stopwatch.Frequency)).OrderByDescending(g => g.Value).Take(25).ToList();
            }
            sb.Append("\n[RLO] deferred actions by type (seconds):");
            foreach (var action in actions)
            {
                sb.Append($"\n  {action.Value,8:F2}  {action.Key}");
            }
            List<KeyValuePair<Type, long>> ctors;
            lock (gate)
            {
                ctors = ctorTicks.ToList();
            }
            if (ctors.Count > 0)
            {
                double Sec(long ticks) => ticks / (double)Stopwatch.Frequency;
                sb.Append($"\n[RLO] startup static constructors (run through RunClassConstructor, mostly classes marked StaticConstructorOnStartup; includes any Harmony patching they do, Mod class constructors are not included): {ctors.Count} types, {Sec(ctors.Sum(c => c.Value)):F2} s total; slowest mods:");
                foreach (var mod in ctors.GroupBy(c => ModOf(c.Key)).Select(g => new { g.Key, Ticks = g.Sum(c => c.Value) }).OrderByDescending(g => g.Ticks).Take(12))
                {
                    sb.Append($"\n  {Sec(mod.Ticks),8:F2}  {mod.Key}");
                }
                sb.Append("\n  slowest types:");
                foreach (var ctor in ctors.OrderByDescending(c => c.Value).Take(10))
                {
                    sb.Append($"\n  {Sec(ctor.Value),8:F2}  {ctor.Key.FullName} ({ModOf(ctor.Key)})");
                }
            }
            if (timeline.Count > 0)
            {
                lock (gate)
                {
                    sb.Append("\n[RLO] phase timeline (seconds since process start):\n  " + string.Join("\n  ", timeline));
                }
            }
            Log.Message(sb.ToString());
            Diagnostic("string stats", StringStats.Report);
            Diagnostic("object stats", ObjectStats.Report);
            Diagnostic("xml stats", XmlStats.Report);
            Diagnostic("heap stats", HeapStats.Report);
        }
    }
}
