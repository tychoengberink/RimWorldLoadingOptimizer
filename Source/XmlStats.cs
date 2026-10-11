using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class XmlStats
    {
        public static void Report()
        {
            if (Environment.GetEnvironmentVariable("LEANMEMORY_XMLSTATS") != "1")
            {
                return;
            }
            var hits = new List<(string field, long nodes)>();
            var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic);
            foreach (var type in assemblies.SelectMany(AccessTools.GetTypesFromAssembly))
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
                    if (field.IsLiteral || fieldType.IsValueType || fieldType.IsPointer || fieldType.IsByRef || fieldType.FullName == "System.MonoFNPtrFakeClass"
                        || !(fieldType == typeof(object) || typeof(XmlNode).IsAssignableFrom(fieldType) || typeof(IEnumerable).IsAssignableFrom(fieldType)) || fieldType == typeof(string))
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
                    long nodes;
                    try
                    {
                        nodes = CountXml(value, 0);
                    }
                    catch
                    {
                        continue;
                    }
                    if (nodes > 1000)
                    {
                        hits.Add(($"{type.FullName}.{field.Name} ({type.Assembly.GetName().Name})", nodes));
                    }
                }
            }
            var sb = new StringBuilder("[RLO] static fields holding XML after startup:");
            foreach (var hit in hits.OrderByDescending(h => h.nodes).Take(20))
            {
                sb.Append($"\n  {hit.nodes,10} nodes  {hit.field}");
            }
            Log.Message(sb.ToString());
        }

        private static long CountXml(object value, int depth)
        {
            switch (value)
            {
                case null:
                    return 0;
                case XmlNode node:
                    return Count(node.OwnerDocument ?? node as XmlDocument ?? (XmlNode)node);
                case LoadableXmlAsset asset:
                    return asset.xmlDoc == null ? 0 : Count(asset.xmlDoc);
                case XmlContainer container:
                    return container.node == null ? 0 : Count(container.node.OwnerDocument ?? (XmlNode)container.node);
                case string _:
                    return 0;
                case IDictionary dict when depth < 2:
                    {
                        long total = 0;
                        var docs = new HashSet<XmlDocument>();
                        foreach (DictionaryEntry entry in dict)
                        {
                            total += Inner(entry.Key, depth, docs) + Inner(entry.Value, depth, docs);
                        }
                        return total;
                    }
                case IEnumerable list when depth < 2:
                    {
                        long total = 0;
                        var docs = new HashSet<XmlDocument>();
                        foreach (var item in list)
                        {
                            total += Inner(item, depth, docs);
                        }
                        return total;
                    }
                default:
                    return 0;
            }
        }

        private static long Inner(object item, int depth, HashSet<XmlDocument> docs)
        {
            if (item is LoadableXmlAsset asset)
            {
                item = asset.xmlDoc;
            }
            else if (item is XmlContainer container)
            {
                item = container.node;
            }
            if (item is XmlNode node)
            {
                var doc = node as XmlDocument ?? node.OwnerDocument;
                return doc != null && docs.Add(doc) ? Count(doc) : 0;
            }
            return item is IEnumerable && !(item is string) ? CountXml(item, depth + 1) : 0;
        }

        private static long Count(XmlNode root)
        {
            long count = 0;
            var stack = new Stack<XmlNode>();
            stack.Push(root);
            while (stack.Count > 0 && count < 50000000)
            {
                var node = stack.Pop();
                count++;
                for (var child = node.FirstChild; child != null; child = child.NextSibling)
                {
                    stack.Push(child);
                }
            }
            return count;
        }
    }
}
