using System;
using System.Collections.Concurrent;
using System.Threading;
using HarmonyLib;

namespace RimWorldLoadingOptimizer
{
    public static class ReflCache
    {
        private static readonly ConcurrentDictionary<object, bool> containsGeneric = new ConcurrentDictionary<object, bool>(ReferenceComparer<object>.Instance);
        private static readonly ConcurrentDictionary<object, Type[]> genericArgs = new ConcurrentDictionary<object, Type[]>(ReferenceComparer<object>.Instance);
        private static long cgHits, gaHits, mismatches;

        public static void Install(Harmony harmony)
        {
            var runtimeType = typeof(Type).Assembly.GetType("System.RuntimeType");
            var containsGenericParameters = AccessTools.PropertyGetter(runtimeType, nameof(Type.ContainsGenericParameters));
            var getGenericArguments = AccessTools.Method(runtimeType, nameof(Type.GetGenericArguments), Type.EmptyTypes);
            harmony.Patch(containsGenericParameters, prefix: new HarmonyMethod(typeof(ReflCache), nameof(ContainsPrefix)), postfix: new HarmonyMethod(typeof(ReflCache), nameof(ContainsPostfix)));
            harmony.Patch(getGenericArguments, prefix: new HarmonyMethod(typeof(ReflCache), nameof(ArgsPrefix)), postfix: new HarmonyMethod(typeof(ReflCache), nameof(ArgsPostfix)));
            MainMenu.UnpatchAfterStartup(containsGenericParameters);
            MainMenu.UnpatchAfterStartup(getGenericArguments);
            LeanFixes.Reports.Add(() => $"reflection cache: ContainsGenericParameters {cgHits} hits / {containsGeneric.Count} types, GetGenericArguments {gaHits} hits / {genericArgs.Count} types" + (RLOMod.Verify ? $", verify mismatches {mismatches}" : ""));
        }

        public static bool ContainsPrefix(object __instance, ref bool __result)
        {
            if (!RLOMod.Verify && containsGeneric.TryGetValue(__instance, out __result))
            {
                Interlocked.Increment(ref cgHits);
                return false;
            }
            return true;
        }

        public static void ContainsPostfix(object __instance, bool __result, bool __runOriginal)
        {
            if (!__runOriginal)
            {
                return;
            }
            if (containsGeneric.TryGetValue(__instance, out var cached))
            {
                Interlocked.Increment(ref cgHits);
                if (cached != __result)
                {
                    Interlocked.Increment(ref mismatches);
                }
                return;
            }
            containsGeneric.TryAdd(__instance, __result);
        }

        public static bool ArgsPrefix(object __instance, ref Type[] __result)
        {
            if (!RLOMod.Verify && genericArgs.TryGetValue(__instance, out var cached))
            {
                Interlocked.Increment(ref gaHits);
                __result = (Type[])cached.Clone();
                return false;
            }
            return true;
        }

        public static void ArgsPostfix(object __instance, Type[] __result, bool __runOriginal)
        {
            if (!__runOriginal || __result == null || __result.Length == 0)
            {
                return;
            }
            if (genericArgs.TryGetValue(__instance, out var cached))
            {
                Interlocked.Increment(ref gaHits);
                if (!Same(cached, __result))
                {
                    Interlocked.Increment(ref mismatches);
                }
                return;
            }
            genericArgs.TryAdd(__instance, (Type[])__result.Clone());
        }

        public static bool Same(object[] a, object[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }
            for (var i = 0; i < a.Length; i++)
            {
                if (!ReferenceEquals(a[i], b[i]))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
