using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static partial class AtlasCache
    {
        private const string Magic = "ATC2";
        private const int Chunk = 4 << 20;

        private static readonly AccessTools.FieldRef<StaticTextureAtlas, List<Texture2D>> Textures =
            AccessTools.FieldRefAccess<StaticTextureAtlas, List<Texture2D>>("textures");
        private static readonly AccessTools.FieldRef<StaticTextureAtlas, Dictionary<Texture2D, Texture2D>> Masks =
            AccessTools.FieldRefAccess<StaticTextureAtlas, Dictionary<Texture2D, Texture2D>>("masks");
        private static readonly AccessTools.FieldRef<
            StaticTextureAtlas,
            Dictionary<Texture, StaticTextureAtlasTile>
        > Tiles = AccessTools.FieldRefAccess<StaticTextureAtlas, Dictionary<Texture, StaticTextureAtlasTile>>("tiles");
        private static readonly AccessTools.FieldRef<StaticTextureAtlas, Texture2D> ColorTex =
            AccessTools.FieldRefAccess<StaticTextureAtlas, Texture2D>("colorTexture");
        private static readonly AccessTools.FieldRef<StaticTextureAtlas, Texture2D> MaskTex =
            AccessTools.FieldRefAccess<StaticTextureAtlas, Texture2D>("maskTexture");
        private static readonly string[] FasterGameLoadingAtlasSettings =
        {
            "TrimStaticAtlases",
            "DeduplicateStaticAtlases",
            "FastStaticAtlasCompression",
            "StaticAtlasesBaking",
            "DelayGraphicLoading",
        };
        private static readonly Action<StaticTextureAtlas, Rect[]> BuildMeshes = AccessTools.MethodDelegate<
            Action<StaticTextureAtlas, Rect[]>
        >(AccessTools.Method(typeof(StaticTextureAtlas), "BuildMeshesForUvs"));

        public static readonly string Dir = Path.Combine(
            GenFilePaths.SaveDataFolderPath,
            "RimWorldLoadingOptimizer",
            "atlas"
        );

        private static Pending current;
        private static Dictionary<Texture2D, (ModContentPack mod, string path)> owners;
        private static readonly Dictionary<string, Dictionary<string, FileInfo>> dirCache =
            new Dictionary<string, Dictionary<string, FileInfo>>();
        private static string globalKey;
        private static bool? uncacheable;
        private static int notReadable,
            skippedUncacheable;
        private static readonly HashSet<string> usedKeys = new HashSet<string>();
        private static readonly Stopwatch total = new Stopwatch();
        private static readonly byte[] buffer = new byte[Chunk];
        private static int hits,
            misses,
            saved,
            failures,
            unknownTextures,
            verified,
            bakeCalls;
        private static readonly MismatchLog verifyMismatches = new MismatchLog("atlas cache");
        private static long savedBytes,
            loadedBytes;

        public static int Hits => hits;
        private static double keySeconds,
            loadSeconds,
            saveSeconds;

        private class Pending
        {
            public StaticTextureAtlas atlas;
            public string key;
            public bool verify;
            public Meta cached;
            public long[] rawLengths = new long[2];
            public long[] rawDiffs = new long[2];
            public ulong[] rawChecksums = new ulong[2];
            public bool rawOk = true;
        }

        private class TexMeta
        {
            public int width,
                height,
                format,
                mips,
                filter,
                wrapU,
                wrapV,
                wrapW,
                aniso;
            public float bias;
            public long bytes;
            public ulong checksum;

            public static TexMeta Of(Texture2D t, long bytes, ulong checksum = 0) =>
                new TexMeta
                {
                    width = t.width,
                    height = t.height,
                    format = (int)t.graphicsFormat,
                    mips = t.mipmapCount,
                    filter = (int)t.filterMode,
                    wrapU = (int)t.wrapModeU,
                    wrapV = (int)t.wrapModeV,
                    wrapW = (int)t.wrapModeW,
                    aniso = t.anisoLevel,
                    bias = t.mipMapBias,
                    bytes = bytes,
                    checksum = checksum,
                };

            public string Describe() =>
                $"{width}x{height} {(GraphicsFormat)format} mips={mips} filter={filter} wrap={wrapU}/{wrapV}/{wrapW} aniso={aniso} bias={bias} bytes={bytes}";

            public void Write(BinaryWriter w)
            {
                w.Write(width);
                w.Write(height);
                w.Write(format);
                w.Write(mips);
                w.Write(filter);
                w.Write(wrapU);
                w.Write(wrapV);
                w.Write(wrapW);
                w.Write(aniso);
                w.Write(bias);
                w.Write(bytes);
                w.Write(checksum);
            }

            public static TexMeta Read(BinaryReader r) =>
                new TexMeta
                {
                    width = r.ReadInt32(),
                    height = r.ReadInt32(),
                    format = r.ReadInt32(),
                    mips = r.ReadInt32(),
                    filter = r.ReadInt32(),
                    wrapU = r.ReadInt32(),
                    wrapV = r.ReadInt32(),
                    wrapW = r.ReadInt32(),
                    aniso = r.ReadInt32(),
                    bias = r.ReadSingle(),
                    bytes = r.ReadInt64(),
                    checksum = r.ReadUInt64(),
                };
        }

        private class Meta
        {
            public string key;
            public Rect[] rects;
            public TexMeta color,
                mask;
        }

        public static void Install(Harmony harmony)
        {
            if (BuildMeshes == null)
            {
                throw new Exception("BuildMeshesForUvs not found");
            }
#if RW17
            if (AccessTools.Method(typeof(StaticTextureAtlas), "ApplyTextureCompression") == null)
            {
                RLOLog.Detail(
                    "atlas cache: not available on this RimWorld version (it caches baked atlases itself); skipped"
                );
                return;
            }
#endif
            harmony.Patch(
                AccessTools.Method(
                    typeof(GlobalTextureAtlasManager),
                    nameof(GlobalTextureAtlasManager.BakeStaticAtlases)
                ),
                prefix: new HarmonyMethod(typeof(AtlasCache), nameof(BakeAllPrefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(AtlasCache), nameof(BakeAllPostfix)) { priority = Priority.Last }
            );
            harmony.Patch(
                AccessTools.Method(typeof(StaticTextureAtlas), nameof(StaticTextureAtlas.Bake)),
                prefix: new HarmonyMethod(typeof(AtlasCache), nameof(BakePrefix)),
                postfix: new HarmonyMethod(typeof(AtlasCache), nameof(BakePostfix)),
                finalizer: new HarmonyMethod(typeof(AtlasCache), nameof(BakeFinalizer))
            );
            harmony.Patch(
                AccessTools.Method(typeof(StaticTextureAtlas), "ApplyTextureCompression"),
                postfix: new HarmonyMethod(typeof(AtlasCache), nameof(CompressPostfix)) { priority = Priority.Last }
            );
            var resourcesLoad = AccessTools.Method(
                typeof(Resources),
                nameof(Resources.Load),
                new[] { typeof(string), typeof(Type) }
            );
            var bundleLoad = AccessTools.Method(
                typeof(AssetBundle),
                nameof(AssetBundle.LoadAsset),
                new[] { typeof(string), typeof(Type) }
            );
            var bundleOpen = AccessTools.Method(
                typeof(AssetBundle),
                nameof(AssetBundle.LoadFromFile),
                new[] { typeof(string) }
            );
            if (resourcesLoad != null && bundleLoad != null && bundleOpen != null)
            {
                harmony.Patch(resourcesLoad, postfix: new HarmonyMethod(typeof(AtlasCache), nameof(ResourceLoaded)));
                harmony.Patch(bundleLoad, postfix: new HarmonyMethod(typeof(AtlasCache), nameof(BundleAssetLoaded)));
                harmony.Patch(bundleOpen, postfix: new HarmonyMethod(typeof(AtlasCache), nameof(BundleOpened)));
                MainMenu.UnpatchAfterStartup(resourcesLoad);
                MainMenu.UnpatchAfterStartup(bundleLoad);
                MainMenu.UnpatchAfterStartup(bundleOpen);
            }
        }

        private static readonly Dictionary<Texture2D, string> origins = new Dictionary<Texture2D, string>(
            ReferenceComparer<Texture2D>.Instance
        );
        private static readonly Dictionary<AssetBundle, string> bundleFiles = new Dictionary<AssetBundle, string>(
            ReferenceComparer<AssetBundle>.Instance
        );

        public static void ResourceLoaded(string path, UnityEngine.Object __result)
        {
            if (__result is Texture2D texture)
            {
                lock (origins)
                {
                    if (!origins.ContainsKey(texture))
                    {
                        origins.Add(texture, "resource " + path);
                    }
                }
            }
        }

        public static void BundleOpened(string path, AssetBundle __result)
        {
            if ((object)__result == null)
            {
                return;
            }
            try
            {
                var file = new FileInfo(path);
                lock (bundleFiles)
                {
                    bundleFiles[__result] = $"{file.FullName} {file.Length}@{file.LastWriteTimeUtc.Ticks}";
                }
            }
            catch (Exception) { }
        }

        public static void BundleAssetLoaded(AssetBundle __instance, string name, UnityEngine.Object __result)
        {
            if (!(__result is Texture2D texture))
            {
                return;
            }
            string file;
            lock (bundleFiles)
            {
                if (!bundleFiles.TryGetValue(__instance, out file))
                {
                    return;
                }
            }
            lock (origins)
            {
                if (!origins.ContainsKey(texture))
                {
                    origins.Add(texture, "bundle " + file + " " + name);
                }
            }
        }

        public static void BakeAllPrefix()
        {
            total.Start();
        }

        public static void BakeAllPostfix()
        {
            total.Stop();
            if (bakeCalls == 0)
            {
                return;
            }
            RLOLog.Detail(
                $"atlas cache: {bakeCalls} atlases: hits {hits}, misses {misses}, saved {saved} ({savedBytes / 1048576} MB), failures {failures}, {unknownTextures} textures from no mod file (their atlases are not cached); "
                    + $"loaded {loadedBytes / 1048576} MB in {loadSeconds:F2} s, saved in {saveSeconds:F2} s, keys {keySeconds:F2} s; BakeStaticAtlases total {total.Elapsed.TotalSeconds:F2} s"
                    + (
                        notReadable > 0
                            ? $"; {notReadable} atlases not readable after compression on this system, caching switched off until settings or mods change"
                            : ""
                    )
                    + (
                        skippedUncacheable > 0
                            ? $"; {skippedUncacheable} atlases skipped (not cacheable on this system)"
                            : ""
                    )
                    + (
                        DevSwitches.Verify
                            ? $"; verify: {verified} atlases compared, {verifyMismatches.Count} mismatches"
                            : ""
                    )
            );
            try
            {
                if (notReadable > 0 && hits == 0 && saved == 0)
                {
                    Directory.CreateDirectory(Dir);
                    File.WriteAllText(MarkerPath, LightKey());
                }
                else if (saved > 0 || hits > 0)
                {
                    File.Delete(MarkerPath);
                }
            }
            catch { }
            Cleanup();
            owners = null;
            dirCache.Clear();
            lock (origins)
            {
                origins.Clear();
            }
            lock (bundleFiles)
            {
                bundleFiles.Clear();
            }
        }

        private static string MarkerPath => Path.Combine(Dir, "uncacheable.txt");

        private static bool Uncacheable()
        {
            if (uncacheable == null)
            {
                try
                {
                    uncacheable = File.Exists(MarkerPath) && File.ReadAllText(MarkerPath) == LightKey();
                }
                catch
                {
                    uncacheable = false;
                }
            }
            return uncacheable.Value;
        }

        public static bool BakePrefix(StaticTextureAtlas __instance, bool rebake)
        {
            current = null;
            if (rebake)
            {
                return true;
            }
            bakeCalls++;
            try
            {
                if (!Prefs.TextureCompression)
                {
                    return true;
                }
                if (Uncacheable())
                {
                    skippedUncacheable++;
                    return true;
                }
                var sw = Stopwatch.StartNew();
                var key = KeyFor(__instance);
                keySeconds += sw.Elapsed.TotalSeconds;
                if (key == null)
                {
                    failures++;
                    return true;
                }
                usedKeys.Add(key);
                var meta = ReadMeta(key);
                if (
                    meta != null
                    && meta.rects.Length == Textures(__instance).Count
                    && (meta.mask != null) == __instance.groupKey.hasMask
                )
                {
                    if (DevSwitches.Verify)
                    {
                        hits++;
                        current = new Pending
                        {
                            atlas = __instance,
                            key = key,
                            verify = true,
                            cached = meta,
                        };
                        return true;
                    }
                    if (Apply(__instance, meta))
                    {
                        hits++;
                        return false;
                    }
                    failures++;
                }
                misses++;
                current = new Pending { atlas = __instance, key = key };
            }
            catch (Exception e)
            {
                failures++;
                current = null;
                RLOLog.Warning("atlas cache: key/load failed, baking normally: " + e);
            }
            return true;
        }

        public static void CompressPostfix(StaticTextureAtlas __instance)
        {
            var p = current;
            if (p == null || p.atlas != __instance)
            {
                return;
            }
            var colorTex = ColorTex(__instance);
            var maskTex = MaskTex(__instance);
            if ((colorTex != null && !colorTex.isReadable) || (maskTex != null && !maskTex.isReadable))
            {
                p.rawOk = false;
                notReadable++;
                return;
            }
            try
            {
                var sw = Stopwatch.StartNew();
                Raw(p, 0, colorTex);
                Raw(p, 1, maskTex);
                if (!p.verify)
                {
                    saveSeconds += sw.Elapsed.TotalSeconds;
                }
            }
            catch (Exception e)
            {
                p.rawOk = false;
                RLOLog.Warning("atlas cache: could not read atlas data: " + e.Message);
            }
        }

        public static void BakePostfix(StaticTextureAtlas __instance)
        {
            var p = current;
            current = null;
            if (p == null || p.atlas != __instance)
            {
                return;
            }
            try
            {
                var textures = Textures(__instance);
                var tiles = Tiles(__instance);
                var color = ColorTex(__instance);
                var mask = MaskTex(__instance);
                if (
                    !p.rawOk
                    || color == null
                    || tiles.Count != textures.Count
                    || textures.Count == 0
                    || (mask != null) != __instance.groupKey.hasMask
                )
                {
                    if (p.verify)
                    {
                        verified++;
                        verifyMismatches.Report(
                            $"verify {p.key}: vanilla bake produced an unusable atlas or data could not be read"
                        );
                    }
                    Delete(p.key);
                    return;
                }
                var meta = new Meta
                {
                    key = p.key,
                    rects = textures.Select(t => tiles[t].uvRect).ToArray(),
                    color = TexMeta.Of(color, p.rawLengths[0], p.rawChecksums[0]),
                    mask = mask == null ? null : TexMeta.Of(mask, p.rawLengths[1], p.rawChecksums[1]),
                };
                if (p.verify)
                {
                    Compare(p, meta, __instance);
                    return;
                }
                var sw = Stopwatch.StartNew();
                WriteMeta(meta);
                MoveOver(RawPath(p.key, 0));
                if (mask != null)
                {
                    MoveOver(RawPath(p.key, 1));
                }
                MoveOver(MetaPath(p.key));
                saveSeconds += sw.Elapsed.TotalSeconds;
                saved++;
                savedBytes += p.rawLengths[0] + p.rawLengths[1];
            }
            catch (Exception e)
            {
                RLOLog.Warning("atlas cache: could not save atlas: " + e.Message);
                Delete(p.key);
            }
        }

        public static Exception BakeFinalizer(Exception __exception)
        {
            if (__exception != null && current != null)
            {
                Delete(current.key);
                current = null;
            }
            return __exception;
        }
    }
}
