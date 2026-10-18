using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class RrssRecipes
    {
        private static readonly MethodInfo AllRecipeUsers = AccessTools.PropertyGetter(typeof(RecipeDef), nameof(RecipeDef.AllRecipeUsers));
        private static readonly MethodInfo IsSurgeryGetter = AccessTools.PropertyGetter(typeof(RecipeDef), nameof(RecipeDef.IsSurgery));
        private static readonly AccessTools.FieldRef<RecipeDef, bool?> SurgeryCached = AccessTools.FieldRefAccess<RecipeDef, bool?>("isSurgeryCached");
        private static readonly List<ThingDef> NoUsers = new List<ThingDef>();

        private static Dictionary<RecipeDef, List<ThingDef>> users;
        private static int installed, patchedMethods, served, surgeries, mismatches;
        private static SelfCheck check;

        public static void Install(Harmony harmony)
        {
            var rebuilder = ModTypes.Find("PeteTimesSix.ResearchReinvented_SteppingStones.PreregRebuilders.PreregRebuilder");
            if (rebuilder == null)
            {
                return;
            }
            var extensions = ModTypes.Find("PeteTimesSix.ResearchReinvented_SteppingStones.Extensions.RecipeDefExtensions");
            var entry = AccessTools.Method(rebuilder, "SetPrerequisitesOnOrphans");
            if (entry == null || extensions == null || AllRecipeUsers == null || IsSurgeryGetter == null)
            {
                VersionCheck.Skip("Research Reinvented: Stepping Stones recipe users", "PreregRebuilder.SetPrerequisitesOnOrphans, RecipeDefExtensions or a RecipeDef getter no longer exists");
                return;
            }
            check = new SelfCheck("Research Reinvented: Stepping Stones recipe users", VersionCheck.Known(
                Fingerprint.Known(entry, "DA8D84A498E939B2"),
                Fingerprint.Known(AllRecipeUsers, "2E918FB1ADB1135C", "8762E8B936E55A89"),
                Fingerprint.Known(AccessTools.EnumeratorMoveNext(AllRecipeUsers), "2F9D95E7037F88F6", "8854E37F42492D34"),
                Fingerprint.Known(IsSurgeryGetter, "5E3C9FE2FC98FDDE", "7B035F4EE8F7FCBA")));
            var transpiler = new HarmonyMethod(typeof(RrssRecipes), nameof(Transpiler));
            foreach (var method in AccessTools.GetDeclaredMethods(rebuilder).Concat(AccessTools.GetDeclaredMethods(extensions)))
            {
                if (method.IsAbstract || method.ContainsGenericParameters || !IlScan.CallsAny(method, AllRecipeUsers, IsSurgeryGetter))
                {
                    continue;
                }
                harmony.Patch(method, transpiler: transpiler);
                patchedMethods++;
            }
            harmony.Patch(entry, prefix: new HarmonyMethod(typeof(RrssRecipes), nameof(Begin)), finalizer: new HarmonyMethod(typeof(RrssRecipes), nameof(End)));
            installed = 1;
        }


        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(AllRecipeUsers))
                {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RrssRecipes), nameof(Users))).WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                }
                else if (instruction.Calls(IsSurgeryGetter))
                {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RrssRecipes), nameof(IsSurgery))).WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                }
                else
                {
                    yield return instruction;
                }
            }
        }

        public static void Begin()
        {
            users = new Dictionary<RecipeDef, List<ThingDef>>();
            var things = DefDatabase<ThingDef>.AllDefsListForReading;
            var seen = new HashSet<RecipeDef>();
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
                    if (!users.TryGetValue(recipe, out var list))
                    {
                        users[recipe] = list = new List<ThingDef>();
                    }
                    list.Add(things[i]);
                }
            }
        }

        public static void End(Exception __exception)
        {
            users = null;
        }

        public static IEnumerable<ThingDef> Users(RecipeDef recipe)
        {
            if (users == null || recipe == null || check.IsOff || !UnityData.IsInMainThread)
            {
                return recipe.AllRecipeUsers;
            }
            served++;
            var result = Enumerate(recipe, users.TryGetValue(recipe, out var list) ? list : NoUsers);
            if (RLOMod.Verify || check.IsChecking)
            {
                var original = recipe.AllRecipeUsers.ToList();
                var same = result.SequenceEqual(original);
                check.Report(same, $"recipe users of {recipe.defName}");
                if (!same)
                {
                    if (mismatches++ < 10)
                    {
                        Log.Warning($"[RLO] Research Reinvented: Stepping Stones verify mismatch: recipe users of {recipe.defName}");
                    }
                    return original;
                }
            }
            return result;
        }

        private static IEnumerable<ThingDef> Enumerate(RecipeDef recipe, List<ThingDef> things)
        {
            var own = recipe.recipeUsers;
            if (own != null)
            {
                for (var i = 0; i < own.Count; i++)
                {
                    yield return own[i];
                }
            }
            for (var i = 0; i < things.Count; i++)
            {
                yield return things[i];
            }
        }

        public static bool IsSurgery(RecipeDef recipe)
        {
            if (users == null || recipe == null || check.IsOff || !UnityData.IsInMainThread)
            {
                return recipe.IsSurgery;
            }
            ref var cached = ref SurgeryCached(recipe);
            if (!cached.HasValue)
            {
                surgeries++;
                cached = false;
                foreach (var user in Users(recipe))
                {
                    if (user.category == ThingCategory.Pawn)
                    {
                        cached = true;
                        break;
                    }
                }
                if (RLOMod.Verify || check.IsChecking)
                {
                    var fast = cached.Value;
                    cached = null;
                    var original = recipe.IsSurgery;
                    check.Report(fast == original, $"IsSurgery of {recipe.defName}");
                    if (fast != original && mismatches++ < 10)
                    {
                        Log.Warning($"[RLO] Research Reinvented: Stepping Stones verify mismatch: IsSurgery of {recipe.defName}");
                    }
                    return original;
                }
            }
            return cached.Value;
        }

        public static string Stats()
        {
            return installed == 0 ? "Research Reinvented: Stepping Stones recipe users: off" : $"Research Reinvented: Stepping Stones recipe users: {patchedMethods} methods redirected, {served} recipe-user lookups served from the index, {surgeries} IsSurgery values computed; {mismatches} verify mismatches";
        }
    }
}
