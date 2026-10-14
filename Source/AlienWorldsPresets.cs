#if RW17
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class AlienWorldsPresets
    {
        public const string Feature = "Alien Worlds preset images";
        private const string Magic = "rlo-aw-1";

        private static readonly MethodInfo Get = AccessTools.Method(typeof(ContentFinder<Texture2D>), nameof(ContentFinder<Texture2D>.Get));
        private static readonly MethodInfo Encode = AccessTools.Method(typeof(ImageConversion), nameof(ImageConversion.EncodeToPNG), new[] { typeof(Texture2D) });
        private static readonly MethodInfo Png = AccessTools.Method(typeof(AlienWorldsPresets), nameof(EncodeOrReuse));

        private static MethodInfo duplicate;
        private static Func<Texture2D, Texture2D> duplicateTexture;
        private static FieldInfo root;
        private static FieldInfo manager;
        private static List<MethodBase> watched;
        private static string cachePath;
        private static string engine;
        private static bool active;

        private static Dictionary<string, (string sha, long length)> stored;
        private static Dictionary<string, (string sha, long length)> current;
        private static Dictionary<string, byte[]> previous;
        private static Dictionary<ModContentPack, Dictionary<string, FileInfo>> files;
        private static long start;

        private static int replaced, refreshes, reused, encoded, uncacheable, verified, mismatches;
        private static long ticks;

        public static void Install(Harmony harmony)
        {
            var type = AccessTools.TypeByName("AlienWorlds.WorldbuilderCompat");
            if (type == null)
            {
                return;
            }
            var refresh = AccessTools.Method(type, "Refresh");
            duplicate = AccessTools.Method(type, "duplicateTexture");
            if (!Fingerprint.Matches(Feature,
                Fingerprint.Known(refresh, "D51EDD5A0FEBA284", "2AEFE36965CC7D63"),
                Fingerprint.Known(duplicate, "067FE2616CED4A70", "71012E0966A24592")))
            {
                return;
            }
            var framework = AccessTools.TypeByName("AlienWorlds.AlienWorldsFramework");
            root = framework == null ? null : AccessTools.Field(framework, "root");
            manager = AccessTools.Field(type, "WorldPresetManager");
            if (root == null || manager == null || Get == null || Encode == null)
            {
                Log.Message($"[RLO] {Feature}: a member it needs is missing. This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            duplicateTexture = AccessTools.MethodDelegate<Func<Texture2D, Texture2D>>(duplicate);
            watched = PatchProcessor.ReadMethodBody(duplicate).Select(p => p.Value).OfType<MethodBase>().Append(duplicate).Append(Encode).Distinct().ToList();
            cachePath = Path.Combine(GenFilePaths.SaveDataFolderPath, "RimWorldLoadingOptimizer", "alienworlds-presets.txt");
            harmony.Patch(refresh,
                prefix: new HarmonyMethod(typeof(AlienWorldsPresets), nameof(Before)),
                finalizer: new HarmonyMethod(typeof(AlienWorldsPresets), nameof(After)),
                transpiler: new HarmonyMethod(typeof(AlienWorldsPresets), nameof(Transpiler)));
            active = replaced == 2;
            if (!active)
            {
                Log.Message($"[RLO] {Feature}: expected 2 image exports, found {replaced}. This speedup is skipped and the original code runs; nothing is broken.");
                return;
            }
            LeanFixes.Reports.Add(Stats);
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var sites = new List<int>();
            for (int i = 0; i + 2 < list.Count; i++)
            {
                if (list[i].Calls(Get) && list[i + 1].Calls(duplicate) && list[i + 2].Calls(Encode))
                {
                    sites.Add(i);
                }
            }
            replaced = sites.Count;
            if (replaced != 2)
            {
                return list;
            }
            foreach (var i in sites)
            {
                list[i].opcode = OpCodes.Call;
                list[i].operand = Png;
                list[i + 1].opcode = OpCodes.Nop;
                list[i + 1].operand = null;
                list[i + 2].opcode = OpCodes.Nop;
                list[i + 2].operand = null;
            }
            return list;
        }

        public static void Before()
        {
            if (!active)
            {
                return;
            }
            start = Stopwatch.GetTimestamp();
            refreshes++;
            current = new Dictionary<string, (string, long)>(StringComparer.Ordinal);
            previous = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            files = new Dictionary<ModContentPack, Dictionary<string, FileInfo>>();
            stored = new Dictionary<string, (string, long)>(StringComparer.Ordinal);
            try
            {
                if (manager.GetValue(null) == null)
                {
                    return;
                }
                stored = Load();
                var dir = Path.Combine((string)root.GetValue(null), "Worldbuilder");
                if (stored.Count == 0 || !Directory.Exists(dir))
                {
                    return;
                }
                var lengths = new HashSet<long>(stored.Values.Select(v => v.length));
                var shas = new HashSet<string>(stored.Values.Select(v => v.sha), StringComparer.Ordinal);
                foreach (var file in Directory.EnumerateFiles(dir, "*.png", SearchOption.AllDirectories))
                {
                    if (!lengths.Contains(new FileInfo(file).Length))
                    {
                        continue;
                    }
                    var bytes = File.ReadAllBytes(file);
                    var sha = Sha(bytes);
                    if (shas.Contains(sha))
                    {
                        previous[sha] = bytes;
                    }
                }
            }
            catch (Exception e)
            {
                previous.Clear();
                Log.Message($"[RLO] {Feature}: could not read last start's images ({e.GetType().Name}: {e.Message}); all images are encoded as usual.");
            }
        }

        public static byte[] EncodeOrReuse(string itemPath, bool reportFailure)
        {
            var texture = ContentFinder<Texture2D>.Get(itemPath, reportFailure);
            if (!active || current == null)
            {
                return ImageConversion.EncodeToPNG(duplicateTexture(texture));
            }
            string key;
            try
            {
                key = Key(itemPath, texture);
            }
            catch (Exception)
            {
                key = null;
            }
            if (key != null && stored.TryGetValue(key, out var entry) && previous.TryGetValue(entry.sha, out var cached) && cached.Length == entry.length)
            {
                if (!RLOMod.Verify)
                {
                    reused++;
                    current[key] = entry;
                    return cached;
                }
                var original = ImageConversion.EncodeToPNG(duplicateTexture(texture));
                verified++;
                if (!original.AsSpan().SequenceEqual(cached))
                {
                    mismatches++;
                    Log.Warning($"[RLO] {Feature} verify: image for '{itemPath}' differs from last start's ({original.Length} vs {cached.Length} bytes)");
                }
                current[key] = (Sha(original), original.Length);
                return original;
            }
            var bytes = ImageConversion.EncodeToPNG(duplicateTexture(texture));
            encoded++;
            if (key == null)
            {
                uncacheable++;
            }
            else
            {
                current[key] = (Sha(bytes), bytes.Length);
            }
            return bytes;
        }

        public static void After(Exception __exception)
        {
            if (!active || current == null)
            {
                return;
            }
            try
            {
                if (__exception == null && manager.GetValue(null) != null)
                {
                    Save(current);
                }
            }
            catch (Exception e)
            {
                Log.Message($"[RLO] {Feature}: could not save the image list ({e.GetType().Name}: {e.Message}); the next start encodes all images again.");
            }
            finally
            {
                current = null;
                previous = null;
                stored = null;
                files = null;
                ticks += Stopwatch.GetTimestamp() - start;
            }
        }

        private static string Key(string itemPath, Texture2D texture)
        {
            if (texture == null || itemPath == null || watched.Any(m => Harmony.GetPatchInfo(m) != null))
            {
                return null;
            }
            var mods = LoadedModManager.RunningModsListForReading;
            ModContentPack winner = null;
            for (int i = mods.Count - 1; i >= 0; i--)
            {
                var holder = mods[i].GetContentHolder<Texture2D>();
                if (holder.contentList.Count == 0)
                {
                    continue;
                }
                var found = holder.Get(itemPath);
                if (found != null)
                {
                    if (!ReferenceEquals(found, texture))
                    {
                        return null;
                    }
                    winner = mods[i];
                    break;
                }
            }
            if (winner == null)
            {
                return null;
            }
            if (!files.TryGetValue(winner, out var all))
            {
                all = ModContentPack.GetAllFilesForMod(winner, GenFilePaths.ContentPath<Texture2D>());
                files[winner] = all;
            }
            var wanted = GenFilePaths.ContentPath<Texture2D>() + itemPath;
            var sources = all
                .Select(p => (rel: p.Key.Replace('\\', '/'), info: p.Value))
                .Where(p => string.Equals(p.rel.Substring(0, p.rel.Length - Path.GetExtension(p.rel).Length), wanted, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.rel, StringComparer.Ordinal)
                .ToList();
            if (sources.Count == 0)
            {
                return null;
            }
            var sb = new StringBuilder();
            sb.Append(Engine()).Append('\n').Append(itemPath).Append('|').Append(winner.PackageId).Append('|')
                .Append(texture.width).Append('x').Append(texture.height).Append('|').Append(texture.format).Append('|').Append(texture.mipmapCount);
            foreach (var (rel, info) in sources)
            {
                sb.Append('\n').Append(rel).Append('|').Append(info.FullName).Append('|').Append(info.Length).Append('@').Append(info.LastWriteTimeUtc.Ticks);
            }
            return Sha(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        private static string Engine()
        {
            if (engine != null)
            {
                return engine;
            }
            var sb = new StringBuilder();
            sb.Append(Magic).Append('|').Append(VersionControl.CurrentVersionStringWithRev).Append('|').Append(RuntimeInformation.FrameworkDescription)
                .Append('|').Append(RuntimeInformation.OSDescription).Append('|').Append(RuntimeInformation.ProcessArchitecture)
                .Append('|').Append(SystemInfo.graphicsDeviceName).Append('|').Append(SystemInfo.graphicsDeviceVendorID).Append('|').Append(SystemInfo.graphicsDeviceVersion)
                .Append('|').Append(UnityData.ComputeShadersSupported).Append('|').Append(Prefs.TextureCompression).Append('|').Append(QualitySettings.activeColorSpace);
            var assemblies = new[] { duplicate.Module.Assembly, typeof(ImageConversion).Assembly, typeof(Graphics).Assembly, typeof(Texture2D).Assembly, typeof(object).Assembly, typeof(System.IO.Compression.ZLibStream).Assembly }
                .Concat(AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name.StartsWith("Waylith", StringComparison.Ordinal)))
                .Distinct()
                .OrderBy(a => a.FullName, StringComparer.Ordinal);
            foreach (var assembly in assemblies)
            {
                sb.Append('\n').Append(assembly.FullName).Append(' ').Append(assembly.ManifestModule.ModuleVersionId);
            }
            engine = sb.ToString();
            return engine;
        }

        private static Dictionary<string, (string sha, long length)> Load()
        {
            var result = new Dictionary<string, (string, long)>(StringComparer.Ordinal);
            if (!File.Exists(cachePath))
            {
                return result;
            }
            foreach (var line in File.ReadAllLines(cachePath))
            {
                var parts = line.Split('\t');
                if (parts.Length == 3 && long.TryParse(parts[2], out var length))
                {
                    result[parts[0]] = (parts[1], length);
                }
            }
            return result;
        }

        private static void Save(Dictionary<string, (string sha, long length)> entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
            var temp = cachePath + ".tmp";
            File.WriteAllLines(temp, entries.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}\t{e.Value.sha}\t{e.Value.length}"));
            File.Move(temp, cachePath, true);
        }

        private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

        public static string Stats()
        {
            var text = $"{Feature}: {reused} reused, {encoded} encoded ({uncacheable} not cacheable) in {refreshes} refreshes, {LeanFixes.Seconds(ticks) * 1000:F0} ms";
            return RLOMod.Verify ? text + $"; verify {verified} checked, {mismatches} mismatches" : text;
        }
    }
}
#endif
