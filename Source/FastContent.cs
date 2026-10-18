using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class FastContent
    {
        private static readonly MethodInfo OriginalGet = AccessTools.Method(typeof(ContentFinder<Texture2D>), nameof(ContentFinder<Texture2D>.Get));
        private static readonly MethodInfo OriginalFolder = AccessTools.Method(typeof(ContentFinder<Texture2D>), nameof(ContentFinder<Texture2D>.GetAllInFolder));
        private static readonly MethodInfo OriginalAudioFolder = AccessTools.Method(typeof(ContentFinder<AudioClip>), nameof(ContentFinder<AudioClip>.GetAllInFolder));
        private static readonly MethodInfo LookupGet = AccessTools.Method(typeof(TextureLookup), nameof(TextureLookup.Get));

        // Which mods have content under a folder, so a folder lookup visits only those holders instead of all of them.
        // A holder is re-indexed when it or its entry count changes, the same signal the per-holder key lists used.
        private static class FolderIndex<T> where T : class
        {
            private static readonly List<int> None = new List<int>();
            private static readonly Dictionary<string, List<int>> byDir = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            private static readonly List<int> nonEmpty = new List<int>();
            private static ModContentHolder<T>[] holders;
            private static int[] counts;
            private static string[][] dirs;

            public static void Forget()
            {
                byDir.Clear();
                nonEmpty.Clear();
                holders = null;
                counts = null;
                dirs = null;
            }

            // The first mod after 'after' whose holder has a key under prefix; every holder up to it is checked for changes first,
            // so a holder that changes while the caller enumerates is seen exactly when the plain mod loop would reach it.
            public static int Next(List<ModContentPack> mods, string prefix, int after)
            {
                if (holders == null || holders.Length != mods.Count)
                {
                    Forget();
                    holders = new ModContentHolder<T>[mods.Count];
                    counts = new int[mods.Count];
                    dirs = new string[mods.Count][];
                    for (var i = 0; i < mods.Count; i++)
                    {
                        Set(i, mods[i].GetContentHolder<T>());
                    }
                }
                var from = after + 1;
                while (true)
                {
                    var list = prefix.NullOrEmpty() ? nonEmpty : byDir.TryGetValue(prefix, out var found) ? found : None;
                    var at = list.BinarySearch(from);
                    var candidate = at >= 0 ? list[at] : ~at < list.Count ? list[~at] : -1;
                    var limit = candidate < 0 ? mods.Count - 1 : candidate;
                    var changed = -1;
                    for (var i = from; i <= limit; i++)
                    {
                        var holder = mods[i].GetContentHolder<T>();
                        if (!ReferenceEquals(holder, holders[i]) || holder.contentList.Count != counts[i])
                        {
                            Unset(i);
                            Set(i, holder);
                            changed = i;
                            break;
                        }
                    }
                    if (changed < 0)
                    {
                        return candidate;
                    }
                    from = changed;
                }
            }

            private static void Set(int mod, ModContentHolder<T> holder)
            {
                holders[mod] = holder;
                counts[mod] = holder.contentList.Count;
                if (counts[mod] == 0)
                {
                    dirs[mod] = new string[0];
                    return;
                }
                Insert(nonEmpty, mod);
                dirs[mod] = DirsOf(holder.contentList.Keys);
                foreach (var dir in dirs[mod])
                {
                    if (!byDir.TryGetValue(dir, out var list))
                    {
                        byDir[dir] = list = new List<int>();
                    }
                    Insert(list, mod);
                }
            }

            private static void Unset(int mod)
            {
                if (counts[mod] != 0)
                {
                    Remove(nonEmpty, mod);
                }
                foreach (var dir in dirs[mod] ?? new string[0])
                {
                    if (byDir.TryGetValue(dir, out var list))
                    {
                        Remove(list, mod);
                        if (list.Count == 0)
                        {
                            byDir.Remove(dir);
                        }
                    }
                }
            }

            private static void Insert(List<int> list, int mod)
            {
                var at = list.BinarySearch(mod);
                if (at < 0)
                {
                    list.Insert(~at, mod);
                }
            }

            private static void Remove(List<int> list, int mod)
            {
                var at = list.BinarySearch(mod);
                if (at >= 0)
                {
                    list.RemoveAt(at);
                }
            }
        }

        // Every folder a key sits in, cut after each '/': a prefix that ends in '/' starts a key exactly when it is one of these.
        private static string[] DirsOf(IEnumerable<string> keys)
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            string last = null;
            var lastLength = -1;
            foreach (var key in keys)
            {
                var end = key.LastIndexOf('/');
                if (end < 0 || (end + 1 == lastLength && string.CompareOrdinal(key, 0, last, 0, lastLength) == 0))
                {
                    continue;
                }
                last = key;
                lastLength = end + 1;
                for (var at = end; at >= 0; at = at > 0 ? key.LastIndexOf('/', at - 1) : -1)
                {
                    if (!found.Add(key.Substring(0, at + 1)))
                    {
                        break;
                    }
                }
            }
            return found.ToArray();
        }

        public static int Misses, Folders, SkippedHolders, Mismatches, Methods;
