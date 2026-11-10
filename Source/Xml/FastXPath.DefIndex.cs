using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static partial class FastXPath
    {
        private sealed class NodeList : XmlNodeList
        {
            private readonly List<XmlNode> nodes;

            public NodeList(List<XmlNode> nodes) => this.nodes = nodes;

            public override int Count => nodes.Count;

            public override XmlNode Item(int index) => index >= 0 && index < nodes.Count ? nodes[index] : null;

            public override IEnumerator GetEnumerator() => nodes.GetEnumerator();
        }

        private sealed class DefIndex
        {
            private readonly XmlDocument doc;
            private readonly Dictionary<string, List<XmlElement>> byDefName = new Dictionary<string, List<XmlElement>>();
            private readonly Dictionary<string, List<XmlElement>> byName = new Dictionary<string, List<XmlElement>>();
            private readonly Dictionary<string, List<XmlElement>> byParentName = new Dictionary<string, List<XmlElement>>();
            private readonly Dictionary<string, List<XmlElement>> byTag = new Dictionary<string, List<XmlElement>>();
            private bool tagsUnordered;
            private HashSet<string> nested;
            private Dictionary<XmlElement, int> ordinals;
            private int maxOrdinal;
            private bool dirty = true;
            private readonly Dictionary<XmlElement, string[]> keysOf = new Dictionary<XmlElement, string[]>();
            private readonly HashSet<XmlElement> stale = new HashSet<XmlElement>();
            public static int Rebuilds, Reindexed;

            private readonly XmlNodeChangedEventHandler inserted;
            private readonly XmlNodeChangedEventHandler removing;
            private readonly XmlNodeChangedEventHandler changed;

            public DefIndex(XmlDocument doc)
            {
                this.doc = doc;
                inserted = (_, e) => OnInserted(e);
                removing = (_, e) => OnRemoving(e);
                changed = (_, e) => Touch(e.Node, e.NewParent);
                doc.NodeInserted += inserted;
                doc.NodeRemoving += removing;
                doc.NodeChanged += changed;
            }

            public void Detach()
            {
                doc.NodeInserted -= inserted;
                doc.NodeRemoving -= removing;
                doc.NodeChanged -= changed;
                byDefName.Clear();
                byName.Clear();
                byParentName.Clear();
                byTag.Clear();
                keysOf.Clear();
                stale.Clear();
                nested = null;
                ordinals = null;
                dirty = true;
            }

            private bool Ready()
            {
                if (dirty)
                {
                    Rebuild();
                    if (dirty)
                    {
                        return false;
                    }
                }
                if (stale.Count > 0)
                {
                    var root = doc.DocumentElement;
                    foreach (var element in stale)
                    {
                        if (element.ParentNode == root && keysOf.ContainsKey(element))
                        {
                            RemoveKeys(element);
                            AddKeys(element);
                            Reindexed++;
                        }
                    }
                    stale.Clear();
                }
                return true;
            }

            public List<XmlElement> ByTag(string tag)
            {
                if (!Ready())
                {
                    return null;
                }
                if (tag == "*")
                {
                    var all = new List<XmlElement>();
                    foreach (XmlNode child in doc.DocumentElement.ChildNodes)
                    {
                        if (child is XmlElement element)
                        {
                            all.Add(element);
                        }
                    }
                    return all;
                }
                if (tagsUnordered)
                {
                    byTag.Clear();
                    foreach (XmlNode child in doc.DocumentElement.ChildNodes)
                    {
                        if (child is XmlElement element && element.NamespaceURI == "")
                        {
                            Bucket(byTag, element.Name).Add(element);
                        }
                    }
                    tagsUnordered = false;
                }
                return byTag.TryGetValue(tag, out var list) ? new List<XmlElement>(list) : new List<XmlElement>();
            }

            public bool OnlyAtRoot(string name)
            {
                var root = doc.DocumentElement;
                return root != null && root.Name == "Defs" && root.NamespaceURI == "" && !Nested().Contains(name);
            }

            public bool OnlyRootIsNamed(string name)
            {
                if (!OnlyAtRoot(name))
                {
                    return false;
                }
                var tagged = ByTag(name);
                return tagged != null && tagged.Count == 0;
            }

            private HashSet<string> Nested()
            {
                if (nested == null)
                {
                    nested = new HashSet<string>();
                    foreach (XmlNode child in doc.DocumentElement.ChildNodes)
                    {
                        if (child is XmlElement element)
                        {
                            AddDescendants(element);
                        }
                    }
                }
                return nested;
            }

            private void AddDescendants(XmlNode node)
            {
                var stack = new Stack<XmlNode>();
                for (var child = node.FirstChild; child != null; child = child.NextSibling)
                {
                    stack.Push(child);
                }
                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    if (!(current is XmlElement))
                    {
                        continue;
                    }
                    nested.Add(current.LocalName);
                    for (var child = current.FirstChild; child != null; child = child.NextSibling)
                    {
                        stack.Push(child);
                    }
                }
            }

            public List<XmlElement> Find(string tag, List<string> keys, List<string> values)
            {
                if (!Ready())
                {
                    return null;
                }
                var found = new List<XmlElement>();
                for (var term = 0; term < values.Count; term++)
                {
                    var key = keys[term];
                    var value = values[term];
                    var map = key == "defName" ? byDefName : key == "@Name" ? byName : byParentName;
                    if (!map.TryGetValue(value, out var candidates))
                    {
                        continue;
                    }
                    foreach (var candidate in candidates)
                    {
                        if ((tag == "*" || candidate.Name == tag) && !found.Contains(candidate))
                        {
                            found.Add(candidate);
                        }
                    }
                }
                if (found.Count > 1)
                {
                    var order = Ordinals();
                    found.Sort((a, b) => order[a].CompareTo(order[b]));
                }
                return found;
            }

            private Dictionary<XmlElement, int> Ordinals()
            {
                if (ordinals == null)
                {
                    ordinals = new Dictionary<XmlElement, int>();
                    var i = 0;
                    foreach (XmlNode child in doc.DocumentElement.ChildNodes)
                    {
                        if (child is XmlElement element)
                        {
                            ordinals[element] = i++;
                        }
                    }
                    maxOrdinal = i;
                }
                return ordinals;
            }

            private void Rebuild()
            {
                byDefName.Clear();
                byName.Clear();
                byParentName.Clear();
                byTag.Clear();
                keysOf.Clear();
                stale.Clear();
                tagsUnordered = false;
                ordinals = null;
                Rebuilds++;
                var root = doc.DocumentElement;
                if (root == null || root.Name != "Defs" || root.NamespaceURI != "")
                {
                    return;
                }
                foreach (XmlNode child in root.ChildNodes)
                {
                    if (child is XmlElement element)
                    {
                        Add(element);
                    }
                }
                dirty = false;
            }

            private static IEnumerable<string> DefNames(XmlElement element)
            {
                foreach (XmlNode child in element.ChildNodes)
                {
                    if (child is XmlElement e && e.LocalName == "defName" && e.NamespaceURI == "")
                    {
                        yield return e.InnerText;
                    }
                }
            }

            private void Add(XmlElement element)
            {
                if (element.NamespaceURI != "")
                {
                    return;
                }
                Bucket(byTag, element.Name).Add(element);
                AddKeys(element);
            }

            private void AddKeys(XmlElement element)
            {
                var keys = new List<string>();
                foreach (var defName in DefNames(element))
                {
                    Bucket(byDefName, defName).Add(element);
                    keys.Add("d" + defName);
                }
                var name = element.GetAttributeNode("Name");
                if (name != null)
                {
                    Bucket(byName, name.Value).Add(element);
                    keys.Add("n" + name.Value);
                }
                var parentName = element.GetAttributeNode("ParentName");
                if (parentName != null)
                {
                    Bucket(byParentName, parentName.Value).Add(element);
                    keys.Add("p" + parentName.Value);
                }
                keysOf[element] = keys.ToArray();
            }

            private void Remove(XmlElement element)
            {
                if (byTag.TryGetValue(element.Name, out var tagged))
                {
                    tagged.Remove(element);
                }
                RemoveKeys(element);
                stale.Remove(element);
            }

            private void RemoveKeys(XmlElement element)
            {
                if (!keysOf.TryGetValue(element, out var keys))
                {
                    return;
                }
                foreach (var key in keys)
                {
                    var map = key[0] == 'd' ? byDefName : key[0] == 'n' ? byName : byParentName;
                    if (map.TryGetValue(key.Substring(1), out var list))
                    {
                        list.Remove(element);
                    }
                }
                keysOf.Remove(element);
            }

            private static List<XmlElement> Bucket(Dictionary<string, List<XmlElement>> map, string key)
            {
                if (!map.TryGetValue(key, out var list))
                {
                    list = new List<XmlElement>(1);
                    map[key] = list;
                }
                return list;
            }

            private void OnInserted(XmlNodeChangedEventArgs e)
            {
                if (nested != null)
                {
                    if (e.NewParent == doc || e.Node is XmlDocument || e.NewParent == null)
                    {
                        nested = null;
                    }
                    else if (e.Node is XmlElement inserted)
                    {
                        if (e.NewParent != doc.DocumentElement)
                        {
                            nested.Add(inserted.LocalName);
                        }
                        AddDescendants(inserted);
                    }
                }
                if (dirty)
                {
                    return;
                }
                if (e.NewParent == doc.DocumentElement && e.Node is XmlElement element)
                {
                    if (element != doc.DocumentElement.LastChild)
                    {
                        tagsUnordered = true;
                    }
                    if (ordinals != null && element == doc.DocumentElement.LastChild)
                    {
                        ordinals[element] = ++maxOrdinal;
                    }
                    else
                    {
                        ordinals = null;
                    }
                    Add(element);
                    return;
                }
                Touch(e.Node, e.NewParent);
            }

            private void OnRemoving(XmlNodeChangedEventArgs e)
            {
                if (dirty)
                {
                    return;
                }
                if (e.OldParent == doc.DocumentElement && e.Node is XmlElement element)
                {
                    ordinals?.Remove(element);
                    Remove(element);
                    return;
                }
                Touch(e.Node, e.OldParent);
            }

            private void Touch(XmlNode node, XmlNode parent)
            {
                if (dirty)
                {
                    return;
                }
                var root = doc.DocumentElement;
                if (node == root || parent == doc || node is XmlDocument)
                {
                    dirty = true;
                    return;
                }
                parent = parent ?? node.ParentNode;
                if (!(node is XmlAttribute) && parent is XmlAttribute owningAttribute)
                {
                    node = owningAttribute;
                    parent = null;
                }
                if (node is XmlAttribute attribute)
                {
                    var owner = attribute.OwnerElement ?? parent as XmlElement;
                    if ((attribute.Name == "Name" || attribute.Name == "ParentName") && owner != null && owner.ParentNode == root)
                    {
                        stale.Add(owner);
                    }
                    return;
                }
                for (var current = node is XmlElement ? node : parent; current != null && current != root; current = current.ParentNode)
                {
                    if (current.LocalName == "defName" && current.ParentNode?.ParentNode == root)
                    {
                        stale.Add((XmlElement)current.ParentNode);
                        return;
                    }
                }
            }
        }
    }
}
