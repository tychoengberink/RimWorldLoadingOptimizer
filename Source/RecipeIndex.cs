using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class RecipeIndex
    {
        private static readonly AccessTools.FieldRef<ThingDef, List<RecipeDef>> Cached =
            AccessTools.FieldRefAccess<ThingDef, List<RecipeDef>>("allRecipesCached");

        private static readonly object gate = new object();
        private static Dictionary<ThingDef, List<RecipeDef>> byUser;
        private static long signature = -1;
        private static long identity = -1;
        private static int identityCheckedAt = int.MinValue;

        // Alpha Implants' static constructor reads AllRecipes for every ThingDef and runs no other code in between
        // (checked by its IL hash), so one full check at the start of that loop covers all of its reads.
        private const string Warmer = "VFECore.AlphaImplants";
        private static Type warmer;
        private static bool warm;

        private static MethodBase getter;
        private static int state;

        public static int Mismatches;
        public static int Served;
        public static int Rebuilds;
        public static int WarmServed;

        public static void Install(Harmony harmony)
        {
            getter = AccessTools.PropertyGetter(typeof(ThingDef), nameof(ThingDef.AllRecipes));
            if (!Fingerprint.Matches("Recipe index", Fingerprint.Known(getter, "BFFB5F09B346E1F5", "04C7808287A8B54D")))
            {
                return;
            }
            harmony.Patch(getter, prefix: new HarmonyMethod(typeof(RecipeIndex), nameof(Prefix)));
            harmony.Patch(AccessTools.Method(typeof(PlayDataLoader), nameof(PlayDataLoader.ClearAllPlayData)),
                postfix: new HarmonyMethod(typeof(RecipeIndex), nameof(Reset)));
            if (RLOMod.Running("sarg.alphaimplants") != null)
            {
                CtorBracket.InstallEarly(harmony);
                CtorBracket.Started += Started;
                CtorBracket.Ended += Ended;
            }
        }

        private static void Started(Type type)
        {
            if (type.FullName != Warmer)
            {
                return;
            }
            warm = false;
            warmer = Fingerprint.Matches("Recipe index (Alpha Implants warm-up)", Fingerprint.Known(type.TypeInitializer, "77567A7CF3EFE009")) && Fingerprint.OnlyOurs(getter) ? type : null;
        }

        private static void Ended(Type type)
        {
            if (type == warmer)
            {
                warmer = null;
                warm = false;
            }
        }

        public static void Reset()
        {
            lock (gate)
            {
                byUser = null;
                signature = -1;
                identity = -1;
                warm = false;
            }
        }

        public static bool Prefix(ThingDef __instance, ref List<RecipeDef> __result)
        {
            if (state == 0)
            {
                state = Fingerprint.OnlyOurs(getter) ? 1 : 2;
            }
            if (state != 1)
            {
                return true;
            }
            var cached = Cached(__instance);
            if (cached != null)
            {
                __result = cached;
                return false;
            }
            lock (gate)
            {
                cached = Cached(__instance);
                if (cached == null)
                {
                    var recipes = DefDatabase<RecipeDef>.AllDefsListForReading;
                    var inWarmup = warmer != null && CtorBracket.Inside(warmer);
                    if (inWarmup && warm && byUser != null)
                    {
                        WarmServed++;
                    }
                    else
                    {
                        var current = Signature(recipes);
                        var now = Environment.TickCount;
                        if (byUser != null && current == signature && (inWarmup || unchecked(now - identityCheckedAt) > 50))
                        {
                            identityCheckedAt = now;
                            if (Identity(recipes) != identity)
                            {
                                byUser = null;
                            }
                        }
                        if (byUser == null || current != signature)
                        {
                            Rebuild(recipes);
                            signature = current;
                            identity = Identity(recipes);
                            identityCheckedAt = now;
                        }
                        warm = inWarmup;
                    }
                    cached = new List<RecipeDef>();
                    if (__instance.recipes != null)
                    {
                        cached.AddRange(__instance.recipes);
                    }
                    if (byUser.TryGetValue(__instance, out var users))
                    {
                        cached.AddRange(users);
                    }
                    if (RLOMod.Verify && !cached.SequenceEqual(Vanilla(__instance, recipes)))
                    {
                        warm = false;
                        if (Mismatches++ < 10)
                        {
                            Log.Warning($"[RLO] recipe index mismatch for {__instance.defName}");
                        }
                    }
                    Cached(__instance) = cached;
                    Served++;
                }
            }
            __result = cached;
            return false;
        }

        private static List<RecipeDef> Vanilla(ThingDef def, List<RecipeDef> recipes)
        {
            var list = new List<RecipeDef>();
            if (def.recipes != null)
            {
                list.AddRange(def.recipes);
            }
            foreach (var recipe in recipes)
            {
                if (recipe.recipeUsers != null && recipe.recipeUsers.Contains(def))
                {
                    list.Add(recipe);
                }
            }
            return list;
        }

        private static long Signature(List<RecipeDef> recipes)
        {
            unchecked
            {
                long hash = recipes.Count;
                for (var i = 0; i < recipes.Count; i++)
                {
                    var users = recipes[i].recipeUsers;
                    hash = hash * 31 + (users?.Count ?? -1);
                }
                return hash;
            }
        }

        private static long Identity(List<RecipeDef> recipes)
        {
            unchecked
            {
                long hash = RuntimeHelpers.GetHashCode(recipes);
                for (var i = 0; i < recipes.Count; i++)
                {
                    hash = hash * 31 + RuntimeHelpers.GetHashCode(recipes[i]);
                    var users = recipes[i].recipeUsers;
                    if (users == null)
                    {
                        continue;
                    }
                    hash = hash * 31 + RuntimeHelpers.GetHashCode(users);
                    for (var j = 0; j < users.Count; j++)
                    {
                        hash = hash * 31 + (users[j] == null ? 0 : RuntimeHelpers.GetHashCode(users[j]));
                    }
                }
                return hash;
            }
        }

        private static void Rebuild(List<RecipeDef> recipes)
        {
            byUser = new Dictionary<ThingDef, List<RecipeDef>>(ReferenceComparer<ThingDef>.Instance);
            var seen = new HashSet<ThingDef>(ReferenceComparer<ThingDef>.Instance);
            for (var i = 0; i < recipes.Count; i++)
            {
                var users = recipes[i].recipeUsers;
                if (users == null)
                {
                    continue;
                }
                seen.Clear();
                foreach (var user in users)
                {
                    if (user == null || !seen.Add(user))
                    {
                        continue;
                    }
                    if (!byUser.TryGetValue(user, out var list))
                    {
                        byUser[user] = list = new List<RecipeDef>();
                    }
                    list.Add(recipes[i]);
                }
            }
            Rebuilds++;
        }
    }
}
