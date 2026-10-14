#if RW17
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class BackgroundGc
    {
        private static readonly MethodInfo Blocking = AccessTools.Method(typeof(GC), nameof(GC.Collect), new[] { typeof(int), typeof(GCCollectionMode) });

        private static int redirected;
        private static long heapAtSkipMb;

        public static void Install(Harmony harmony)
        {
            var targets = new[] { typeof(PlayDataLoader) }.Concat(typeof(PlayDataLoader).GetNestedTypes(AccessTools.all))
                .SelectMany(type => type.GetMethods(AccessTools.all | BindingFlags.DeclaredOnly))
                .Where(method => !method.IsAbstract && !method.ContainsGenericParameters && method.GetMethodBody() != null && IlScan.CallsAny(method, Blocking))
                .ToList();
            if (targets.Count != 1)
            {
                Log.Message($"[RLO] load-end memory clean-up: expected one forced collection in the loading code, found {targets.Count}; skipped");
                return;
            }
            if (!Fingerprint.Matches("Skip the load-end memory clean-up", Fingerprint.Known(targets[0], "ECC3E392B58F8845")))
            {
                return;
            }
            harmony.Patch(targets[0], transpiler: new HarmonyMethod(typeof(BackgroundGc), nameof(Transpiler)));
            if (RLOMod.Settings.Supports("loadingprogress"))
            {
                LongEventHandler.ExecuteWhenFinished(() => RLOMod.Try("skip the load-end memory clean-up (Loading Progress)", true, () => PatchLoadingProgress(harmony)));
            }
            MainMenu.Then(() => Log.Message(redirected == 0
                ? "[RLO] load-end memory clean-up: not reached before the main menu"
                : $"[RLO] load-end memory clean-up: the forced full collection was skipped (managed heap {heapAtSkipMb} MB at that point, {GC.GetTotalMemory(false) >> 20} MB at the main menu); the runtime collects as usual"));
        }

        private static void PatchLoadingProgress(Harmony harmony)
        {
            var type = AccessTools.TypeByName("ilyvion.LoadingProgress.StaticConstructorOnStartupUtilityReplacement");
            var method = type == null ? null : AccessTools.Method(type, "CollectGarbage");
            if (method == null || !IlScan.CallsAny(method, Blocking))
            {
                return;
            }
            if (!Fingerprint.Matches("Skip the load-end memory clean-up (Loading Progress)", Fingerprint.Known(method, "098D3D0C587B916C")))
            {
                return;
            }
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(BackgroundGc), nameof(Transpiler)));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            IlScan.Replace(instructions, Blocking, AccessTools.Method(typeof(BackgroundGc), nameof(Collect)));

        public static void Collect(int generation, GCCollectionMode mode)
        {
            heapAtSkipMb = GC.GetTotalMemory(false) >> 20;
            redirected++;
        }
    }
}
#endif
