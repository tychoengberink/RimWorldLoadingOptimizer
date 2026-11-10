using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static partial class AtlasCache
    {
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
            sb.Append(t.width)
                .Append('x')
                .Append(t.height)
                .Append(' ')
                .Append((int)t.graphicsFormat)
                .Append(' ')
                .Append(t.mipmapCount)
                .Append(' ')
                .Append((int)t.filterMode)
                .Append(' ')
                .Append((int)t.wrapMode)
                .Append(' ');
            if (!owners.TryGetValue(t, out var owner))
            {
                string origin;
                lock (origins)
                {
                    origins.TryGetValue(t, out origin);
                }
                if (origin == null)
                {
                    unknownTextures++;
                    return false;
                }
                sb.Append(origin);
                return true;
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
                        sb.Append(' ')
                            .Append(folder.Length)
                            .Append(ext)
                            .Append(fi.Length)
                            .Append('@')
                            .Append(fi.LastWriteTimeUtc.Ticks);
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
            sb.Append(Magic)
                .Append('|')
                .Append(VersionControl.CurrentVersionStringWithRev)
                .Append('|')
                .Append(Application.unityVersion)
                .Append('|')
                .Append(SystemInfo.graphicsDeviceType)
                .Append('|')
                .Append(SystemInfo.graphicsDeviceName)
                .Append('|')
                .Append(SystemInfo.graphicsDeviceVersion)
                .Append('|')
                .Append(UnityData.ComputeShadersSupported)
                .Append('|')
                .Append(Prefs.TextureCompression)
                .Append('|')
                .Append(typeof(AtlasCache).Assembly.ManifestModule.ModuleVersionId);
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
            sb.Append(Magic)
                .Append('|')
                .Append(VersionControl.CurrentVersionStringWithRev)
                .Append('|')
                .Append(Application.unityVersion)
                .Append('|')
                .Append(SystemInfo.graphicsDeviceType)
                .Append('|')
                .Append(SystemInfo.graphicsDeviceName)
                .Append('|')
                .Append(SystemInfo.maxTextureSize)
                .Append('|')
                .Append(UnityData.ComputeShadersSupported)
                .Append('|')
                .Append(Prefs.TextureCompression)
                .Append('|')
                .Append(QualitySettings.activeColorSpace)
                .Append('\n');
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
                        foreach (
                            var fi in di.GetFiles("*", SearchOption.AllDirectories)
                                .OrderBy(f => f.FullName, StringComparer.Ordinal)
                        )
                        {
                            sb.Append(' ')
                                .Append(fi.FullName.Substring(folder.Length))
                                .Append(':')
                                .Append(fi.Length)
                                .Append('@')
                                .Append(fi.LastWriteTimeUtc.Ticks);
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
