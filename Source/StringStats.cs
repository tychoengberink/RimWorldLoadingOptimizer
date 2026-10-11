using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class StringStats
    {
        private static readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();

        public static void Report()
        {
            if (Environment.GetEnvironmentVariable("LEANMEMORY_STRINGSTATS") != "1")
            {
                return;
            }
            var visited = new HashSet<object>(ReferenceComparer<object>.Instance);
            var strings = new HashSet<object>(ReferenceComparer<object>.Instance);
            var unique = new HashSet<string>();
            var stack = new Stack<object>();
            foreach (var defType in GenDefDatabase.AllDefTypesWithDatabases())
            {
                foreach (var def in (IEnumerable)GenGeneric.GetStaticPropertyOnGenericType(typeof(DefDatabase<>), defType, "AllDefs"))
                {
                    stack.Push(def);
                }
            }
            while (stack.Count > 0 && visited.Count < 20000000)
            {
                var obj = stack.Pop();
                if (obj == null || !visited.Add(obj))
                {
                    continue;
                }
                if (obj is string s)
                {
                    strings.Add(s);
                    unique.Add(s);
                    continue;
                }
                var type = obj.GetType();
                if (obj is UnityEngine.Object || type.IsPrimitive || type.IsEnum || obj is Type || obj is Delegate)
                {
                    continue;
                }
                if (obj is ICollection enumerable && !(obj is IDictionary))
                {
                    foreach (var item in enumerable)
                    {
                        if (item != null && !item.GetType().IsValueType)
                        {
                            stack.Push(item);
                        }
                    }
                    continue;
                }
                foreach (var field in Fields(type))
                {
                    var value = field.GetValue(obj);
                    if (value != null && !value.GetType().IsValueType)
                    {
                        stack.Push(value);
                    }
                }
            }
            long Bytes(IEnumerable<string> items)
            {
                long total = 0;
                foreach (var item in items)
                {
                    total += 20 + 2L * item.Length;
                }
                return total;
            }
            var all = new List<string>();
            foreach (var o in strings)
            {
                all.Add((string)o);
            }
            var total = Bytes(all);
            var distinct = Bytes(unique);
            Log.Message($"[RLO] def string stats: {all.Count} string objects ({total >> 20} MB), {unique.Count} distinct ({distinct >> 20} MB); dedup would save ~{(total - distinct) >> 20} MB; walked {visited.Count} objects");
        }

        private static FieldInfo[] Fields(Type type)
        {
            if (!fieldCache.TryGetValue(type, out var fields))
            {
                var list = new List<FieldInfo>();
                for (var t = type; t != null && t != typeof(object); t = t.BaseType)
                {
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if ((!f.FieldType.IsValueType || f.FieldType == typeof(string)) && !f.FieldType.IsPointer && f.FieldType.FullName != "System.MonoFNPtrFakeClass")
                        {
                            list.Add(f);
                        }
                    }
                }
                fieldCache[type] = fields = list.ToArray();
            }
            return fields;
        }
    }
}
