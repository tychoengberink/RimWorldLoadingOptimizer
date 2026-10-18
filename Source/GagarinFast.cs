using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Xml;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class GagarinFast
    {
        private static Harmony harmony;
        private static bool reusePatches, installed, loadPatched;
        private static MethodInfo loadMethod;
        private static bool dummyAssetNeeded;
        [ThreadStatic] private static bool bypass;
        [ThreadStatic] private static ModContentPack loadingPatchesOf;
        private static bool collecting;
        private static readonly Dictionary<ModContentPack, LoadableXmlAsset[]> parsedPatches = new Dictionary<ModContentPack, LoadableXmlAsset[]>();
        private static int loads, loadMismatches, reused, reuseMismatches, collected, dropped;
        private static double loadSeconds;
#if RW17
        private static bool parallelRead;
        private static int parallelLoads, parallelFallbacks;
        private static double parallelSeconds;
        private static string parallelNote = "";
        [ThreadStatic] private static string pendingPath;
        [ThreadStatic] private static XmlElement premarkedRoot;
#endif
        private static string skippedNote = "";

        private static bool missileGirl;

        private static SelfCheck patchCheck;

        public static void Install(Harmony harmony, bool parallelXmlLoader)
        {
            GagarinFast.harmony = harmony;
            missileGirl = RLOMod.Settings.Supports("missilegirl");
            reusePatches = !parallelXmlLoader && AccessTools.Method(typeof(ModContentPack), "LoadPatches") != null && AccessTools.Method(typeof(DirectXmlLoader), nameof(DirectXmlLoader.XmlAssetsInModFolder)) != null;
            if (reusePatches)
            {
                patchCheck = new SelfCheck("Reuse of parsed patch files", VersionCheck.Code(
                    AccessTools.Method(typeof(ModContentPack), "LoadPatches"),
                    AccessTools.Method(typeof(DirectXmlLoader), nameof(DirectXmlLoader.XmlAssetsInModFolder))), 20);
            }
            var loadModXml = AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.LoadModXML));
            if (parallelXmlLoader)
            {
                if (missileGirl)
                {
                    harmony.Patch(loadModXml, postfix: new HarmonyMethod(typeof(GagarinFast), nameof(LoadGagarinOnce)) { priority = Priority.First });
                }
            }
            else
            {
                harmony.Patch(loadModXml,
                    prefix: new HarmonyMethod(typeof(GagarinFast), nameof(LoadModXMLPrefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(GagarinFast), nameof(LoadModXMLPostfix)) { priority = Priority.Last });
            }
            if (reusePatches)
            {
                harmony.Patch(AccessTools.Method(typeof(ModContentPack), "LoadPatches"),
                    prefix: new HarmonyMethod(typeof(GagarinFast), nameof(LoadPatchesPrefix)),
                    finalizer: new HarmonyMethod(typeof(GagarinFast), nameof(LoadPatchesFinalizer)));
                harmony.Patch(AccessTools.Method(typeof(DirectXmlLoader), nameof(DirectXmlLoader.XmlAssetsInModFolder)),
                    prefix: new HarmonyMethod(typeof(GagarinFast), nameof(AssetsPrefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(GagarinFast), nameof(AssetsPostfix)) { priority = Priority.Last });
                harmony.Patch(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.ErrorCheckPatches)),
                    postfix: new HarmonyMethod(typeof(GagarinFast), nameof(DropUnused)));
            }
            installed = true;
        }

        public static void LoadModXMLPrefix()
        {
            collecting = reusePatches;
            LoadGagarinOnce();
        }

        public static void LoadGagarinOnce()
        {
            if (loadPatched || !missileGirl)
            {
                return;
            }
            loadPatched = true;
            try
            {
                PatchGagarin();
            }
            catch (Exception e)
            {
                Log.Warning("[RLO] gagarin cache load left unchanged: " + e);
            }
        }

        public static void LoadModXMLPostfix()
        {
            collecting = false;
        }

        private static void PatchGagarin()
        {
            var helper = AccessTools.TypeByName("Gagarin.CachedDefHelper");
            if (helper == null)
            {
                skippedNote = "gagarin absent";
                return;
            }
            loadMethod = AccessTools.Method(helper, "Load", new[] { typeof(XmlDocument), typeof(Dictionary<XmlNode, LoadableXmlAsset>) });
            if (loadMethod == null)
            {
                skippedNote = "gagarin members changed";
                return;
            }
            var code = PatchProcessor.GetOriginalInstructions(loadMethod);
            var assetAt = code.FindIndex(i => i.opcode == OpCodes.Newobj && Equals(i.operand, AssetConstructor));
            if (code.Count(i => i.Calls(DocumentLoad)) != 1 || code.Count(i => i.Calls(ImportNode)) != 1 || code.Count(i => i.opcode == OpCodes.Newobj && Equals(i.operand, AssetConstructor)) != 1
                || assetAt + 1 >= code.Count || code[assetAt + 1].opcode != OpCodes.Pop)
            {
                skippedNote = "gagarin cache loader reads its cache another way";
                return;
            }
            var info = Harmony.GetPatchInfo(AssetConstructor);
            var foreign = info == null ? new List<Patch>() : info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
                .Where(p => p.PatchMethod.DeclaringType?.Assembly != helper.Assembly).ToList();
            dummyAssetNeeded = foreign.Count > 0;
            if (dummyAssetNeeded)
            {
                skippedNote = "unused cache asset still built (patched by " + string.Join(", ", foreign.Select(p => p.owner)) + ")";
            }
#if RW17
            parallelRead = ParallelReadUsable(code);
#endif
            harmony.Patch(loadMethod, prefix: new HarmonyMethod(typeof(GagarinFast), nameof(LoadStart)), postfix: new HarmonyMethod(typeof(GagarinFast), nameof(LoadEnd)),
                transpiler: new HarmonyMethod(typeof(GagarinFast), nameof(LoadTranspiler)));
        }

