#if RW17
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Xml;
using Microsoft.Win32.SafeHandles;

namespace RimWorldLoadingOptimizer
{
    // Reads Missile Girl's Unified.xml cache into the def document on several threads. The file is one root element
    // whose children are written one per line by an indenting XmlWriter, so it is cut at "\n  <Item" into chunks and
    // every chunk is read as an XML fragment on its own thread. Each node is built the way XmlDocument.ReadNode
    // (XmlLoader.LoadNode) builds it, owned by the same document and appended in the same order; only the shared
    // name table of the document is reached under a lock. A cut that is not between two root children leaves a chunk
    // that is not well formed (an element, comment or CDATA section left open), so any doubt ends in an exception and
    // the caller reads the file the original way.
    public static class GagarinParallelRead
    {
        public static int ChunkBytes = 4 << 20;
        private static readonly byte[] Declaration = Encoding.ASCII.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        private static readonly byte[] RootStart = Encoding.ASCII.GetBytes("<DefXmlStorage>");
        private static readonly byte[] RootEnd = Encoding.ASCII.GetBytes("</DefXmlStorage>");
        private static readonly byte[] Cut = Encoding.ASCII.GetBytes("\n  <Item");

        private static Func<XmlDocument, string, string, string, object> addXmlName, addAttrXmlName;
        private static Func<object, bool, XmlDocument, XmlElement> newElement;
        private static Func<object, XmlDocument, XmlAttribute> newAttribute;
        private static Func<XmlNode, XmlNode, XmlDocument, XmlNode> appendForLoad;
        private static FieldInfo inserting, inserted, idAttributes, childKeyIndex;
        private static PropertyInfo isLoading, dtdSchemaInfo;

        public static bool Premark = true;
        public static int Threads, Chunks, Nodes;
        public static string LastFallback = "";

