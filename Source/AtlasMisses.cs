#if RW17
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Verse;
using Waylith.Assets;
using Waylith.Rendering;
using Texture = UnityEngine.Texture;
using Texture2D = UnityEngine.Texture2D;

namespace RimWorldLoadingOptimizer
{
    [Hook]
    public static class AtlasMisses
    {
        private static AccessTools.FieldRef<StaticTextureAtlas, List<Texture2D>> textures;
        private static AccessTools.FieldRef<StaticTextureAtlas, Dictionary<Texture2D, Texture2D>> masks;
        private static AccessTools.FieldRef<StaticTextureAtlas, Dictionary<Texture, StaticTextureAtlasTile>> tiles;
        private static AccessTools.FieldRef<StaticTextureAtlas, string> colorKey;
        private static AccessTools.FieldRef<StaticTextureAtlas, string> maskKey;
        private static MethodInfo compressionEnabled;

        private static readonly List<string> pages = new List<string>();
        private static int hits, keyless, uncached, compressionOff;
        private static Dictionary<Texture2D, string> owners;

        public static void Install(Harmony harmony)
        {
            var bake = AccessTools.Method(typeof(StaticTextureAtlas), nameof(StaticTextureAtlas.Bake));
            compressionEnabled = AccessTools.PropertyGetter(typeof(GlobalTextureAtlasManager), "AtlasCompressionEnabled");
            if (bake == null || compressionEnabled == null)
            {
                Log.Message("[RLO] atlas cache miss report: the game's atlas baking or compression switch was not found; report skipped");
                return;
            }
            try
            {
                textures = AccessTools.FieldRefAccess<StaticTextureAtlas, List<Texture2D>>("textures");
                masks = AccessTools.FieldRefAccess<StaticTextureAtlas, Dictionary<Texture2D, Texture2D>>("masks");
                tiles = AccessTools.FieldRefAccess<StaticTextureAtlas, Dictionary<Texture, StaticTextureAtlasTile>>("tiles");
                colorKey = AccessTools.FieldRefAccess<StaticTextureAtlas, string>("colorCacheKey");
                maskKey = AccessTools.FieldRefAccess<StaticTextureAtlas, string>("maskCacheKey");
            }
            catch (Exception e)
            {
                Log.Message($"[RLO] atlas cache miss report: the game's atlas fields changed ({e.Message}); report skipped");
                return;
            }
            harmony.Patch(bake, postfix: new HarmonyMethod(typeof(AtlasMisses), nameof(BakePostfix)));
            LeanFixes.Reports.Add(Stats);
        }

        public static void BakePostfix(StaticTextureAtlas __instance)
        {
            try
            {
                pages.Add(Describe(__instance));
            }
            catch (Exception e)
            {
                pages.Add($"{__instance.groupKey}: no diagnosis ({e.GetType().Name}: {e.Message})");
            }
        }

        private static string Describe(StaticTextureAtlas atlas)
        {
            var members = textures(atlas);
            var page = $"{atlas.groupKey} ({members.Count} textures)";
            var color = colorKey(atlas);
            if (atlas.AdoptedFromCache)
            {
                hits++;
                return $"{page}: hit, key {Short(color)}";
            }
            if (!(bool)compressionEnabled.Invoke(null, null))
            {
                compressionOff++;
                return $"{page}: miss, texture compression is off and the game only caches compressed pages";
            }
            if (color == null)
            {
                keyless++;
                return $"{page}: miss, no cache key so this page is baked and never saved: {WhyNoKey(atlas, false)}";
            }
            if (atlas.groupKey.hasMask && maskKey(atlas) == null)
            {
                keyless++;
                return $"{page}: miss, no cache key for the mask page so this page is baked and never saved: {WhyNoKey(atlas, true)}";
            }
            uncached++;
            return $"{page}: miss, key {Short(color)} has no saved page (first bake with these inputs, or an input changed since the last start); "
                + $"members {Digest(members.Select(t => t?.CoreTexture?.SourcePath ?? "name:" + t?.name))}, files {Digest(members.Select(Stamp))}, layout {Digest(members.Select(t => UvOf(atlas, t)))}";
        }

