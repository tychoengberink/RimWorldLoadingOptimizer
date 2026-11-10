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
        private static readonly MethodInfo OriginalGet = AccessTools.Method(
            typeof(ContentFinder<Texture2D>),
            nameof(ContentFinder<Texture2D>.Get)
        );
        private static readonly MethodInfo OriginalFolder = AccessTools.Method(
            typeof(ContentFinder<Texture2D>),
            nameof(ContentFinder<Texture2D>.GetAllInFolder)
        );
        private static readonly MethodInfo OriginalAudioFolder = AccessTools.Method(
            typeof(ContentFinder<AudioClip>),
            nameof(ContentFinder<AudioClip>.GetAllInFolder)
        );
        private static readonly MethodInfo LookupGet = AccessTools.Method(
            typeof(TextureLookup),
            nameof(TextureLookup.Get)
        );

        // Which mods have content under a folder, so a folder lookup visits only those holders instead of all of them.
        // A holder is re-indexed when it or its entry count changes, the same signal the per-holder key lists used.
        private static class BundleFolders<T>
            where T : class
        {
            public struct Entry
            {
                public int Mod,
                    Bundle;
                public string Base;
            }

            private static readonly List<Entry> None = new List<Entry>();
            private static Dictionary<string, List<Entry>> bySuffix;
            private static long stamp = -1;
            public static int Fallbacks,
                Builds;

            public static string Suffix(string folderPath)
            {
                var lower = folderPath.ToLower();
                return lower.Length == 0 || lower[lower.Length - 1] == '/' ? lower : lower + "/";
            }

            private static long Stamp(List<ModContentPack> mods)
            {
                long sum = mods.Count;
                for (var i = 0; i < mods.Count; i++)
                {
                    sum = sum * 31 + mods[i].assetBundles.loadedAssetBundles.Count;
                }
                return sum;
            }

            public static List<Entry> Lookup(string folderPath)
            {
                if (folderPath == null || Path.IsPathRooted(folderPath))
                {
                    return null;
                }
                var mods = LoadedModManager.RunningModsListForReading;
                var current = Stamp(mods);
                if (current != stamp || bySuffix == null)
                {
                    bySuffix = Build(mods);
                    stamp = current;
                    Builds++;
                }
                if (bySuffix == null)
                {
                    return null;
                }
                return bySuffix.TryGetValue(Suffix(folderPath), out var list) ? list : None;
            }

            private static Dictionary<string, List<Entry>> Build(List<ModContentPack> mods)
            {
                var result = new Dictionary<string, List<Entry>>();
                var modsDir = Path.Combine("Assets", "Data");
                for (var i = mods.Count - 1; i >= 0; i--)
                {
                    var count = mods[i].assetBundles.loadedAssetBundles.Count;
                    if (count == 0)
                    {
                        continue;
                    }
                    var bases = new List<string>
                    {
                        Path.Combine(
                                Path.Combine(Path.Combine(modsDir, mods[i].FolderName), GenFilePaths.ContentPath<T>())
                                    .Replace('\\', '/'),
                                ""
                            )
                            .ToLower(),
                    };
                    if (!mods[i].IsOfficialMod)
                    {
                        bases.Add(
                            Path.Combine(
                                    Path.Combine(
                                            Path.Combine(modsDir, mods[i].PackageIdPlayerFacing),
                                            GenFilePaths.ContentPath<T>()
                                        )
                                        .Replace('\\', '/'),
                                    ""
                                )
                                .ToLower()
                        );
                    }
                    if (bases.Any(b => b.Length == 0 || b[b.Length - 1] != '/'))
                    {
                        return null;
                    }
                    for (var j = 0; j < count; j++)
                    {
                        foreach (var basePath in bases)
                        {
                            var suffixes = new HashSet<string>();
                            foreach (var name in mods[i].AllAssetNamesInBundle(j))
                            {
                                if (!name.StartsWith(basePath, StringComparison.Ordinal))
                                {
                                    continue;
                                }
                                for (var k = basePath.Length - 1; k < name.Length; k++)
                                {
                                    if (name[k] == '/')
                                    {
                                        suffixes.Add(name.Substring(basePath.Length, k + 1 - basePath.Length));
                                    }
                                }
                            }
                            foreach (var suffix in suffixes)
                            {
                                if (!result.TryGetValue(suffix, out var list))
                                {
                                    list = new List<Entry>();
                                    result[suffix] = list;
                                }
                                list.Add(
                                    new Entry
                                    {
                                        Mod = i,
                                        Bundle = j,
                                        Base = basePath,
                                    }
                                );
                            }
                        }
                    }
                }
                return result;
            }
        }

        private static class FolderIndex<T>
            where T : class
        {
            private static readonly List<int> None = new List<int>();
            private static readonly Dictionary<string, List<int>> byDir = new Dictionary<string, List<int>>(
                StringComparer.Ordinal
            );
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
                    var list =
                        prefix.NullOrEmpty() ? nonEmpty
                        : byDir.TryGetValue(prefix, out var found) ? found
                        : None;
                    var at = list.BinarySearch(from);
                    var candidate =
                        at >= 0 ? list[at]
                        : ~at < list.Count ? list[~at]
                        : -1;
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

        public static int Misses,
            Folders,
            SkippedHolders,
            Methods;
        public static readonly MismatchLog Mismatches = new MismatchLog("fast content");
#if NATIVE_ASSETS
        public static int BundleLookups,
            SkippedLoads,
            SkippedSearches;
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
            check = new SelfCheck(
                "Fast content lookups",
                VersionCheck.Code(
                    OriginalGet,
                    OriginalFolder,
                    AccessTools.EnumeratorMoveNext(OriginalFolder),
                    AccessTools.EnumeratorMoveNext(OriginalAudioFolder)
                )
            );
#if NATIVE_ASSETS
            InstallBundles();
#endif
            harmony.Patch(
                AccessTools.Method(typeof(ModContentPack), "ReloadContentInt"),
                postfix: new HarmonyMethod(typeof(FastContent), nameof(ForgetKeys))
            );
            harmony.Patch(
                AccessTools.Method(typeof(ModContentPack), nameof(ModContentPack.ClearDestroy)),
                postfix: new HarmonyMethod(typeof(FastContent), nameof(ForgetKeys))
            );
            redirectGet = textureLookup ? LookupGet : AccessTools.Method(typeof(FastContent), nameof(Get));
            if (textureLookup && LookupGet != null && OriginalGet != null)
            {
                harmony.Patch(LookupGet, transpiler: new HarmonyMethod(typeof(FastContent), nameof(MissTranspiler)));
                Methods++;
            }
            var folderTranspiler = new HarmonyMethod(typeof(FastContent), nameof(FolderTranspiler));
            var lookupTranspiler = new HarmonyMethod(typeof(FastContent), nameof(LookupTranspiler));
            var modGraphics = new List<Type>();
            foreach (
                var type in GenTypes
                    .AllSubclasses(typeof(Graphic))
                    .Concat(new[] { typeof(Graphic), typeof(RimWorld.ExpansionDef) })
            )
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
            foreach (
                var type in typeof(Verse.Sound.AudioGrain_Folder)
                    .GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
                    .Concat(new[] { typeof(Verse.Sound.AudioGrain_Folder) })
            )
            {
                PatchCallers(
                    harmony,
                    type,
                    OriginalAudioFolder,
                    new HarmonyMethod(typeof(FastContent), nameof(AudioTranspiler)),
                    null
                );
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
                    PatchCallers(
                        harmony,
                        type,
                        OriginalGet,
                        lookupTranspiler,
                        m => m.Name.Contains("<PostLoad>") || m.Name.Contains("<ResolveReferences>")
                    );
                }
            }
            StartupPhaseFixes.Reports.Add(Stats);
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

        private static void PatchCallers(
            Harmony harmony,
            Type type,
            MethodInfo target,
            HarmonyMethod transpiler,
            Func<MethodInfo, bool> filter
        )
        {
            if (!AssemblySafety.CanScan(type) || type.ContainsGenericParameters)
            {
                return;
            }
            foreach (
                var method in type.GetMethods(
                    BindingFlags.Instance
                        | BindingFlags.Static
                        | BindingFlags.Public
                        | BindingFlags.NonPublic
                        | BindingFlags.DeclaredOnly
                )
            )
            {
                if (
                    method.IsAbstract
                    || method.ContainsGenericParameters
                    || (filter != null && !filter(method))
                    || !IlScan.CallsAny(method, target)
                )
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
                    RLOLog.Warning($"fast content: could not patch {type.FullName}.{method.Name}: {e.Message}");
                }
            }
        }

        public static IEnumerable<CodeInstruction> MissTranspiler(IEnumerable<CodeInstruction> instructions) =>
            IlScan.Replace(instructions, OriginalGet, AccessTools.Method(typeof(FastContent), nameof(Get)));

        public static IEnumerable<CodeInstruction> FolderTranspiler(IEnumerable<CodeInstruction> instructions) =>
            IlScan.Replace(
                instructions,
                OriginalFolder,
                AccessTools.Method(typeof(FastContent), nameof(GetAllInFolder))
            );

        public static IEnumerable<CodeInstruction> AudioTranspiler(IEnumerable<CodeInstruction> instructions) =>
            IlScan.Replace(
                instructions,
                OriginalAudioFolder,
                AccessTools.Method(typeof(FastContent), nameof(GetAllInAudioFolder))
            );

        public static IEnumerable<CodeInstruction> LookupTranspiler(IEnumerable<CodeInstruction> instructions) =>
            IlScan.Replace(instructions, OriginalGet, redirectGet);