#if NATIVE_ASSETS
        public static int BundleLookups, SkippedLoads;
#endif

        public static bool Installed { get; private set; }

        private static MethodInfo redirectGet;
        private static SelfCheck check;

        public static void Install(Harmony harmony, bool textureLookup)
        {
            if (OriginalGet == null || OriginalFolder == null || OriginalAudioFolder == null)
            {
                VersionCheck.Skip("Fast content lookups", "a ContentFinder method it replaces no longer exists");
                return;
            }
            check = new SelfCheck("Fast content lookups", VersionCheck.Known(
#if RW17
                Fingerprint.Known(OriginalGet, "D4D313BA56F77956", "B802294668DE64B1"),
#else
                Fingerprint.Known(OriginalGet, "D4D313BA56F77956"),
#endif
#if RW17
                Fingerprint.Known(OriginalFolder, "989773FE5AF81641", "1BF1D81A23779287"),
#else
                Fingerprint.Known(OriginalFolder, "989773FE5AF81641"),
#endif
#if RW17
                Fingerprint.Known(AccessTools.EnumeratorMoveNext(OriginalFolder), "96B68A4A067048DD", "698878502D9AFD33"),
#else
                Fingerprint.Known(AccessTools.EnumeratorMoveNext(OriginalFolder), "96B68A4A067048DD"),
#endif
#if RW17
                Fingerprint.Known(AccessTools.EnumeratorMoveNext(OriginalAudioFolder), "96B68A4A067048DD", "79A23DF611EA101D")));
#else
                Fingerprint.Known(AccessTools.EnumeratorMoveNext(OriginalAudioFolder), "96B68A4A067048DD")));
#endif
#if NATIVE_ASSETS
            InstallBundles();
