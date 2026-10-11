using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class EarlyLoadBoost
    {
        private const float Budget = 0.25f;

        private static AccessTools.FieldRef<object, int> skipFrames;

        public static void Install(Harmony harmony)
        {
            var delayedActions = AccessTools.TypeByName("FasterGameLoading.DelayedActions");
            var loader = AccessTools.TypeByName("FasterGameLoading.EarlyModContentLoader");
            var budget = AccessTools.PropertyGetter(delayedActions, "MaxImpactThisFrame");
            var update = AccessTools.Method(loader, "Update");
            if (budget == null || update == null || AccessTools.Field(loader, "skipFrames") == null)
            {
                return;
            }
            skipFrames = AccessTools.FieldRefAccess<int>(loader, "skipFrames");
            harmony.Patch(budget, postfix: new HarmonyMethod(typeof(EarlyLoadBoost), nameof(BudgetPostfix)));
            harmony.Patch(update, prefix: new HarmonyMethod(typeof(EarlyLoadBoost), nameof(UpdatePrefix)));
        }

        private static bool Startup => !PlayDataLoader.Loaded && Current.Game == null;

        public static void BudgetPostfix(ref float __result)
        {
            if (Startup && __result < Budget)
            {
                __result = Budget;
            }
        }

        public static void UpdatePrefix(object __instance)
        {
            if (Startup)
            {
                skipFrames(__instance) = 0;
            }
        }
    }
}