        // The internal members of System.Xml the reader builds nodes with; null when all are there.
        public static string Init()
        {
            var doc = typeof(XmlDocument);
            var xmlName = doc.Assembly.GetType("System.Xml.XmlName");
            const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var add = doc.GetMethod("AddXmlName", All, null, new[] { typeof(string), typeof(string), typeof(string), typeof(System.Xml.Schema.IXmlSchemaInfo) }, null);
            var addAttr = doc.GetMethod("AddAttrXmlName", All, null, new[] { typeof(string), typeof(string), typeof(string), typeof(System.Xml.Schema.IXmlSchemaInfo) }, null);
            var element = xmlName == null ? null : typeof(XmlElement).GetConstructor(All, null, new[] { xmlName, typeof(bool), doc }, null);
            var attribute = xmlName == null ? null : typeof(XmlAttribute).GetConstructor(All, null, new[] { xmlName, doc }, null);
            var append = typeof(XmlNode).GetMethod("AppendChildForLoad", All, null, new[] { typeof(XmlNode), doc }, null);
            inserting = doc.GetField("_onNodeInsertingDelegate", All);
            inserted = doc.GetField("_onNodeInsertedDelegate", All);
            idAttributes = doc.GetField("_htElementIDAttrDecl", All);
            childKeyIndex = doc.GetField("ChildKeyIndex", All);
            isLoading = doc.GetProperty("IsLoading", All);
            dtdSchemaInfo = doc.GetProperty("DtdSchemaInfo", All);
            if (add == null || addAttr == null || element == null || attribute == null || append == null || inserting == null || inserted == null || idAttributes == null || isLoading == null || dtdSchemaInfo == null)
            {
                return "System.Xml members changed";
            }
            addXmlName = NameAdder(add);
            addAttrXmlName = NameAdder(addAttr);
            var m = new DynamicMethod("RLO_NewElement", typeof(XmlElement), new[] { typeof(object), typeof(bool), doc }, doc.Module, true);
            var il = m.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, xmlName);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Newobj, element);
            il.Emit(OpCodes.Ret);
            newElement = (Func<object, bool, XmlDocument, XmlElement>)m.CreateDelegate(typeof(Func<object, bool, XmlDocument, XmlElement>));
            m = new DynamicMethod("RLO_NewAttribute", typeof(XmlAttribute), new[] { typeof(object), doc }, doc.Module, true);
            il = m.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, xmlName);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Newobj, attribute);
            il.Emit(OpCodes.Ret);
            newAttribute = (Func<object, XmlDocument, XmlAttribute>)m.CreateDelegate(typeof(Func<object, XmlDocument, XmlAttribute>));
            m = new DynamicMethod("RLO_AppendChildForLoad", typeof(XmlNode), new[] { typeof(XmlNode), typeof(XmlNode), doc }, doc.Module, true);
            il = m.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Callvirt, append);
            il.Emit(OpCodes.Ret);
            appendForLoad = (Func<XmlNode, XmlNode, XmlDocument, XmlNode>)m.CreateDelegate(typeof(Func<XmlNode, XmlNode, XmlDocument, XmlNode>));
            return null;
        }

        private static Func<XmlDocument, string, string, string, object> NameAdder(MethodInfo method)
        {
            var m = new DynamicMethod("RLO_" + method.Name, typeof(object), new[] { typeof(XmlDocument), typeof(string), typeof(string), typeof(string) }, typeof(XmlDocument).Module, true);
            var il = m.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Call, method);
            il.Emit(OpCodes.Ret);
            return (Func<XmlDocument, string, string, string, object>)m.CreateDelegate(typeof(Func<XmlDocument, string, string, string, object>));
        }

        // The root element as XmlDocument.ReadNode would return it after MoveToContent, or null (with
        // LastFallback set) when the file is left to the original reader.
        public static XmlElement TryRead(XmlDocument target, string path, XmlReaderSettings settings, int threads)
        {
            try
            {
                if (newElement == null)
                {
                    LastFallback = "not initialized";
                    return null;
                }
                if ((bool)isLoading.GetValue(target) || inserting.GetValue(target) != null || inserted.GetValue(target) != null || idAttributes.GetValue(target) != null
                    || dtdSchemaInfo.GetValue(target) != null || (childKeyIndex != null && childKeyIndex.GetValue(target) != null))
                {
                    LastFallback = "the def document is not plain";
                    return null;
                }
                if (settings == null || settings.ConformanceLevel != ConformanceLevel.Document || settings.DtdProcessing == DtdProcessing.Parse || settings.ValidationType != ValidationType.None
                    || settings.NameTable != null)
                {
                    LastFallback = "Missile Girl reads its cache with other settings";
                    return null;
                }
                using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None))
                {
                    var bounds = Bounds(handle);
                    if (bounds == null)
                    {
                        LastFallback = "the cache file is not laid out as expected";
                        return null;
                    }
                    var chunkSettings = settings.Clone();
                    chunkSettings.ConformanceLevel = ConformanceLevel.Fragment;
                    chunkSettings.CloseInput = false;
                    var results = new List<XmlNode>[bounds.Count - 1];
                    var names = new object();
                    Exception failure = null;
                    var next = -1;
                    var count = Math.Max(1, Math.Min(threads, results.Length));
                    void Work()
                    {
                        try
                        {
                            var builder = new Builder(target, names);
                            int i;
                            while ((i = Interlocked.Increment(ref next)) < results.Length && Volatile.Read(ref failure) == null)
                            {
                                results[i] = builder.ReadChunk(handle, bounds[i], bounds[i + 1], chunkSettings);
                            }
                        }
                        catch (Exception e)
                        {
                            Interlocked.CompareExchange(ref failure, e, null);
                        }
                    }
                    // The calling thread reads too, next to count - 1 helpers.
                    // Nothing may still build nodes once this returns, as the original reader may run next on the same
                    // document, so every helper that started is joined whatever happened.
                    var workers = new Thread[count - 1];
                    var started = 0;
                    try
                    {
                        for (; started < workers.Length; started++)
                        {
                            workers[started] = new Thread(Work, 1 << 20) { IsBackground = true, Name = "RLO cache reader " + (started + 1) };
                            workers[started].Start();
                        }
                    }
                    catch (Exception e)
                    {
                        Interlocked.CompareExchange(ref failure, e, null);
                    }
                    Work();
                    for (var w = 0; w < started; w++)
                    {
                        workers[w].Join();
                    }
                    if (failure != null)
                    {
                        LastFallback = failure.GetType().Name + ": " + failure.Message;
                        return null;
                    }
                    var root = target.CreateElement(string.Empty, "DefXmlStorage", string.Empty);
                    root.IsEmpty = false;
                    var nodes = 0;
                    foreach (var list in results)
                    {
                        foreach (var node in list)
                        {
                            appendForLoad(root, node, target);
                            nodes++;
                        }
                    }
                    Threads = count;
                    Chunks = results.Length;
                    Nodes = nodes;
                    LastFallback = "";
                    return root;
                }
            }
            catch (Exception e)
            {
                LastFallback = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }

        // Byte offsets of the chunks: the first starts right after <DefXmlStorage>, the last ends at
        // </DefXmlStorage>, the ones between start at the '<' of a "\n  <Item".
        private static List<long> Bounds(SafeFileHandle handle)
        {
            var length = RandomAccess.GetLength(handle);
            var head = new byte[(int)Math.Min(length, 4096)];
            if (RandomAccess.Read(handle, head, 0) != head.Length)
            {
                return null;
            }
            var at = 0;
            if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            {
                at = 3;
            }
            if (!At(head, at, Declaration))
            {
                return null;
            }
            at += Declaration.Length;
            while (at < head.Length && IsSpace(head[at]))
            {
                at++;
            }
            if (!At(head, at, RootStart))
            {
                return null;
            }
            long start = at + RootStart.Length;
            var tail = new byte[(int)Math.Min(length - start, 4096)];
            var tailAt = length - tail.Length;
            if (RandomAccess.Read(handle, tail, tailAt) != tail.Length)
            {
                return null;
            }
            var end = tail.Length;
            while (end > 0 && IsSpace(tail[end - 1]))
            {
                end--;
            }
            if (end < RootEnd.Length || !At(tail, end - RootEnd.Length, RootEnd))
            {
                return null;
            }
            var bodyEnd = tailAt + end - RootEnd.Length;
            if (bodyEnd < start)
            {
                return null;
            }
            var bounds = new List<long> { start };
            var window = new byte[1 << 16];
            for (var target = start + ChunkBytes; target < bodyEnd; target += ChunkBytes)
            {
                var from = Math.Max(target, bounds[bounds.Count - 1] + 1);
                var found = -1L;
                while (found < 0 && from < bodyEnd)
                {
                    var read = RandomAccess.Read(handle, window.AsSpan(0, (int)Math.Min(window.Length, bodyEnd - from)), from);
                    if (read <= 0)
                    {
                        return null;
                    }
                    var index = window.AsSpan(0, read).IndexOf(Cut);
                    if (index >= 0)
                    {
                        found = from + index + 1;
                    }
                    else if (read < Cut.Length)
                    {
                        break;
                    }
                    else
                    {
                        from += read - Cut.Length + 1;
                    }
                }
                if (found < 0)
                {
                    break;
                }
                bounds.Add(found);
                target = Math.Max(target, found);
            }
            bounds.Add(bodyEnd);
            return bounds;
        }

        // What a deep XmlDocument.ImportNode leaves on a copied subtree: every element without children is
        // written as <x />. Missile Girl's loader imports the first child of every cached item; the readers do
        // this ahead for those children so the main thread can skip it (see Premarked).
        public static void MarkEmpty(XmlNode root)
        {
            var stack = new Stack<XmlNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node is XmlElement element && !element.HasChildNodes)
                {
                    if (!element.IsEmpty)
                    {
                        element.IsEmpty = true;
                    }
                    continue;
                }
                for (var child = node.FirstChild; child != null; child = child.NextSibling)
                {
                    stack.Push(child);
                }
            }
        }

        // True for the first child of an item of a root this reader built, which MarkEmpty already ran on.
        public static bool Premarked(XmlNode node, XmlElement root)
        {
            var item = node.ParentNode;
            return Premark && root != null && item != null && item.ParentNode == root && item.FirstChild == node;
        }

        private static bool At(byte[] buffer, int at, byte[] what) => at >= 0 && at + what.Length <= buffer.Length && buffer.AsSpan(at, what.Length).SequenceEqual(what);

        private static bool IsSpace(byte b) => b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n';

        private sealed class Builder
        {
            private readonly XmlDocument doc;
            private readonly object names;
            private readonly Dictionary<NameKey, object> elementNames = new Dictionary<NameKey, object>();
            private readonly Dictionary<NameKey, object> attributeNames = new Dictionary<NameKey, object>();

            public Builder(XmlDocument doc, object names)
            {
                this.doc = doc;
                this.names = names;
            }

            public List<XmlNode> ReadChunk(SafeFileHandle handle, long from, long to, XmlReaderSettings settings)
            {
                var list = new List<XmlNode>();
                using (var stream = new SliceStream(handle, from, to))
                using (var text = new StreamReader(stream, Encoding.UTF8, false, 1 << 16))
                using (var reader = XmlReader.Create(text, settings))
                {
                    reader.Read();
                    while (!reader.EOF)
                    {
                        if (reader.NodeType != XmlNodeType.Element)
                        {
                            throw new NotSupportedException($"{reader.NodeType} between the cached defs");
                        }
                        var item = LoadElement(reader);
                        if (Premark && item.FirstChild != null)
                        {
                            MarkEmpty(item.FirstChild);
                        }
                        list.Add(item);
                    }
                }
                return list;
            }

            // XmlLoader.LoadNode for an element the reader is on, followed by the reader.Read() of
            // XmlLoader.ReadCurrentNode.
            private XmlNode LoadElement(XmlReader reader)
            {
                XmlNode parent = null;
                do
                {
                    XmlNode node;
                    switch (reader.NodeType)
                    {
                        case XmlNodeType.Element:
                        {
                            var empty = reader.IsEmptyElement;
                            var element = newElement(Name(elementNames, addXmlName, reader), true, doc);
                            element.IsEmpty = empty;
                            if (reader.MoveToFirstAttribute())
                            {
                                var attributes = element.Attributes;
                                do
                                {
                                    attributes.Append(LoadAttribute(reader));
                                }
                                while (reader.MoveToNextAttribute());
                                reader.MoveToElement();
                            }
                            if (!empty)
                            {
                                if (parent != null)
                                {
                                    appendForLoad(parent, element, doc);
                                }
                                parent = element;
                                continue;
                            }
                            if (reader.SchemaInfo != null)
                            {
                                throw new NotSupportedException("schema info");
                            }
                            node = element;
                            break;
                        }
                        case XmlNodeType.EndElement:
                            if (parent == null)
                            {
                                throw new NotSupportedException("end element without a start");
                            }
                            if (reader.SchemaInfo != null)
                            {
                                throw new NotSupportedException("schema info");
                            }
                            if (parent.ParentNode == null)
                            {
                                reader.Read();
                                return parent;
                            }
                            parent = parent.ParentNode;
                            continue;
                        case XmlNodeType.Text:
                            node = doc.CreateTextNode(reader.Value);
                            break;
                        case XmlNodeType.SignificantWhitespace:
                            node = doc.CreateSignificantWhitespace(reader.Value);
                            break;
                        case XmlNodeType.Whitespace:
                            node = doc.CreateWhitespace(reader.Value);
                            break;
                        case XmlNodeType.CDATA:
                            node = doc.CreateCDataSection(reader.Value);
                            break;
                        case XmlNodeType.ProcessingInstruction:
                            node = doc.CreateProcessingInstruction(reader.Name, reader.Value);
                            break;
                        case XmlNodeType.Comment:
                            node = doc.CreateComment(reader.Value);
                            break;
                        default:
                            throw new NotSupportedException(reader.NodeType.ToString());
                    }
                    if (parent != null)
                    {
                        appendForLoad(parent, node, doc);
                        continue;
                    }
                    reader.Read();
                    return node;
                }
                while (reader.Read());
                throw new XmlException("unexpected end of a cached def");
            }

            private XmlAttribute LoadAttribute(XmlReader reader)
            {
                if (reader.IsDefault)
                {
                    throw new NotSupportedException("default attribute");
                }
                var attribute = newAttribute(Name(attributeNames, addAttrXmlName, reader), doc);
                if (reader.SchemaInfo != null)
                {
                    throw new NotSupportedException("schema info");
                }
                while (reader.ReadAttributeValue())
                {
                    if (reader.NodeType != XmlNodeType.Text)
                    {
                        throw new NotSupportedException(reader.NodeType + " in an attribute");
                    }
                    appendForLoad(attribute, doc.CreateTextNode(reader.Value), doc);
                }
                return attribute;
            }

            private object Name(Dictionary<NameKey, object> cache, Func<XmlDocument, string, string, string, object> add, XmlReader reader)
            {
                var key = new NameKey(reader.Prefix, reader.LocalName, reader.NamespaceURI);
                if (!cache.TryGetValue(key, out var name))
                {
                    lock (names)
                    {
                        name = add(doc, key.Prefix, key.LocalName, key.Namespace);
                    }
                    cache.Add(key, name);
                }
                return name;
            }
        }

        // Names of one reader come from its own name table, so the same name is the same string object.
        private readonly struct NameKey : IEquatable<NameKey>
        {
            public readonly string Prefix, LocalName, Namespace;

            public NameKey(string prefix, string localName, string ns)
            {
                Prefix = prefix;
                LocalName = localName;
                Namespace = ns;
            }

            public bool Equals(NameKey other) => (object)Prefix == other.Prefix && (object)LocalName == other.LocalName && (object)Namespace == other.Namespace;

            public override bool Equals(object obj) => obj is NameKey other && Equals(other);

            public override int GetHashCode() => RuntimeHelpers.GetHashCode(LocalName) ^ (RuntimeHelpers.GetHashCode(Prefix) * 31) ^ (RuntimeHelpers.GetHashCode(Namespace) * 17);
        }

        private sealed class SliceStream : Stream
        {
            private readonly SafeFileHandle handle;
            private readonly long end;
            private long position;

            public SliceStream(SafeFileHandle handle, long start, long end)
            {
                this.handle = handle;
                position = start;
                this.end = end;
            }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                var count = (int)Math.Min(buffer.Length, end - position);
                if (count <= 0)
                {
                    return 0;
                }
                var read = RandomAccess.Read(handle, buffer.Slice(0, count), position);
                if (read <= 0)
                {
                    throw new IOException("the cache file got shorter while it was read");
                }
                position += read;
                return read;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
#endif
