using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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
        private static Func<string> unifiedPath;
        private static Func<Dictionary<string, LoadableXmlAsset>> xmlAssets;
        private static Func<ModContentPack> core;
        private static bool dummyAssetNeeded;
        [ThreadStatic] private static bool bypass;
        [ThreadStatic] private static ModContentPack loadingPatchesOf;
        private static bool collecting;
        private static readonly Dictionary<ModContentPack, LoadableXmlAsset[]> parsedPatches = new Dictionary<ModContentPack, LoadableXmlAsset[]>();
        private static int loads, loadMismatches, reused, reuseMismatches, collected, dropped, pruned;
        private static double loadSeconds;
        private static string skippedNote = "";

        public static void Install(Harmony harmony)
        {
            GagarinFast.harmony = harmony;
            reusePatches = Fingerprint.Matches("Reuse of parsed patch files",
                Fingerprint.Known(AccessTools.Method(typeof(ModContentPack), "LoadPatches"), "7BD200C227994829", "38D8E170612E6A8C"),
                Fingerprint.Known(AccessTools.Method(typeof(DirectXmlLoader), nameof(DirectXmlLoader.XmlAssetsInModFolder)), "443A7DDA2E4D9183", "B562C17CC83C679E"));
            harmony.Patch(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.LoadModXML)),
                prefix: new HarmonyMethod(typeof(GagarinFast), nameof(LoadModXMLPrefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(GagarinFast), nameof(LoadModXMLPostfix)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.LoadModXML)),
                postfix: new HarmonyMethod(typeof(GagarinFast), nameof(PruneStaleHashes)) { priority = Priority.First });
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
            if (loadPatched)
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

        public static void PruneStaleHashes()
        {
            var context = AccessTools.TypeByName("Gagarin.Context");
            var usingCache = context == null ? null : AccessTools.PropertyGetter(context, "IsUsingCache");
            if (usingCache == null || (bool)usingCache.Invoke(null, null)
                || !(AccessTools.Field(context, "Assets")?.GetValue(null) is HashSet<string> assets)
                || !(AccessTools.Field(context, "AssetsHashes")?.GetValue(null) is Dictionary<string, string> hashes)
                || !(AccessTools.Field(context, "AssetsHashesInt")?.GetValue(null) is Dictionary<string, ulong> hashesInt))
            {
                return;
            }
            lock (hashes)
            {
                foreach (var key in hashes.Keys.Where(k => !assets.Contains(k)).ToList())
                {
                    hashes.Remove(key);
                    pruned++;
                }
                foreach (var key in hashesInt.Keys.Where(k => !assets.Contains(k)).ToList())
                {
                    hashesInt.Remove(key);
                }
            }
        }

        private static void PatchGagarin()
        {
            var helper = AccessTools.TypeByName("Gagarin.CachedDefHelper");
            var context = AccessTools.TypeByName("Gagarin.Context");
            var env = AccessTools.TypeByName("Gagarin.GagarinEnvironmentInfo");
            if (helper == null || context == null || env == null)
            {
                skippedNote = "gagarin absent";
                return;
            }
            loadMethod = AccessTools.Method(helper, "Load", new[] { typeof(XmlDocument), typeof(Dictionary<XmlNode, LoadableXmlAsset>) });
            var pathGetter = AccessTools.PropertyGetter(env, "UnifiedXmlFilePath");
            var assetsField = AccessTools.Field(context, "XmlAssets");
            var coreField = AccessTools.Field(context, "Core");
            if (loadMethod == null || pathGetter == null || assetsField == null || coreField == null)
            {
                skippedNote = "gagarin members changed";
                return;
            }
            if (!Fingerprint.Matches("Faster Missile Girl cache load", Fingerprint.Known(loadMethod, "A45EE8A2551B3B1D", "42AFA8C7AEFCE6D8")))
            {
                skippedNote = "gagarin cache loader changed";
                return;
            }
            unifiedPath = () => (string)pathGetter.Invoke(null, null);
            xmlAssets = () => (Dictionary<string, LoadableXmlAsset>)assetsField.GetValue(null);
            core = () => (ModContentPack)coreField.GetValue(null);
            var ctor = AccessTools.Constructor(typeof(LoadableXmlAsset), new[] { typeof(FileInfo), typeof(ModContentPack) });
            var info = Harmony.GetPatchInfo(ctor);
            var foreign = info == null ? new List<Patch>() : info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
                .Where(p => p.PatchMethod.DeclaringType?.Assembly != helper.Assembly).ToList();
            dummyAssetNeeded = foreign.Count > 0;
            if (dummyAssetNeeded)
            {
                skippedNote = "unused cache asset still built (patched by " + string.Join(", ", foreign.Select(p => p.owner)) + ")";
            }
            harmony.Patch(loadMethod, prefix: new HarmonyMethod(typeof(GagarinFast), nameof(LoadPrefix)));
        }

        public static bool LoadPrefix(XmlDocument document, Dictionary<XmlNode, LoadableXmlAsset> assets)
        {
            if (bypass)
            {
                return true;
            }
            var watch = Stopwatch.StartNew();
            var settings = new XmlReaderSettings
            {
                IgnoreComments = true,
                IgnoreWhitespace = true,
                CheckCharacters = false
            };
            var path = unifiedPath();
            using (var input = new StringReader(File.ReadAllText(path)))
            using (var reader = XmlReader.Create(input, settings))
            {
                if (dummyAssetNeeded)
                {
                    new LoadableXmlAsset(new FileInfo(path), core());
                }
                assets.Clear();
                document.RemoveAll();
                document.Load(reader);
                var storage = document.DocumentElement;
                document.RemoveAll();
                var defs = document.CreateElement("Defs");
                document.AppendChild(defs);
                var lookup = xmlAssets();
                var items = new List<XmlElement>(storage.ChildNodes.Count);
                foreach (XmlElement item in storage.ChildNodes)
                {
                    items.Add(item);
                }
                foreach (var item in items)
                {
                    var def = item.FirstChild;
                    if (def == null)
                    {
                        def = document.ImportNode(def, true);
                    }
                    item.RemoveChild(def);
                    MarkEmpty(def);
                    if (lookup.TryGetValue(item.GetAttribute("path"), out var asset))
                    {
                        assets[def] = asset;
                    }
                    defs.AppendChild(def);
                }
            }
            watch.Stop();
            loads++;
            loadSeconds += watch.Elapsed.TotalSeconds;
            if (Prefs.LogVerbose)
            {
                Log.Warning($"GAGARIN: <color=green>Loaded from cache!</color> Loading cache took <color=red>{watch.ElapsedMilliseconds / 1000} seconds</color>");
            }
            else
            {
                Log.Warning("GARGARIN: <color=green>Finished loading XML from cache!</color>");
            }
            if (RLOMod.Verify)
            {
                VerifyLoad(document, assets);
            }
            return false;
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
                return "gagarin: off";
            }
            return $"gagarin cache loads: {loads} in {loadSeconds:F2} s, {loadMismatches} verify mismatches{(skippedNote.Length > 0 ? " (" + skippedNote + ")" : "")}; patch files reused for {reused} of {collected} mods ({dropped} unused), {reuseMismatches} verify mismatches; {pruned} stale asset hashes pruned before the cache rebuild";
        }
    }
}
