#if RW17
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace RimWorldLoadingOptimizer
{
    public static class TypeTokenCache
    {
        private const string Feature = "Harmony type token cache";

        private static readonly Dictionary<(Module, Type), int> tokens = new Dictionary<(Module, Type), int>();
        private static readonly MismatchLog mismatches = new MismatchLog(Feature);
        private static SelfCheck check;
        private static long hits, misses, again;

        public static void Install(Harmony harmony)
        {
            var updater = typeof(object).Assembly.GetType("WaylithClr.MethodBodyUpdater");
            var target = updater == null ? null : AccessTools.Method(updater, "GetNamedTypeToken");
            var parameters = target?.GetParameters();
            if (target == null || !target.IsStatic || target.ReturnType != typeof(int) || parameters.Length != 2
                || !typeof(Module).IsAssignableFrom(parameters[0].ParameterType) || !typeof(Type).IsAssignableFrom(parameters[1].ParameterType))
            {
                VersionCheck.Skip(Feature, "the runtime's GetNamedTypeToken no longer exists in this form");
                return;
            }
            check = new SelfCheck(Feature, VersionCheck.Code(target));
            if (check.IsOff)
            {
                return;
            }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(TypeTokenCache), nameof(Prefix)), postfix: new HarmonyMethod(typeof(TypeTokenCache), nameof(Postfix)));
            StartupPhaseFixes.Reports.Add(Stats);
        }

        public static bool Prefix(Module module, Type type, ref int __result, out bool __state)
        {
            __state = true;
            if (check.IsOff || type.Assembly.IsCollectible || module.Assembly.IsCollectible)
            {
                __state = false;
                return true;
            }
            lock (tokens)
            {
                if (!tokens.TryGetValue((module, type), out var token))
                {
                    return true;
                }
                hits++;
                if (DevSwitches.Verify || check.IsChecking)
                {
                    return true;
                }
                __result = token;
            }
            __state = false;
            return false;
        }

        public static void Postfix(Module module, Type type, int __result, bool __state)
        {
            if (!__state)
            {
                return;
            }
            lock (tokens)
            {
                if (!tokens.TryGetValue((module, type), out var known))
                {
                    tokens[(module, type)] = __result;
                    misses++;
                    return;
                }
                if (known != __result)
                {
                    again++;
                }
                var same = known == __result || Denotes(module, known, type) && Denotes(module, __result, type);
                check.Report(same, $"{type.FullName} in {module.Name}");
                if (!same)
                {
                    mismatches.Report($"{type.FullName} in {module.Name}: token {known:X8} from the cache, {__result:X8} from the runtime");
                    tokens[(module, type)] = __result;
                }
            }
        }

        private static bool Denotes(Module module, int token, Type type)
        {
            try
            {
                return module.ResolveType(token) == type;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Stats()
        {
            lock (tokens)
            {
                return $"{Feature}: {hits} type tokens answered from the cache, {misses} made by the runtime; while checking, the runtime made a second token for the same type {again} times, {mismatches}";
            }
        }
    }
}
#endif