#if RW17
        private static readonly MethodInfo ReadAllText = AccessTools.Method(typeof(File), nameof(File.ReadAllText), new[] { typeof(string) });

        // Reading the cache on several threads (GagarinParallelRead) builds the nodes the way XmlDocument.ReadNode
        // does, so the System.Xml code it stands in for, and the code it relies on being safe from several threads
        // on one document, has to be the one it was checked against.
        private const string ParallelFeature = "Missile Girl cache read on several threads";
        private static SelfCheck parallelCheck;
        private static int parallelChecks;

        private static bool ParallelReadUsable(List<CodeInstruction> code)
        {
            if (code.Count(i => i.Calls(ReadAllText)) != 1)
            {
                parallelNote = "Missile Girl reads its cache file another way";
                return false;
            }
            var xml = typeof(XmlDocument).Assembly;
            var loader = xml.GetType("System.Xml.XmlLoader");
            var xmlName = xml.GetType("System.Xml.XmlName");
            if (loader == null || xmlName == null)
            {
                parallelNote = "System.Xml members changed";
                return false;
            }
            var s = new[] { typeof(string) };
            var shared = new (MethodBase method, string calls)[]
            {
                (AccessTools.Constructor(typeof(XmlElement), new[] { xmlName, typeof(bool), typeof(XmlDocument) }), "ArgumentException..ctor,SR.get_Xdom_Empty_LocalName,String.get_Length,XmlDocument.CheckName,XmlDocument.CheckName,XmlDocument.get_IsLoading,XmlLinkedNode..ctor,XmlName.get_LocalName,XmlName.get_LocalName,XmlName.get_Prefix"),
                (AccessTools.Constructor(typeof(XmlAttribute), new[] { xmlName, typeof(XmlDocument) }), "ArgumentException..ctor,SR.get_Xdom_Attr_Name,String.get_Length,XmlDocument.CheckName,XmlDocument.CheckName,XmlDocument.get_IsLoading,XmlName.get_LocalName,XmlName.get_LocalName,XmlName.get_Prefix,XmlNode..ctor"),
                (AccessTools.Method(typeof(XmlNode), "AppendChildForLoad"), "XmlDocument.GetInsertEventArgsForLoad,XmlNode.AfterEvent,XmlNode.BeforeEvent,XmlNode.NestTextNodes,XmlNode.SetParentForLoad,XmlNode.SetParentForLoad,XmlNode.get_IsText,XmlNode.get_IsText,XmlNode.get_LastNode,XmlNode.set_LastNode,XmlNode.set_LastNode"),
                (AccessTools.Method(typeof(XmlElement), "AppendChildForLoad"), "XmlDocument.GetInsertEventArgsForLoad,XmlNode.AfterEvent,XmlNode.BeforeEvent,XmlNode.NestTextNodes,XmlNode.SetParentForLoad,XmlNode.SetParentForLoad,XmlNode.get_IsText,XmlNode.get_IsText"),
                (AccessTools.Method(typeof(XmlAttribute), "AppendChildForLoad"), "XmlDocument.GetInsertEventArgsForLoad,XmlNode.AfterEvent,XmlNode.BeforeEvent,XmlNode.NestTextNodes,XmlNode.SetParentForLoad,XmlNode.SetParentForLoad,XmlNode.get_IsText,XmlNode.get_IsText"),
                (AccessTools.Method(typeof(XmlDocument), nameof(XmlDocument.CreateTextNode), s), "XmlText..ctor"),
                (AccessTools.Method(typeof(XmlDocument), nameof(XmlDocument.CreateSignificantWhitespace), s), "XmlSignificantWhitespace..ctor"),
                (AccessTools.Method(typeof(XmlDocument), nameof(XmlDocument.CreateWhitespace), s), "XmlWhitespace..ctor"),
                (AccessTools.Method(typeof(XmlDocument), nameof(XmlDocument.CreateCDataSection), s), "XmlCDataSection..ctor"),
                (AccessTools.Method(typeof(XmlDocument), nameof(XmlDocument.CreateComment), s), "XmlComment..ctor"),
                (AccessTools.Method(typeof(XmlDocument), nameof(XmlDocument.CreateProcessingInstruction), new[] { typeof(string), typeof(string) }), "ArgumentNullException.ThrowIfNull,XmlProcessingInstruction..ctor"),
                (AccessTools.Method(typeof(XmlDocument), "AddXmlName"), "DomNameTable.AddName"),
                (AccessTools.Method(typeof(XmlDocument), "AddAttrXmlName"), "ArgumentException..ctor,SR.Format,SR.get_Xdom_Attr_Reserved_XmlNS,String.get_Length,XmlDocument.AddXmlName,XmlDocument.get_IsLoading,XmlName.get_LocalName,XmlName.get_NamespaceURI,XmlName.get_Prefix,XmlName.get_Prefix"),
                (AccessTools.Method(typeof(XmlDocument), "GetEventArgs"), "XmlChildKeyIndex.OnMutating,XmlNodeChangedEventArgs..ctor"),
                (AccessTools.Method(typeof(XmlDocument), "GetInsertEventArgsForLoad"), "XmlNode.get_Value,XmlNodeChangedEventArgs..ctor"),
                (AccessTools.PropertyGetter(typeof(XmlElement), nameof(XmlElement.Attributes)), "Monitor.Enter,Monitor.Exit,XmlAttributeCollection..ctor,XmlNode.get_OwnerDocument"),
                (AccessTools.Method(typeof(XmlAttributeCollection), nameof(XmlAttributeCollection.Append), new[] { typeof(XmlAttribute) }), "ArgumentException..ctor,SR.get_Xdom_NamedNode_Context,XmlAttribute.get_OwnerElement,XmlAttributeCollection.Detach,XmlAttributeCollection.InsertParentIntoElementIdAttrMap,XmlDocument.get_IsLoading,XmlNamedNodeMap.AddNode,XmlNamedNodeMap.AddNodeForLoad,XmlNode.get_OwnerDocument,XmlNode.get_OwnerDocument"),
                (AccessTools.Method(typeof(XmlAttributeCollection), "AddNode", new[] { typeof(XmlNode) }), "XmlAttributeCollection.InsertParentIntoElementIdAttrMap,XmlAttributeCollection.RemoveDuplicateAttribute,XmlNamedNodeMap.AddNode"),
                (AccessTools.Method(typeof(XmlAttributeCollection), "InsertParentIntoElementIdAttrMap"), "String.op_Equality,String.op_Equality,XmlAttribute.get_XmlName,XmlAttribute.get_XmlName,XmlDocument.AddElementWithId,XmlDocument.GetIDInfoByElement,XmlElement.get_XmlName,XmlName.get_LocalName,XmlName.get_LocalName,XmlName.get_Prefix,XmlName.get_Prefix,XmlNode.get_OwnerDocument,XmlNode.get_OwnerDocument,XmlNode.get_OwnerDocument,XmlNode.get_Value"),
                (AccessTools.Method(typeof(XmlNamedNodeMap), "AddNode", new[] { typeof(XmlNode) }), "SmallXmlNodeList.Add,XmlAttribute.get_OwnerElement,XmlNode.AfterEvent,XmlNode.BeforeEvent,XmlNode.GetEventArgs,XmlNode.SetParent,XmlNode.get_NodeType,XmlNode.get_ParentNode,XmlNode.get_Value"),
                (AccessTools.Method(typeof(XmlNamedNodeMap), "AddNodeForLoad"), "SmallXmlNodeList.Add,XmlDocument.GetInsertEventArgsForLoad,XmlNode.AfterEvent,XmlNode.BeforeEvent,XmlNode.SetParent")
            };
            foreach (var (method, calls) in shared)
            {
                if (!VersionCheck.Pattern(ParallelFeature, method, code => VersionCheck.CallsExactly(code, calls), "makes exactly the calls the read on several threads was checked against"))
                {
                    parallelNote = "System.Xml code it uses from several threads changed";
                    return false;
                }
            }
            var reproduced = new MethodBase[]
            {
                ReadAllText,
                AccessTools.Method(typeof(File), nameof(File.ReadAllText), new[] { typeof(string), typeof(System.Text.Encoding) }),
                AccessTools.Method(typeof(XmlDocument), nameof(XmlDocument.ReadNode), new[] { typeof(XmlReader) }),
                AccessTools.Method(loader, "ReadCurrentNode", new[] { typeof(XmlDocument), typeof(XmlReader) }),
                AccessTools.Method(loader, "LoadNode", new[] { typeof(bool) }),
                AccessTools.Method(loader, "LoadAttributeNode", Type.EmptyTypes),
                AccessTools.Method(typeof(XmlDocument), nameof(XmlDocument.CreateElement), new[] { typeof(string), typeof(string), typeof(string) }),
                AccessTools.Method(typeof(XmlDocument), nameof(XmlDocument.CreateAttribute), new[] { typeof(string), typeof(string), typeof(string) }),
            };
            parallelCheck = new SelfCheck(ParallelFeature, VersionCheck.Code(reproduced.Concat(shared.Select(t => t.method)).ToArray()), 1);
            if (parallelCheck.IsOff)
            {
                parallelNote = "a System.Xml method it needs no longer exists";
                return false;
            }
            var reason = GagarinParallelRead.Init();
            if (reason != null)
            {
                parallelNote = reason;
                return false;
            }
            return true;
        }

        // Missile Girl reads the whole cache into one string first; with the read on several threads the file is
        // opened by GagarinParallelRead instead and Missile Girl's reader gets nothing to read.
        public static string ReadCacheText(string path)
        {
            if (bypass || !parallelRead || !File.Exists(path))
            {
                return File.ReadAllText(path);
            }
            pendingPath = path;
            return string.Empty;
        }
