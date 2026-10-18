using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class TypeNameIndex
    {
        private sealed class Entry
        {
            public int Position;
            public Type Type;
            public bool Ambiguous;
        }

        private static readonly object gate = new object();
        private static Func<IEnumerable<Assembly>> allActive;
        private static Assembly[] order;
        private static Dictionary<string, Entry> index;
        private static int[] unindexed;
        private static long fast, slow, builds, mismatches, ticks;
        private static SelfCheck check;
        private static Func<string, Type> original;
        [ThreadStatic] private static bool bypass;

        public static void Install(Harmony harmony)
        {
            var raw = AccessTools.Method(typeof(GenTypes), "GetTypeInAnyAssemblyRaw");
            var getter = AccessTools.PropertyGetter(typeof(GenTypes), "AllActiveAssemblies");
            if (raw == null || getter == null)
            {
                VersionCheck.Skip("Def type name lookups", "GenTypes.GetTypeInAnyAssemblyRaw or AllActiveAssemblies no longer exists");
                return;
            }
            check = new SelfCheck("Def type name lookups", VersionCheck.Known(Fingerprint.Known(raw, "DF60D54276C7BDD1", "592CFBFFBEAD7F19"), Fingerprint.Known(AccessTools.EnumeratorMoveNext(getter), "FDCF63F8727F0C37", "DE6251F281093029")));
            allActive = AccessTools.MethodDelegate<Func<IEnumerable<Assembly>>>(getter);
            original = AccessTools.MethodDelegate<Func<string, Type>>((MethodInfo)raw);
            harmony.Patch(raw, prefix: new HarmonyMethod(typeof(TypeNameIndex), nameof(Prefix)));
            LeanFixes.Reports.Add(Stats);
        }

        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal) { "int", "uint", "short", "ushort", "float", "double", "long", "ulong", "byte", "sbyte", "char", "bool", "decimal", "string" };

        private static bool Simple(string name)
        {
            if (string.IsNullOrEmpty(name) || name[name.Length - 1] == '?' || Keywords.Contains(name))
            {
                return false;
            }
            foreach (var c in name)
            {
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_' || c == '.' || c == '`' || c == '+'))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool Ascii(string s)
        {
            foreach (var c in s)
            {
                if (c > 127)
                {
                    return false;
                }
            }
            return true;
        }

        private static Assembly[] CurrentOrder()
        {
            var list = new List<Assembly> { typeof(GenTypes).Assembly };
            list.AddRange(allActive());
            list.Add(typeof(object).Assembly);
            return list.ToArray();
        }

        private static bool IsCurrentOrder(Assembly[] a)
        {
            if (a == null || a.Length < 2 || a[0] != typeof(GenTypes).Assembly)
            {
                return false;
            }
            var i = 1;
            foreach (var assembly in allActive())
            {
                if (i >= a.Length - 1 || a[i] != assembly)
                {
                    return false;
                }
                i++;
            }
            return i == a.Length - 1 && a[i] == typeof(object).Assembly;
        }

        private static readonly Dictionary<Assembly, Dictionary<string, (Type type, bool ambiguous)>> perAssembly = new Dictionary<Assembly, Dictionary<string, (Type, bool)>>();

        private static Dictionary<string, (Type type, bool ambiguous)> Local(Assembly assembly)
        {
            if (perAssembly.TryGetValue(assembly, out var cached))
            {
                return cached;
            }
            Type[] types;
            var simpleName = assembly.GetName().Name ?? "";
            try
            {
                types = assembly.IsDynamic || simpleName == "netstandard" || simpleName == "mscorlib" || simpleName.StartsWith("System", StringComparison.Ordinal) || simpleName.StartsWith("Microsoft", StringComparison.Ordinal) ? null : assembly.GetTypes();
            }
            catch
            {
                types = null;
            }
            Dictionary<string, (Type, bool)> local = null;
            if (types != null && AllAscii(types))
            {
                local = new Dictionary<string, (Type, bool)>(StringComparer.Ordinal);
                foreach (var type in types)
                {
                    var fullName = type.FullName;
                    if (fullName == null)
                    {
                        continue;
                    }
                    var key = fullName.ToLowerInvariant();
                    if (local.TryGetValue(key, out var existing))
                    {
                        local[key] = (existing.Item1, true);
                        continue;
                    }
                    local[key] = (type, false);
                }
            }
            if (!assembly.IsDynamic)
            {
                perAssembly[assembly] = local;
            }
            return local;
        }

        private static void Build(Assembly[] assemblies)
        {
            builds++;
            var map = new Dictionary<string, Entry>(StringComparer.Ordinal);
            var skipped = new List<int>();
            var seen = new HashSet<Assembly>();
            for (var p = 0; p < assemblies.Length; p++)
            {
                var assembly = assemblies[p];
                if (p == assemblies.Length - 1)
                {
                    skipped.Add(p);
                    continue;
                }
                if (!seen.Add(assembly))
                {
                    continue;
                }
                var local = Local(assembly);
                if (local == null)
                {
                    skipped.Add(p);
                    continue;
                }
                foreach (var pair in local)
                {
                    if (!map.ContainsKey(pair.Key))
                    {
                        map[pair.Key] = new Entry { Position = p, Type = pair.Value.type, Ambiguous = pair.Value.ambiguous };
                    }
                }
            }
            order = assemblies;
            index = map;
            unindexed = skipped.ToArray();
        }

        private static bool AllAscii(Type[] types)
        {
            foreach (var type in types)
            {
                var fullName = type?.FullName;
                if (fullName != null && !Ascii(fullName))
                {
                    return false;
                }
            }
            return true;
        }

        private static Type Lookup(string typeName, Assembly[] assemblies, Dictionary<string, Entry> map, int[] skipped)
        {
            map.TryGetValue(typeName.ToLowerInvariant(), out var entry);
            var limit = entry?.Position ?? int.MaxValue;
            foreach (var p in skipped)
            {
                if (p >= limit)
                {
                    break;
                }
                var found = p == assemblies.Length - 1 ? Type.GetType(typeName, false, true) : assemblies[p].GetType(typeName, false, true);
                if (found != null)
                {
                    return found;
                }
            }
            if (entry == null)
            {
                return null;
            }
            if (entry.Ambiguous)
            {
                return assemblies[entry.Position].GetType(typeName, false, true);
            }
            return entry.Type;
        }

        private static Type Original(string typeName)
        {
            bypass = true;
            try
            {
                return original(typeName);
            }
            finally
            {
                bypass = false;
            }
        }

        public static bool Prefix(string typeName, ref Type __result)
        {
            if (bypass || check.IsOff)
            {
                return true;
            }
            if (!Simple(typeName))
            {
                slow++;
                return true;
            }
            var start = Stopwatch.GetTimestamp();
            Assembly[] assemblies;
            Dictionary<string, Entry> map;
            int[] skipped;
            lock (gate)
            {
                if (!IsCurrentOrder(order))
                {
                    Build(CurrentOrder());
                }
                assemblies = order;
                map = index;
                skipped = unindexed;
            }
            var result = Lookup(typeName, assemblies, map, skipped);
            if (RLOMod.Verify || check.IsChecking)
            {
                var expected = Original(typeName);
                check.Report(expected == result, $"type {typeName}");
                if (expected != result)
                {
                    Interlocked.Increment(ref mismatches);
                    Log.Warning($"[RLO] type name index mismatch for {typeName}: {result} vs {expected}");
                    result = expected;
                }
            }
            Interlocked.Increment(ref fast);
            Interlocked.Add(ref ticks, Stopwatch.GetTimestamp() - start);
            __result = result;
            return false;
        }

        public static string Stats() => $"type name index: {fast} fast lookups in {ticks / (double)Stopwatch.Frequency:F2} s, {slow} left to vanilla, {builds} builds, {unindexed?.Length ?? 0} unindexed assemblies" + (RLOMod.Verify ? $", verify mismatches {mismatches}" : "");
    }
}
