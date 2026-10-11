using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class HeapStats
    {
        private const int Cap = 15000000;
        private static readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();

        public static void Report()
        {
            if (Environment.GetEnvironmentVariable("LEANMEMORY_HEAPSTATS") != "1")
            {
                return;
            }
            var visited = new HashSet<object>(ReferenceComparer<object>.Instance);
            var owners = new List<(string owner, long objects, long bytes)>();
            var scanned = new HashSet<Assembly>(LoadedModManager.RunningModsListForReading.SelectMany(m => m.assemblies.loadedAssemblies)) { typeof(Def).Assembly };
            scanned.Remove(typeof(HeapStats).Assembly);
            foreach (var assembly in scanned.Where(a => !a.IsDynamic).OrderBy(a => a == typeof(Def).Assembly ? 0 : 1))
            {
                foreach (var type in AccessTools.GetTypesFromAssembly(assembly))
                {
                    if (type == null || type.ContainsGenericParameters)
                    {
                        continue;
                    }
                    FieldInfo[] fields;
                    try
                    {
                        fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    }
                    catch
                    {
                        continue;
                    }
                    foreach (var field in fields)
                    {
                        var fieldType = field.FieldType;
                        if (field.IsLiteral || fieldType.FullName == null || !(fieldType.IsClass || fieldType.IsInterface || fieldType.IsArray) || fieldType.IsPointer || fieldType.IsByRef || fieldType.FullName == "System.MonoFNPtrFakeClass" || visited.Count >= Cap)
                        {
                            continue;
                        }
                        object value;
                        try
                        {
                            value = field.GetValue(null);
                        }
                        catch
                        {
                            continue;
                        }
                        var (objects, bytes) = Walk(value, visited);
                        if (bytes > 8 << 20)
                        {
                            owners.Add(($"{type.FullName}.{field.Name} ({assembly.GetName().Name})", objects, bytes));
                        }
                    }
                }
            }
            var report = new StringBuilder($"[RLO] largest static owners of managed memory (walked {visited.Count} objects{(visited.Count >= Cap ? ", capped" : "")}):");
            foreach (var owner in owners.OrderByDescending(o => o.bytes).Take(40))
            {
                report.Append($"\n  {owner.bytes >> 20,7} MB  {owner.objects,10} objects  {owner.owner}");
            }
            Log.Message(report.ToString());
        }

        private static (long, long) Walk(object root, HashSet<object> visited)
        {
            long objects = 0, bytes = 0;
            var stack = new Stack<object>();
            stack.Push(root);
            while (stack.Count > 0 && visited.Count < Cap)
            {
                var obj = stack.Pop();
                if (obj == null || !visited.Add(obj))
                {
                    continue;
                }
                objects++;
                var type = obj.GetType();
                if (obj is string s)
                {
                    bytes += 20 + 2L * s.Length;
                    continue;
                }
                if (obj is UnityEngine.Object || obj is Type || obj is MemberInfo || obj is Assembly || obj is Delegate || obj is Module)
                {
                    bytes += 32;
                    continue;
                }
                if (obj is Array array)
                {
                    var element = type.GetElementType();
                    if (element != null && (element.IsPrimitive || element.IsEnum))
                    {
                        bytes += 32 + (long)array.Length * Math.Max(1, ElementSize(element));
                        continue;
                    }
                    bytes += 32 + 8L * array.Length;
                    if (element != null && !element.IsValueType)
                    {
                        foreach (var item in array)
                        {
                            if (item != null)
                            {
                                stack.Push(item);
                            }
                        }
                    }
                    continue;
                }
                var fields = Fields(type);
                bytes += 16 + 8L * fields.Length;
                foreach (var field in fields)
                {
                    object value;
                    try
                    {
                        value = field.GetValue(obj);
                    }
                    catch
                    {
                        continue;
                    }
                    if (value != null && !value.GetType().IsValueType)
                    {
                        stack.Push(value);
                    }
                }
            }
            return (objects, bytes);
        }

        private static int ElementSize(Type element)
        {
            if (element == typeof(byte) || element == typeof(sbyte) || element == typeof(bool))
            {
                return 1;
            }
            if (element == typeof(short) || element == typeof(ushort) || element == typeof(char))
            {
                return 2;
            }
            if (element == typeof(long) || element == typeof(ulong) || element == typeof(double))
            {
                return 8;
            }
            return 4;
        }

        private static FieldInfo[] Fields(Type type)
        {
            if (!fieldCache.TryGetValue(type, out var fields))
            {
                var list = new List<FieldInfo>();
                for (var t = type; t != null && t != typeof(object); t = t.BaseType)
                {
                    foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        var fieldType = field.FieldType;
                        if (fieldType.FullName != null && (fieldType.IsClass || fieldType.IsInterface || fieldType.IsArray) && !fieldType.IsPointer && !fieldType.IsByRef && fieldType.FullName != "System.MonoFNPtrFakeClass")
                        {
                            list.Add(field);
                        }
                    }
                }
                fieldCache[type] = fields = list.ToArray();
            }
            return fields;
        }
    }
}
