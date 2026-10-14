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
        private static string skippedNote = "";

        private static bool missileGirl;

        public static void Install(Harmony harmony, bool parallelXmlLoader)
        {
            GagarinFast.harmony = harmony;
            missileGirl = RLOMod.Settings.Supports("missilegirl");
            reusePatches = !parallelXmlLoader && Fingerprint.Matches("Reuse of parsed patch files",
                Fingerprint.Known(AccessTools.Method(typeof(ModContentPack), "LoadPatches"), "7BD200C227994829", "38D8E170612E6A8C"),
#if RW17
                Fingerprint.Known(AccessTools.Method(typeof(DirectXmlLoader), nameof(DirectXmlLoader.XmlAssetsInModFolder)), "443A7DDA2E4D9183", "B562C17CC83C679E", "760A5C052131CDB0", "C466BF8CB37814BD"));
#else
                Fingerprint.Known(AccessTools.Method(typeof(DirectXmlLoader), nameof(DirectXmlLoader.XmlAssetsInModFolder)), "443A7DDA2E4D9183", "B562C17CC83C679E"));
#endif
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
            if (!Fingerprint.Matches("Faster Missile Girl cache load", Fingerprint.Known(loadMethod, "A45EE8A2551B3B1D", "42AFA8C7AEFCE6D8")))
            {
                skippedNote = "gagarin cache loader changed";
                return;
            }
            var code = PatchProcessor.GetOriginalInstructions(loadMethod);
            if (code.Count(i => i.Calls(DocumentLoad)) != 1 || code.Count(i => i.Calls(ImportNode)) != 1 || code.Count(i => i.opcode == OpCodes.Newobj && Equals(i.operand, AssetConstructor)) != 1)
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
            harmony.Patch(loadMethod, prefix: new HarmonyMethod(typeof(GagarinFast), nameof(LoadStart)), postfix: new HarmonyMethod(typeof(GagarinFast), nameof(LoadEnd)),
                transpiler: new HarmonyMethod(typeof(GagarinFast), nameof(LoadTranspiler)));
        }

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
            reader.MoveToContent();
            readRoots.Remove(cache);
            readRoots.Add(cache, (XmlElement)target.ReadNode(reader));
        }

        public static XmlElement DocumentElementOf(XmlDocument document) => readRoots.TryGetValue(document, out var root) ? root : document.DocumentElement;

        public static XmlNode Adopt(XmlDocument target, XmlNode node, bool deep)
        {
            if (bypass || node.OwnerDocument != target)
            {
                return target.ImportNode(node, deep);
            }
            node.ParentNode?.RemoveChild(node);
            MarkEmpty(node);
            return node;
        }

        public static LoadableXmlAsset CacheAsset(FileInfo file, ModContentPack mod) => bypass || dummyAssetNeeded ? new LoadableXmlAsset(file, mod) : null;

        public static void LoadStart(out Stopwatch __state)
        {
            __state = bypass ? null : Stopwatch.StartNew();
        }

        public static void LoadEnd(XmlDocument document, Dictionary<XmlNode, LoadableXmlAsset> assets, Stopwatch __state)
        {
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
                if (fastDef.OuterXml != slowDef.OuterXml || fastAsset != slowAsset)
                {
                    loadMismatches++;
                    if (loadMismatches <= 5)
                    {
                        Log.Warning($"[RLO] gagarin load mismatch at def {i}: {fastDef.Name}");
                    }
                }
            }
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
            if (bypass || mod == null || foldersToLoadDebug != null || folderPath != "Patches/" || loadingPatchesOf != mod)
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
            if (RLOMod.Verify)
            {
                VerifyPatches(mod, stored);
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

        private static void VerifyPatches(ModContentPack mod, LoadableXmlAsset[] stored)
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
            if (!same)
            {
                reuseMismatches++;
                Log.Warning($"[RLO] reused patch files differ for {mod.PackageId}");
            }
        }

        public static string Stats()
        {
            if (!installed)
            {
                return "Missile Girl cache helpers: off";
            }
            return $"Missile Girl cache loads: {loads} in {loadSeconds:F2} s, {loadMismatches} verify mismatches{(skippedNote.Length > 0 ? " (" + skippedNote + ")" : "")}\npatch files parsed once and reused within this start (not Missile Girl's cache) for {reused} of {collected} mods ({dropped} unused), {reuseMismatches} verify mismatches";
        }
    }
}
