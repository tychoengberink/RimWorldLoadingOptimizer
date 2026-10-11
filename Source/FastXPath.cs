using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.XPath;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class FastXPath
    {
        private static readonly Regex RootPath = new Regex(@"^/?(?:Defs|\*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex DefPath = new Regex(
            @"^/?(?:Defs|\*)/(?<tag>\*|[A-Za-z_][\w.\-]*)\[\s*(?<k>defName|@Name|@ParentName)\s*=\s*(?<v>""[^""]*""|'[^']*')(?:\s+or\s+(?<k>defName|@Name|@ParentName)\s*=\s*(?<v>""[^""]*""|'[^']*'))*\s*\](?<preds>(?:\[[^\[\]]*\])*)(?:/(?<rest>.+))?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex UnsafeRest = new Regex(@"^/|\.\.|::|\bDefs\b|\|", RegexOptions.Compiled);

        private static readonly Regex UnsafePredicate = new Regex(@"\[\s*[\d(\-]|position\s*\(|last\s*\(|count\s*\(|sum\s*\(|number\s*\(|string-length\s*\(|floor\s*\(|ceiling\s*\(|round\s*\(|\.\.|::|/|\|", RegexOptions.Compiled);

        private static readonly Regex Positional = new Regex(@"\b(?:position|last)\s*\(", RegexOptions.Compiled);

        private static readonly Regex BooleanCall = new Regex(@"^(?:not|boolean|contains|starts-with|true|false)\s*\(", RegexOptions.Compiled);

        private static readonly Regex SimplePath = new Regex(@"^@?(?:[A-Za-z_][\w.\-]*|\*)(?:/@?(?:[A-Za-z_][\w.\-]*|\*))*$", RegexOptions.Compiled);

        private static readonly Regex Name = new Regex(@"^[A-Za-z_][\w.\-]*", RegexOptions.Compiled);

        private static readonly Regex PositionOnly = new Regex(@"^\[\s*(?:position\s*\(\s*\)\s*=\s*)?(?<n>\d+)\s*\]$", RegexOptions.Compiled);

        private static bool patching;
        private static int patchingThread;

        private static ConditionalWeakTable<XmlDocument, DefIndex> Indexes = new ConditionalWeakTable<XmlDocument, DefIndex>();

        private static readonly List<DefIndex> Attached = new List<DefIndex>();

        private static readonly Dictionary<string, int> FallbackShapes = new Dictionary<string, int>();
        private static readonly Regex Literal = new Regex(@"(""[^""]*""|'[^']*')", RegexOptions.Compiled);

        public static int Hits;
        public static int Fallbacks;
        public static int Mismatches;
        public static int PatchedMethods;
        public static readonly Stopwatch PatchTime = new Stopwatch();

        private static readonly MethodInfo XmlSelectNodes = AccessTools.Method(typeof(XmlNode), nameof(XmlNode.SelectNodes), new[] { typeof(string) });
        private static readonly MethodInfo XmlSelectSingleNode = AccessTools.Method(typeof(XmlNode), nameof(XmlNode.SelectSingleNode), new[] { typeof(string) });

        public static void Install(Harmony harmony)
        {
            var transpiler = new HarmonyMethod(typeof(FastXPath), nameof(Transpiler)) { priority = Priority.Last };
            foreach (var type in CandidateTypes())
            {
                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (method.IsAbstract || method.ContainsGenericParameters || type.ContainsGenericParameters || !CallsAny(method, XmlSelectNodes, XmlSelectSingleNode))
                    {
                        continue;
                    }
                    try
                    {
                        harmony.Patch(method, transpiler: transpiler);
                        PatchedMethods++;
                    }
                    catch (Exception e)
                    {
                        Log.Warning($"[RLO] could not accelerate {type.FullName}.{method.Name}: {e.Message}");
                    }
                }
            }
            harmony.Patch(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.ApplyPatches)),
                prefix: new HarmonyMethod(typeof(FastXPath), nameof(StartTimer)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(FastXPath), nameof(StopTimer)) { priority = Priority.Last });
            var postInheritance = AccessTools.Method(AccessTools.TypeByName("PostInheritanceOperation.PatchQueue"), "ApplyPatches");
            if (postInheritance != null)
            {
                harmony.Patch(postInheritance,
                    prefix: new HarmonyMethod(typeof(FastXPath), nameof(StartTimer)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(FastXPath), nameof(StopWindow)) { priority = Priority.Last });
            }
        }

        private static IEnumerable<Type> CandidateTypes()
        {
            var patchOperations = GenTypes.AllSubclasses(typeof(PatchOperation)).Concat(new[] { typeof(PatchOperation) }).ToList();
            var gameAssembly = typeof(PatchOperation).Assembly;
            var modAssemblies = patchOperations.Select(t => t.Assembly).Where(a => a != gameAssembly && a != typeof(FastXPath).Assembly).Distinct().Where(AssemblySafety.CanScan);
            var types = new HashSet<Type>(patchOperations.Where(t => t.Assembly == gameAssembly));
            foreach (var assembly in modAssemblies)
            {
                foreach (var type in AccessTools.GetTypesFromAssembly(assembly))
                {
                    types.Add(type);
                }
            }
            return types;
        }

        private static bool CallsAny(MethodInfo method, params MethodInfo[] targets) => IlScan.Decide(method, v => v is MethodInfo m && targets.Contains(m));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(XmlSelectNodes))
                {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FastXPath), nameof(SelectNodes))).WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                }
                else if (instruction.Calls(XmlSelectSingleNode))
                {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FastXPath), nameof(SelectSingleNode))).WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                }
                else
                {
                    yield return instruction;
                }
            }
        }

        public static void StartTimer()
        {
            patchingThread = Thread.CurrentThread.ManagedThreadId;
            patching = true;
            PatchTime.Start();
        }

        public static bool HasIndex(XmlDocument doc) => patching && Indexes.TryGetValue(doc, out _);

        public static Exception StopWindow(Exception __exception)
        {
            PatchTime.Stop();
            patching = false;
            foreach (var attached in Attached)
            {
                attached.Detach();
            }
            Attached.Clear();
            Indexes = new ConditionalWeakTable<XmlDocument, DefIndex>();
            return __exception;
        }

        public static Exception StopTimer(Exception __exception)
        {
            StopWindow(null);
            Log.Message($"[RLO] XML patching took {PatchTime.Elapsed.TotalSeconds:F1} s; fast XPath hits {Hits}, fallbacks {Fallbacks}, accelerated methods {PatchedMethods}, index rebuilds {DefIndex.Rebuilds}, defs re-indexed {DefIndex.Reindexed}" +
                        (RLOMod.Verify ? $", verify mismatches {Mismatches}" : "") +
                        "\n  top fallback shapes:" + string.Concat(FallbackShapes.OrderByDescending(p => p.Value).Take(12).Select(p => $"\n    {p.Value,5}  {p.Key}")));
            return __exception;
        }

        public static XmlNodeList SelectNodes(XmlNode context, string xpath)
        {
            if (TryFast(context, xpath, out var fast))
            {
                if (RLOMod.Verify)
                {
                    Check(xpath, fast, context.SelectNodes(xpath));
                }
                return fast;
            }
            return context.SelectNodes(xpath);
        }

        public static XmlNode SelectSingleNode(XmlNode context, string xpath)
        {
            if (TryFast(context, xpath, out var fast))
            {
                var node = fast.Count > 0 ? fast[0] : null;
                if (RLOMod.Verify && node != context.SelectSingleNode(xpath))
                {
                    Mismatch(xpath);
                }
                return node;
            }
            return context.SelectSingleNode(xpath);
        }

        private static bool TryFast(XmlNode context, string xpath, out XmlNodeList result)
        {
            result = null;
            if (!patching || !(context is XmlDocument doc) || xpath == null || Thread.CurrentThread.ManagedThreadId != patchingThread)
            {
                return false;
            }
            xpath = xpath.Trim();
            var index = Indexes.GetValue(doc, d =>
            {
                var created = new DefIndex(d);
                Attached.Add(created);
                return created;
            });
            if (RootPath.IsMatch(xpath))
            {
                var root = doc.DocumentElement;
                if (root == null || root.NamespaceURI != "" || (xpath.Contains("Defs") && root.Name != "Defs"))
                {
                    Fallback(xpath);
                    return false;
                }
                Hits++;
                result = new NodeList(new List<XmlNode> { root });
                return true;
            }
            var original = xpath;
            xpath = Rewrite(index, Normalize(xpath));
            if (xpath == null)
            {
                Fallback(original);
                return false;
            }
            var match = DefPath.Match(xpath);
            if (!match.Success)
            {
                return TryByTag(index, xpath, original, out result);
            }
            var preds = match.Groups["preds"].Value;
            var rest = match.Groups["rest"].Success ? match.Groups["rest"].Value : null;
            var position = PositionOnly.Match(preds);
            if (position.Success)
            {
                preds = "";
            }
            if ((rest != null && UnsafeRest.IsMatch(rest)) || (preds.Length > 0 && UnsafePredicate.IsMatch(preds)))
            {
                return TryByTag(index, xpath, original, out result);
            }
            var keys = match.Groups["k"].Captures.Cast<Capture>().Select(c => c.Value).ToList();
            var values = match.Groups["v"].Captures.Cast<Capture>().Select(c => c.Value.Substring(1, c.Value.Length - 2)).ToList();
            var defs = index.Find(match.Groups["tag"].Value, keys, values);
            if (defs == null)
            {
                Fallback("(index unavailable) " + xpath);
                return false;
            }
            Hits++;
            if (position.Success)
            {
                var n = int.Parse(position.Groups["n"].Value);
                defs = n >= 1 && n <= defs.Count ? new List<XmlElement> { defs[n - 1] } : new List<XmlElement>();
            }
            if (preds.Length == 0 && rest == null)
            {
                result = new NodeList(defs.Cast<XmlNode>().ToList());
                return true;
            }
            var selector = "self::node()" + preds + (rest != null ? "/" + rest : "");
            if (defs.Count == 1)
            {
                result = defs[0].SelectNodes(selector);
                return true;
            }
            var all = new List<XmlNode>();
            foreach (var def in defs)
            {
                all.AddRange(def.SelectNodes(selector).Cast<XmlNode>());
            }
            result = new NodeList(all);
            return true;
        }

        private static string Normalize(string xpath)
        {
            var builder = new StringBuilder(xpath.Length);
            char quote = '\0';
            for (var i = 0; i < xpath.Length; i++)
            {
                var c = xpath[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                    builder.Append(c);
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    quote = c;
                    builder.Append(c);
                    continue;
                }
                if (char.IsWhiteSpace(c))
                {
                    var j = i;
                    while (j < xpath.Length && char.IsWhiteSpace(xpath[j]))
                    {
                        j++;
                    }
                    var prev = builder.Length > 0 ? builder[builder.Length - 1] : '/';
                    var next = j < xpath.Length ? xpath[j] : '/';
                    if (!IsSlashOrBracket(prev) && !IsSlashOrBracket(next))
                    {
                        builder.Append(' ');
                    }
                    i = j - 1;
                    continue;
                }
                builder.Append(c);
            }
            return builder.ToString();
        }

        private static bool IsSlashOrBracket(char c) => c == '/' || c == '[' || c == ']';

        private static string Rewrite(DefIndex index, string xpath)
        {
            if (!xpath.StartsWith("//"))
            {
                return xpath;
            }
            if (xpath.StartsWith("//Defs/"))
            {
                return index.OnlyRootIsNamed("Defs") ? xpath.Substring(1) : null;
            }
            var name = Name.Match(xpath, 2, xpath.Length - 2);
            if (!name.Success || name.Index != 2 || name.Value == "Defs")
            {
                return null;
            }
            var end = 2 + name.Length;
            if (end < xpath.Length && xpath[end] != '[' && xpath[end] != '/')
            {
                return null;
            }
            return index.OnlyAtRoot(name.Value) ? "/Defs/" + xpath.Substring(2) : null;
        }

        private static bool TryByTag(DefIndex index, string xpath, string original, out XmlNodeList result)
        {
            result = null;
            var i = xpath.StartsWith("/") ? 1 : 0;
            if (string.CompareOrdinal(xpath, i, "Defs/", 0, 5) == 0)
            {
                i += 5;
            }
            else if (string.CompareOrdinal(xpath, i, "*/", 0, 2) == 0)
            {
                i += 2;
            }
            else
            {
                Fallback(original);
                return false;
            }
            string tag;
            if (i < xpath.Length && xpath[i] == '*')
            {
                tag = "*";
            }
            else
            {
                var name = Name.Match(xpath, i, xpath.Length - i);
                if (!name.Success || name.Index != i)
                {
                    Fallback(original);
                    return false;
                }
                tag = name.Value;
            }
            i += tag.Length;
            var preds = new StringBuilder();
            while (i < xpath.Length && xpath[i] == '[')
            {
                var close = Close(xpath, i);
                if (close < 0)
                {
                    Fallback(original);
                    return false;
                }
                var pred = xpath.Substring(i + 1, close - i - 1);
                if (Positional.IsMatch(pred) || !NotNumeric(pred.Trim()))
                {
                    Fallback(original);
                    return false;
                }
                preds.Append('[').Append(pred).Append(']');
                i = close + 1;
            }
            string rest = null;
            if (i < xpath.Length)
            {
                if (xpath[i] != '/' || i + 1 >= xpath.Length)
                {
                    Fallback(original);
                    return false;
                }
                rest = xpath.Substring(i + 1);
                if (UnsafeRest.IsMatch(rest))
                {
                    Fallback(original);
                    return false;
                }
            }
            var defs = index.ByTag(tag);
            if (defs == null)
            {
                Fallback("(index unavailable) " + original);
                return false;
            }
            var all = new List<XmlNode>();
            if (preds.Length == 0 && rest == null)
            {
                all.AddRange(defs);
            }
            else
            {
                try
                {
                    var expression = XPathExpression.Compile("self::node()" + preds + (rest != null ? "/" + rest : ""));
                    foreach (var def in defs)
                    {
                        var iterator = def.CreateNavigator().Select(expression);
                        while (iterator.MoveNext())
                        {
                            all.Add(((IHasXmlNode)iterator.Current).GetNode());
                        }
                    }
                }
                catch (Exception)
                {
                    Fallback("(evaluation failed) " + original);
                    return false;
                }
            }
            Hits++;
            result = new NodeList(all);
            return true;
        }

        private static int Close(string s, int open)
        {
            var depth = 0;
            char quote = '\0';
            for (var i = open; i < s.Length; i++)
            {
                var c = s[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c;
                }
                else if (c == '[' || c == '(')
                {
                    depth++;
                }
                else if (c == ']' || c == ')')
                {
                    if (--depth == 0)
                    {
                        return c == ']' ? i : -1;
                    }
                }
            }
            return -1;
        }

        private static bool NotNumeric(string pred)
        {
            var depth = 0;
            char quote = '\0';
            for (var i = 0; i < pred.Length; i++)
            {
                var c = pred[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    quote = c;
                }
                else if (c == '[' || c == '(')
                {
                    depth++;
                }
                else if (c == ']' || c == ')')
                {
                    depth--;
                }
                else if (depth == 0 && (c == '=' || c == '<' || c == '>'))
                {
                    return true;
                }
                else if (depth == 0 && char.IsWhiteSpace(c) && (Word(pred, i + 1, "and") || Word(pred, i + 1, "or")))
                {
                    return true;
                }
            }
            if (BooleanCall.IsMatch(pred))
            {
                var open = pred.IndexOf('(');
                return CloseParen(pred, open) == pred.Length - 1;
            }
            return SimplePath.IsMatch(pred);
        }

        private static bool Word(string s, int at, string word)
        {
            if (string.CompareOrdinal(s, at, word, 0, word.Length) != 0)
            {
                return false;
            }
            var after = at + word.Length;
            return after < s.Length && (char.IsWhiteSpace(s[after]) || s[after] == '(');
        }

        private static int CloseParen(string s, int open)
        {
            var depth = 0;
            char quote = '\0';
            for (var i = open; i < s.Length; i++)
            {
                var c = s[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c;
                }
                else if (c == '(' || c == '[')
                {
                    depth++;
                }
                else if ((c == ')' || c == ']') && --depth == 0)
                {
                    return i;
                }
            }
            return -1;
        }

        private static void Fallback(string xpath)
        {
            Fallbacks++;
            if (FallbackShapes.Count > 2000)
            {
                return;
            }
            var shape = Literal.Replace(xpath, "\"…\"");
            shape = shape.Length > 90 ? shape.Substring(0, 90) : shape;
            FallbackShapes.TryGetValue(shape, out var n);
            FallbackShapes[shape] = n + 1;
        }

        private static void Check(string xpath, XmlNodeList fast, XmlNodeList slow)
        {
            if (!fast.Cast<XmlNode>().SequenceEqual(slow.Cast<XmlNode>()))
            {
                Mismatch(xpath);
            }
        }

        private static void Mismatch(string xpath)
        {
            if (Mismatches++ < 20)
            {
                Log.Warning($"[RLO] fast XPath mismatch: {xpath}");
            }
        }

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
