using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class TypeIndex
    {
        private static readonly object gate = new object();
        private static readonly Dictionary<string, Type> byFullName = new Dictionary<string, Type>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Type> byName = new Dictionary<string, Type>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Type> found = new Dictionary<string, Type>(StringComparer.Ordinal);
        private static readonly HashSet<Assembly> indexed = new HashSet<Assembly>();

        public static int Lookups;
        public static int CachedHits;
        public static int Mismatches;

        public static void Install(Harmony harmony)
        {
            if (!Fingerprint.Matches("Type name index",
                Fingerprint.Known(AccessTools.Method(typeof(AccessTools), nameof(AccessTools.TypeByName)), "624D91367FA5A6CB", "5DB8125A670F78A7"),
                Fingerprint.Known(AccessTools.Method(typeof(AccessTools), nameof(AccessTools.AllTypes)), "E5988DC554860258", "4865BF6750797681"),
                Fingerprint.Known(AccessTools.Method(typeof(AccessTools), nameof(AccessTools.AllAssemblies)), "5A35D5946C2A7BFC", "6C8092C81C75E970")))
            {
                return;
            }
            harmony.Patch(AccessTools.Method(typeof(AccessTools), nameof(AccessTools.TypeByName)), prefix: new HarmonyMethod(typeof(TypeIndex), nameof(Prefix)) { priority = Priority.Last });
        }

        private static void Add(Assembly assembly)
        {
            if (indexed.Contains(assembly) || assembly.FullName.StartsWith("Microsoft.VisualStudio"))
            {
                return;
            }
            var types = AccessTools.GetTypesFromAssembly(assembly);
            indexed.Add(assembly);
            foreach (var type in types)
            {
                var fullName = type.FullName;
                if (fullName != null && !byFullName.ContainsKey(fullName))
                {
                    byFullName[fullName] = type;
                }
                if (!byName.ContainsKey(type.Name))
                {
                    byName[type.Name] = type;
                }
            }
        }

        private static bool TryIndexed(string name, out Type type)
        {
            type = null;
            lock (gate)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!indexed.Contains(assembly))
                    {
                        try
                        {
                            Add(assembly);
                        }
                        catch
                        {
                            return false;
                        }
                    }
                }
                Lookups++;
                if (!byFullName.TryGetValue(name, out type))
                {
                    byName.TryGetValue(name, out type);
                }
                return true;
            }
        }

        public static bool Prefix(string name, ref Type __result)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf('[') >= 0 || name.IndexOf(',') >= 0)
            {
                return true;
            }
            lock (gate)
            {
                if (found.TryGetValue(name, out var cached))
                {
                    CachedHits++;
                    __result = cached;
                    return false;
                }
            }
            var type = typeof(AccessTools).Assembly.GetType(name, false) ?? typeof(object).Assembly.GetType(name, false);
            if (type == null)
            {
                foreach (var assembly in AccessTools.AllAssemblies())
                {
                    type = assembly.GetType(name, false);
                    if (type != null)
                    {
                        break;
                    }
                }
            }
            if (type == null)
            {
                if (!TryIndexed(name, out type))
                {
                    return true;
                }
                if (RLOMod.Verify)
                {
                    var source = AccessTools.AllTypes().ToArray();
                    var original = source.FirstOrDefault(t => t.FullName == name) ?? source.FirstOrDefault(t => t.Name == name);
                    if (original != type)
                    {
                        Mismatches++;
                        Log.Warning($"[RLO] type index mismatch for {name}: {type} vs {original}");
                    }
                }
            }
            if (type != null)
            {
                lock (gate)
                {
                    found[name] = type;
                }
            }
            __result = type;
            return false;
        }

        public static string Stats() => $"type index: {Lookups} slow-path lookups, {CachedHits} cached hits, {byFullName.Count} types, {Mismatches} verify mismatches";
    }
}