#endif

        private static readonly MethodInfo DocumentLoad = AccessTools.Method(typeof(XmlDocument), nameof(XmlDocument.Load), new[] { typeof(XmlReader) });
        private static readonly MethodInfo ImportNode = AccessTools.Method(typeof(XmlDocument), nameof(XmlDocument.ImportNode), new[] { typeof(XmlNode), typeof(bool) });
        private static readonly MethodInfo DocumentElementGetter = AccessTools.PropertyGetter(typeof(XmlDocument), nameof(XmlDocument.DocumentElement));
        private static readonly ConstructorInfo AssetConstructor = AccessTools.Constructor(typeof(LoadableXmlAsset), new[] { typeof(FileInfo), typeof(ModContentPack) });
        private static readonly ConditionalWeakTable<XmlDocument, XmlElement> readRoots = new ConditionalWeakTable<XmlDocument, XmlElement>();

        public static IEnumerable<CodeInstruction> LoadTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(DocumentLoad))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0).WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GagarinFast), nameof(ReadInto)));
                }
                else if (instruction.Calls(ImportNode))
                {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GagarinFast), nameof(Adopt))).WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                }
                else if (instruction.Calls(DocumentElementGetter))
                {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GagarinFast), nameof(DocumentElementOf))).WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                }
#if RW17
                else if (parallelRead && instruction.Calls(ReadAllText))
                {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GagarinFast), nameof(ReadCacheText))).WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                }
