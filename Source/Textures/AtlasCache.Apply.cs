using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using HarmonyLib;
using RimWorld;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static partial class AtlasCache
    {
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
                RLOLog.Detail($"atlas cache: verify {atlas.groupKey} {fresh.rects.Length} tiles {fresh.color.Describe()}: identical (rects, format, {fresh.color.bytes + (fresh.mask?.bytes ?? 0)} pixel bytes)");
            }
            else
            {
                verifyMismatches.Report($"verify {atlas.groupKey}: " + string.Join("; ", problems));
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
                RLOLog.Warning($"atlas cache: cached atlas {meta.key} unusable, baking normally: {e.Message}");
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
#if RW17
                        NativeArray<byte>.Copy(raw, off, mine, 0, n);
#else
                        raw.GetSubArray(off, n).CopyTo(mine);
#endif
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
#if RW17
                    NativeArray<byte>.Copy(raw, off, chunk, 0, n);
#else
                    raw.GetSubArray(off, n).CopyTo(chunk);
#endif
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
    }
}
