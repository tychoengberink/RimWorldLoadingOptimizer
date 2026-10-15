using System;
using System.Linq;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class CtorFixes
    {
        private static Harmony harmony;

        public static bool Enabled => harmony != null;

        private static readonly string[] Mods =
        {
            "whatsthatmod", "steppingstones", "lunar",
#if RW17
            "alienworlds",
#endif
        };

        public static bool Needed(RLOSettings settings) => Mods.Any(settings.Supports);

        private static bool On(string id) => RLOMod.Settings.Supports(id);

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
                if (interject != null && On("loadingprogress"))
                {
                    Try("Loading Progress hook", () => harmony.Patch(interject, prefix: new HarmonyMethod(typeof(CtorFixes), nameof(BeforeStaticConstructors)) { priority = Priority.First }));
                }
            });
            Try("What's That Mod", On("whatsthatmod"), () => WtmPatchScan.InstallEarly(harmony));
        }

        private static void Try(string fix, Action install) => RLOMod.Try(fix + " speedup", true, install);

        private static void Try(string fix, bool enabled, Action install) => RLOMod.Try(fix + " speedup", enabled, install);

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
            Try("Research Reinvented: Stepping Stones", On("steppingstones"), () => RrssRecipes.Install(harmony));
            Try("Lunar inlining fixer", On("lunar"), () => LunarInlining.Install(harmony));
            Try("What's That Mod", On("whatsthatmod"), () => WtmPatchScan.InstallLate(harmony));
#if RW17
            Try("Alien Worlds Framework", On("alienworlds"), () => AlienWorldsPresets.Install(harmony));
#endif
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
