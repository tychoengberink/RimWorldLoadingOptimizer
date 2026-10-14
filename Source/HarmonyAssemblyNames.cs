#if RW17
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class HarmonyAssemblyNames
    {
        private const string Feature = "Harmony assembly name cache";
        private static readonly ConditionalWeakTable<Assembly, string> names = new ConditionalWeakTable<Assembly, string>();
        private static readonly MethodInfo getName = AccessTools.Method(typeof(Assembly), nameof(Assembly.GetName), Type.EmptyTypes);
        private static readonly MethodInfo getFullName = AccessTools.PropertyGetter(typeof(AssemblyName), nameof(AssemblyName.FullName));
        private static int redirected;
        private static long hits, misses, mismatches;

        public static void Install(Harmony harmony)
        {
            var loader = typeof(Harmony).Assembly.GetType("HarmonyLib.GeneratedAssemblyLoader");
            var ctors = loader?.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? new ConstructorInfo[0];
            var ctor = ctors.Length == 1 ? ctors[0] : null;
            var closures = ctor == null ? new List<MethodInfo>() : PatchProcessor.ReadMethodBody(ctor)
                .Where(p => p.Key == OpCodes.Ldftn)
                .Select(p => p.Value as MethodInfo)
                .Where(m => m != null && m.DeclaringType?.DeclaringType == loader)
                .Distinct()
                .ToList();
            if (closures.Count != 1)
            {
                Log.Message($"[RLO] {Feature}: Harmony's generated assembly loader does not have the expected shape. This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            if (!Fingerprint.Matches(Feature,
                Fingerprint.Known(ctor, "0995A15C7BF271F3"),
                Fingerprint.Known(closures[0], "83F363FB75E3968E")))
            {
                return;
            }
            harmony.Patch(closures[0], transpiler: new HarmonyMethod(typeof(HarmonyAssemblyNames), nameof(Transpiler)));
            if (redirected == 0)
            {
                Log.Message($"[RLO] {Feature}: the assembly name lookup was not found exactly once. This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            LeanFixes.Reports.Add(Stats);
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var at = Enumerable.Range(0, Math.Max(0, list.Count - 1))
                .Where(i => Calls(list[i], getName) && Calls(list[i + 1], getFullName) && list[i + 1].labels.Count == 0 && list[i + 1].blocks.Count == 0)
                .ToList();
            if (at.Count != 1)
            {
                return list;
            }
            var first = list[at[0]];
            list[at[0]] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(HarmonyAssemblyNames), nameof(FullNameOf))).WithLabels(first.labels).WithBlocks(first.blocks);
            list.RemoveAt(at[0] + 1);
            Interlocked.Increment(ref redirected);
            return list;
        }

        private static bool Calls(CodeInstruction instruction, MethodInfo method) =>
            (instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) && instruction.operand is MethodInfo m && m == method;

        public static string FullNameOf(Assembly assembly)
        {
            if (assembly is null)
            {
                return assembly.GetName().FullName;
            }
            if (names.TryGetValue(assembly, out var name))
            {
                Interlocked.Increment(ref hits);
                if (RLOMod.Verify)
                {
                    var fresh = assembly.GetName().FullName;
                    if (fresh != name)
                    {
                        if (Interlocked.Increment(ref mismatches) <= 10)
                        {
                            Log.Warning($"[RLO] {Feature} verify mismatch: cached {name}, now {fresh}");
                        }
                        return fresh;
                    }
                }
                return name;
            }
            Interlocked.Increment(ref misses);
            name = assembly.GetName().FullName;
            names.AddOrUpdate(assembly, name);
            return name;
        }

        public static string Stats() => $"Harmony assembly name cache: {Interlocked.Read(ref hits)} hits, {Interlocked.Read(ref misses)} names computed" + (RLOMod.Verify ? $", {Interlocked.Read(ref mismatches)} verify mismatches" : "");
    }
}
#endif
