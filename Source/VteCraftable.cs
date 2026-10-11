using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using RimWorld;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class VteCraftable
    {
        private static MethodInfo original;
        private static FieldInfo specific, collectable;
        private static HashSet<ThingDef> products;
        private static bool active;
        private static int calls, served, checks, mismatches, marketCalls, marketServed, marketChecks, marketMismatches;
        private static Dictionary<ThingDef, RecipeDef> firstRecipe;

        public static void Install(Harmony harmony)
        {
            var utils = ModTypes.Find("VanillaTradingExpanded.Utils");
            var initialize = utils == null ? null : AccessTools.Method(utils, "Initialize");
            original = utils == null ? null : AccessTools.GetDeclaredMethods(utils).FirstOrDefault(m => m.Name.StartsWith("<Initialize>g__IsCraftableOrCollectable") && m.IsStatic && m.ReturnType == typeof(bool));
            specific = utils == null ? null : AccessTools.Field(utils, "craftableItemsSpecific");
            collectable = utils == null ? null : AccessTools.Field(utils, "collectableThings");
            if (initialize == null || original == null || specific?.FieldType != typeof(HashSet<ThingDef>) || collectable?.FieldType != typeof(HashSet<ThingDef>))
            {
                return;
            }
            if (!Fingerprint.Matches("Vanilla Trading Expanded craftable check", Fingerprint.Known(original, "E5E6F170C8BBDCED")) || !Ctors2Mod.OnlyOurs(original))
            {
                return;
            }
            harmony.Patch(initialize, prefix: new HarmonyMethod(typeof(VteCraftable), nameof(Begin)), finalizer: new HarmonyMethod(typeof(VteCraftable), nameof(End)));
            harmony.Patch(original, prefix: new HarmonyMethod(typeof(VteCraftable), nameof(Prefix)));
            var calculable = AccessTools.Method(typeof(StatWorker_MarketValue), nameof(StatWorker_MarketValue.CalculableRecipe));
            if (Ctors2Mod.Enabled("vtemarket") && calculable != null && (Harmony.GetPatchInfo(calculable)?.Transpilers.Count ?? 0) == 0 && Fingerprint.Matches("market value recipe lookup", Fingerprint.Known(calculable, "ADB567B0EEA6459A")))
            {
                harmony.Patch(calculable, transpiler: new HarmonyMethod(typeof(VteCraftable), nameof(CalculableTranspiler)));
            }
            Ctors2Mod.Reports.Add(Stats);
        }

        public static void Begin()
        {
            active = UnityData.IsInMainThread;
            products = null;
            firstRecipe = null;
        }

        public static void End()
        {
            active = false;
            products = null;
            firstRecipe = null;
        }

        public static IEnumerable<CodeInstruction> CalculableTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(VteCraftable), nameof(Calculable)));
            yield return new CodeInstruction(OpCodes.Ret);
        }

        public static RecipeDef Calculable(BuildableDef def)
        {
            if (!active || !UnityData.IsInMainThread)
            {
                return OriginalCalculable(def);
            }
            marketCalls++;
            if (!def.CostList.NullOrEmpty() || def.CostStuffCount > 0)
            {
                return null;
            }
            if (firstRecipe == null)
            {
                firstRecipe = new Dictionary<ThingDef, RecipeDef>(ReferenceComparer<ThingDef>.Instance);
                var recipes = DefDatabase<RecipeDef>.AllDefsListForReading;
                for (var i = 0; i < recipes.Count; i++)
                {
                    var recipe = recipes[i];
                    if (recipe.products == null || recipe.products.Count != 1)
                    {
                        continue;
                    }
                    var product = recipe.products[0].thingDef;
                    if (product != null && !firstRecipe.ContainsKey(product))
                    {
                        firstRecipe[product] = recipe;
                    }
                }
            }
            RecipeDef result = null;
            if (def is ThingDef thing && firstRecipe.TryGetValue(thing, out var found))
            {
                result = found;
                for (var j = 0; j < found.ingredients.Count; j++)
                {
                    if (!found.ingredients[j].IsFixedIngredient)
                    {
                        result = null;
                        break;
                    }
                }
            }
            marketServed++;
            if (Ctors2Mod.Verify)
            {
                marketChecks++;
                if (OriginalCalculable(def) != result && ++marketMismatches <= 10)
                {
                    Log.Warning($"[RLO] market recipe verify mismatch for {def}");
                }
            }
            return result;
        }

        private static RecipeDef OriginalCalculable(BuildableDef def)
        {
            if (def.CostList.NullOrEmpty() && def.CostStuffCount <= 0)
            {
                var recipes = DefDatabase<RecipeDef>.AllDefsListForReading;
                for (var i = 0; i < recipes.Count; i++)
                {
                    var recipe = recipes[i];
                    if (recipe.products == null || recipe.products.Count != 1 || recipe.products[0].thingDef != def)
                    {
                        continue;
                    }
                    for (var j = 0; j < recipe.ingredients.Count; j++)
                    {
                        if (!recipe.ingredients[j].IsFixedIngredient)
                        {
                            return null;
                        }
                    }
                    return recipe;
                }
            }
            return null;
        }

        public static bool Prefix(ThingDef thingDef, ref bool __result)
        {
            calls++;
            if (!active || !UnityData.IsInMainThread)
            {
                return true;
            }
            if (products == null)
            {
                products = new HashSet<ThingDef>(ReferenceComparer<ThingDef>.Instance);
                foreach (var recipe in DefDatabase<RecipeDef>.AllDefs)
                {
                    if (recipe.products != null)
                    {
                        foreach (var product in recipe.products)
                        {
                            products.Add(product.thingDef);
                        }
                    }
                }
            }
            __result = products.Contains(thingDef) || ((HashSet<ThingDef>)specific.GetValue(null)).Contains(thingDef) || ((HashSet<ThingDef>)collectable.GetValue(null)).Contains(thingDef);
            served++;
            if (Ctors2Mod.Verify)
            {
                checks++;
                if (Original(thingDef) != __result && ++mismatches <= 10)
                {
                    Log.Warning($"[RLO] vte verify mismatch for {thingDef}");
                }
            }
            return false;
        }

        private static bool Original(ThingDef thingDef)
        {
            foreach (var recipe in DefDatabase<RecipeDef>.AllDefs)
            {
                if (recipe.products != null)
                {
                    foreach (var product in recipe.products)
                    {
                        if (product.thingDef == thingDef)
                        {
                            return true;
                        }
                    }
                }
            }
            return ((HashSet<ThingDef>)specific.GetValue(null)).Contains(thingDef) || ((HashSet<ThingDef>)collectable.GetValue(null)).Contains(thingDef);
        }

        public static string Stats() => $"vte craftable check: {calls} calls, {served} served from the product set" + (Ctors2Mod.Verify ? $", verify checks {checks}, mismatches {mismatches}" : "")
            + $"; market recipe lookups {marketCalls} in window, {marketServed} served" + (Ctors2Mod.Verify ? $", verify checks {marketChecks}, mismatches {marketMismatches}" : "");
    }
}
