#if RW17
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class HarmonyRebuildTiming
    {
        private const string Feature = "Harmony rebuild timing";
        private const int Wrapper = 0, Create = 1, Copy = 2, Fast = 3, Slow = 4, Scan = 5, Load = 6, Detour = 7;
        private static readonly long[] ticks = new long[8];
        private static readonly long[] calls = new long[8];
        private static long wrapperJit, detourJit, references;
        private static readonly Dictionary<MethodBase, int> rebuilds = new Dictionary<MethodBase, int>();
        private static readonly Dictionary<string, int> referenced = new Dictionary<string, int>();
        private static Dictionary<MethodBase, byte[]> state;
        private static PropertyInfo assemblyReferences;
        private static FieldInfo dependencies;
        private static volatile bool active;
        [ThreadStatic] private static bool slowPath;

        public static void Install(Harmony harmony)
        {
            var harmonyAssembly = typeof(Harmony).Assembly;
            Type T(string name) => harmonyAssembly.GetType(name);
            var loader = T("HarmonyLib.GeneratedAssemblyLoader");
            var ctors = loader?.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? new ConstructorInfo[0];
            var update = AccessTools.Method(T("HarmonyLib.PatchFunctions"), "UpdateWrapper");
            var create = AccessTools.Method(T("HarmonyLib.MethodCreator"), "CreateReplacement");
            var copy = AccessTools.Method(T("HarmonyLib.MethodBodyReader"), "FinalizeILCodes");
            var generate = AccessTools.Method(T("HarmonyLib.MethodCreatorConfig"), "GenerateMethod");
            var slow = loader == null ? null : AccessTools.Method(loader, "Generate", new[] { T("MonoMod.Utils.DynamicMethodDefinition"), typeof(IEnumerable<Assembly>) });
            var scan = ctors.Length == 1 ? ctors[0] : null;
            var load = loader == null ? null : AccessTools.Method(loader, "LoadCollectible");
            var detour = AccessTools.Method(T("HarmonyLib.PatchTools"), "DetourMethod");
            assemblyReferences = AccessTools.Property(T("Mono.Cecil.ModuleDefinition"), "AssemblyReferences");
            dependencies = loader == null ? null : AccessTools.Field(loader, "dependencies");
            state = AccessTools.Field(T("HarmonyLib.HarmonySharedState"), "state")?.GetValue(null) as Dictionary<MethodBase, byte[]>;
            if (assemblyReferences == null || dependencies == null || state == null)
            {
                Log.Message($"[RLO] {Feature}: Harmony's internals do not have the expected shape. The Harmony rebuild timing report is skipped; nothing is broken.");
                return;
            }
            if (!Fingerprint.Matches(Feature,
                Fingerprint.Known(update, "4F007C93FF50DD95"),
                Fingerprint.Known(create, "E08133BE98F80F7A"),
                Fingerprint.Known(copy, "A42CDD9F5D3F9A16"),
                Fingerprint.Known(generate, "C830F7F89F2A09F2", "70FA6453A755267A", "7F428227DC9587CB"),
                Fingerprint.Known(slow, "1DF21651824663A3"),
                Fingerprint.Known(scan, "0995A15C7BF271F3"),
                Fingerprint.Known(load, "7141C1E3A7D26AB8"),
                Fingerprint.Known(detour, "1D823234BBA87170")))
            {
                return;
            }
            HarmonyMethod H(string name) => new HarmonyMethod(typeof(HarmonyRebuildTiming), name);
            active = true;
            harmony.Patch(update, prefix: H(nameof(WrapperStart)), postfix: H(nameof(WrapperEnd)));
            harmony.Patch(create, prefix: H(nameof(Start)), postfix: H(nameof(CreateEnd)));
            harmony.Patch(copy, prefix: H(nameof(Start)), postfix: H(nameof(CopyEnd)));
            harmony.Patch(generate, prefix: H(nameof(GenerateStart)), postfix: H(nameof(GenerateEnd)));
            harmony.Patch(slow, prefix: H(nameof(SlowStart)));
            harmony.Patch(scan, prefix: H(nameof(Start)), postfix: H(nameof(ScanEnd)));
            harmony.Patch(load, prefix: H(nameof(Start)), postfix: H(nameof(LoadEnd)));
            harmony.Patch(detour, prefix: H(nameof(DetourStart)), postfix: H(nameof(DetourEnd)));
            MainMenu.Then(Report);
        }

        private static long JitTicks() => JitInfo.GetCompilationTime(true).Ticks;

        private static void Add(int stage, long started)
        {
            if (active)
            {
                Interlocked.Add(ref ticks[stage], Stopwatch.GetTimestamp() - started);
                Interlocked.Increment(ref calls[stage]);
            }
        }

        public static void Start(out long __state) => __state = Stopwatch.GetTimestamp();

        public static void WrapperStart(out (long time, long jit) __state) => __state = (Stopwatch.GetTimestamp(), JitTicks());

        public static void WrapperEnd(MethodBase original, (long time, long jit) __state)
        {
            if (!active)
            {
                return;
            }
            Add(Wrapper, __state.time);
            Interlocked.Add(ref wrapperJit, JitTicks() - __state.jit);
            lock (rebuilds)
            {
                rebuilds.TryGetValue(original, out var count);
                rebuilds[original] = count + 1;
            }
        }

        public static void CreateEnd(long __state) => Add(Create, __state);

        public static void CopyEnd(long __state) => Add(Copy, __state);

        public static void GenerateStart(out long __state)
        {
            slowPath = false;
            __state = Stopwatch.GetTimestamp();
        }

        public static void GenerateEnd(bool structuredHelper, long __state)
        {
            if (!structuredHelper)
            {
                Add(slowPath ? Slow : Fast, __state);
            }
            slowPath = false;
        }

        public static void SlowStart() => slowPath = true;

        public static void ScanEnd(object __instance, object module, long __state)
        {
            if (!active)
            {
                return;
            }
            Add(Scan, __state);
            Interlocked.Add(ref references, (assemblyReferences.GetValue(module) as ICollection)?.Count ?? 0);
            if (dependencies.GetValue(__instance) is Dictionary<string, Assembly> found)
            {
                lock (referenced)
                {
                    foreach (var name in found.Keys)
                    {
                        var shortName = name.Split(',')[0];
                        referenced.TryGetValue(shortName, out var count);
                        referenced[shortName] = count + 1;
                    }
                }
            }
        }

        public static void LoadEnd(long __state) => Add(Load, __state);

        public static void DetourStart(out (long time, long jit) __state) => __state = (Stopwatch.GetTimestamp(), JitTicks());

        public static void DetourEnd((long time, long jit) __state)
        {
            if (active)
            {
                Add(Detour, __state.time);
                Interlocked.Add(ref detourJit, JitTicks() - __state.jit);
            }
        }

        private static string Name(MethodBase method) => $"{method.DeclaringType?.FullName}.{method.Name}";

        public static void Report()
        {
            active = false;
            double S(int stage) => Interlocked.Read(ref ticks[stage]) / (double)Stopwatch.Frequency;
            double J(long jit) => jit / (double)TimeSpan.TicksPerSecond;
            int unique;
            lock (state)
            {
                unique = state.Count;
            }
            List<KeyValuePair<MethodBase, int>> perMethod;
            lock (rebuilds)
            {
                perMethod = rebuilds.OrderByDescending(r => r.Value).ToList();
            }
            List<KeyValuePair<string, int>> refs;
            lock (referenced)
            {
                refs = referenced.OrderByDescending(r => r.Value).Take(8).ToList();
            }
            var lines = new List<string>
            {
                $"Harmony rebuilds: {calls[Wrapper]} in {S(Wrapper):F1} s (JIT on the patching thread {J(wrapperJit):F1} s); building replacements {S(Create):F1} s, of which IL copy and transpilers {S(Copy):F1} s; " +
                $"generate: dynamic method path {calls[Fast]}x {S(Fast):F1} s, assembly path {calls[Slow]}x {S(Slow):F1} s; " +
                $"assembly dependency scans {calls[Scan]}x {S(Scan):F1} s over {Interlocked.Read(ref references)} references; assembly loads {calls[Load]}x {S(Load):F1} s; detours {calls[Detour]}x {S(Detour):F1} s (JIT {J(detourJit):F1} s)",
                perMethod.Count == 0
                    ? $"Harmony patched methods at the main menu: {unique}; no rebuilds seen"
                    : $"Harmony patched methods at the main menu: {unique}; {perMethod.Count} rebuilt during startup, rebuilds per method mean {perMethod.Average(r => r.Value):F2}, max {perMethod[0].Value}; most rebuilt: {string.Join(", ", perMethod.Take(8).Select(r => $"{Name(r.Key)} {r.Value}"))}",
                $"Harmony assembly path references: {(refs.Count == 0 ? "none" : string.Join(", ", refs.Select(r => $"{r.Key} {r.Value}")))}",
            };
            Log.Message("[RLO] " + string.Join("\n[RLO] ", lines));
        }
    }
}
#endif
