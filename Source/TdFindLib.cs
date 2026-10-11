using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class TdFindLib
    {
        private static readonly MethodInfo AllRecipeUsers = AccessTools.PropertyGetter(typeof(RecipeDef), nameof(RecipeDef.AllRecipeUsers));
        private static readonly Dictionary<Assembly, string> modNames = new Dictionary<Assembly, string>();
        private static FieldInfo coreAssembly;
        private static MethodInfo modNameFor;
        private static List<(MethodInfo method, Type first)> allExtensions;
        private static int extCalls, extChecks, extMismatches;
        private static Type startup;
        private static Dictionary<RecipeDef, List<ThingDef>> users;
        private static int nameCalls, nameHits, nameChecks, nameMismatches, recipeCalls, recipeServed, recipeChecks, recipeMismatches, windows;

        public static void Install(Harmony harmony)
        {
            var fieldData = ModTypes.Find("TD_Find_Lib.FieldData");
            modNameFor = fieldData == null ? null : AccessTools.Method(fieldData, "ModNameFor", new[] { typeof(Type) });
            coreAssembly = fieldData == null ? null : AccessTools.Field(fieldData, "coreAssembly");
            startup = ModTypes.Find("TD_Find_Lib.ModStartup");
            if (modNameFor == null || coreAssembly?.FieldType != typeof(Assembly) || modNameFor.ReturnType != typeof(string))
            {
                return;
            }
            if (Ctors2Mod.Enabled("tdfname") && Fingerprint.Matches("TD Find Lib mod names", Fingerprint.Known(modNameFor, "ADCE7B1BA8B98562")) && Ctors2Mod.OnlyOurs(modNameFor))
            {
                harmony.Patch(modNameFor, prefix: new HarmonyMethod(typeof(TdFindLib), nameof(ModNamePrefix)));
            }
            if (Ctors2Mod.Enabled("tdfrecipes") && startup != null && Fingerprint.Matches("TD Find Lib recipe users", Fingerprint.Known(AllRecipeUsers, "8762E8B936E55A89"), Fingerprint.Known(UsersMoveNext(), "8854E37F42492D34")) && Ctors2Mod.OnlyOurs(AllRecipeUsers))
            {
                harmony.Patch(AllRecipeUsers, prefix: new HarmonyMethod(typeof(TdFindLib), nameof(UsersPrefix)));
                CtorBracket.Started += t => { if (t == startup) { users = null; windows++; } };
                CtorBracket.Ended += t => { if (t == startup) users = null; };
            }
            var extensionMethods = AccessTools.Method(fieldData, "GetExtensionMethods", new[] { typeof(Type) });
            if (Ctors2Mod.Enabled("tdfext") && extensionMethods?.ReturnType == typeof(IEnumerable<MethodInfo>) && Fingerprint.Matches("TD Find Lib extension methods", Fingerprint.Known(extensionMethods, "A566CEAC1F285CD0")) && Ctors2Mod.OnlyOurs(extensionMethods))
            {
                harmony.Patch(extensionMethods, prefix: new HarmonyMethod(typeof(TdFindLib), nameof(ExtensionPrefix)));
            }
            Ctors2Mod.Reports.Add(Stats);
        }

        public static MethodInfo UsersMoveNext()
        {
            var iterator = typeof(RecipeDef).GetNestedTypes(BindingFlags.NonPublic).FirstOrDefault(t => t.Name.StartsWith("<get_AllRecipeUsers>"));
            return iterator == null ? null : AccessTools.Method(iterator, "MoveNext");
        }

        public static bool ExtensionPrefix(Type extendedType, ref IEnumerable<MethodInfo> __result)
        {
            if (!UnityData.IsInMainThread)
            {
                return true;
            }
            extCalls++;
            if (allExtensions == null)
            {
                allExtensions = new List<(MethodInfo, Type)>();
                foreach (var type in GenTypes.AllTypesWithAttribute<ExtensionAttribute>())
                {
                    if (!type.IsSealed || type.IsGenericType || type.IsNested)
                    {
                        continue;
                    }
                    foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (!method.IsDefined(typeof(ExtensionAttribute), false))
                        {
                            continue;
                        }
                        var parameters = method.GetParameters();
                        if (parameters.Length != 0)
                        {
                            allExtensions.Add((method, parameters[0].ParameterType));
                        }
                    }
                }
            }
            var result = allExtensions.Where(e => e.first == extendedType).Select(e => e.method).ToList();
            if (Ctors2Mod.Verify)
            {
                extChecks++;
                var original = from type in GenTypes.AllTypesWithAttribute<ExtensionAttribute>()
                    where type.IsSealed && !type.IsGenericType && !type.IsNested
                    from method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    where method.IsDefined(typeof(ExtensionAttribute), false)
                    where method.GetParameters().Length != 0 && method.GetParameters()[0].ParameterType == extendedType
                    select method;
                if (!result.SequenceEqual(original) && ++extMismatches <= 10)
                {
                    Log.Warning($"[RLO] tdf extension methods verify mismatch for {extendedType}");
                }
            }
            __result = result;
            return false;
        }

        public static bool ModNamePrefix(Type assType, ref string __result)
        {
            if (!UnityData.IsInMainThread)
            {
                return true;
            }
            nameCalls++;
            var assembly = assType.Assembly;
            if (assembly == (Assembly)coreAssembly.GetValue(null))
            {
                __result = null;
                return false;
            }
            if (!modNames.TryGetValue(assembly, out var name))
            {
                name = LoadedModManager.RunningModsListForReading.FirstOrDefault(p => p.assemblies.loadedAssemblies.Contains(assembly))?.Name;
                modNames[assembly] = name;
            }
            else
            {
                nameHits++;
                if (Ctors2Mod.Verify)
                {
                    nameChecks++;
                    if (LoadedModManager.RunningModsListForReading.FirstOrDefault(p => p.assemblies.loadedAssemblies.Contains(assembly))?.Name != name && ++nameMismatches <= 10)
                    {
                        Log.Warning($"[RLO] tdf mod name verify mismatch for {assembly}");
                    }
                }
            }
            __result = name;
            return false;
        }

        public static bool UsersPrefix(RecipeDef __instance, ref IEnumerable<ThingDef> __result)
        {
            if (!CtorBracket.Inside(startup))
            {
                return true;
            }
            recipeCalls++;
            if (users == null)
            {
                users = new Dictionary<RecipeDef, List<ThingDef>>();
                foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    if (def.recipes == null)
                    {
                        continue;
                    }
                    foreach (var recipe in def.recipes)
                    {
                        if (recipe == null)
                        {
                            continue;
                        }
                        if (!users.TryGetValue(recipe, out var list))
                        {
                            users[recipe] = list = new List<ThingDef>();
                        }
                        if (list.Count == 0 || !ReferenceEquals(list[list.Count - 1], def))
                        {
                            list.Add(def);
                        }
                    }
                }
            }
            recipeServed++;
            __result = Users(__instance, users.TryGetValue(__instance, out var found) ? found : null);
            if (Ctors2Mod.Verify)
            {
                recipeChecks++;
                if (!Users(__instance, found).SequenceEqual(OriginalUsers(__instance)) && ++recipeMismatches <= 10)
                {
                    Log.Warning($"[RLO] tdf recipe users verify mismatch for {__instance}");
                }
            }
            return false;
        }

        private static IEnumerable<ThingDef> Users(RecipeDef recipe, List<ThingDef> found)
        {
            if (recipe.recipeUsers != null)
            {
                for (var i = 0; i < recipe.recipeUsers.Count; i++)
                {
                    yield return recipe.recipeUsers[i];
                }
            }
            if (found != null)
            {
                for (var i = 0; i < found.Count; i++)
                {
                    yield return found[i];
                }
            }
        }

        private static IEnumerable<ThingDef> OriginalUsers(RecipeDef recipe)
        {
            if (recipe.recipeUsers != null)
            {
                foreach (var user in recipe.recipeUsers)
                {
                    yield return user;
                }
            }
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (def.recipes != null && def.recipes.Contains(recipe))
                {
                    yield return def;
                }
            }
        }

        public static string Stats() => $"td find lib: mod names {nameCalls} calls, {nameHits} cached" + (Ctors2Mod.Verify ? $" (verify {nameChecks}, mismatches {nameMismatches})" : "")
            + $"; extension methods {extCalls} calls" + (Ctors2Mod.Verify ? $" (verify {extChecks}, mismatches {extMismatches})" : "")
            + $"; recipe users {windows} windows, {recipeCalls} calls, {recipeServed} served" + (Ctors2Mod.Verify ? $" (verify {recipeChecks}, mismatches {recipeMismatches})" : "");
    }
}
