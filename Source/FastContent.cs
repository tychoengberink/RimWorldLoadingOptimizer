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

        private sealed class Keys
        {
            public int Count;
            public string[] Sorted;
        }

        private static readonly Dictionary<object, Keys> keys = new Dictionary<object, Keys>();

        public static int Misses, Folders, SkippedHolders, Mismatches, Methods;

        private static MethodInfo redirectGet;

        public static void Install(Harmony harmony, bool textureLookup)
        {
            if (!Fingerprint.Matches("Fast content lookups",
                Fingerprint.Known(OriginalGet, "D4D313BA56F77956"),
                Fingerprint.Known(OriginalFolder, "989773FE5AF81641"),
                Fingerprint.Known(AccessTools.EnumeratorMoveNext(OriginalFolder), "96B68A4A067048DD"),
                Fingerprint.Known(AccessTools.EnumeratorMoveNext(OriginalAudioFolder), "96B68A4A067048DD")))
            {
                return;
            }
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
        }

        public static void ForgetKeys()
        {
            keys.Clear();
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

        public static string Stats() => $"fast content: {Methods} methods, {Misses} misses resolved, {Folders} folder lookups ({SkippedHolders} holders skipped), {Mismatches} verify mismatches";

        public static Texture2D Get(string itemPath, bool reportFailure)
        {
            if (Compatibility.LazyTextures || !UnityData.IsInMainThread)
            {
                return ContentFinder<Texture2D>.Get(itemPath, reportFailure);
            }
            var result = Find(itemPath);
            if (RLOMod.Verify && !ReferenceEquals(result, ContentFinder<Texture2D>.Get(itemPath, false)))
            {
                Mismatch($"get {itemPath}");
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
            Misses++;
            var loaded = Resources.Load<Texture2D>(GenFilePaths.ContentPath<Texture2D>() + itemPath);
            if ((object)loaded != null)
            {
                return loaded;
            }
            return Bundles(itemPath);
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
            if (Compatibility.LazyTextures || !UnityData.IsInMainThread)
            {
                return ContentFinder<Texture2D>.GetAllInFolder(folderPath);
            }
            Folders++;
            if (!RLOMod.Verify)
            {
                return Folder(folderPath);
            }
            var fast = Folder(folderPath).ToList();
            if (!fast.SequenceEqual(ContentFinder<Texture2D>.GetAllInFolder(folderPath)))
            {
                Mismatch($"folder {folderPath}");
            }
            return fast;
        }

        public static IEnumerable<AudioClip> GetAllInAudioFolder(string folderPath)
        {
            if (!UnityData.IsInMainThread)
            {
                return ContentFinder<AudioClip>.GetAllInFolder(folderPath);
            }
            Folders++;
            if (!RLOMod.Verify)
            {
                return Folder<AudioClip>(folderPath, ModAssetBundlesHandler.AudioClipExtensions);
            }
            var fast = Folder<AudioClip>(folderPath, ModAssetBundlesHandler.AudioClipExtensions).ToList();
            if (!fast.SequenceEqual(ContentFinder<AudioClip>.GetAllInFolder(folderPath)))
            {
                Mismatch($"audio folder {folderPath}");
            }
            return fast;
        }

        private static IEnumerable<Texture2D> Folder(string folderPath) => Folder<Texture2D>(folderPath, ModAssetBundlesHandler.TextureExtensions);

        private static IEnumerable<T> Folder<T>(string folderPath, string[] validExtensions) where T : UnityEngine.Object
        {
            var prefix = folderPath.NullOrEmpty() || folderPath[folderPath.Length - 1] == '/' ? folderPath : folderPath + "/";
            foreach (var mod in LoadedModManager.RunningModsListForReading)
            {
                var holder = mod.GetContentHolder<T>();
                if (!MayContain(holder, prefix))
                {
                    SkippedHolders++;
                    continue;
                }
                foreach (var item in holder.GetAllUnderPath(folderPath))
                {
                    yield return item;
                }
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

        private static bool MayContain<T>(ModContentHolder<T> holder, string prefix) where T : class
        {
            var count = holder.contentList.Count;
            if (count == 0)
            {
                return false;
            }
            if (prefix.NullOrEmpty())
            {
                return true;
            }
            if (!keys.TryGetValue(holder, out var entry) || entry.Count != count)
            {
                var sorted = holder.contentList.Keys.ToArray();
                Array.Sort(sorted, StringComparer.Ordinal);
                keys[holder] = entry = new Keys { Count = count, Sorted = sorted };
            }
            var lo = 0;
            var hi = entry.Sorted.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (string.CompareOrdinal(entry.Sorted[mid], prefix) < 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            return lo < entry.Sorted.Length && entry.Sorted[lo].StartsWith(prefix, StringComparison.Ordinal);
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