#endif
                else if (instruction.opcode == OpCodes.Newobj && Equals(instruction.operand, AssetConstructor))
                {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GagarinFast), nameof(CacheAsset))).WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                }
                else
                {
                    yield return instruction;
                }
            }
        }

        public static void ReadInto(XmlDocument cache, XmlReader reader, XmlDocument target)
        {
            if (bypass)
            {
                cache.Load(reader);
                return;
            }
#if RW17
            premarkedRoot = null;
            var path = pendingPath;
            pendingPath = null;
            if (path != null)
            {
                readRoots.Remove(cache);
                readRoots.Add(cache, parallelCheck.IsChecking ? CheckedRead(path, reader.Settings, target) : ReadParallel(path, reader.Settings, target));
                return;
            }
#endif
            reader.MoveToContent();
            readRoots.Remove(cache);
            readRoots.Add(cache, (XmlElement)target.ReadNode(reader));
        }

#if RW17
        // The parallel reader does what these do without calling them, so a Harmony patch on one of them (which
        // would see every node of the cache) leaves the read to the original code.
        private static string PatchedStandIn()
        {
            var xml = typeof(XmlDocument).Assembly;
            var loader = xml.GetType("System.Xml.XmlLoader");
            var xmlName = xml.GetType("System.Xml.XmlName");
            if (loader == null || xmlName == null)
            {
                return "System.Xml members changed";
            }
            var s = new[] { typeof(string) };
            var methods = new MethodBase[]
            {
                ReadAllText,
                AccessTools.Method(typeof(File), nameof(File.ReadAllText), new[] { typeof(string), typeof(System.Text.Encoding) }),
                AccessTools.Method(typeof(XmlDocument), nameof(XmlDocument.ReadNode), new[] { typeof(XmlReader) }),
                AccessTools.Method(loader, "ReadCurrentNode", new[] { typeof(XmlDocument), typeof(XmlReader) }),
                AccessTools.Method(loader, "LoadNode", new[] { typeof(bool) }),
                AccessTools.Method(loader, "LoadAttributeNode", Type.EmptyTypes),
                AccessTools.Method(typeof(XmlDocument), nameof(XmlDocument.CreateElement), new[] { typeof(string), typeof(string), typeof(string) }),
                AccessTools.Method(typeof(XmlDocument), nameof(XmlDocument.CreateAttribute), new[] { typeof(string), typeof(string), typeof(string) }),
                AccessTools.Method(typeof(XmlDocument), "AddXmlName"),
                AccessTools.Method(typeof(XmlDocument), "AddAttrXmlName"),
                AccessTools.Method(typeof(XmlDocument), nameof(XmlDocument.CreateTextNode), s),
                AccessTools.Method(typeof(XmlDocument), nameof(XmlDocument.CreateCDataSection), s),
                AccessTools.Method(typeof(XmlDocument), nameof(XmlDocument.CreateSignificantWhitespace), s),
                AccessTools.Method(typeof(XmlDocument), nameof(XmlDocument.CreateWhitespace), s),
                AccessTools.Method(typeof(XmlDocument), nameof(XmlDocument.CreateComment), s),
                AccessTools.Method(typeof(XmlDocument), nameof(XmlDocument.CreateProcessingInstruction), new[] { typeof(string), typeof(string) }),
                AccessTools.Constructor(typeof(XmlElement), new[] { xmlName, typeof(bool), typeof(XmlDocument) }),
                AccessTools.Constructor(typeof(XmlAttribute), new[] { xmlName, typeof(XmlDocument) }),
                AccessTools.PropertySetter(typeof(XmlElement), nameof(XmlElement.IsEmpty)),
                AccessTools.Method(typeof(XmlAttributeCollection), nameof(XmlAttributeCollection.Append), new[] { typeof(XmlAttribute) }),
                AccessTools.Method(typeof(XmlNode), "AppendChildForLoad"),
                AccessTools.Method(typeof(XmlElement), "AppendChildForLoad"),
                AccessTools.Method(typeof(XmlAttribute), "AppendChildForLoad"),
            };
            foreach (var method in methods)
            {
                if (method == null)
                {
                    return "System.Xml members changed";
                }
                var info = Harmony.GetPatchInfo(method);
                if (info != null && info.Owners.Count > 0)
                {
                    return $"{method.DeclaringType?.Name}.{method.Name} is patched by {string.Join(", ", info.Owners)}";
                }
            }
            return null;
        }

        private static XmlElement ReadParallel(string path, XmlReaderSettings settings, XmlDocument target)
        {
            var watch = Stopwatch.StartNew();
            var skip = PatchedStandIn();
            var root = skip == null ? GagarinParallelRead.TryRead(target, path, settings, Math.Min(6, Math.Max(1, Environment.ProcessorCount - 2))) : null;
            if (skip != null)
            {
                GagarinParallelRead.LastFallback = skip;
            }
            if (root != null)
            {
                parallelLoads++;
                parallelSeconds += watch.Elapsed.TotalSeconds;
                premarkedRoot = root;
                return root;
            }
            parallelFallbacks++;
            Log.Message("[RLO] Missile Girl cache read on several threads: left to the original reader (" + GagarinParallelRead.LastFallback + ")");
            using (var input = new StringReader(File.ReadAllText(path)))
            using (var original = XmlReader.Create(input, settings))
            {
                original.MoveToContent();
                return (XmlElement)target.ReadNode(original);
            }
        }
