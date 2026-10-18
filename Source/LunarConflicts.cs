using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class LunarConflicts
    {
        private static readonly MethodInfo GetPatchInfo = AccessTools.Method(typeof(Harmony), nameof(Harmony.GetPatchInfo));
        private static readonly Dictionary<MethodBase, (byte[] bytes, Patches patches)> cache = new Dictionary<MethodBase, (byte[], Patches)>();
        private static Dictionary<MethodBase, byte[]> state;
        private static int methods, calls, hits, misses, checks, mismatches;
        private static SelfCheck check;

        public static void Install(Harmony harmony)
        {
            var group = ModTypes.Find("LunarFramework.Patching.PatchGroup");
            if (group == null)
            {
                return;
            }
            state = AccessTools.Field(AccessTools.TypeByName("HarmonyLib.HarmonySharedState"), "state")?.GetValue(null) as Dictionary<MethodBase, byte[]>;
            var conflicts = AccessTools.Method(group, "CheckForConflicts", new[] { typeof(string), typeof(Action<MethodBase, Patch>) });
            if (state == null || conflicts == null || GetPatchInfo == null)
            {
                VersionCheck.Skip("Lunar conflict check", "HarmonySharedState.state, Harmony.GetPatchInfo or PatchGroup.CheckForConflicts no longer exists");
                return;
            }
            var sharedGet = AccessTools.Method(AccessTools.TypeByName("HarmonyLib.HarmonySharedState"), "GetPatchInfo");
            var processorGet = AccessTools.Method(typeof(PatchProcessor), nameof(PatchProcessor.GetPatchInfo));
            var code = VersionCheck.Code(
                conflicts,
                GetPatchInfo,
                processorGet,
                sharedGet);
            if (!VersionCheck.Pattern("Lunar conflict check", conflicts, code => VersionCheck.Calls(code, GetPatchInfo) >= 1, "reads patch info with Harmony.GetPatchInfo"))
            {
                return;
            }
            check = new SelfCheck("Lunar conflict check", code);
            harmony.Patch(conflicts, transpiler: new HarmonyMethod(typeof(LunarConflicts), nameof(Transpiler)));
            Ctors2Mod.Reports.Add(Stats);
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            methods++;
            return IlScan.Replace(instructions, GetPatchInfo, AccessTools.Method(typeof(LunarConflicts), nameof(Info)));
        }

        private static byte[] Bytes(MethodBase method)
        {
            lock (state)
            {
                return state.TryGetValue(method, out var bytes) ? bytes : null;
            }
        }

        public static Patches Info(MethodBase method)
        {
            calls++;
            if (method == null || check.IsOff)
            {
                return Harmony.GetPatchInfo(method);
            }
            var before = Bytes(method);
            if (before != null && cache.TryGetValue(method, out var entry) && ReferenceEquals(entry.bytes, before))
            {
                hits++;
                if (Ctors2Mod.Verify || check.IsChecking)
                {
                    var fresh = Harmony.GetPatchInfo(method);
                    var same = Compare(method, entry.patches, fresh);
                    check.Report(same, $"patch info of {method.Name}");
                    if (!same)
                    {
                        return fresh;
                    }
                }
                return entry.patches;
            }
            misses++;
            var patches = Harmony.GetPatchInfo(method);
            if (before != null && patches != null && ReferenceEquals(before, Bytes(method)))
            {
                cache[method] = (before, patches);
            }
            return patches;
        }

        private static bool Compare(MethodBase method, Patches cached, Patches fresh)
        {
            checks++;
            bool Same(IEnumerable<Patch> a, IEnumerable<Patch> b) => a.Select(Key).SequenceEqual(b.Select(Key));
            if (fresh == null || !Same(cached.Prefixes, fresh.Prefixes) || !Same(cached.Postfixes, fresh.Postfixes) || !Same(cached.Transpilers, fresh.Transpilers)
                || !Same(cached.Finalizers, fresh.Finalizers) || !Same(cached.InnerPrefixes, fresh.InnerPrefixes) || !Same(cached.InnerPostfixes, fresh.InnerPostfixes))
            {
                if (++mismatches <= 10)
                {
                    Log.Warning($"[RLO] lunar verify mismatch for {method.FullDescription()}");
                }
                return false;
            }
            return true;
        }

        private static string Key(Patch p) => $"{p.index}|{p.owner}|{p.priority}|{string.Join(",", p.before ?? new string[0])}|{string.Join(",", p.after ?? new string[0])}|{p.debug}|{p.PatchMethod?.MetadataToken}|{p.PatchMethod?.Module.ModuleVersionId}";

        public static string Stats() => $"lunar conflict check: {methods} methods, {calls} patch-info calls, {hits} served from cache, {misses} deserialized" + (Ctors2Mod.Verify ? $", verify checks {checks}, mismatches {mismatches}" : "");
    }
}
