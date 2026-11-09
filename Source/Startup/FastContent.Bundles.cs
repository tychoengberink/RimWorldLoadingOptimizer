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
    public static partial class FastContent
    {
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
        private static SelfCheck bundleCheck;
        private static Dictionary<string, Texture2D> bundleCache;
        private static AccessTools.FieldRef<int> bundleCacheStamp;
        private static Func<string, Texture2D> uncached;
        private static List<BundleMod> bundleMods;
        private static int[] bundleCounts;
        private static HashSet<string> bundleItems;
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
                RLOLog.Detail("Fast content bundle lookups: a member it needs is missing. This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            var assetNames = AccessTools.PropertyGetter(Reader, "AssetNames");
            var lookup = AccessTools.Method(Reader, "Lookup");
            var load = AccessTools.Method(Reader, "Load");
            var add = AccessTools.Method(Reader, "Add");
            if (BundlesCached == null || BundlesUncached == null || BundlesStamp == null || BundleLoad == null || BundleNames == null || assetNames == null || lookup == null || load == null || add == null)
            {
                VersionCheck.Skip("Fast content bundle lookups", "a method it needs no longer exists");
                return;
            }
            bundleCheck = new SelfCheck("Fast content bundle lookups", VersionCheck.Code(
                BundlesCached,
                BundlesUncached,
                BundlesStamp,
                BundleLoad,
                BundleNames,
                assetNames,
                lookup,
                load,
                add));
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
                RLOLog.Detail($"Fast content bundle lookups: could not reach the bundle cache ({e.Message}). This speedup is skipped and the original code runs; nothing is broken.");
            }
        }

        // ContentFinder<Texture2D>.TryFindAssetInModBundles with the same cache and stamp, around the faster search.
        private static Texture2D NativeBundles(string itemPath)
        {
            if (bundleCache == null || bundleCheck.IsOff || itemPath == null || !UnityData.IsInMainThread || !BundlesUnpatched())
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
            var found = InSomeBundle(itemPath) ? SearchBundles(itemPath) : null;
            if (DevSwitches.Verify || bundleCheck.IsChecking)
            {
                var original = uncached(itemPath);
                var identical = ReferenceEquals(found, original);
                if (!identical)
                {
                    Mismatches.Report($"bundles {itemPath}");
                    found = original;
                }
                bundleCheck.Report(identical, $"bundles {itemPath}");
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
            bundleItems = IndexBundleItems(list);
            return true;
        }

        private static HashSet<string> IndexBundleItems(List<BundleMod> mods)
        {
            var items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var extensions = ModAssetBundlesHandler.TextureExtensions;
            foreach (var entry in mods)
            {
                var prefixes = entry.ViaPackage ? new[] { Prefix(entry.ByFolder), Prefix(entry.ByPackage) } : new[] { Prefix(entry.ByFolder) };
                foreach (var bundle in entry.Mod.assetBundles.loadedAssetBundles)
                {
                    var names = NamesOf(bundle);
                    if (names == null)
                    {
                        return null;
                    }
                    foreach (var name in names)
                    {
                        var normalized = name.Replace('\\', '/');
                        foreach (var prefix in prefixes)
                        {
                            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            foreach (var extension in extensions)
                            {
                                if (normalized.Length >= prefix.Length + extension.Length && normalized.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                                {
                                    items.Add(normalized.Substring(prefix.Length, normalized.Length - prefix.Length - extension.Length));
                                }
                            }
                        }
                    }
                }
            }
            return items;
        }

        private static string Prefix(string folder)
        {
            var combined = Path.Combine(folder, "x");
            return combined.Substring(0, combined.Length - 1).Replace('\\', '/');
        }

        private static bool InSomeBundle(string itemPath)
        {
            if (bundleItems == null || itemPath.Length == 0 || Path.IsPathRooted(itemPath))
            {
                return true;
            }
            if (bundleItems.Contains(itemPath.Replace('\\', '/')))
            {
                return true;
            }
            SkippedSearches++;
            return false;
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
    }
}
