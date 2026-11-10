using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class EarlyLoadBoost
    {
        private const string Feature = "Faster early content loading";
        private const float Budget = 0.25f;

        private static AccessTools.FieldRef<object, int> skipFrames;

        public static void Install(Harmony harmony)
        {
            var delayedActions = AccessTools.TypeByName("FasterGameLoading.DelayedActions");
            var loader = AccessTools.TypeByName("FasterGameLoading.EarlyModContentLoader");
            var budget = AccessTools.PropertyGetter(delayedActions, "MaxImpactThisFrame");
            var update = AccessTools.Method(loader, "Update");
            var field = loader == null ? null : AccessTools.Field(loader, "skipFrames");
            if (budget == null || update == null || field == null)
            {
                return;
            }
            if (budget.ReturnType != typeof(float) || field.FieldType != typeof(int))
            {
                VersionCheck.Skip(Feature, "the frame budget or the frame skip counter has another type");
                return;
            }
            if (!VersionCheck.Pattern(Feature, update, code => CountsDown(code, field), "counts its skipped frames down"))
            {
                return;
            }
            skipFrames = AccessTools.FieldRefAccess<int>(loader, "skipFrames");
            harmony.Patch(budget, postfix: new HarmonyMethod(typeof(EarlyLoadBoost), nameof(BudgetPostfix)));
            harmony.Patch(update, prefix: new HarmonyMethod(typeof(EarlyLoadBoost), nameof(UpdatePrefix)));
        }

        private static bool CountsDown(List<CodeInstruction> code, FieldInfo field)
        {
            for (var i = 2; i < code.Count; i++)
            {
                if (code[i].opcode == OpCodes.Stfld && Equals(code[i].operand, field) && code[i - 1].opcode == OpCodes.Sub && code[i - 2].opcode == OpCodes.Ldc_I4_1)
                {
                    return code.Any(c => c.opcode == OpCodes.Ldfld && Equals(c.operand, field));
                }
            }
            return false;
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
