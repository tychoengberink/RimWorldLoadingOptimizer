using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.XPath;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static partial class FastXPath
    {
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
            var n = 0;
            if (position.Success && !int.TryParse(position.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out n))
            {
                Fallback("(position out of range) " + xpath);
                return false;
            }
            Hits++;
            if (position.Success)
            {
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
            if (!xpath.StartsWith("//", StringComparison.Ordinal))
            {
                return xpath;
            }
            if (xpath.StartsWith("//Defs/", StringComparison.Ordinal))
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
            var i = xpath.StartsWith("/", StringComparison.Ordinal) ? 1 : 0;
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
                Mismatches.Report(xpath);
            }
        }
    }
}
