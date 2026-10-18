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
    public static class PatchInfoReuse
    {
        private const string Feature = "Harmony patch state reuse";
        private static readonly ConditionalWeakTable<byte[], StrongBox<int>> versions = new ConditionalWeakTable<byte[], StrongBox<int>>();
        private static Dictionary<MethodBase, byte[]> state;
        private static MethodInfo getPatchInfo;
        private static MethodInfo deserializeMethod;
        private static Func<byte[], PatchInfo> deserialize;
        private static long recorded, hits, misses, mismatches;
        private static SelfCheck check;

        public static void Install(Harmony harmony)
        {
            var harmonyAssembly = typeof(Harmony).Assembly;
            var shared = harmonyAssembly.GetType("HarmonyLib.HarmonySharedState");
            var serialization = harmonyAssembly.GetType("HarmonyLib.PatchInfoSerialization");
            var config = harmonyAssembly.GetType("HarmonyLib.MethodCreatorConfig");
            getPatchInfo = shared == null ? null : AccessTools.Method(shared, "GetPatchInfo", new[] { typeof(MethodBase) });
            deserializeMethod = serialization == null ? null : AccessTools.Method(serialization, "Deserialize", new[] { typeof(byte[]) });
            var prepare = config == null ? null : AccessTools.Method(config, "Prepare");
            var update = shared == null ? null : AccessTools.Method(shared, "UpdatePatchInfo");
            state = shared == null ? null : AccessTools.Field(shared, "state")?.GetValue(null) as Dictionary<MethodBase, byte[]>;
            if (state == null || deserializeMethod == null || deserializeMethod.ReturnType != typeof(PatchInfo))
            {
                Log.Message($"[RLO] {Feature}: Harmony's patch state does not have the expected shape. This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            if (getPatchInfo == null || prepare == null || update == null)
            {
                VersionCheck.Skip(Feature, "a method it needs no longer exists");
                return;
            }
            if (!VersionCheck.Pattern(Feature, prepare, code => VersionCheck.Calls(code, getPatchInfo) == 1 && code.All(OnlyReadsVersionCount),
                "reads only the version count from the patch state"))
            {
                return;
            }
            check = new SelfCheck(Feature, VersionCheck.Code(getPatchInfo, prepare, deserializeMethod, update));
            if (check.IsOff)
            {
                return;
            }
            deserialize = AccessTools.MethodDelegate<Func<byte[], PatchInfo>>(deserializeMethod);
            harmony.Patch(getPatchInfo, transpiler: new HarmonyMethod(typeof(PatchInfoReuse), nameof(RecordTranspiler)));
            harmony.Patch(prepare, transpiler: new HarmonyMethod(typeof(PatchInfoReuse), nameof(PrepareTranspiler)));
            LeanFixes.Reports.Add(Stats);
        }

        private static bool OnlyReadsVersionCount(CodeInstruction instruction)
        {
            switch (instruction.operand)
            {
                case FieldInfo field:
                    if (field.DeclaringType == typeof(PatchInfo))
                    {
                        return field.Name == nameof(PatchInfo.VersionCount) && instruction.opcode == OpCodes.Ldfld;
                    }
                    return instruction.opcode != OpCodes.Stfld && instruction.opcode != OpCodes.Stsfld || !field.FieldType.IsAssignableFrom(typeof(PatchInfo));
                case MethodBase method:
                    if (method.DeclaringType == typeof(PatchInfo))
                    {
                        return method is ConstructorInfo ? instruction.opcode == OpCodes.Newobj : method.Name == "get_" + nameof(PatchInfo.VersionCount);
                    }
                    return method == getPatchInfo || method.GetParameters().All(p => p.ParameterType != typeof(PatchInfo));
                default:
                    return true;
            }
        }

        public static IEnumerable<CodeInstruction> RecordTranspiler(IEnumerable<CodeInstruction> instructions) =>
            IlScan.Replace(instructions, deserializeMethod, AccessTools.Method(typeof(PatchInfoReuse), nameof(Deserialize)));

        public static IEnumerable<CodeInstruction> PrepareTranspiler(IEnumerable<CodeInstruction> instructions) =>
            IlScan.Replace(instructions, getPatchInfo, AccessTools.Method(typeof(PatchInfoReuse), nameof(ForPrepare)));

        public static PatchInfo Deserialize(byte[] bytes)
        {
            var info = deserialize(bytes);
            if (!versions.TryGetValue(bytes, out _))
            {
                versions.AddOrUpdate(bytes, new StrongBox<int>(info.VersionCount));
                Interlocked.Increment(ref recorded);
            }
            return info;
        }

        public static PatchInfo ForPrepare(MethodBase original)
        {
            byte[] bytes;
            lock (state)
            {
                state.TryGetValue(original, out bytes);
            }
            if (bytes == null)
            {
                return null;
            }
            if (check.IsOff)
            {
                return deserialize(bytes);
            }
            if (!versions.TryGetValue(bytes, out var version))
            {
                Interlocked.Increment(ref misses);
                return Deserialize(bytes);
            }
            Interlocked.Increment(ref hits);
            if (RLOMod.Verify || check.IsChecking)
            {
                var fresh = deserialize(bytes).VersionCount;
                check.Report(fresh == version.Value, $"version of {original.DeclaringType?.FullName}.{original.Name}");
                if (fresh != version.Value)
                {
                    if (Interlocked.Increment(ref mismatches) <= 10)
                    {
                        Log.Warning($"[RLO] {Feature} verify mismatch for {original.DeclaringType?.FullName}.{original.Name}: reused version {version.Value}, deserialized {fresh}");
                    }
                    return new PatchInfo { VersionCount = fresh };
                }
            }
            return new PatchInfo { VersionCount = version.Value };
        }

        public static string Stats() => $"Harmony patch state reuse: {Interlocked.Read(ref hits)} deserializations skipped, {Interlocked.Read(ref misses)} misses, {Interlocked.Read(ref recorded)} states recorded" + (RLOMod.Verify ? $", {Interlocked.Read(ref mismatches)} verify mismatches" : "");
    }
}
#endif
