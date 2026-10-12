using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using RimWorld;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class AtlasCache
    {
        private const string Magic = "ATC2";
        private const int Chunk = 4 << 20;

        private static readonly AccessTools.FieldRef<StaticTextureAtlas, List<Texture2D>> Textures = AccessTools.FieldRefAccess<StaticTextureAtlas, List<Texture2D>>("textures");
        private static readonly AccessTools.FieldRef<StaticTextureAtlas, Dictionary<Texture2D, Texture2D>> Masks = AccessTools.FieldRefAccess<StaticTextureAtlas, Dictionary<Texture2D, Texture2D>>("masks");
        private static readonly AccessTools.FieldRef<StaticTextureAtlas, Dictionary<Texture, StaticTextureAtlasTile>> Tiles = AccessTools.FieldRefAccess<StaticTextureAtlas, Dictionary<Texture, StaticTextureAtlasTile>>("tiles");
        private static readonly AccessTools.FieldRef<StaticTextureAtlas, Texture2D> ColorTex = AccessTools.FieldRefAccess<StaticTextureAtlas, Texture2D>("colorTexture");
        private static readonly AccessTools.FieldRef<StaticTextureAtlas, Texture2D> MaskTex = AccessTools.FieldRefAccess<StaticTextureAtlas, Texture2D>("maskTexture");
        private static readonly string[] FasterGameLoadingAtlasSettings = { "TrimStaticAtlases", "DeduplicateStaticAtlases", "FastStaticAtlasCompression", "StaticAtlasesBaking", "DelayGraphicLoading" };
        private static readonly Action<StaticTextureAtlas, Rect[]> BuildMeshes = AccessTools.MethodDelegate<Action<StaticTextureAtlas, Rect[]>>(AccessTools.Method(typeof(StaticTextureAtlas), "BuildMeshesForUvs"));

        public static readonly string Dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimWorldLoadingOptimizer", "atlas");

        private static Pending current;
        private static Dictionary<Texture2D, (ModContentPack mod, string path)> owners;
        private static readonly Dictionary<string, Dictionary<string, FileInfo>> dirCache = new Dictionary<string, Dictionary<string, FileInfo>>();
        private static string globalKey;
        private static bool? uncacheable;
        private static int notReadable, skippedUncacheable;
        private static readonly HashSet<string> usedKeys = new HashSet<string>();
        private static readonly Stopwatch total = new Stopwatch();
        private static readonly byte[] buffer = new byte[Chunk];
        private static int hits, misses, saved, failures, unknownTextures, verified, verifyMismatches, bakeCalls;
        private static long savedBytes, loadedBytes;

        public static int Hits => hits;
        private static double keySeconds, loadSeconds, saveSeconds;

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
            public int width, height, format, mips, filter, wrapU, wrapV, wrapW, aniso;
            public float bias;
            public long bytes;
            public ulong checksum;

            public static TexMeta Of(Texture2D t, long bytes, ulong checksum = 0) => new TexMeta
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

            public string Describe() => $"{width}x{height} {(GraphicsFormat)format} mips={mips} filter={filter} wrap={wrapU}/{wrapV}/{wrapW} aniso={aniso} bias={bias} bytes={bytes}";

            public void Write(BinaryWriter w)
            {
                w.Write(width); w.Write(height); w.Write(format); w.Write(mips); w.Write(filter);
                w.Write(wrapU); w.Write(wrapV); w.Write(wrapW); w.Write(aniso); w.Write(bias); w.Write(bytes); w.Write(checksum);
            }

            public static TexMeta Read(BinaryReader r) => new TexMeta
            {
                width = r.ReadInt32(), height = r.ReadInt32(), format = r.ReadInt32(), mips = r.ReadInt32(), filter = r.ReadInt32(),
                wrapU = r.ReadInt32(), wrapV = r.ReadInt32(), wrapW = r.ReadInt32(), aniso = r.ReadInt32(), bias = r.ReadSingle(), bytes = r.ReadInt64(), checksum = r.ReadUInt64(),
            };
        }

        private class Meta
        {
            public string key;
            public Rect[] rects;
            public TexMeta color, mask;
        }

        public static void Install(Harmony harmony)
        {
            if (BuildMeshes == null)
            {
                throw new Exception("BuildMeshesForUvs not found");
            }
            harmony.Patch(AccessTools.Method(typeof(GlobalTextureAtlasManager), nameof(GlobalTextureAtlasManager.BakeStaticAtlases)),
                prefix: new HarmonyMethod(typeof(AtlasCache), nameof(BakeAllPrefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(AtlasCache), nameof(BakeAllPostfix)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(StaticTextureAtlas), nameof(StaticTextureAtlas.Bake)),
                prefix: new HarmonyMethod(typeof(AtlasCache), nameof(BakePrefix)),
                postfix: new HarmonyMethod(typeof(AtlasCache), nameof(BakePostfix)),
                finalizer: new HarmonyMethod(typeof(AtlasCache), nameof(BakeFinalizer)));
            harmony.Patch(AccessTools.Method(typeof(StaticTextureAtlas), "ApplyTextureCompression"),
                postfix: new HarmonyMethod(typeof(AtlasCache), nameof(CompressPostfix)) { priority = Priority.Last });
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
            Log.Message($"[RLO] atlas cache: {bakeCalls} atlases: hits {hits}, misses {misses}, saved {saved} ({savedBytes / 1048576} MB), failures {failures}, {unknownTextures} textures from no mod file (their atlases are not cached); "
                + $"loaded {loadedBytes / 1048576} MB in {loadSeconds:F2} s, saved in {saveSeconds:F2} s, keys {keySeconds:F2} s; BakeStaticAtlases total {total.Elapsed.TotalSeconds:F2} s"
                + (notReadable > 0 ? $"; {notReadable} atlases not readable after compression on this system, caching switched off until settings or mods change" : "")
                + (skippedUncacheable > 0 ? $"; {skippedUncacheable} atlases skipped (not cacheable on this system)" : "")
                + (RLOMod.Verify ? $"; verify: {verified} atlases compared, {verifyMismatches} mismatches" : ""));
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
            catch
            {
            }
            Cleanup();
            owners = null;
            dirCache.Clear();
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
                if (meta != null && meta.rects.Length == Textures(__instance).Count && (meta.mask != null) == __instance.groupKey.hasMask)
                {
                    if (RLOMod.Verify)
                    {
                        hits++;
                        current = new Pending { atlas = __instance, key = key, verify = true, cached = meta };
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
                Log.Warning("[RLO] atlas cache: key/load failed, baking normally: " + e);
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
                Log.Warning("[RLO] atlas cache: could not read atlas data: " + e.Message);
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
                if (!p.rawOk || color == null || tiles.Count != textures.Count || textures.Count == 0 || (mask != null) != __instance.groupKey.hasMask)
                {
                    if (p.verify)
                    {
                        verified++;
                        verifyMismatches++;
                        Log.Warning($"[RLO] atlas cache: verify {p.key}: vanilla bake produced an unusable atlas or data could not be read");
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
                Log.Warning("[RLO] atlas cache: could not save atlas: " + e.Message);
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

        private static void Compare(Pending p, Meta fresh, StaticTextureAtlas atlas)
        {
            verified++;
            var problems = new List<string>();
            var c = p.cached;
            for (int i = 0; i < fresh.rects.Length; i++)
            {
                if (fresh.rects[i] != c.rects[i])
                {
                    problems.Add($"rect {i} ({Textures(atlas)[i].name}) {fresh.rects[i]} vs cached {c.rects[i]}");
                    if (problems.Count > 5)
                    {
                        break;
                    }
                }
            }
            if (fresh.color.Describe() != c.color.Describe())
            {
                problems.Add($"color {fresh.color.Describe()} vs cached {c.color.Describe()}");
            }
            if ((fresh.mask?.Describe() ?? "") != (c.mask?.Describe() ?? ""))
            {
                problems.Add($"mask {fresh.mask?.Describe()} vs cached {c.mask?.Describe()}");
            }
            if (p.rawDiffs[0] != 0 || p.rawDiffs[1] != 0)
            {
                problems.Add($"pixel bytes differ: color {p.rawDiffs[0]}, mask {p.rawDiffs[1]}");
            }
            var probe = new StaticTextureAtlas(atlas.groupKey);
            foreach (var t in Textures(atlas))
            {
                probe.Insert(t, Masks(atlas).TryGetValue(t, out var m) ? m : null);
            }
            if (!Apply(probe, c))
            {
                problems.Add("loading the cached atlas failed");
            }
            else
            {
                var lc = TexMeta.Of(ColorTex(probe), c.color.bytes).Describe();
                if (lc != fresh.color.Describe())
                {
                    problems.Add($"loaded color {lc} vs vanilla {fresh.color.Describe()}");
                }
                if (fresh.mask != null && TexMeta.Of(MaskTex(probe), c.mask.bytes).Describe() != fresh.mask.Describe())
                {
                    problems.Add("loaded mask differs from vanilla");
                }
                var tiles = Tiles(probe);
                if (tiles.Count != fresh.rects.Length || Textures(atlas).Where((t, i) => tiles[t].uvRect != fresh.rects[i]).Any())
                {
                    problems.Add("loaded tiles differ from vanilla");
                }
            }
            probe.Destroy();
            if (problems.Count == 0)
            {
                Log.Message($"[RLO] atlas cache: verify {atlas.groupKey} {fresh.rects.Length} tiles {fresh.color.Describe()}: identical (rects, format, {fresh.color.bytes + (fresh.mask?.bytes ?? 0)} pixel bytes)");
            }
            else
            {
                verifyMismatches++;
                Log.Warning($"[RLO] atlas cache: verify {atlas.groupKey} mismatch: " + string.Join("; ", problems));
            }
        }

        private static bool Apply(StaticTextureAtlas atlas, Meta meta)
        {
            var sw = Stopwatch.StartNew();
            Texture2D color = null, mask = null;
            try
            {
                color = Load(meta.color, RawPath(meta.key, 0));
                if (meta.mask != null)
                {
                    mask = Load(meta.mask, RawPath(meta.key, 1));
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[RLO] atlas cache: cached atlas {meta.key} unusable, baking normally: {e.Message}");
                if (color != null)
                {
                    UnityEngine.Object.DestroyImmediate(color);
                }
                if (mask != null)
                {
                    UnityEngine.Object.DestroyImmediate(mask);
                }
                return false;
            }
            var old = ColorTex(atlas);
            if (old != null)
            {
                UnityEngine.Object.DestroyImmediate(old);
            }
            color.name = "TextureAtlas_" + atlas.groupKey + "_" + color.GetInstanceID();
            ColorTex(atlas) = color;
            if (mask != null)
            {
                mask.name = "Mask_" + color.name;
                MaskTex(atlas) = mask;
            }
            BuildMeshes(atlas, meta.rects);
            loadSeconds += sw.Elapsed.TotalSeconds;
            loadedBytes += meta.color.bytes + (meta.mask?.bytes ?? 0);
            return true;
        }

        private static Texture2D Load(TexMeta m, string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                if (fs.Length != m.bytes)
                {
                    throw new IOException($"size {fs.Length} != {m.bytes}");
                }
                var flags = TextureCreationFlags.DontInitializePixels | (m.mips > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None);
                var tex = new Texture2D(m.width, m.height, (GraphicsFormat)m.format, m.mips, flags);
                var ptr = Marshal.AllocHGlobal(new IntPtr(m.bytes));
                try
                {
                    long off = 0;
                    var sum = ChecksumSeed;
                    while (off < m.bytes)
                    {
                        int n = fs.Read(buffer, 0, (int)Math.Min(Chunk, m.bytes - off));
                        if (n <= 0)
                        {
                            throw new IOException("unexpected end of file");
                        }
                        sum = Checksum(buffer, n, sum);
                        Marshal.Copy(buffer, 0, new IntPtr(ptr.ToInt64() + off), n);
                        off += n;
                    }
                    if (sum != m.checksum)
                    {
                        throw new IOException("checksum mismatch (damaged cache file)");
                    }
                    tex.LoadRawTextureData(ptr, checked((int)m.bytes));
                }
                catch
                {
                    UnityEngine.Object.DestroyImmediate(tex);
                    throw;
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
                tex.filterMode = (FilterMode)m.filter;
                tex.wrapModeU = (TextureWrapMode)m.wrapU;
                tex.wrapModeV = (TextureWrapMode)m.wrapV;
                tex.wrapModeW = (TextureWrapMode)m.wrapW;
                tex.anisoLevel = m.aniso;
                tex.mipMapBias = m.bias;
                tex.Apply(false, true);
                if (tex.width != m.width || tex.height != m.height || (int)tex.graphicsFormat != m.format || tex.mipmapCount != m.mips)
                {
                    UnityEngine.Object.DestroyImmediate(tex);
                    throw new IOException("texture properties differ after load");
                }
                return tex;
            }
        }

        private static void Raw(Pending p, int slot, Texture2D tex)
        {
            if (tex == null)
            {
                return;
            }
            var raw = tex.GetRawTextureData<byte>();
            p.rawLengths[slot] = raw.Length;
            if (p.verify)
            {
                var path = RawPath(p.key, slot);
                var other = new byte[Chunk];
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                {
                    long diffs = Math.Abs(fs.Length - raw.Length);
                    for (int off = 0; off < raw.Length && off < fs.Length; off += Chunk)
                    {
                        int n = (int)Math.Min(Math.Min(Chunk, raw.Length - off), fs.Length - off);
                        var mine = n == Chunk ? buffer : new byte[n];
                        raw.GetSubArray(off, n).CopyTo(mine);
                        int read = 0;
                        while (read < n)
                        {
                            read += fs.Read(other, read, n - read);
                        }
                        for (int i = 0; i < n; i++)
                        {
                            if (mine[i] != other[i])
                            {
                                diffs++;
                            }
                        }
                    }
                    p.rawDiffs[slot] = diffs;
                }
                return;
            }
            Directory.CreateDirectory(Dir);
            var sum = ChecksumSeed;
            using (var fs = new FileStream(RawPath(p.key, slot) + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                for (int off = 0; off < raw.Length; off += Chunk)
                {
                    int n = Math.Min(Chunk, raw.Length - off);
                    var chunk = n == Chunk ? buffer : new byte[n];
                    raw.GetSubArray(off, n).CopyTo(chunk);
                    sum = Checksum(chunk, n, sum);
                    fs.Write(chunk, 0, n);
                }
                fs.Flush(true);
            }
            p.rawChecksums[slot] = sum;
        }

        private const ulong ChecksumSeed = 0xCBF29CE484222325UL;

        private static unsafe ulong Checksum(byte[] data, int count, ulong h)
        {
            fixed (byte* start = data)
            {
                var words = (ulong*)start;
                var whole = count / 8;
                for (var i = 0; i < whole; i++)
                {
                    h = (h ^ words[i]) * 0x100000001B3UL;
                    h ^= h >> 29;
                }
                for (var i = whole * 8; i < count; i++)
                {
                    h = (h ^ start[i]) * 0x100000001B3UL;
                }
            }
            return h;
        }

        private static string MetaPath(string key) => Path.Combine(Dir, key + ".meta");

        private static string RawPath(string key, int slot) => Path.Combine(Dir, key + (slot == 0 ? ".color" : ".mask"));

        private static void WriteMeta(Meta m)
        {
            using (var w = new BinaryWriter(new FileStream(MetaPath(m.key) + ".tmp", FileMode.Create, FileAccess.Write)))
            {
                w.Write(Magic);
                w.Write(m.key);
                w.Write(m.rects.Length);
                foreach (var r in m.rects)
                {
                    w.Write(r.x); w.Write(r.y); w.Write(r.width); w.Write(r.height);
                }
                m.color.Write(w);
                w.Write(m.mask != null);
                m.mask?.Write(w);
            }
        }

        private static Meta ReadMeta(string key)
        {
            var path = MetaPath(key);
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                using (var r = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read)))
                {
                    if (r.ReadString() != Magic || r.ReadString() != key)
                    {
                        return null;
                    }
                    var m = new Meta { key = key, rects = new Rect[r.ReadInt32()] };
                    for (int i = 0; i < m.rects.Length; i++)
                    {
                        m.rects[i] = new Rect(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    }
                    m.color = TexMeta.Read(r);
                    if (r.ReadBoolean())
                    {
                        m.mask = TexMeta.Read(r);
                    }
                    return m;
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[RLO] atlas cache: unreadable cache entry {key}: {e.Message}");
                return null;
            }
        }

        private static void MoveOver(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(path + ".tmp", path);
        }

        private static void Delete(string key)
        {
            foreach (var path in new[] { MetaPath(key), RawPath(key, 0), RawPath(key, 1) })
            {
                foreach (var p in new[] { path, path + ".tmp" })
                {
                    try
                    {
                        if (File.Exists(p))
                        {
                            File.Delete(p);
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void Cleanup()
        {
            try
            {
                if (!Directory.Exists(Dir) || usedKeys.Count == 0)
                {
                    return;
                }
                foreach (var file in Directory.GetFiles(Dir))
                {
                    var name = Path.GetFileName(file);
                    var dot = name.IndexOf('.');
                    if (dot > 0 && file != MarkerPath && !usedKeys.Contains(name.Substring(0, dot)))
                    {
                        File.Delete(file);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning("[RLO] atlas cache: cleanup failed: " + e.Message);
            }
        }

        private static string KeyFor(StaticTextureAtlas atlas)
        {
            if (globalKey == null)
            {
                globalKey = GlobalKey();
            }
            if (owners == null)
            {
                owners = new Dictionary<Texture2D, (ModContentPack, string)>();
                foreach (var mod in LoadedModManager.RunningModsListForReading)
                {
                    foreach (var kv in mod.GetContentHolder<Texture2D>().contentList)
                    {
                        if (kv.Value != null && !owners.ContainsKey(kv.Value))
                        {
                            owners.Add(kv.Value, (mod, kv.Key));
                        }
                    }
                }
            }
            var sb = new StringBuilder(globalKey);
            sb.Append('\n').Append(atlas.groupKey.group).Append('|').Append(atlas.groupKey.hasMask).Append('\n');
            var masks = Masks(atlas);
            foreach (var t in Textures(atlas))
            {
                if (t == null)
                {
                    return null;
                }
                if (!Identify(sb, t))
                {
                    return null;
                }
                if (masks.TryGetValue(t, out var m) && m != null)
                {
                    sb.Append(" mask ");
                    if (!Identify(sb, m))
                    {
                        return null;
                    }
                }
                sb.Append('\n');
            }
            return Hash(sb.ToString());
        }

        private static bool Identify(StringBuilder sb, Texture2D t)
        {
            sb.Append(t.width).Append('x').Append(t.height).Append(' ').Append((int)t.graphicsFormat).Append(' ').Append(t.mipmapCount)
                .Append(' ').Append((int)t.filterMode).Append(' ').Append((int)t.wrapMode).Append(' ');
            if (!owners.TryGetValue(t, out var owner))
            {
                unknownTextures++;
                return false;
            }
            sb.Append(owner.mod.PackageId).Append(':').Append(owner.path);
            var rel = owner.path.Replace('\\', '/');
            var slash = rel.LastIndexOf('/');
            var dir = slash < 0 ? "" : rel.Substring(0, slash);
            var file = slash < 0 ? rel : rel.Substring(slash + 1);
            foreach (var folder in owner.mod.foldersToLoadDescendingOrder)
            {
                var files = DirFiles(Path.Combine(Path.Combine(folder, "Textures"), dir));
                foreach (var ext in new[] { ".png", ".dds", ".jpg", ".jpeg", ".psd" })
                {
                    if (files.TryGetValue(file + ext, out var fi))
                    {
                        sb.Append(' ').Append(folder.Length).Append(ext).Append(fi.Length).Append('@').Append(fi.LastWriteTimeUtc.Ticks);
                    }
                }
            }
            return true;
        }

        private static Dictionary<string, FileInfo> DirFiles(string dir)
        {
            if (!dirCache.TryGetValue(dir, out var files))
            {
                files = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
                var di = new DirectoryInfo(dir);
                if (di.Exists)
                {
                    foreach (var fi in di.GetFiles())
                    {
                        files[fi.Name] = fi;
                    }
                }
                dirCache[dir] = files;
            }
            return files;
        }

        private static string LightKey()
        {
            var sb = new StringBuilder();
            sb.Append(Magic).Append('|').Append(VersionControl.CurrentVersionStringWithRev).Append('|').Append(Application.unityVersion)
                .Append('|').Append(SystemInfo.graphicsDeviceType).Append('|').Append(SystemInfo.graphicsDeviceName).Append('|').Append(SystemInfo.graphicsDeviceVersion)
                .Append('|').Append(UnityData.ComputeShadersSupported).Append('|').Append(Prefs.TextureCompression).Append('|').Append(typeof(AtlasCache).Assembly.ManifestModule.ModuleVersionId);
            var fgl = AccessTools.TypeByName("FasterGameLoading.FasterGameLoadingSettings");
            foreach (var name in FasterGameLoadingAtlasSettings)
            {
                var prop = fgl == null ? null : AccessTools.Property(fgl, name);
                sb.Append(' ').Append(name).Append('=').Append(prop?.GetValue(null));
            }
            foreach (var mod in LoadedModManager.RunningModsListForReading)
            {
                sb.Append(' ').Append(mod.PackageId);
            }
            return sb.ToString();
        }

        private static string GlobalKey()
        {
            var sb = new StringBuilder();
            sb.Append(Magic).Append('|').Append(VersionControl.CurrentVersionStringWithRev).Append('|').Append(Application.unityVersion)
                .Append('|').Append(SystemInfo.graphicsDeviceType).Append('|').Append(SystemInfo.graphicsDeviceName).Append('|').Append(SystemInfo.maxTextureSize)
                .Append('|').Append(UnityData.ComputeShadersSupported).Append('|').Append(Prefs.TextureCompression).Append('|').Append(QualitySettings.activeColorSpace).Append('\n');
            var fgl = AccessTools.TypeByName("FasterGameLoading.FasterGameLoadingSettings");
            foreach (var name in FasterGameLoadingAtlasSettings)
            {
                var prop = fgl == null ? null : AccessTools.Property(fgl, name);
                sb.Append(name).Append('=').Append(prop?.GetValue(null)).Append(' ');
            }
            sb.Append('\n');
            foreach (var mod in LoadedModManager.RunningModsListForReading)
            {
                sb.Append(mod.PackageId);
                foreach (var folder in mod.foldersToLoadDescendingOrder)
                {
                    foreach (var sub in new[] { "Assemblies", "AssetBundles" })
                    {
                        var di = new DirectoryInfo(Path.Combine(folder, sub));
                        if (!di.Exists)
                        {
                            continue;
                        }
                        foreach (var fi in di.GetFiles("*", SearchOption.AllDirectories).OrderBy(f => f.FullName, StringComparer.Ordinal))
                        {
                            sb.Append(' ').Append(fi.FullName.Substring(folder.Length)).Append(':').Append(fi.Length).Append('@').Append(fi.LastWriteTimeUtc.Ticks);
                        }
                    }
                }
                sb.Append('\n');
            }
            return Hash(sb.ToString());
        }

        private static string Hash(string s)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder(40);
                for (int i = 0; i < 20; i++)
                {
                    sb.Append(bytes[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }
    }
}