#endif

#if RW17
        private static XmlElement CheckedRead(string path, XmlReaderSettings settings, XmlDocument target)
        {
            var fast = ReadParallel(path, settings, new XmlDocument());
            var parallel = premarkedRoot != null;
            premarkedRoot = null;
            XmlElement slow;
            using (var input = new StringReader(File.ReadAllText(path)))
            using (var original = XmlReader.Create(input, settings))
            {
                original.MoveToContent();
                slow = (XmlElement)target.ReadNode(original);
            }
            if (parallel)
            {
                parallelChecks++;
                parallelCheck.Report(fast.OuterXml == slow.OuterXml && SameShape(fast, slow), "the read cache differs from Missile Girl's own read");
            }
            return slow;
        }
#endif

        public static XmlElement DocumentElementOf(XmlDocument document) => readRoots.TryGetValue(document, out var root) ? root : document.DocumentElement;

        public static XmlNode Adopt(XmlDocument target, XmlNode node, bool deep)
        {
            if (bypass || node.OwnerDocument != target)
            {
                return target.ImportNode(node, deep);
            }
#if RW17
            var premarked = GagarinParallelRead.Premarked(node, premarkedRoot);
#else
            var premarked = false;
#endif
            node.ParentNode?.RemoveChild(node);
            if (!premarked)
            {
                MarkEmpty(node);
            }
            return node;
        }

        public static LoadableXmlAsset CacheAsset(FileInfo file, ModContentPack mod) => bypass || dummyAssetNeeded ? new LoadableXmlAsset(file, mod) : null;

        public static void LoadStart(out Stopwatch __state)
        {
            __state = bypass ? null : Stopwatch.StartNew();
        }

        public static void LoadEnd(XmlDocument document, Dictionary<XmlNode, LoadableXmlAsset> assets, Stopwatch __state)
        {
#if RW17
            if (!bypass)
            {
                premarkedRoot = null;
                pendingPath = null;
            }
#endif
            if (__state == null)
            {
                return;
            }
            loads++;
            loadSeconds += __state.Elapsed.TotalSeconds;
            if (RLOMod.Verify)
            {
                VerifyLoad(document, assets);
            }
        }

        private static void MarkEmpty(XmlNode root)
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

        private static void VerifyLoad(XmlDocument document, Dictionary<XmlNode, LoadableXmlAsset> assets)
        {
            var original = new XmlDocument();
            var originalAssets = new Dictionary<XmlNode, LoadableXmlAsset>();
            bypass = true;
            try
            {
                loadMethod.Invoke(null, new object[] { original, originalAssets });
            }
            finally
            {
                bypass = false;
            }
            var fast = document.DocumentElement.ChildNodes;
            var slow = original.DocumentElement.ChildNodes;
            if (document.ChildNodes.Count != original.ChildNodes.Count || fast.Count != slow.Count || assets.Count != originalAssets.Count)
            {
                loadMismatches++;
                Log.Warning($"[RLO] gagarin load shape mismatch: {document.ChildNodes.Count}/{original.ChildNodes.Count} roots, {fast.Count}/{slow.Count} defs, {assets.Count}/{originalAssets.Count} assets");
                return;
            }
            var fastDef = document.DocumentElement.FirstChild;
            var slowDef = original.DocumentElement.FirstChild;
            for (var i = 0; fastDef != null; i++, fastDef = fastDef.NextSibling, slowDef = slowDef.NextSibling)
            {
                assets.TryGetValue(fastDef, out var fastAsset);
                originalAssets.TryGetValue(slowDef, out var slowAsset);
                if (fastDef.OuterXml != slowDef.OuterXml || fastAsset != slowAsset || !SameShape(fastDef, slowDef))
                {
                    loadMismatches++;
                    if (loadMismatches <= 5)
                    {
                        Log.Warning($"[RLO] gagarin load mismatch at def {i}: {fastDef.Name}");
                    }
                }
            }
        }

        // What OuterXml does not show: node kinds (text, CDATA, whitespace), how text nodes are split, empty
        // elements and attribute order.
        private static bool SameShape(XmlNode fast, XmlNode slow)
        {
            var stack = new Stack<(XmlNode, XmlNode)>();
            stack.Push((fast, slow));
            while (stack.Count > 0)
            {
                var (a, b) = stack.Pop();
                if (a.NodeType != b.NodeType || a.Name != b.Name || a.NamespaceURI != b.NamespaceURI || a.Value != b.Value || a.ChildNodes.Count != b.ChildNodes.Count
                    || (a is XmlElement element && element.IsEmpty != ((XmlElement)b).IsEmpty) || (a.Attributes?.Count ?? 0) != (b.Attributes?.Count ?? 0))
                {
                    return false;
                }
                for (var i = 0; i < (a.Attributes?.Count ?? 0); i++)
                {
                    stack.Push((a.Attributes[i], b.Attributes[i]));
                }
                for (XmlNode x = a.FirstChild, y = b.FirstChild; x != null; x = x.NextSibling, y = y.NextSibling)
                {
                    stack.Push((x, y));
                }
            }
            return true;
        }

        public static void DropUnused()
        {
            lock (parsedPatches)
            {
                dropped += parsedPatches.Count;
                parsedPatches.Clear();
            }
        }

        public static void LoadPatchesPrefix(ModContentPack __instance)
        {
            loadingPatchesOf = __instance;
        }

        public static void LoadPatchesFinalizer(Exception __exception)
        {
            loadingPatchesOf = null;
        }

        public static bool AssetsPrefix(ModContentPack mod, string folderPath, List<string> foldersToLoadDebug, ref LoadableXmlAsset[] __result)
        {
            if (bypass || mod == null || foldersToLoadDebug != null || folderPath != "Patches/" || loadingPatchesOf != mod || patchCheck.IsOff)
            {
                return true;
            }
            LoadableXmlAsset[] stored;
            lock (parsedPatches)
            {
                if (!parsedPatches.TryGetValue(mod, out stored))
                {
                    return true;
                }
                parsedPatches.Remove(mod);
            }
            __result = stored;
            reused++;
            if (RLOMod.Verify || patchCheck.IsChecking)
            {
                __result = VerifyPatches(mod, stored);
            }
            return false;
        }

        public static void AssetsPostfix(ModContentPack mod, string folderPath, List<string> foldersToLoadDebug, LoadableXmlAsset[] __result)
        {
            if (!collecting || bypass || mod == null || foldersToLoadDebug != null || folderPath != "Patches/" || __result == null)
            {
                return;
            }
            lock (parsedPatches)
            {
                parsedPatches[mod] = __result;
                collected++;
            }
        }

        private static LoadableXmlAsset[] VerifyPatches(ModContentPack mod, LoadableXmlAsset[] stored)
        {
            LoadableXmlAsset[] fresh;
            bypass = true;
            try
            {
                fresh = DirectXmlLoader.XmlAssetsInModFolder(mod, "Patches/");
            }
            finally
            {
                bypass = false;
            }
            var same = fresh.Length == stored.Length;
            for (var i = 0; same && i < fresh.Length; i++)
            {
                same = fresh[i].FullFilePath == stored[i].FullFilePath && fresh[i].mod == stored[i].mod
                    && (fresh[i].xmlDoc == null) == (stored[i].xmlDoc == null)
                    && (fresh[i].xmlDoc == null || fresh[i].xmlDoc.OuterXml == stored[i].xmlDoc.OuterXml);
            }
            patchCheck.Report(same, $"patch files of {mod.PackageId}");
            if (!same)
            {
                reuseMismatches++;
                Log.Warning($"[RLO] reused patch files differ for {mod.PackageId}");
                return fresh;
            }
            return stored;
        }

        public static string Stats()
        {
            if (!installed)
            {
                return "Missile Girl cache helpers: off";
            }
#if RW17
            var parallel = parallelRead
                ? $"; read on {GagarinParallelRead.Threads} threads ({GagarinParallelRead.Chunks} chunks, {GagarinParallelRead.Nodes} items) {parallelLoads} times in {parallelSeconds:F2} s, {parallelFallbacks} left to the original reader, {parallelChecks} compared with Missile Girl's own read"
                : "; read on one thread" + (parallelNote.Length > 0 ? " (" + parallelNote + ")" : "");
#else
            var parallel = "";
#endif
            return $"Missile Girl cache loads: {loads} in {loadSeconds:F2} s{parallel}, {loadMismatches} verify mismatches{(skippedNote.Length > 0 ? " (" + skippedNote + ")" : "")}\npatch files parsed once and reused within this start (not Missile Girl's cache) for {reused} of {collected} mods ({dropped} unused), {reuseMismatches} verify mismatches";
        }
    }
}
