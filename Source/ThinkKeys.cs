using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimWorldLoadingOptimizer
{
    public static class ThinkKeys
    {
        private static HashSet<int> assignedKeys;
        private static Action<ThinkNode, int> setKey;
        private static readonly Dictionary<long, (int key, uint step)> resume = new Dictionary<long, (int, uint)>();
        private static bool installed;
        private static SelfCheck check;
        private static long calls, resumed, steps, savedSteps, mismatches;

        public static void Install(Harmony harmony)
        {
            check = new SelfCheck("Think tree keys", VersionCheck.Known(
                Fingerprint.Known(AccessTools.Method(typeof(ThinkTreeKeyAssigner), nameof(ThinkTreeKeyAssigner.AssignSingleKey)), "5F80A0260F27978E", "4D3275DCC7E962A1"),
                Fingerprint.Known(AccessTools.Method(typeof(ThinkTreeKeyAssigner), "NextUnusedKeyFor"), "7E8C53C4A99BEA8A", "30BAC1C63224F969"),
                Fingerprint.Known(AccessTools.PropertyGetter(typeof(Rand), nameof(Rand.Int)), "74EE147097A6CFC8", "5DD38681AFED91F0"),
                Fingerprint.Known(AccessTools.Method(typeof(Rand), nameof(Rand.PushState), new[] { typeof(int) }), "99D8A9EEA95443AD", "77FBC5DDF3B58E43"),
                Fingerprint.Known(AccessTools.Method(typeof(Gen), nameof(Gen.HashCombineInt), new[] { typeof(int), typeof(int) }), "B633B26FE82DCA7E", "CD5CA098156F1883"),
                Fingerprint.Known(AccessTools.Method(typeof(GenText), nameof(GenText.StableStringHash)), "C560D0D8E1CDEB24", "B18235CB8CB5E22A")));
            assignedKeys = (HashSet<int>)AccessTools.Field(typeof(ThinkTreeKeyAssigner), "assignedKeys").GetValue(null);
            setKey = AccessTools.MethodDelegate<Action<ThinkNode, int>>(AccessTools.Method(typeof(ThinkNode), "SetUniqueSaveKey"));
            harmony.Patch(AccessTools.Method(typeof(ThinkTreeKeyAssigner), nameof(ThinkTreeKeyAssigner.AssignSingleKey)), prefix: new HarmonyMethod(typeof(ThinkKeys), nameof(AssignSingleKeyPrefix)),
                postfix: new HarmonyMethod(typeof(ThinkKeys), nameof(AssignSingleKeyPostfix)));
            harmony.Patch(AccessTools.Method(typeof(ThinkTreeKeyAssigner), nameof(ThinkTreeKeyAssigner.Reset)), postfix: new HarmonyMethod(typeof(ThinkKeys), nameof(ResetPostfix)));
            installed = true;
        }

        public static void ResetPostfix()
        {
            resume.Clear();
        }

        public static bool AssignSingleKeyPrefix(ThinkNode node, int startHash, out (bool checking, long id, int key, uint step) __state)
        {
            __state = default;
            if (check.IsOff)
            {
                return true;
            }
            var pathHash = 0;
            for (var ancestor = node; ancestor != null; ancestor = ancestor.parent)
            {
                pathHash = Gen.HashCombineInt(pathHash, GenText.StableStringHash(ancestor.GetType().Name));
            }
            var seed = (uint)startHash;
            var id = ((long)startHash << 32) | (uint)pathHash;
            var key = pathHash;
            var step = 0u;
            if (resume.TryGetValue(id, out var state))
            {
                key = state.key;
                step = state.step;
                resumed++;
                savedSteps += step;
            }
            while (assignedKeys.Contains(key))
            {
                key ^= MurmurHash.GetInt(seed, step++);
                steps++;
            }
            if (RLOMod.Verify || check.IsChecking)
            {
                __state = (true, id, key, step);
                return true;
            }
            assignedKeys.Add(key);
            resume[id] = (key, step);
            setKey(node, key);
            calls++;
            return false;
        }

        public static void AssignSingleKeyPostfix(ThinkNode node, (bool checking, long id, int key, uint step) __state)
        {
            if (!__state.checking)
            {
                return;
            }
            calls++;
            var same = node.UniqueSaveKey == __state.key;
            check.Report(same, $"think key of {node.GetType().Name}");
            if (same)
            {
                resume[__state.id] = (__state.key, __state.step);
                return;
            }
            resume.Remove(__state.id);
            mismatches++;
            if (mismatches <= 5)
            {
                Log.Warning($"[RLO] think key mismatch for {node.GetType().FullName}: fast {__state.key}, original {node.UniqueSaveKey}");
            }
        }

        public static string Stats()
        {
            return installed
                ? $"think keys: {calls} single assigns, {resumed} resumed, {steps} probes, {savedSteps} probes skipped, {mismatches} verify mismatches"
                : "think keys: off";
        }
    }
}
