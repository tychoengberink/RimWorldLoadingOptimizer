using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using KTrie;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class ShadowedTextures
    {
        private static readonly AccessTools.FieldRef<ModContentHolder<Texture2D>, StringTrieSet> Trie =
            AccessTools.FieldRefAccess<ModContentHolder<Texture2D>, StringTrieSet>("contentListTrie");

        public static bool IncludeCodeMods;

        public static void Release()
        {
            try
            {
                ReleaseInternal();
            }
            catch (Exception e)
            {
                RLOLog.Error($"shadowed texture release failed, nothing was freed: {e}");
            }
        }

        private static void ReleaseInternal()
        {
#if RW17
            var winners = new Dictionary<string, Texture2D>();
#else
            var winners = new HashSet<string>();
#endif
            var released = new List<Texture2D>();
            var keptForCode = new List<Texture2D>();
            var meleePaths = MeleeWeaponPaths();
            var keptForMelee = 0;
            var mods = LoadedModManager.RunningModsListForReading;
            for (var i = mods.Count - 1; i >= 0; i--)
            {
                var holder = mods[i].GetContentHolder<Texture2D>();
                var hasCode = mods[i].assemblies.loadedAssemblies.Count > 0;
                foreach (var path in holder.contentList.Keys.ToList())
                {
#if RW17
                    if (winners.TryAdd(path, holder.contentList[path]))
#else
                    if ((object)holder.contentList[path] == null)
                    {
                        continue;
                    }
                    if (winners.Add(path))
#endif
                    {
                        continue;
                    }
                    if (hasCode && !IncludeCodeMods)
                    {
                        keptForCode.Add(holder.contentList[path]);
                        continue;
                    }
                    if (meleePaths != null && IsUnder(meleePaths, path))
                    {
                        keptForMelee++;
                        continue;
                    }
#if RW17
                    var winner = winners[path];
                    var shadowed = holder.contentList[path];
                    if (winner == null || shadowed == null || winner.name != shadowed.name)
                    {
                        continue;
                    }
                    released.Add(shadowed);
                    holder.contentList[path] = winner;
#else
                    released.Add(holder.contentList[path]);
                    holder.contentList.Remove(path);
                    Trie(holder).Remove(path);
#endif
                }
            }

            var kept = new HashSet<Texture2D>(mods.SelectMany(m => m.GetContentHolder<Texture2D>().contentList.Values));
            long bytes = 0;
            var destroyed = 0;
            foreach (var texture in released.Where(t => t != null && t != BaseContent.BadTex && !kept.Contains(t)).Distinct())
            {
#if RW17
                var size = EstimatedBytes(texture);
                bytes += size;
                ShadowedUploads.Note(texture, true, size);
#else
                bytes += EstimatedBytes(texture);
#endif
                destroyed++;
                UnityEngine.Object.Destroy(texture);
            }
            var codeTextures = keptForCode.Where(t => t != null).Distinct().ToList();
            long keptBytes = codeTextures.Sum(t => EstimatedBytes(t));
#if RW17
            foreach (var texture in codeTextures)
            {
                ShadowedUploads.Note(texture, false, EstimatedBytes(texture));
            }
#endif
#if !NATIVE_ASSETS
            RLOLog.Detail($"released {destroyed} shadowed textures (~{bytes / (1 << 20)} MB), kept {codeTextures.Count} overridden textures of code mods (~{keptBytes / (1 << 20)} MB) and {keptForMelee} melee weapon textures for Melee Animation; streamed {AudioStreaming.Streamed} extra sound files ({AudioStreaming.StreamedBytes >> 20} MB on disk) instead of decoding them; {MonoHeap.Stats()}");
#else
            RLOLog.Detail($"released {destroyed} shadowed textures (~{bytes / (1 << 20)} MB), kept {codeTextures.Count} overridden textures of code mods (~{keptBytes / (1 << 20)} MB) and {keptForMelee} melee weapon textures for Melee Animation; {MonoHeap.Stats()}");
#endif
        }

        private static HashSet<string> MeleeWeaponPaths()
        {
            if (AccessTools.TypeByName("AM.Retexture.RetextureUtility") == null)
            {
                return null;
            }
            var paths = new HashSet<string>();
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                var texPath = def.graphicData?.texPath;
                if (!string.IsNullOrEmpty(texPath) && def.IsMeleeWeapon)
                {
                    paths.Add(texPath);
                }
            }
            return paths;
        }

        private static bool IsUnder(HashSet<string> roots, string path)
        {
            for (var current = path; ; )
            {
                if (roots.Contains(current))
                {
                    return true;
                }
                var slash = current.LastIndexOf('/');
                if (slash <= 0)
                {
                    return false;
                }
                current = current.Substring(0, slash);
            }
        }

        private static long EstimatedBytes(Texture2D texture)
        {
            long total = 0;
            for (var mip = 0; mip < texture.mipmapCount; mip++)
            {
#if RW17
                var width = Math.Max(1, texture.width >> mip);
                var height = Math.Max(1, texture.height >> mip);
                total += texture.format switch
                {
                    TextureFormat.DXT1 => ((width + 3) / 4) * ((height + 3) / 4) * 8L,
                    TextureFormat.DXT5 or TextureFormat.BC7 => ((width + 3) / 4) * ((height + 3) / 4) * 16L,
                    _ => width * height * 4L,
                };
#else
                total += (long)GraphicsFormatUtility.ComputeMipmapSize(Math.Max(1, texture.width >> mip), Math.Max(1, texture.height >> mip), texture.graphicsFormat);
#endif
            }
            return total;
        }
    }
}
