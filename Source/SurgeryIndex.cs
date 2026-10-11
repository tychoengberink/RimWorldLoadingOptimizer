using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class SurgeryIndex
    {
        private static readonly AccessTools.FieldRef<ResearchProjectDef, List<Def>> UnlockedCache = AccessTools.FieldRefAccess<ResearchProjectDef, List<Def>>("cachedUnlockedDefs");
        private static readonly AccessTools.FieldRef<RecipeDef, bool?> SurgeryCache = AccessTools.FieldRefAccess<RecipeDef, bool?>("isSurgeryCached");
        private static int builds, filled, mismatches;
        private static long ticks;

        public static void Install(Harmony harmony)
        {
            var unlocked = AccessTools.PropertyGetter(typeof(ResearchProjectDef), nameof(ResearchProjectDef.UnlockedDefs));
            var surgery = AccessTools.PropertyGetter(typeof(RecipeDef), nameof(RecipeDef.IsSurgery));
            var users = AccessTools.EnumeratorMoveNext(AccessTools.PropertyGetter(typeof(RecipeDef), nameof(RecipeDef.AllRecipeUsers)));
            if (!Fingerprint.Matches("surgery index", Fingerprint.Known(unlocked, "44C6E6A900DE84FC", "7E7F4A04B95ADF25"), Fingerprint.Known(surgery, "5E3C9FE2FC98FDDE", "7B035F4EE8F7FCBA"), Fingerprint.Known(users, "2F9D95E7037F88F6", "8854E37F42492D34")))
            {
                return;
            }
            harmony.Patch(unlocked, prefix: new HarmonyMethod(typeof(SurgeryIndex), nameof(Prefix)));
            LeanFixes.Reports.Add(Stats);
        }

        public static void Prefix(ResearchProjectDef __instance)
        {
            if (UnlockedCache(__instance) != null)
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
                if (RLOMod.Verify)
                {
                    var slow = false;
                    foreach (var user in recipe.AllRecipeUsers)
                    {
                        if (user.category == ThingCategory.Pawn)
                        {
                            slow = true;
                            break;
                        }
                    }
                    if (slow != value)
                    {
                        mismatches++;
                        value = slow;
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