#endif
            harmony.Patch(AccessTools.Method(typeof(ModContentPack), "ReloadContentInt"), postfix: new HarmonyMethod(typeof(FastContent), nameof(ForgetKeys)));
            harmony.Patch(AccessTools.Method(typeof(ModContentPack), nameof(ModContentPack.ClearDestroy)), postfix: new HarmonyMethod(typeof(FastContent), nameof(ForgetKeys)));
            redirectGet = textureLookup ? LookupGet : AccessTools.Method(typeof(FastContent), nameof(Get));
            if (textureLookup && LookupGet != null && OriginalGet != null)
            {
                harmony.Patch(LookupGet, transpiler: new HarmonyMethod(typeof(FastContent), nameof(MissTranspiler)));
                Methods++;
            }
            var folderTranspiler = new HarmonyMethod(typeof(FastContent), nameof(FolderTranspiler));
            var lookupTranspiler = new HarmonyMethod(typeof(FastContent), nameof(LookupTranspiler));
            var modGraphics = new List<Type>();
            foreach (var type in GenTypes.AllSubclasses(typeof(Graphic)).Concat(new[] { typeof(Graphic), typeof(RimWorld.ExpansionDef) }))
            {
                if (type.Assembly == typeof(Graphic).Assembly)
                {
                    PatchCallers(harmony, type, OriginalFolder, folderTranspiler, null);
                }
                else
                {
                    modGraphics.Add(type);
                }
            }
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                foreach (var type in modGraphics)
                {
                    PatchCallers(harmony, type, OriginalFolder, folderTranspiler, null);
                }
            });
            foreach (var type in typeof(Verse.Sound.AudioGrain_Folder).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public).Concat(new[] { typeof(Verse.Sound.AudioGrain_Folder) }))
            {
                PatchCallers(harmony, type, OriginalAudioFolder, new HarmonyMethod(typeof(FastContent), nameof(AudioTranspiler)), null);
            }
            foreach (var type in typeof(Def).Assembly.GetTypes())
            {
                var outer = type;
                while (outer.DeclaringType != null)
                {
                    outer = outer.DeclaringType;
                }
                if (outer == typeof(MaterialPool) || outer == typeof(ShaderParameter))
                {
                    PatchCallers(harmony, type, OriginalGet, lookupTranspiler, null);
                }
                else if (typeof(Def).IsAssignableFrom(outer))
                {
                    PatchCallers(harmony, type, OriginalGet, lookupTranspiler, m => m.Name.Contains("<PostLoad>") || m.Name.Contains("<ResolveReferences>"));
                }
            }
            LeanFixes.Reports.Add(Stats);
            Installed = true;
        }

        public static void ForgetKeys()
        {
            FolderIndex<Texture2D>.Forget();
            FolderIndex<AudioClip>.Forget();
#if NATIVE_ASSETS
            bundleNames.Clear();
            bundleMods = null;
#endif
        }

        private static void PatchCallers(Harmony harmony, Type type, MethodInfo target, HarmonyMethod transpiler, Func<MethodInfo, bool> filter)
        {
            if (!AssemblySafety.CanScan(type) || type.ContainsGenericParameters)
            {
                return;
            }
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.IsAbstract || method.ContainsGenericParameters || (filter != null && !filter(method)) || !IlScan.CallsAny(method, target))
                {
                    continue;
                }
                try
                {
                    harmony.Patch(method, transpiler: transpiler);
                    Methods++;
                }
                catch (Exception e)
                {
                    Log.Warning($"[RLO] fast content: could not patch {type.FullName}.{method.Name}: {e.Message}");
                }
            }
        }


        public static IEnumerable<CodeInstruction> MissTranspiler(IEnumerable<CodeInstruction> instructions) =>
            IlScan.Replace(instructions, OriginalGet, AccessTools.Method(typeof(FastContent), nameof(Get)));

        public static IEnumerable<CodeInstruction> FolderTranspiler(IEnumerable<CodeInstruction> instructions) =>
            IlScan.Replace(instructions, OriginalFolder, AccessTools.Method(typeof(FastContent), nameof(GetAllInFolder)));

        public static IEnumerable<CodeInstruction> AudioTranspiler(IEnumerable<CodeInstruction> instructions) =>
            IlScan.Replace(instructions, OriginalAudioFolder, AccessTools.Method(typeof(FastContent), nameof(GetAllInAudioFolder)));

        public static IEnumerable<CodeInstruction> LookupTranspiler(IEnumerable<CodeInstruction> instructions) =>
            IlScan.Replace(instructions, OriginalGet, redirectGet);

#if NATIVE_ASSETS
        public static string Stats() => $"fast content: {Methods} methods, {Misses} misses resolved, {BundleLookups} bundle lookups ({SkippedLoads} bundle loads skipped), {Folders} folder lookups ({SkippedHolders} holders skipped), {Mismatches} verify mismatches";
#else
        public static string Stats() => $"fast content: {Methods} methods, {Misses} misses resolved, {Folders} folder lookups ({SkippedHolders} holders skipped), {Mismatches} verify mismatches";
#endif

        public static Texture2D Get(string itemPath, bool reportFailure)
        {
            if (check.IsOff || Compatibility.LazyTextures || !UnityData.IsInMainThread)
            {
                return ContentFinder<Texture2D>.Get(itemPath, reportFailure);
            }
            return Report(itemPath, reportFailure, Find(itemPath));
        }

        // For a caller that already knows no mod's content holder has a texture at this path (the texture lookup index).
        public static Texture2D GetOutsideHolders(string itemPath, bool reportFailure)
        {
            if (check.IsOff || Compatibility.LazyTextures || !UnityData.IsInMainThread)
            {
                return Get(itemPath, reportFailure);
            }
            return Report(itemPath, reportFailure, Outside(itemPath));
        }

        private static Texture2D Report(string itemPath, bool reportFailure, Texture2D result)
        {
            if (RLOMod.Verify || check.IsChecking)
            {
                var expected = ContentFinder<Texture2D>.Get(itemPath, false);
                var same = ReferenceEquals(result, expected);
                if (!same)
                {
                    Mismatch($"get {itemPath}");
                    result = expected;
                }
                check.Report(same, $"texture {itemPath}");
            }
            if ((object)result == null && reportFailure)
            {
                var text = ContentFinderRequester.requester != null ? " for def '" + ContentFinderRequester.requester.defName + "'" : "";
                Log.Error("Could not load " + typeof(Texture2D).Name + " at '" + itemPath + "'" + text + " in any active mod or in base resources.");
            }
            return result;
        }

        private static Texture2D Find(string itemPath)
        {
            var mods = LoadedModManager.RunningModsListForReading;
            for (var i = mods.Count - 1; i >= 0; i--)
            {
                var found = mods[i].GetContentHolder<Texture2D>().Get(itemPath);
                if ((object)found != null)
                {
                    return found;
                }
            }
            return Outside(itemPath);
        }

        private static Texture2D Outside(string itemPath)
        {
            Misses++;
            var loaded = Resources.Load<Texture2D>(GenFilePaths.ContentPath<Texture2D>() + itemPath);
            if ((object)loaded != null)
            {
                return loaded;
            }
#if NATIVE_ASSETS
            return NativeBundles(itemPath);
#else
            return Bundles(itemPath);
#endif
        }