#if NATIVE_ASSETS
        public static string Stats() =>
            $"fast content: {Methods} methods, {Misses} misses resolved, {BundleLookups} bundle lookups ({SkippedSearches} answered from the bundle index, {SkippedLoads} bundle loads skipped), {Folders} folder lookups ({SkippedHolders} holders skipped), {Mismatches.Count} verify mismatches";
#else
        public static string Stats() =>
            $"fast content: {Methods} methods, {Misses} misses resolved, {Folders} folder lookups ({SkippedHolders} holders skipped), {Mismatches.Count} verify mismatches";
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
            if (DevSwitches.Verify || check.IsChecking)
            {
                var expected = ContentFinder<Texture2D>.Get(itemPath, false);
                var same = ReferenceEquals(result, expected);
                if (!same)
                {
                    Mismatches.Report($"get {itemPath}");
                    result = expected;
                }
                check.Report(same, $"texture {itemPath}");
            }
            if ((object)result == null && reportFailure)
            {
                var text =
                    ContentFinderRequester.requester != null
                        ? " for def '" + ContentFinderRequester.requester.defName + "'"
                        : "";
                Log.Error(
                    "Could not load "
                        + typeof(Texture2D).Name
                        + " at '"
                        + itemPath
                        + "'"
                        + text
                        + " in any active mod or in base resources."
                );
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
                    var folderAssetPath = Path.Combine(
                        Path.Combine(byFolder, GenFilePaths.ContentPath<Texture2D>()),
                        itemPath
                    );
                    var packageAssetPath = Path.Combine(
                        Path.Combine(byPackage, GenFilePaths.ContentPath<Texture2D>()),
                        itemPath
                    );
                    foreach (var extension in extensions)
                    {
                        if (
                            bundle.LoadAsset(folderAssetPath + extension, typeof(Texture2D))
                            is Texture2D byFolderTexture
                        )
                        {
                            return byFolderTexture;
                        }
                        if (
                            viaPackage
                            && bundle.LoadAsset(packageAssetPath + extension, typeof(Texture2D))
                                is Texture2D byPackageTexture
                        )
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
            if (!DevSwitches.Verify && !check.IsChecking)
            {
                return Folder(folderPath);
            }
            var fast = Folder(folderPath).ToList();
            var original = ContentFinder<Texture2D>.GetAllInFolder(folderPath).ToList();
            var same = fast.SequenceEqual(original);
            if (!same)
            {
                Mismatches.Report($"folder {folderPath}");
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
            if (!DevSwitches.Verify && !check.IsChecking)
            {
                return Folder<AudioClip>(folderPath, ModAssetBundlesHandler.AudioClipExtensions);
            }
            var fast = Folder<AudioClip>(folderPath, ModAssetBundlesHandler.AudioClipExtensions).ToList();
            var original = ContentFinder<AudioClip>.GetAllInFolder(folderPath).ToList();
            var same = fast.SequenceEqual(original);
            if (!same)
            {
                Mismatches.Report($"audio folder {folderPath}");
            }
            check.Report(same, $"audio folder {folderPath}");
            return same ? fast : original;
        }

        private static IEnumerable<Texture2D> Folder(string folderPath) =>
            Folder<Texture2D>(folderPath, ModAssetBundlesHandler.TextureExtensions);

        private static IEnumerable<T> Folder<T>(string folderPath, string[] validExtensions)
            where T : UnityEngine.Object
        {
            var prefix =
                folderPath.NullOrEmpty() || folderPath[folderPath.Length - 1] == '/' ? folderPath : folderPath + "/";
            var running = LoadedModManager.RunningModsListForReading;
            for (var last = -1; ; )
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
            var bundles = BundleFolders<T>.Lookup(folderPath);
            if (bundles != null)
            {
                var running2 = LoadedModManager.RunningModsListForReading;
                var suffix = BundleFolders<T>.Suffix(folderPath);
                foreach (var entry in bundles)
                {
                    var assetBundle = running2[entry.Mod].assetBundles.loadedAssetBundles[entry.Bundle];
                    foreach (
                        var item in running2[entry.Mod]
                            .AllAssetNamesInBundleTrie(entry.Bundle)
                            .GetByPrefix(entry.Base + suffix)
                    )
                    {
                        if (validExtensions.Contains(Path.GetExtension(item)))
                        {
                            yield return (T)assetBundle.LoadAsset(item, typeof(T));
                        }
                    }
                }
                yield break;
            }
            BundleFolders<T>.Fallbacks++;
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
                    var withFolderName = Path.Combine(
                            Path.Combine(dirForBundleWithFolderName, GenFilePaths.ContentPath<T>()).Replace('\\', '/'),
                            folderPath
                        )
                        .ToLower();
                    var withPackageId = Path.Combine(
                            Path.Combine(dirForBundleWithPackageId, GenFilePaths.ContentPath<T>()).Replace('\\', '/'),
                            folderPath
                        )
                        .ToLower();
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
    }
}
