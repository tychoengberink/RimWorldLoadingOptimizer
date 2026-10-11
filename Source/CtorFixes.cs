using System;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class CtorFixes
    {
        private static Harmony harmony;

        public static bool Enabled => harmony != null;

        public static void InstallEarly(Harmony h)
        {
            harmony = h;
            var callAll = AccessTools.Method(typeof(StaticConstructorOnStartupUtility), nameof(StaticConstructorOnStartupUtility.CallAll));
            if (callAll != null)
            {
                harmony.Patch(callAll, prefix: new HarmonyMethod(typeof(CtorFixes), nameof(BeforeStaticConstructors)) { priority = Priority.First });
            }
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                var interject = AccessTools.Method(AccessTools.TypeByName("ilyvion.LoadingProgress.StaticConstructorOnStartupUtilityReplacement"), "Interject");
                if (interject != null)
                {
                    Try("Loading Progress hook", () => harmony.Patch(interject, prefix: new HarmonyMethod(typeof(CtorFixes), nameof(BeforeStaticConstructors)) { priority = Priority.First }));
                }
            });
            Try("Children Wear Anything", () => CwaTextures.InstallEarly(harmony));
            Try("What's That Mod", () => WtmPatchScan.InstallEarly(harmony));
        }

        private static void Try(string fix, Action install)
        {
            try
            {
                install();
            }
            catch (Exception e)
            {
                Log.Error($"[RLO] {fix} speedup could not be enabled and was skipped: {e}");
            }
        }

        public static string Stats() => $"{CwaTextures.Stats()}; {RrssRecipes.Stats()}; {VehiclePatterns.Stats()}; {LunarInlining.Stats()}; {GiddyOffsets.Stats()}; {WtmPatchScan.Stats()}";

        private static bool lateInstalled;

        public static void BeforeStaticConstructors()
        {
            InstallLate();
        }

        private static void InstallLate()
        {
            if (!Enabled || lateInstalled || !UnityData.IsInMainThread)
            {
                return;
            }
            lateInstalled = true;
            Try("Children Wear Anything", () => CwaTextures.Install(harmony));
            Try("Research Reinvented: Stepping Stones", () => RrssRecipes.Install(harmony));
            Try("Vehicle Framework", () => VehiclePatterns.Install(harmony));
            Try("Lunar inlining fixer", () => LunarInlining.Install(harmony));
            Try("Giddy-Up", () => GiddyOffsets.Install(harmony));
            Try("What's That Mod", () => WtmPatchScan.InstallLate(harmony));
        }

        [StaticConstructorOnStartup]
        public static class Late
        {
            static Late()
            {
                InstallLate();
            }
        }
    }
}
