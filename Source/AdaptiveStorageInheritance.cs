using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Xml;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class AdaptiveStorageInheritance
    {
        private static FieldInfo resolvedNodesField;
        private static FieldInfo resolvedXmlNodeField;
        private static bool installed;
        private static int calls, replaced, fallbacks, mismatches;
        private static long ticks;

        public static void Install(Harmony harmony)
        {
            var method = AccessTools.Method(AccessTools.TypeByName("PostInheritanceOperation.PatchQueue"), "ApplyXmlInheritanceOnDocument");
            if (method == null || !Fingerprint.Matches("Adaptive Storage inheritance", Fingerprint.Known(method, "1C87D576AF71706B", "E20EBA40BE410EC8")))
            {
                return;
            }
            resolvedNodesField = AccessTools.Field(typeof(XmlInheritance), "resolvedNodes");
            resolvedXmlNodeField = AccessTools.Field(AccessTools.Inner(typeof(XmlInheritance), "XmlInheritanceNode"), "resolvedXmlNode");
            if (resolvedNodesField == null || resolvedXmlNodeField == null)
            {
                return;
            }
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(AdaptiveStorageInheritance), nameof(Prefix)));
            installed = true;
        }

        public static bool Prefix(XmlDocument xmlDocument)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                return !Apply(xmlDocument);
            }
            finally
            {
                ticks += Stopwatch.GetTimestamp() - start;
            }
        }

        private static bool Apply(XmlDocument document)
        {
            calls++;
            if (FastXPath.HasIndex(document))
            {
                fallbacks++;
                return false;
            }
            var root = document.DocumentElement;
            var resolvedNodes = (IDictionary)resolvedNodesField.GetValue(null);
            var children = new List<XmlNode>(root.ChildNodes.Count);
            foreach (XmlNode child in root.ChildNodes)
            {
                children.Add(child);
            }
            foreach (var child in children)
            {
                if (child is XmlElement element && resolvedNodes.Contains(element) && resolvedXmlNodeField.GetValue(resolvedNodes[element]) is XmlNode resolved
                    && resolved != element && resolved.OwnerDocument == document && resolved.ParentNode != null)
                {
                    fallbacks++;
                    return false;
                }
            }
            var result = new List<XmlNode>(children.Count);
            var changed = false;
            foreach (var child in children)
            {
                if (child is XmlElement element && resolvedNodes.Contains(element))
                {
                    var node = resolvedNodes[element];
                    var resolved = resolvedXmlNodeField.GetValue(node) as XmlNode;
                    if (resolved != element && resolved != null && !resolvedNodes.Contains(resolved))
                    {
                        resolvedNodes.Add(resolved, node);
                        if (document != resolved.OwnerDocument)
                        {
                            resolved = document.ImportNode(resolved, true);
                            resolvedXmlNodeField.SetValue(node, resolved);
                        }
                        result.Add(resolved);
                        replaced++;
                        changed = true;
                        continue;
                    }
                }
                result.Add(child);
            }
            if (!changed)
            {
                return true;
            }
            while (root.FirstChild != null)
            {
                root.RemoveChild(root.FirstChild);
            }
            foreach (var node in result)
            {
                root.AppendChild(node);
            }
            if (RLOMod.Verify)
            {
                var i = 0;
                foreach (XmlNode child in root.ChildNodes)
                {
                    if (i >= result.Count || child != result[i])
                    {
                        mismatches++;
                        break;
                    }
                    i++;
                }
                if (i != result.Count)
                {
                    mismatches++;
                }
            }
            return true;
        }

        public static string Stats() => !installed ? "Adaptive Storage inheritance: not active" : $"Adaptive Storage inheritance: {calls} calls, {replaced} defs replaced in {LeanFixes.Seconds(ticks):F2} s, {fallbacks} fallbacks, {mismatches} verify mismatches";
    }
}