#if NATIVE_ASSETS

        private static readonly MethodInfo BundlesCached = AccessTools.Method(typeof(ContentFinder<Texture2D>), nameof(ContentFinder<Texture2D>.TryFindAssetInModBundles));
        private static readonly MethodInfo BundlesUncached = AccessTools.Method(typeof(ContentFinder<Texture2D>), "TryFindAssetInModBundlesUncached");
        private static readonly MethodInfo BundlesStamp = AccessTools.Method(typeof(ContentFinder<Texture2D>), "LoadedAssetBundlesStamp");
        private static readonly MethodInfo BundleLoad = AccessTools.Method(typeof(AssetBundle), nameof(AssetBundle.LoadAsset), new[] { typeof(string), typeof(Type) });
        private static readonly MethodInfo BundleNames = AccessTools.Method(typeof(AssetBundle), nameof(AssetBundle.GetAllAssetNames));
        private static readonly Type Reader = typeof(AssetBundle).Assembly.GetType("UnityStubs.Internal.AssetBundleReader");

        private sealed class BundleMod
        {
            public ModContentPack Mod;
            public string ByFolder;
            public string ByPackage;
            public bool ViaPackage;
        }

        private static readonly Dictionary<AssetBundle, HashSet<string>> bundleNames = new Dictionary<AssetBundle, HashSet<string>>(ReferenceComparer<AssetBundle>.Instance);
        private static MethodBase[] bundleWatched;
        private static Dictionary<string, Texture2D> bundleCache;
        private static AccessTools.FieldRef<int> bundleCacheStamp;
        private static Func<string, Texture2D> uncached;
        private static List<BundleMod> bundleMods;
        private static int[] bundleCounts;
        private static string bundleContentPath;
        private static int bundlesCheckedFrame = -1;
        private static bool bundlesUnpatched;

        // ContentFinder.TryFindAssetInModBundlesUncached builds two paths per mod and asks every bundle of every mod for each
        // texture extension. A bundle only finds a name its reader holds (case-insensitively, as given or with '\' turned into '/'),
        // so a bundle is asked only for names in its own name list, and only mods that have bundles are visited, in the same order.
        private static void InstallBundles()
        {
            var cacheField = AccessTools.Field(typeof(ContentFinder<Texture2D>), "bundleAssetCache");
            var stampField = AccessTools.Field(typeof(ContentFinder<Texture2D>), "bundleAssetCacheStamp");
            if (Reader == null || cacheField == null || stampField == null)
            {
                Log.Message("[RLO] Fast content bundle lookups: a member it needs is missing. This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            var assetNames = AccessTools.PropertyGetter(Reader, "AssetNames");
            var lookup = AccessTools.Method(Reader, "Lookup");
            var load = AccessTools.Method(Reader, "Load");
            var add = AccessTools.Method(Reader, "Add");
            if (!Fingerprint.Matches("Fast content bundle lookups",
                Fingerprint.Known(BundlesCached, "31BEF324BCEEB3A3", "E53E3DCCD9A2EAA0", "E58242CDAC3C383B", "F5ACDB3CC0076413"),
                Fingerprint.Known(BundlesUncached, "CA4DDD75F0EB8D20", "ABE94861D20EE2B1", "53CDFA3625336D55"),
                Fingerprint.Known(BundlesStamp, "10FECC823644C13F", "3F2E6BE7758897E1", "1BB4D65FD5959F85"),
                Fingerprint.Known(BundleLoad, "511A977FFDB448DB", "37DAE16A13128E81", "793653BCDCD8C51D"),
                Fingerprint.Known(BundleNames, "7141E6725935DB6B", "A82143A1CC3605CB", "6F72022B71604E51"),
                Fingerprint.Known(assetNames, "0246209DC1CC3B49", "9DD1571FC99C751C", "E4767AA4EDB3FF12"),
                Fingerprint.Known(lookup, "2D103CB183F3338C", "F01FD9B607196604", "448BE879CCE10F89"),
                Fingerprint.Known(load, "14EB43EE93D96A98", "C30F690A23105717", "9BACF4C86AD5807A", "9DB1894E980D738C"),
                Fingerprint.Known(add, "FA1CBE72A67EDFB6", "FF1CD22A0E143850", "3B9D5A1F188FA5CF")))
            {
                return;
            }
            try
            {
                bundleCacheStamp = AccessTools.StaticFieldRefAccess<int>(stampField);
                uncached = AccessTools.MethodDelegate<Func<string, Texture2D>>(BundlesUncached);
                bundleWatched = new MethodBase[] { BundlesCached, BundlesUncached, BundlesStamp, BundleLoad, BundleNames, assetNames, lookup, load, add };
                bundleCache = (Dictionary<string, Texture2D>)cacheField.GetValue(null);
            }
            catch (Exception e)
            {
                bundleCache = null;
                Log.Message($"[RLO] Fast content bundle lookups: could not reach the bundle cache ({e.Message}). This speedup is skipped and the original code runs; nothing is broken.");
            }
        }

        // ContentFinder<Texture2D>.TryFindAssetInModBundles with the same cache and stamp, around the faster search.
        private static Texture2D NativeBundles(string itemPath)
        {
            if (bundleCache == null || itemPath == null || !UnityData.IsInMainThread || !BundlesUnpatched())
            {
                return ContentFinder<Texture2D>.TryFindAssetInModBundles(itemPath);
            }
            var mods = LoadedModManager.RunningModsListForReading;
            var stamp = mods.Count;
            var same = bundleMods != null && bundleCounts.Length == mods.Count;
            for (var i = 0; i < mods.Count; i++)
            {
                var count = mods[i].assetBundles.loadedAssetBundles.Count;
                stamp = unchecked(stamp * 31 + count);
                same = same && bundleCounts[i] == count;
            }
            var contentPath = GenFilePaths.ContentPath<Texture2D>();
            if ((!same || contentPath != bundleContentPath) && !IndexBundles(mods, contentPath))
            {
                return ContentFinder<Texture2D>.TryFindAssetInModBundles(itemPath);
            }
            if (stamp != bundleCacheStamp())
            {
                bundleCache.Clear();
                bundleCacheStamp() = stamp;
            }
            if (bundleCache.TryGetValue(itemPath, out var cached) && ((object)cached == null || cached != null))
            {
                return cached;
            }
            BundleLookups++;
            var found = SearchBundles(itemPath);
            if (RLOMod.Verify && !ReferenceEquals(found, uncached(itemPath)))
            {
                Mismatch($"bundles {itemPath}");
            }
            bundleCache[itemPath] = found;
            return found;
        }

        private static bool BundlesUnpatched()
        {
            var frame = Time.frameCount;
            if (frame != bundlesCheckedFrame)
            {
                bundlesCheckedFrame = frame;
                bundlesUnpatched = bundleWatched.All(Fingerprint.OnlyOurs);
            }
            return bundlesUnpatched;
        }

        private static bool IndexBundles(List<ModContentPack> mods, string contentPath)
        {
            bundleMods = null;
            var root = Path.Combine("Assets", "Data");
            var list = new List<BundleMod>();
            var counts = new int[mods.Count];
            for (var i = mods.Count - 1; i >= 0; i--)
            {
                var mod = mods[i];
                // The original combines both names for every mod and throws on a missing one; leave that to it.
                if (mod.FolderName == null || mod.PackageIdPlayerFacing == null || contentPath == null)
                {
                    return false;
                }
                counts[i] = mod.assetBundles.loadedAssetBundles.Count;
                if (counts[i] == 0)
                {
                    continue;
                }
                list.Add(new BundleMod
                {
                    Mod = mod,
                    ByFolder = Path.Combine(Path.Combine(root, mod.FolderName), contentPath),
                    ByPackage = Path.Combine(Path.Combine(root, mod.PackageIdPlayerFacing), contentPath),
                    ViaPackage = !mod.IsOfficialMod
                });
            }
            bundleMods = list;
            bundleCounts = counts;
            bundleContentPath = contentPath;
            return true;
        }

        private static Texture2D SearchBundles(string itemPath)
        {
            var extensions = ModAssetBundlesHandler.TextureExtensions;
            foreach (var entry in bundleMods)
            {
                var folderAssetPath = Path.Combine(entry.ByFolder, itemPath);
                var packageAssetPath = Path.Combine(entry.ByPackage, itemPath);
                foreach (var bundle in entry.Mod.assetBundles.loadedAssetBundles)
                {
                    var names = NamesOf(bundle);
                    foreach (var extension in extensions)
                    {
                        var byFolder = folderAssetPath + extension;
                        if (MayHold(names, byFolder) && bundle.LoadAsset(byFolder, typeof(Texture2D)) is Texture2D byFolderTexture)
                        {
                            return byFolderTexture;
                        }
                        if (!entry.ViaPackage)
                        {
                            continue;
                        }
                        var byPackage = packageAssetPath + extension;
                        if (MayHold(names, byPackage) && bundle.LoadAsset(byPackage, typeof(Texture2D)) is Texture2D byPackageTexture)
                        {
                            return byPackageTexture;
                        }
                    }
                }
            }
            return null;
        }

        private static HashSet<string> NamesOf(AssetBundle bundle)
        {
            if ((object)bundle == null)
            {
                return null;
            }
            if (!bundleNames.TryGetValue(bundle, out var names))
            {
                // The reader's name table is fixed once the bundle exists; an unloaded bundle lists nothing and loads nothing.
                bundleNames[bundle] = names = new HashSet<string>(bundle.GetAllAssetNames(), StringComparer.OrdinalIgnoreCase);
            }
            return names;
        }

        // False only when the bundle's reader cannot find this name: not as given, not with '\' as '/', and the name has a folder,
        // so the reader's bare file name fallback does not apply either.
        private static bool MayHold(HashSet<string> names, string name)
        {
            if (names == null || names.Contains(name))
            {
                return true;
            }
            var normalized = name.Replace('\\', '/');
            if (normalized.IndexOf('/') < 0 || (!ReferenceEquals(normalized, name) && names.Contains(normalized)))
            {
                return true;
            }
            SkippedLoads++;
            return false;
        }
#endif

        private static Texture2D Bundles(string itemPath)
        {
            var extensions = ModAssetBundlesHandler.TextureExtensions;
            var mods = LoadedModManager.RunningModsListForReading;
            var root = Path.Combine("Assets", "Data");
            for (var i = mods.Count - 1; i >= 0; i--)
            {
                var bundles = mods[i].assetBundles.loadedAssetBundles;
                if (bundles.Count == 0)
                {
                    continue;
                }
                var byFolder = Path.Combine(root, mods[i].FolderName);
                var byPackage = Path.Combine(root, mods[i].PackageIdPlayerFacing);
                var viaPackage = !mods[i].IsOfficialMod;
                foreach (var bundle in bundles)
                {
                    var folderAssetPath = Path.Combine(Path.Combine(byFolder, GenFilePaths.ContentPath<Texture2D>()), itemPath);
                    var packageAssetPath = Path.Combine(Path.Combine(byPackage, GenFilePaths.ContentPath<Texture2D>()), itemPath);
                    foreach (var extension in extensions)
                    {
                        if (bundle.LoadAsset(folderAssetPath + extension, typeof(Texture2D)) is Texture2D byFolderTexture)
                        {
                            return byFolderTexture;
                        }
                        if (viaPackage && bundle.LoadAsset(packageAssetPath + extension, typeof(Texture2D)) is Texture2D byPackageTexture)
                        {
                            return byPackageTexture;
                        }
                    }
                }
            }
            return null;
        }

        public static IEnumerable<Texture2D> GetAllInFolder(string folderPath)
        {
            if (check.IsOff || Compatibility.LazyTextures || !UnityData.IsInMainThread)
            {
                return ContentFinder<Texture2D>.GetAllInFolder(folderPath);
            }
            Folders++;
            if (!RLOMod.Verify && !check.IsChecking)
            {
                return Folder(folderPath);
            }
            var fast = Folder(folderPath).ToList();
            var original = ContentFinder<Texture2D>.GetAllInFolder(folderPath).ToList();
            var same = fast.SequenceEqual(original);
            if (!same)
            {
                Mismatch($"folder {folderPath}");
            }
            check.Report(same, $"folder {folderPath}");
            return same ? fast : original;
        }

        public static IEnumerable<AudioClip> GetAllInAudioFolder(string folderPath)
        {
            if (check.IsOff || !UnityData.IsInMainThread)
            {
                return ContentFinder<AudioClip>.GetAllInFolder(folderPath);
            }
            Folders++;
            if (!RLOMod.Verify && !check.IsChecking)
            {
                return Folder<AudioClip>(folderPath, ModAssetBundlesHandler.AudioClipExtensions);
            }
            var fast = Folder<AudioClip>(folderPath, ModAssetBundlesHandler.AudioClipExtensions).ToList();
            var original = ContentFinder<AudioClip>.GetAllInFolder(folderPath).ToList();
            var same = fast.SequenceEqual(original);
            if (!same)
            {
                Mismatch($"audio folder {folderPath}");
            }
            check.Report(same, $"audio folder {folderPath}");
            return same ? fast : original;
        }

        private static IEnumerable<Texture2D> Folder(string folderPath) => Folder<Texture2D>(folderPath, ModAssetBundlesHandler.TextureExtensions);

        private static IEnumerable<T> Folder<T>(string folderPath, string[] validExtensions) where T : UnityEngine.Object
        {
            var prefix = folderPath.NullOrEmpty() || folderPath[folderPath.Length - 1] == '/' ? folderPath : folderPath + "/";
            var running = LoadedModManager.RunningModsListForReading;
            for (var last = -1; ;)
            {
                var next = FolderIndex<T>.Next(running, prefix, last);
                SkippedHolders += (next < 0 ? running.Count : next) - last - 1;
                if (next < 0)
                {
                    break;
                }
                foreach (var item in running[next].GetContentHolder<T>().GetAllUnderPath(folderPath))
                {
                    yield return item;
                }
                last = next;
            }
            foreach (var item in Resources.LoadAll<T>(GenFilePaths.ContentPath<T>() + folderPath))
            {
                yield return item;
            }
            var mods = LoadedModManager.RunningModsListForReading;
            var modsDir = Path.Combine("Assets", "Data");
            for (var i = mods.Count - 1; i >= 0; i--)
            {
                if (mods[i].assetBundles.loadedAssetBundles.Count == 0)
                {
                    continue;
                }
                var dirForBundleWithFolderName = Path.Combine(modsDir, mods[i].FolderName);
                var dirForBundleWithPackageId = Path.Combine(modsDir, mods[i].PackageIdPlayerFacing);
                var canLoadViaPackageId = !mods[i].IsOfficialMod;
                for (var j = 0; j < mods[i].assetBundles.loadedAssetBundles.Count; j++)
                {
                    var assetBundle = mods[i].assetBundles.loadedAssetBundles[j];
                    var withFolderName = Path.Combine(Path.Combine(dirForBundleWithFolderName, GenFilePaths.ContentPath<T>()).Replace('\\', '/'), folderPath).ToLower();
                    var withPackageId = Path.Combine(Path.Combine(dirForBundleWithPackageId, GenFilePaths.ContentPath<T>()).Replace('\\', '/'), folderPath).ToLower();
                    if (withFolderName[withFolderName.Length - 1] != '/')
                    {
                        withFolderName += "/";
                    }
                    if (withPackageId[withPackageId.Length - 1] != '/')
                    {
                        withPackageId += "/";
                    }
                    foreach (var item in mods[i].AllAssetNamesInBundleTrie(j).GetByPrefix(withFolderName))
                    {
                        if (validExtensions.Contains(Path.GetExtension(item)))
                        {
                            yield return (T)assetBundle.LoadAsset(item, typeof(T));
                        }
                    }
                    if (!canLoadViaPackageId)
                    {
                        continue;
                    }
                    foreach (var item in mods[i].AllAssetNamesInBundleTrie(j).GetByPrefix(withPackageId))
                    {
                        if (validExtensions.Contains(Path.GetExtension(item)))
                        {
                            yield return (T)assetBundle.LoadAsset(item, typeof(T));
                        }
                    }
                }
            }
        }

        private static void Mismatch(string what)
        {
            if (Mismatches++ < 20)
            {
                Log.Warning($"[RLO] fast content verify mismatch: {what}");
            }
        }
    }
}
