using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class ModuleCache
    {
        private static readonly Dictionary<string, Module> modules = new Dictionary<string, Module>();
#if RW17
        private static readonly HashSet<string> ambiguous = new HashSet<string>();
#endif
        private static bool installed;
        [ThreadStatic] private static bool bypass;
        private static long hits, misses, mismatches;

        public static void Install(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(AccessTools), nameof(AccessTools.GetMethodByModuleAndToken));
#if RW17
            if (!Fingerprint.Matches("Harmony module cache", Fingerprint.Known(target, "72843BE77F2A63CD", "8FC8E731CC03B7C8", "20441ABE42CC22DF")))
#else
            if (!Fingerprint.Matches("Harmony module cache", Fingerprint.Known(target, "72843BE77F2A63CD", "8FC8E731CC03B7C8")))
#endif
            {
                return;
            }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(ModuleCache), nameof(Prefix)));
            installed = true;
        }

        public static bool Prefix(string moduleGUID, int token, ref MethodInfo __result)
        {
            if (bypass || moduleGUID == null)
            {
                return true;
            }
            Module module;
            lock (modules)
            {
                modules.TryGetValue(moduleGUID, out module);
            }
            if (module == null)
            {
#if RW17
                lock (modules)
                {
                    if (ambiguous.Contains(moduleGUID))
                    {
                        return true;
                    }
                }
                var matching = AccessTools.AllAssemblies().SelectMany(a => a.GetLoadedModules()).Where(m => m.ModuleVersionId.ToString() == moduleGUID).Take(2).ToList();
                if (matching.Count != 1)
                {
                    if (matching.Count > 1)
                    {
                        lock (modules)
                        {
                            ambiguous.Add(moduleGUID);
                        }
                    }
                    return true;
                }
                module = matching[0];
#else
                module = AccessTools.AllAssemblies().SelectMany(a => a.GetLoadedModules()).FirstOrDefault(m => m.ModuleVersionId.ToString() == moduleGUID);
                if (module == null)
                {
                    return true;
                }
#endif
                lock (modules)
                {
                    modules[moduleGUID] = module;
                    misses++;
                }
            }
            else
            {
                lock (modules)
                {
                    hits++;
                }
            }
            __result = (MethodInfo)module.ResolveMethod(token);
            if (RLOMod.Verify)
            {
                bypass = true;
                try
                {
                    var original = AccessTools.GetMethodByModuleAndToken(moduleGUID, token);
                    if (original != __result)
                    {
                        lock (modules)
                        {
                            mismatches++;
                        }
                        Log.Warning($"[RLO] module cache mismatch for {moduleGUID}:{token}: {__result} vs {original}");
                        __result = original;
                    }
                }
                finally
                {
                    bypass = false;
                }
            }
            return false;
        }

        public static string Stats()
        {
            lock (modules)
            {
#if RW17
                return installed ? $"harmony module cache: {hits} hits, {misses} misses, {modules.Count} modules, {mismatches} verify mismatches, {ambiguous.Count} loaded twice (left to Harmony)" : "harmony module cache: off";
#else
                return installed ? $"harmony module cache: {hits} hits, {misses} misses, {modules.Count} modules, {mismatches} verify mismatches" : "harmony module cache: off";
#endif
            }
        }
    }
}
