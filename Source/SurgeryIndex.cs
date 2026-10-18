using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class SurgeryIndex
    {
        private static AccessTools.FieldRef<ResearchProjectDef, List<Def>> UnlockedCache;
        private static AccessTools.FieldRef<RecipeDef, bool?> SurgeryCache;
        private static int builds, filled, mismatches;
        private static long ticks;
        private static SelfCheck check;

        public static void Install(Harmony harmony)
        {
            var unlocked = AccessTools.PropertyGetter(typeof(ResearchProjectDef), nameof(ResearchProjectDef.UnlockedDefs));
            var surgery = AccessTools.PropertyGetter(typeof(RecipeDef), nameof(RecipeDef.IsSurgery));
            var allUsers = AccessTools.PropertyGetter(typeof(RecipeDef), nameof(RecipeDef.AllRecipeUsers));
            var unlockedField = AccessTools.Field(typeof(ResearchProjectDef), "cachedUnlockedDefs");
            var surgeryField = AccessTools.Field(typeof(RecipeDef), "isSurgeryCached");
            if (unlocked == null || surgery == null || allUsers == null || unlockedField?.FieldType != typeof(List<Def>) || surgeryField?.FieldType != typeof(bool?))
            {
                VersionCheck.Skip("Surgery index", "ResearchProjectDef.UnlockedDefs, RecipeDef.IsSurgery or their cache fields no longer exist");
                return;
            }
            UnlockedCache = AccessTools.FieldRefAccess<ResearchProjectDef, List<Def>>(unlockedField);
            SurgeryCache = AccessTools.FieldRefAccess<RecipeDef, bool?>(surgeryField);
            check = new SelfCheck("Surgery index", VersionCheck.Code(unlocked, surgery, AccessTools.EnumeratorMoveNext(allUsers)));
            harmony.Patch(unlocked, prefix: new HarmonyMethod(typeof(SurgeryIndex), nameof(Prefix)));
            LeanFixes.Reports.Add(Stats);
        }

        public static void Prefix(ResearchProjectDef __instance)
        {
            if (check.IsOff || UnlockedCache(__instance) != null)
            {
                return;
            }
            var recipes = DefDatabase<RecipeDef>.AllDefsListForReading;
            var anyUncached = false;
            for (var i = 0; i < recipes.Count; i++)
            {
                if (!SurgeryCache(recipes[i]).HasValue)
                {
                    anyUncached = true;
                    break;
                }
            }
            if (!anyUncached)
            {
                return;
            }
            var start = Stopwatch.GetTimestamp();
            builds++;
            var pawnRecipes = new HashSet<RecipeDef>();
            foreach (var thing in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (thing.category != ThingCategory.Pawn || thing.recipes == null)
                {
                    continue;
                }
                foreach (var recipe in thing.recipes)
                {
                    if (recipe != null)
                    {
                        pawnRecipes.Add(recipe);
                    }
                }
            }
            for (var i = 0; i < recipes.Count; i++)
            {
                var recipe = recipes[i];
                if (SurgeryCache(recipe).HasValue)
                {
                    continue;
                }
                var value = false;
                var nullUser = false;
                if (recipe.recipeUsers != null)
                {
                    for (var k = 0; k < recipe.recipeUsers.Count; k++)
                    {
                        if (recipe.recipeUsers[k] == null)
                        {
                            nullUser = true;
                            break;
                        }
                        if (recipe.recipeUsers[k].category == ThingCategory.Pawn)
                        {
                            value = true;
                            break;
                        }
                    }
                }
                if (nullUser)
                {
                    continue;
                }
                value = value || pawnRecipes.Contains(recipe);
                if (RLOMod.Verify || check.IsChecking)
                {
                    var slow = recipe.IsSurgery;
                    check.Report(slow == value, $"IsSurgery of {recipe.defName}");
                    if (slow != value)
                    {
                        mismatches++;
                        break;
                    }
                }
                SurgeryCache(recipe) = value;
                filled++;
            }
            ticks += Stopwatch.GetTimestamp() - start;
        }

        public static string Stats() => $"surgery index: {builds} builds, {filled} recipes filled in {LeanFixes.Seconds(ticks):F2} s" + (RLOMod.Verify ? $", verify mismatches {mismatches}" : "");
    }
}
