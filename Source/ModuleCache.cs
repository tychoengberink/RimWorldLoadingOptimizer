using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class ModuleCache
    {
        private static readonly Dictionary<string, Module> modules = new Dictionary<string, Module>();
#if RW17
        private static readonly HashSet<string> ambiguous = new HashSet<string>();
        // Every module of the assemblies scanned so far, by MVID, with how many plain assemblies carry it. An assembly is scanned once:
        // a lookup only scans the ones loaded since. MVIDs seen in dynamic, collectible or Visual Studio assemblies are left to Harmony,
        // since those can gain a module, unload, or sit outside AllAssemblies.
        private static readonly Dictionary<Guid, (Module module, int count)> index = new Dictionary<Guid, (Module, int)>();
        private static readonly HashSet<Guid> uncertain = new HashSet<Guid>();
        private static readonly HashSet<Assembly> scanned = new HashSet<Assembly>(ReferenceComparer<Assembly>.Instance);
        private static readonly ConditionalWeakTable<Assembly, object> scannedCollectible = new ConditionalWeakTable<Assembly, object>();
        // The last GetAssemblies() result, kept only while it holds no collectible assembly: new assemblies are appended, so when it is
        // still a prefix of the current list only the tail needs looking at.
        private static Assembly[] lastList = Array.Empty<Assembly>();
        private static bool broken;
        private static long rescans;
#endif
        private static bool installed;
        private static SelfCheck check;
        [ThreadStatic] private static bool bypass;
        private static long hits, misses, mismatches;

        public static void Install(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(AccessTools), nameof(AccessTools.GetMethodByModuleAndToken));
            if (target == null)
            {
                VersionCheck.Skip("Harmony module cache", "AccessTools.GetMethodByModuleAndToken no longer exists");
                return;
            }
#if RW17
            check = new SelfCheck("Harmony module cache", VersionCheck.Known(Fingerprint.Known(target, "72843BE77F2A63CD", "8FC8E731CC03B7C8", "20441ABE42CC22DF", "B9B9E24F59BDF221")));
#else
            check = new SelfCheck("Harmony module cache", VersionCheck.Known(Fingerprint.Known(target, "72843BE77F2A63CD", "8FC8E731CC03B7C8")));
#endif
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(ModuleCache), nameof(Prefix)));
            installed = true;
        }

        public static bool Prefix(string moduleGUID, int token, ref MethodInfo __result)
        {
            if (bypass || moduleGUID == null || check.IsOff)
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
                    // Only the canonical form can equal a ModuleVersionId.ToString(); anything else is left to Harmony.
                    if (ambiguous.Contains(moduleGUID) || !Guid.TryParse(moduleGUID, out var guid) || guid.ToString() != moduleGUID)
                    {
                        return true;
                    }
                    Refresh();
                    if (broken || uncertain.Contains(guid) || !index.TryGetValue(guid, out var entry))
                    {
                        return true;
                    }
                    if (entry.count > 1)
                    {
                        ambiguous.Add(moduleGUID);
                        return true;
                    }
                    module = entry.module;
                }
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
            if (RLOMod.Verify || check.IsChecking)
            {
                bypass = true;
                try
                {
                    var original = AccessTools.GetMethodByModuleAndToken(moduleGUID, token);
                    check.Report(original == __result, $"module {moduleGUID} token {token}");
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

#if RW17
        // Called under the lock. Scans the assemblies not scanned before; a dynamic assembly without a module yet is scanned again next time.
        private static void Refresh()
        {
            if (broken)
            {
                return;
            }
            try
            {
                var all = AppDomain.CurrentDomain.GetAssemblies();
                var start = 0;
                if (all.Length >= lastList.Length)
                {
                    start = lastList.Length;
                    for (int i = 0; i < lastList.Length; i++)
                    {
                        if (!ReferenceEquals(all[i], lastList[i]))
                        {
                            start = 0;
                            break;
                        }
                    }
                }
                if (start == all.Length)
                {
                    return;
                }
                rescans++;
                var keep = true;
                for (int i = start; i < all.Length; i++)
                {
                    var assembly = all[i];
                    var collectible = assembly.IsCollectible;
                    keep &= !collectible;
                    if (collectible ? scannedCollectible.TryGetValue(assembly, out _) : scanned.Contains(assembly))
                    {
                        continue;
                    }
                    var dynamic = assembly.IsDynamic;
                    var found = assembly.GetLoadedModules();
                    if (dynamic)
                    {
                        found = found.Concat(assembly.GetModules()).ToArray();
                    }
                    var shaky = dynamic || collectible || assembly.FullName.StartsWith("Microsoft.VisualStudio");
                    foreach (var module in found)
                    {
                        var id = module.ModuleVersionId;
                        if (shaky)
                        {
                            uncertain.Add(id);
                        }
                        else
                        {
                            index.TryGetValue(id, out var entry);
                            index[id] = (entry.module ?? module, entry.count + 1);
                        }
                    }
                    if (dynamic && found.Length == 0)
                    {
                        keep = false;
                        continue;
                    }
                    if (collectible)
                    {
                        scannedCollectible.Add(assembly, null);
                    }
                    else
                    {
                        scanned.Add(assembly);
                    }
                }
                lastList = keep ? all : Array.Empty<Assembly>();
            }
            catch (Exception e)
            {
                broken = true;
                Log.Warning($"[RLO] Harmony module cache: indexing the loaded assemblies failed ({e.GetType().Name}: {e.Message}). This speedup is skipped and the original code runs; nothing is broken.");
            }
        }
#endif

        public static string Stats()
        {
            lock (modules)
            {
#if RW17
                return installed ? $"harmony module cache: {hits} hits, {misses} misses, {modules.Count} modules, {mismatches} verify mismatches, {ambiguous.Count} loaded twice (left to Harmony), {index.Count} modules indexed in {rescans} scans{(broken ? ", index off" : "")}" : "harmony module cache: off";
#else
                return installed ? $"harmony module cache: {hits} hits, {misses} misses, {modules.Count} modules, {mismatches} verify mismatches" : "harmony module cache: off";
#endif
            }
        }
    }
}