        private static string WhyNoKey(StaticTextureAtlas atlas, bool mask)
        {
            if ((mask ? atlas.MaskTexture : atlas.ColorTexture) == null)
            {
                return "the page texture is missing";
            }
            var members = textures(atlas);
            var tileMap = tiles(atlas);
            if (tileMap.Count != members.Count)
            {
                return $"{members.Count - tileMap.Count} textures were left out of the packing";
            }
            var maskMap = masks(atlas);
            var examples = new List<string>();
            var count = 0;
            foreach (var texture in members)
            {
                var problem = StampProblem(texture);
                if (problem == null && mask)
                {
                    problem = maskMap.TryGetValue(texture, out var maskTexture) ? StampProblem(maskTexture) : "no mask texture";
                    problem = problem == null ? null : "mask " + problem;
                }
                if (problem == null && !tileMap.ContainsKey(texture))
                {
                    problem = "no tile";
                }
                if (problem == null)
                {
                    continue;
                }
                count++;
                if (examples.Count < 3)
                {
                    examples.Add($"{Owner(texture)}: {problem}");
                }
            }
            return count == 0
                ? "the game's key builder gave up for a reason this report cannot see"
                : $"{count} member(s) without a stable file stamp, for example {string.Join("; ", examples)}";
        }

        private static string StampProblem(Texture2D texture)
        {
            if (texture == null)
            {
                return "texture is gone";
            }
            var path = texture.CoreTexture?.SourcePath;
            if (path == null)
            {
                return $"'{texture.name}' has no source file (made in code or loaded from a bundle)";
            }
            if (ContentPack.TryGetTextureContentHash(path, out _) || File.Exists(path))
            {
                return null;
            }
            return $"source file {path} no longer exists";
        }

        private static string Stamp(Texture2D texture)
        {
            var path = texture?.CoreTexture?.SourcePath;
            if (path == null)
            {
                return "none";
            }
            if (ContentPack.TryGetTextureContentHash(path, out var hash))
            {
                return hash;
            }
            if (AtlasStamps.TryGet(path, out var exists, out var length, out var ticks))
            {
                return exists ? $"{length}:{ticks}" : "missing";
            }
            var file = new FileInfo(path);
            return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing";
        }

        private static string UvOf(StaticTextureAtlas atlas, Texture2D texture) =>
            texture != null && tiles(atlas).TryGetValue(texture, out var tile) ? $"{tile.uvRect.x}:{tile.uvRect.y}:{tile.uvRect.width}:{tile.uvRect.height}" : "none";

        private static string Owner(Texture2D texture)
        {
            if (owners == null)
            {
                owners = new Dictionary<Texture2D, string>(ReferenceComparer<Texture2D>.Instance);
                foreach (var mod in LoadedModManager.RunningModsListForReading)
                {
                    foreach (var entry in mod.GetContentHolder<Texture2D>().contentList)
                    {
                        if (entry.Value != null && !owners.ContainsKey(entry.Value))
                        {
                            owners[entry.Value] = $"{mod.Name} ({entry.Key})";
                        }
                    }
                }
            }
            return texture != null && owners.TryGetValue(texture, out var owner) ? owner : "not a mod texture";
        }

        private static string Short(string key) => key == null ? "none" : key.Substring(0, Math.Min(12, key.Length));

        private static string Digest(IEnumerable<string> parts) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts)))).Substring(0, 12);

        public static string Stats()
        {
            var writes = GpuTextureService.TextureCacheWrites ? "on" : "off";
            var damaged = AssetPack.CorruptEntries;
            var head = $"atlas cache of the game: {pages.Count} pages, {hits} loaded from cache, {keyless} without a cache key, {uncached} not in the cache, {compressionOff} with compression off; cache writes {writes}, damaged asset pack entries {damaged}"
                + (damaged > 0 ? " (the game saves no atlas page while any entry is damaged)" : "");
            return string.Join("\n", pages.Select(p => "  atlas page " + p).Prepend(head));
        }
    }
}
#endif
