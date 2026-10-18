using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class AsmCache
    {
        private static readonly object gate = new object();
        private static Assembly[] list;
        private static int generation;
        [ThreadStatic] private static bool inside;
        private static readonly ConcurrentDictionary<object, string> fullNames = new ConcurrentDictionary<object, string>(ReferenceComparer<object>.Instance);
        private static long listHits, listBuilds, nameHits, mismatches;

        public static void Install(Harmony harmony)
        {
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            var getAssemblies = AccessTools.Method(typeof(AppDomain), nameof(AppDomain.GetAssemblies), Type.EmptyTypes);
            harmony.Patch(getAssemblies, prefix: new HarmonyMethod(typeof(AsmCache), nameof(ListPrefix)) { priority = Priority.First });
            var runtimeAssembly = typeof(Assembly).Assembly.GetType("System.Reflection.RuntimeAssembly");
            var fullName = AccessTools.PropertyGetter(runtimeAssembly, nameof(Assembly.FullName));
            harmony.Patch(fullName, prefix: new HarmonyMethod(typeof(AsmCache), nameof(NamePrefix)), postfix: new HarmonyMethod(typeof(AsmCache), nameof(NamePostfix)));
            MainMenu.UnpatchAfterStartup(getAssemblies);
            MainMenu.UnpatchAfterStartup(fullName);
            MainMenu.Then(() => AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad);
            LeanFixes.Reports.Add(() => $"assembly list cache: GetAssemblies {listHits} hits, {listBuilds} builds; Assembly.FullName {nameHits} hits / {fullNames.Count} assemblies" + (RLOMod.Verify ? $", verify mismatches {mismatches}" : ""));
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            lock (gate)
            {
                generation++;
                list = null;
            }
        }

        public static bool ListPrefix(AppDomain __instance, ref Assembly[] __result)
        {
            if (inside || !ReferenceEquals(__instance, AppDomain.CurrentDomain))
            {
                return true;
            }
            Assembly[] cached;
            int seen;
            lock (gate)
            {
                cached = list;
                seen = generation;
            }
            if (cached != null && !RLOMod.Verify)
            {
                Interlocked.Increment(ref listHits);
                __result = (Assembly[])cached.Clone();
                return false;
            }
            Assembly[] fresh;
            inside = true;
            try
            {
                fresh = __instance.GetAssemblies();
            }
            finally
            {
                inside = false;
            }
            if (cached != null)
            {
                Interlocked.Increment(ref listHits);
                bool unchanged;
                lock (gate)
                {
                    unchanged = generation == seen;
                }
                if (unchanged && !ReflCache.Same(cached, fresh) && Interlocked.Increment(ref mismatches) <= 5)
                {
                    var added = fresh.Except(cached).Select(a => a.GetName().Name);
                    var removed = cached.Except(fresh).Select(a => a.GetName().Name);
                    Log.Warning($"[RLO] assembly list cache verify mismatch: {cached.Length} cached, {fresh.Length} now; added [{string.Join(", ", added)}], removed [{string.Join(", ", removed)}]");
                }
            }
            lock (gate)
            {
                if (generation == seen && list == null)
                {
                    list = (Assembly[])fresh.Clone();
                    listBuilds++;
                }
            }
            __result = fresh;
            return false;
        }

        public static bool NamePrefix(Assembly __instance, ref string __result)
        {
            if (!RLOMod.Verify && fullNames.TryGetValue(__instance, out __result))
            {
                Interlocked.Increment(ref nameHits);
                return false;
            }
            return true;
        }

        public static void NamePostfix(Assembly __instance, string __result, bool __runOriginal)
        {
            if (!__runOriginal || __result == null)
            {
                return;
            }
            if (fullNames.TryGetValue(__instance, out var cached))
            {
                Interlocked.Increment(ref nameHits);
                if (cached != __result && Interlocked.Increment(ref mismatches) <= 5)
                {
                    Log.Warning($"[RLO] assembly name cache verify mismatch: cached {cached}, now {__result}");
                }
                return;
            }
            fullNames.TryAdd(__instance, __result);
        }
    }
}
