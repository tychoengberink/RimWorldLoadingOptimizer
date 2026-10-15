#if RW17
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimWorldLoadingOptimizer
{
    // Indexes for two startup scans that compare every ThingDef with every RecipeDef. Nothing is re-implemented: the
    // original loops still run, but over only the recipes that can match (Vanilla Trading Expanded and the vanilla
    // market value recipe check) or over an index of the ThingDefs whose recipe lists contain the recipe (TD Find Lib).
    public static class RecipeScans
    {
        public const string TradingFeature = "Vanilla Trading Expanded recipe scans";
        public const string FindFeature = "TD Find Lib recipe users";

        private const int RecheckMs = 50;

        private static readonly List<RecipeDef> NoRecipes = new List<RecipeDef>();
        private static readonly List<ThingDef> NoThings = new List<ThingDef>();

        // Vanilla Trading Expanded: Utils.Initialize, its IsCraftableOrCollectable and the vanilla CalculableRecipe.
        private static MethodInfo calculable, craftable, allDefs, allDefsForReading;
        private static int tradingDepth, calculableRedirects, craftableRedirects;
        private static bool tradingOthers;
        private static List<RecipeDef> recipesList;
        private static int recipesCount, recipesCheckedAt;
        private static long recipesIdentity;
        private static Dictionary<ThingDef, List<RecipeDef>> singleProduct;
        private static Dictionary<ThingDef, RecipeDef[]> anyProduct;
        private static bool singleBroken, anyBroken;
        private static int calculableServed, craftableServed, tradingBuilds, tradingMismatches, tradingSkipped;
        private static long tradingBuildTicks;

        // TD Find Lib: ModStartup's static constructor reads RecipeDef.AllRecipeUsers through ThingQueryHealth's.
        private static MethodInfo usersGetter;
        private static MethodBase usersMoveNext;
        private static Type findStartup;
        private static bool findOthers;
        private static List<ThingDef> thingsList;
        private static long thingsSignature, thingsIdentity;
        private static int thingsCheckedAt;
        private static Dictionary<RecipeDef, List<ThingDef>> usersByRecipe;
        private static int usersServed, usersFallbacks, findBuilds, findMismatches;
        private static long findBuildTicks;

        public static void InstallTrading(Harmony harmony)
        {
            var utils = AccessTools.TypeByName("VanillaTradingExpanded.Utils");
            if (utils == null)
            {
                return;
            }
            var initialize = AccessTools.Method(utils, "Initialize");
            var markup = AccessTools.Method(utils, "InitMarkupValues");
            craftable = AccessTools.Method(utils, "<Initialize>g__IsCraftableOrCollectable|20_5");
            calculable = AccessTools.Method(typeof(StatWorker_MarketValue), nameof(StatWorker_MarketValue.CalculableRecipe));
            allDefs = AccessTools.PropertyGetter(typeof(DefDatabase<RecipeDef>), nameof(DefDatabase<RecipeDef>.AllDefs));
            allDefsForReading = AccessTools.PropertyGetter(typeof(DefDatabase<RecipeDef>), nameof(DefDatabase<RecipeDef>.AllDefsListForReading));
            if (!Fingerprint.Matches(TradingFeature,
                Fingerprint.Known(initialize, "B55D7BB8D8E0CA6A", "3D8B59FFF55A41BE"),
                Fingerprint.Known(markup, "D66AC84D250593C7"),
                Fingerprint.Known(craftable, "110B7CD3D3034617", "E5E6F170C8BBDCED"),
                Fingerprint.Known(calculable, "B810284997729033", "ADB567B0EEA6459A"),
                Fingerprint.Known(allDefs, "F97A1D76E615BDC6", "ED131EC61EAF709C"),
                Fingerprint.Known(allDefsForReading, "F97A1D76E615BDC6", "ED131EC61EAF709C")))
            {
                return;
            }
            var last = Priority.Last;
            harmony.Patch(calculable, transpiler: new HarmonyMethod(typeof(RecipeScans), nameof(CalculableTranspiler)) { priority = last });
            harmony.Patch(craftable, transpiler: new HarmonyMethod(typeof(RecipeScans), nameof(CraftableTranspiler)) { priority = last });
            if (calculableRedirects != 1 || craftableRedirects != 1)
            {
                harmony.Unpatch(calculable, HarmonyPatchType.Transpiler, harmony.Id);
                harmony.Unpatch(craftable, HarmonyPatchType.Transpiler, harmony.Id);
                Log.Message($"[RLO] {TradingFeature}: the recipe list reads were not found as expected ({calculableRedirects} and {craftableRedirects} of 1). This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            harmony.Patch(initialize, prefix: new HarmonyMethod(typeof(RecipeScans), nameof(TradingBegin)), finalizer: new HarmonyMethod(typeof(RecipeScans), nameof(TradingEnd)));
            MainMenu.UnpatchAfterStartup(calculable);
            MainMenu.UnpatchAfterStartup(craftable);
            MainMenu.UnpatchAfterStartup(initialize);
            LeanFixes.Reports.Add(TradingStats);
        }

        public static void InstallFind(Harmony harmony)
        {
            var startup = AccessTools.TypeByName("TD_Find_Lib.ModStartup");
            var health = AccessTools.TypeByName("TD_Find_Lib.ThingQueryHealth");
            if (startup == null || health == null)
            {
                return;
            }
            usersGetter = AccessTools.PropertyGetter(typeof(RecipeDef), nameof(RecipeDef.AllRecipeUsers));
            usersMoveNext = AccessTools.EnumeratorMoveNext(usersGetter);
            if (!Fingerprint.Matches(FindFeature,
                Fingerprint.Known(startup.TypeInitializer, "D4CA87E5395ABA7A", "9375A78BAC8BE1B8"),
                Fingerprint.Known(health.TypeInitializer, "EBF5F6E43DB40A99", "EDAE68D443DE21DC"),
                Fingerprint.Known(usersGetter, "6D08E5424E14DA06", "8762E8B936E55A89"),
                Fingerprint.Known(usersMoveNext, "5E29B211731A3C4D", "8854E37F42492D34")))
            {
                return;
            }
            findStartup = startup;
            harmony.Patch(usersGetter, prefix: new HarmonyMethod(typeof(RecipeScans), nameof(UsersPrefix)));
            CtorBracket.InstallEarly(harmony);
            CtorBracket.Started += FindStarted;
            CtorBracket.Ended += FindEnded;
            MainMenu.UnpatchAfterStartup(usersGetter);
            LeanFixes.Reports.Add(FindStats);
        }

        // ---------------------------------------------------------------- Vanilla Trading Expanded

        public static IEnumerable<CodeInstruction> CalculableTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            return Redirect(instructions, allDefsForReading, nameof(Candidates), out calculableRedirects);
        }

        public static IEnumerable<CodeInstruction> CraftableTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            return Redirect(instructions, allDefs, nameof(Producers), out craftableRedirects);
        }

        // Replaces the one read of the recipe list with a read that also passes the method's first argument, so the
        // loop that follows runs over the recipes that can match it instead of all of them.
        private static List<CodeInstruction> Redirect(IEnumerable<CodeInstruction> instructions, MethodInfo from, string to, out int redirects)
        {
            var list = instructions.ToList();
            var calls = list.Where(i => i.opcode == OpCodes.Call && i.operand is MethodInfo m && m == from).ToList();
            redirects = calls.Count;
            if (calls.Count != 1)
            {
                return list;
            }
            var at = list.IndexOf(calls[0]);
            var load = new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(calls[0]).MoveBlocksFrom(calls[0]);
            calls[0].operand = AccessTools.Method(typeof(RecipeScans), to);
            list.Insert(at, load);
            return list;
        }

        public static void TradingBegin()
        {
            if (!UnityData.IsInMainThread)
            {
                return;
            }
            if (tradingDepth++ == 0)
            {
                ClearTrading();
                tradingOthers = !Fingerprint.OnlyOurs(calculable) || !Fingerprint.OnlyOurs(craftable);
                if (tradingOthers)
                {
                    tradingSkipped++;
                }
            }
        }

        public static void TradingEnd()
        {
            if (UnityData.IsInMainThread && tradingDepth > 0 && --tradingDepth == 0)
            {
                ClearTrading();
            }
        }

        private static void ClearTrading()
        {
            recipesList = null;
            singleProduct = null;
            anyProduct = null;
        }

        // Called by StatWorker_MarketValue.CalculableRecipe in place of DefDatabase<RecipeDef>.AllDefsListForReading.
        // The loop in that method returns at the first recipe with exactly one product that is the def, so a list
        // with only that recipe (or no recipe) gives the same result.
        public static List<RecipeDef> Candidates(BuildableDef def)
        {
            var all = DefDatabase<RecipeDef>.AllDefsListForReading;
            if (tradingDepth == 0 || !UnityData.IsInMainThread || !TradingReady(all) || singleBroken)
            {
                return all;
            }
            var result = def is ThingDef thing && singleProduct.TryGetValue(thing, out var found) ? found : NoRecipes;
            if (result.Count == 1)
            {
                var products = result[0].products;
                if (products == null || products.Count != 1 || products[0] == null || products[0].thingDef != def)
                {
                    BuildTrading(all);
                    if (singleBroken)
                    {
                        return all;
                    }
                    result = def is ThingDef again && singleProduct.TryGetValue(again, out found) ? found : NoRecipes;
                }
            }
            calculableServed++;
            if (RLOMod.Verify)
            {
                RecipeDef first = null;
                for (var i = 0; i < all.Count; i++)
                {
                    var products = all[i].products;
                    if (products != null && products.Count == 1 && products[0].thingDef == def)
                    {
                        first = all[i];
                        break;
                    }
                }
                if (!ReferenceEquals(first, result.Count == 1 ? result[0] : null))
                {
                    if (tradingMismatches++ < 10)
                    {
                        Log.Warning($"[RLO] {TradingFeature} verify mismatch: market value recipe of {def.defName}");
                    }
                    return all;
                }
            }
            return result;
        }

        // Called by Vanilla Trading Expanded's IsCraftableOrCollectable in place of DefDatabase<RecipeDef>.AllDefs.
        // Its loop returns true at the first recipe that has the def among its products, so a list with one such
        // recipe (or no recipe) gives the same result.
        public static IEnumerable<RecipeDef> Producers(ThingDef thingDef)
        {
            var all = DefDatabase<RecipeDef>.AllDefsListForReading;
            if (tradingDepth == 0 || !UnityData.IsInMainThread || !ReferenceEquals(DefDatabase<RecipeDef>.AllDefs, all) || !TradingReady(all) || anyBroken)
            {
                return DefDatabase<RecipeDef>.AllDefs;
            }
            var result = anyProduct.TryGetValue(thingDef, out var found) ? found : Array.Empty<RecipeDef>();
            craftableServed++;
            if (RLOMod.Verify)
            {
                var made = all.Any(r => r.products != null && r.products.Any(p => p.thingDef == thingDef));
                if (made != (result.Length == 1))
                {
                    if (tradingMismatches++ < 10)
                    {
                        Log.Warning($"[RLO] {TradingFeature} verify mismatch: recipe products of {thingDef.defName}");
                    }
                    return all;
                }
            }
            return result;
        }

        private static bool TradingReady(List<RecipeDef> all)
        {
            if (tradingOthers)
            {
                return false;
            }
            if (singleProduct == null || !ReferenceEquals(all, recipesList) || all.Count != recipesCount)
            {
                BuildTrading(all);
                return true;
            }
            var now = Environment.TickCount;
            if (unchecked(now - recipesCheckedAt) > RecheckMs)
            {
                recipesCheckedAt = now;
                tradingOthers = !Fingerprint.OnlyOurs(calculable) || !Fingerprint.OnlyOurs(craftable);
                if (tradingOthers)
                {
                    tradingSkipped++;
                    return false;
                }
                if (RecipesIdentity(all) != recipesIdentity)
                {
                    BuildTrading(all);
                }
            }
            return true;
        }

        private static void BuildTrading(List<RecipeDef> all)
        {
            var start = Stopwatch.GetTimestamp();
            var single = new Dictionary<ThingDef, List<RecipeDef>>(ReferenceComparer<ThingDef>.Instance);
            var any = new Dictionary<ThingDef, RecipeDef[]>(ReferenceComparer<ThingDef>.Instance);
            singleBroken = false;
            anyBroken = false;
            for (var i = 0; i < all.Count; i++)
            {
                var recipe = all[i];
                var products = recipe.products;
                if (products == null)
                {
                    continue;
                }
                for (var j = 0; j < products.Count; j++)
                {
                    var product = products[j];
                    if (product == null)
                    {
                        // The original loops would throw here for every def they scan past this recipe.
                        anyBroken = true;
                        continue;
                    }
                    if (product.thingDef != null && !any.ContainsKey(product.thingDef))
                    {
                        any[product.thingDef] = new[] { recipe };
                    }
                }
                if (products.Count == 1)
                {
                    var product = products[0];
                    if (product == null)
                    {
                        singleBroken = true;
                    }
                    else if (product.thingDef != null && !single.ContainsKey(product.thingDef))
                    {
                        single[product.thingDef] = new List<RecipeDef> { recipe };
                    }
                }
            }
            singleProduct = single;
            anyProduct = any;
            recipesList = all;
            recipesCount = all.Count;
            recipesIdentity = RecipesIdentity(all);
            recipesCheckedAt = Environment.TickCount;
            tradingBuilds++;
            tradingBuildTicks += Stopwatch.GetTimestamp() - start;
        }

        private static long RecipesIdentity(List<RecipeDef> all)
        {
            unchecked
            {
                long hash = RuntimeHelpers.GetHashCode(all) * 31L + all.Count;
                for (var i = 0; i < all.Count; i++)
                {
                    var recipe = all[i];
                    hash = hash * 31 + (recipe == null ? 0 : RuntimeHelpers.GetHashCode(recipe));
                    var products = recipe?.products;
                    if (products == null)
                    {
                        hash = hash * 31 - 1;
                        continue;
                    }
                    hash = hash * 31 + RuntimeHelpers.GetHashCode(products);
                    hash = hash * 31 + products.Count;
                    for (var j = 0; j < products.Count; j++)
                    {
                        var product = products[j];
                        hash = hash * 31 + (product == null ? 0 : RuntimeHelpers.GetHashCode(product));
                        hash = hash * 31 + (product?.thingDef == null ? 0 : RuntimeHelpers.GetHashCode(product.thingDef));
                    }
                }
                return hash;
            }
        }

        private static string TradingStats()
        {
            var text = $"{TradingFeature}: {calculableServed:N0} market value recipe checks and {craftableServed:N0} product checks answered from the index ({tradingBuilds} builds in {LeanFixes.Seconds(tradingBuildTicks):F2} s)";
            if (tradingSkipped > 0)
            {
                text += $", stepped aside {tradingSkipped} times because another mod patches the same method";
            }
            if (singleBroken || anyBroken)
            {
                text += ", a recipe has an empty product entry so the original scan ran";
            }
            if (RLOMod.Verify)
            {
                text += $"; verify: {tradingMismatches} mismatches";
            }
            return text;
        }

        // ---------------------------------------------------------------- TD Find Lib

        private static void FindStarted(Type type)
        {
            if (type != findStartup)
            {
                return;
            }
            usersByRecipe = null;
            thingsList = null;
            findOthers = !Fingerprint.OnlyOurs(usersGetter) || !Fingerprint.OnlyOurs(usersMoveNext);
        }

        private static void FindEnded(Type type)
        {
            if (type != findStartup)
            {
                return;
            }
            usersByRecipe = null;
            thingsList = null;
        }

        private static bool FindActive() => findStartup != null && !findOthers && CtorBracket.Inside(findStartup);

        public static bool UsersPrefix(RecipeDef __instance, ref IEnumerable<ThingDef> __result)
        {
            if (!FindActive())
            {
                return true;
            }
            __result = Users(__instance);
            return false;
        }

        // Same order as RecipeDef.AllRecipeUsers: its own recipeUsers, read as the enumeration goes, then every
        // ThingDef whose recipes contain it, in DefDatabase order.
        private static IEnumerable<ThingDef> Users(RecipeDef recipe)
        {
            if (recipe.recipeUsers != null)
            {
                for (var i = 0; i < recipe.recipeUsers.Count; i++)
                {
                    yield return recipe.recipeUsers[i];
                }
            }
            var things = DefDatabase<ThingDef>.AllDefsListForReading;
            var users = FindActive() ? Lookup(recipe, things) : null;
            if (users == null)
            {
                usersFallbacks++;
                users = Scan(recipe, things);
            }
            else if (RLOMod.Verify)
            {
                var original = Scan(recipe, things);
                if (!original.SequenceEqual(users, ReferenceComparer<ThingDef>.Instance))
                {
                    if (findMismatches++ < 10)
                    {
                        Log.Warning($"[RLO] {FindFeature} verify mismatch: recipe users of {recipe.defName}");
                    }
                    users = original;
                }
            }
            for (var i = 0; i < users.Count; i++)
            {
                yield return users[i];
            }
        }

        private static List<ThingDef> Scan(RecipeDef recipe, List<ThingDef> things)
        {
            var list = new List<ThingDef>();
            for (var i = 0; i < things.Count; i++)
            {
                if (things[i].recipes != null && things[i].recipes.Contains(recipe))
                {
                    list.Add(things[i]);
                }
            }
            return list;
        }

        private static List<ThingDef> Lookup(RecipeDef recipe, List<ThingDef> things)
        {
            var signature = ThingsSignature(things);
            if (usersByRecipe != null && signature == thingsSignature && ReferenceEquals(things, thingsList))
            {
                var now = Environment.TickCount;
                if (unchecked(now - thingsCheckedAt) > RecheckMs)
                {
                    thingsCheckedAt = now;
                    findOthers = !Fingerprint.OnlyOurs(usersGetter) || !Fingerprint.OnlyOurs(usersMoveNext);
                    if (findOthers)
                    {
                        return null;
                    }
                    if (ThingsIdentity(things) != thingsIdentity)
                    {
                        usersByRecipe = null;
                    }
                }
            }
            if (usersByRecipe == null || signature != thingsSignature || !ReferenceEquals(things, thingsList))
            {
                BuildFind(things);
                thingsSignature = signature;
            }
            usersServed++;
            return usersByRecipe.TryGetValue(recipe, out var users) ? users : NoThings;
        }

        // List<RecipeDef>.Contains compares with EqualityComparer<RecipeDef>.Default (Equals(object), which RecipeDef
        // does not override), so the index groups recipes with that same comparer.
        private static void BuildFind(List<ThingDef> things)
        {
            var start = Stopwatch.GetTimestamp();
            var comparer = EqualityComparer<RecipeDef>.Default;
            var map = new Dictionary<RecipeDef, List<ThingDef>>(comparer);
            var seen = new HashSet<RecipeDef>(comparer);
            for (var i = 0; i < things.Count; i++)
            {
                var recipes = things[i].recipes;
                if (recipes == null)
                {
                    continue;
                }
                seen.Clear();
                for (var j = 0; j < recipes.Count; j++)
                {
                    var recipe = recipes[j];
                    if (recipe == null || !seen.Add(recipe))
                    {
                        continue;
                    }
                    if (!map.TryGetValue(recipe, out var list))
                    {
                        map[recipe] = list = new List<ThingDef>();
                    }
                    list.Add(things[i]);
                }
            }
            usersByRecipe = map;
            thingsList = things;
            thingsIdentity = ThingsIdentity(things);
            thingsCheckedAt = Environment.TickCount;
            findBuilds++;
            findBuildTicks += Stopwatch.GetTimestamp() - start;
        }

        private static long ThingsSignature(List<ThingDef> things)
        {
            unchecked
            {
                long hash = things.Count;
                for (var i = 0; i < things.Count; i++)
                {
                    hash = hash * 31 + (things[i].recipes?.Count ?? -1);
                }
                return hash;
            }
        }

        private static long ThingsIdentity(List<ThingDef> things)
        {
            unchecked
            {
                long hash = RuntimeHelpers.GetHashCode(things);
                for (var i = 0; i < things.Count; i++)
                {
                    hash = hash * 31 + RuntimeHelpers.GetHashCode(things[i]);
                    var recipes = things[i].recipes;
                    if (recipes == null)
                    {
                        continue;
                    }
                    hash = hash * 31 + RuntimeHelpers.GetHashCode(recipes);
                    for (var j = 0; j < recipes.Count; j++)
                    {
                        var recipe = recipes[j];
                        hash = hash * 31 + (recipe == null ? 0 : RuntimeHelpers.GetHashCode(recipe) ^ recipe.defNameHash);
                    }
                }
                return hash;
            }
        }

        private static string FindStats()
        {
            var text = $"{FindFeature}: {usersServed:N0} recipe-user lookups answered from the index ({findBuilds} builds in {LeanFixes.Seconds(findBuildTicks):F2} s), {usersFallbacks:N0} scanned as before";
            if (RLOMod.Verify)
            {
                text += $"; verify: {findMismatches} mismatches";
            }
            return text;
        }
    }
}
#endif
