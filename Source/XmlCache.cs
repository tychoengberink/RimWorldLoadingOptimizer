#if RW17
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class XmlCache
    {
        public const string Feature = "XML cache";
        private const string Format = "rlo-xmlcache 1";
        private const string MissileGirlHarmonyId = "VR.MissileGirl.Gagarin";

        private static readonly FieldInfo AssetName = AccessTools.Field(typeof(LoadableXmlAsset), nameof(LoadableXmlAsset.name));
        private static readonly FieldInfo AssetFolder = AccessTools.Field(typeof(LoadableXmlAsset), nameof(LoadableXmlAsset.fullFolderPath));
        private static readonly FieldInfo AssetMod = AccessTools.Field(typeof(LoadableXmlAsset), nameof(LoadableXmlAsset.mod));

        private sealed class Input
        {
            public string PackageId;
            public List<string> Folders;
        }

        private sealed class Cached
        {
            public XmlDocument Document;
            public List<LoadableXmlAsset> Assets;
            public int[] Owners;
            public Dictionary<string, int> Counts;
            public int Patched;
        }

        private static string Dir => Path.Combine(GenFilePaths.SaveDataFolderPath, "RimWorldLoadingOptimizer", "xmlcache");
        private static string XmlPath => Path.Combine(Dir, "defs.xml");
        private static string MetaPath => Path.Combine(Dir, "defs.meta");

        private static Task<string> fingerprint;
        private static Task<Cached> cached;
        private static bool armed = true;
        private static bool active;
        private static bool skipPatchCleanup;
        private static Cached hit;
        private static List<string> pendingAssets;
        private static int[] pendingOwners;
        private static string state = "not reached";
        private static int files;
        private static double fingerprintSeconds, readSeconds, waitSeconds, writeSeconds;
        private static long readBytes;
        private static Task<System.Collections.Concurrent.ConcurrentDictionary<ModContentPack, LoadableXmlAsset[]>> patchFiles;
        private static int patchFilesServed;
        [ThreadStatic] private static bool readingAhead;
        private static double patchSeconds;

        public static bool CacheFileExists => File.Exists(MetaPath) && File.Exists(XmlPath);

        public static void Install(Harmony harmony)
        {
            if (AssetName == null || AssetFolder == null || AssetMod == null)
            {
                VersionCheck.Skip(Feature, "the game's XML asset fields changed");
                return;
            }
            var mods = LoadedModManager.RunningModsListForReading;
            var inputs = mods.Select(m => new Input { PackageId = m.PackageId, Folders = m.foldersToLoadDescendingOrder.ToList() }).ToList();
            var settingFiles = SettingFiles(mods);
            var version = RimWorld.VersionControl.CurrentVersionStringWithRev;
            fingerprint = Task.Run(() => Fingerprint(version, inputs, settingFiles));
            cached = fingerprint.ContinueWith(t => t.Status == TaskStatus.RanToCompletion ? Read(t.Result, mods) : null, TaskContinuationOptions.ExecuteSynchronously);
            patchFiles = fingerprint.ContinueWith(t => t.Status == TaskStatus.RanToCompletion && t.Result == StoredFingerprint() ? ReadPatchFiles(mods) : null);

            harmony.Patch(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.LoadModXML)),
                prefix: new HarmonyMethod(typeof(XmlCache), nameof(LoadModXmlPrefix)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.CombineIntoUnifiedXML)),
                prefix: new HarmonyMethod(typeof(XmlCache), nameof(CombinePrefix)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.ApplyPatches)),
                prefix: new HarmonyMethod(typeof(XmlCache), nameof(ApplyPatchesPrefix)) { priority = Priority.Last },
                postfix: new HarmonyMethod(typeof(XmlCache), nameof(ApplyPatchesPostfix)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(DirectXmlLoader), nameof(DirectXmlLoader.XmlAssetsInModFolder)),
                prefix: new HarmonyMethod(typeof(XmlCache), nameof(PatchFilesPrefix)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.ClearCachedPatches)),
                prefix: new HarmonyMethod(typeof(XmlCache), nameof(ClearCachedPatchesPrefix)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.ParseAndProcessXML)),
                postfix: new HarmonyMethod(typeof(XmlCache), nameof(ParseAndProcessXmlPostfix)) { priority = Priority.Last });

            var missileGirl = AccessTools.Constructor(AccessTools.TypeByName("MissileGirl.RocketMod"), new[] { typeof(ModContentPack) });
            if (Harmony.GetPatchInfo(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.LoadModXML)))?.Owners.Contains(MissileGirlHarmonyId) == true)
            {
                MissileGirlOff();
            }
            else if (missileGirl != null)
            {
                harmony.Patch(missileGirl, postfix: new HarmonyMethod(typeof(XmlCache), nameof(MissileGirlOff)));
            }
            LeanFixes.Reports.Add(Stats);
        }

        private static List<string> SettingFiles(List<ModContentPack> mods)
        {
            var assemblies = new HashSet<Assembly>(typeof(PatchOperation).AllSubclasses().Select(t => t.Assembly));
            var result = new List<string>();
            foreach (var mod in mods)
            {
                if (mod.assemblies.loadedAssemblies.Any(assemblies.Contains) && Directory.Exists(GenFilePaths.ConfigFolderPath))
                {
                    result.AddRange(Directory.GetFiles(GenFilePaths.ConfigFolderPath, $"Mod_{mod.FolderName}_*.xml").OrderBy(f => f, StringComparer.Ordinal));
                }
            }
            return result;
        }

        private static string Fingerprint(string version, List<Input> inputs, List<string> settingFiles)
        {
            var watch = Stopwatch.StartNew();
            var perMod = new string[inputs.Count];
            var counts = new int[inputs.Count];
            Parallel.For(0, inputs.Count, i =>
            {
                var sb = new StringBuilder();
                sb.Append(inputs[i].PackageId).Append('\n');
                foreach (var folder in inputs[i].Folders)
                {
                    sb.Append("folder ").Append(folder).Append('\n');
                    counts[i] += Stamp(sb, folder, "Defs/", "*.xml") + Stamp(sb, folder, "Patches/", "*.xml") + Stamp(sb, folder, "Assemblies/", "*.dll");
                }
                perMod[i] = sb.ToString();
            });
            using (var sha = SHA256.Create())
            {
                var all = new StringBuilder();
                all.Append(Format).Append('\n').Append(version).Append('\n');
                foreach (var mod in perMod)
                {
                    all.Append(mod);
                }
                foreach (var file in settingFiles)
                {
                    all.Append("setting ").Append(Path.GetFileName(file)).Append(' ').Append(Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(file)))).Append('\n');
                }
                files = counts.Sum();
                var result = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(all.ToString())));
                fingerprintSeconds = watch.Elapsed.TotalSeconds;
                return result;
            }
        }

        private static int Stamp(StringBuilder sb, string folder, string sub, string pattern)
        {
            var dir = new DirectoryInfo(Path.Combine(folder, sub));
            if (!dir.Exists)
            {
                return 0;
            }
            var found = dir.GetFiles(pattern, SearchOption.AllDirectories);
            Array.Sort(found, (a, b) => string.CompareOrdinal(a.FullName, b.FullName));
            foreach (var file in found)
            {
                sb.Append(file.FullName, folder.Length, file.FullName.Length - folder.Length).Append(' ').Append(file.Length).Append(' ').Append(file.LastWriteTimeUtc.Ticks).Append('\n');
            }
            return found.Length;
        }

        private static string StoredFingerprint()
        {
            try
            {
                if (!File.Exists(MetaPath) || !File.Exists(XmlPath))
                {
                    return null;
                }
                using (var reader = new StreamReader(MetaPath))
                {
                    return reader.ReadLine() == Format ? reader.ReadLine()?.Substring("fingerprint ".Length) : null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool WillHit()
        {
            try
            {
                return fingerprint != null && fingerprint.Result != null && fingerprint.Result == StoredFingerprint();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Cached Read(string current, List<ModContentPack> mods)
        {
            if (current == null || current != StoredFingerprint())
            {
                state = File.Exists(MetaPath) ? "miss, a mod, a file, a setting or the game changed" : "miss, no cache yet";
                return null;
            }
            try
            {
                var watch = Stopwatch.StartNew();
                var result = new Cached { Assets = new List<LoadableXmlAsset>(), Counts = new Dictionary<string, int>() };
                foreach (var line in File.ReadLines(MetaPath).Skip(2))
                {
                    var space = line.IndexOf(' ');
                    var kind = space < 0 ? line : line.Substring(0, space);
                    var rest = space < 0 ? "" : line.Substring(space + 1);
                    switch (kind)
                    {
                        case "asset":
                            var parts = rest.Split('\t');
                            var index = int.Parse(parts[0]);
                            result.Assets.Add(Asset(index >= 0 && index < mods.Count ? mods[index] : null, parts[1], parts[2]));
                            break;
                        case "owners":
                            result.Owners = rest.Length == 0 ? new int[0] : rest.Split(' ').Select(int.Parse).ToArray();
                            break;
                        case "count":
                            var tab = rest.LastIndexOf('\t');
                            result.Counts[rest.Substring(0, tab)] = int.Parse(rest.Substring(tab + 1));
                            break;
                        case "patched":
                            result.Patched = int.Parse(rest);
                            break;
                    }
                }
                var document = new XmlDocument();
                var settings = new XmlReaderSettings { IgnoreComments = false, IgnoreWhitespace = false, CheckCharacters = false, DtdProcessing = DtdProcessing.Prohibit };
                using (var stream = new FileStream(XmlPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                using (var reader = XmlReader.Create(stream, settings))
                {
                    readBytes = stream.Length;
                    document.Load(reader);
                }
                if (result.Owners == null || document.DocumentElement == null || document.DocumentElement.ChildNodes.Count != result.Owners.Length)
                {
                    state = "miss, the cache file does not match its index";
                    return null;
                }
                result.Document = document;
                readSeconds = watch.Elapsed.TotalSeconds;
                return result;
            }
            catch (Exception e)
            {
                state = $"miss, the cache could not be read ({e.GetType().Name}: {e.Message})";
                return null;
            }
        }

        private static System.Collections.Concurrent.ConcurrentDictionary<ModContentPack, LoadableXmlAsset[]> ReadPatchFiles(List<ModContentPack> mods)
        {
            var watch = Stopwatch.StartNew();
            var result = new System.Collections.Concurrent.ConcurrentDictionary<ModContentPack, LoadableXmlAsset[]>();
            Parallel.ForEach(mods, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, mod =>
            {
                readingAhead = true;
                try
                {
                    result[mod] = DirectXmlLoader.XmlAssetsInModFolder(mod, "Patches/");
                }
                finally
                {
                    readingAhead = false;
                }
            });
            patchSeconds = watch.Elapsed.TotalSeconds;
            return result;
        }

        public static bool PatchFilesPrefix(ModContentPack mod, string folderPath, List<string> foldersToLoadDebug, ref LoadableXmlAsset[] __result)
        {
            if (readingAhead || !skipPatchCleanup || folderPath != "Patches/" || foldersToLoadDebug != null || patchFiles == null)
            {
                return true;
            }
            System.Collections.Concurrent.ConcurrentDictionary<ModContentPack, LoadableXmlAsset[]> ready;
            try
            {
                ready = patchFiles.Result;
            }
            catch (Exception)
            {
                return true;
            }
            if (ready == null || !ready.TryRemove(mod, out var assets))
            {
                return true;
            }
            patchFilesServed++;
            __result = assets;
            return false;
        }

        private static LoadableXmlAsset Asset(ModContentPack mod, string folder, string name)
        {
            var asset = (LoadableXmlAsset)RuntimeHelpers.GetUninitializedObject(typeof(LoadableXmlAsset));
            AssetName.SetValue(asset, name);
            AssetFolder.SetValue(asset, folder);
            AssetMod.SetValue(asset, mod);
            return asset;
        }

        public static void MissileGirlOff()
        {
            if (!WillHit())
            {
                return;
            }
            new Harmony(MissileGirlHarmonyId).UnpatchAll(MissileGirlHarmonyId);
            Log.Message("[RLO] XML cache: Missile Girl's XML caching stays off for this start, RimWorld Loading Optimizer's XML cache is used instead");
        }

        public static bool LoadModXmlPrefix(ref List<LoadableXmlAsset> __result)
        {
            if (!armed)
            {
                return true;
            }
            armed = false;
            var watch = Stopwatch.StartNew();
            try
            {
                hit = cached?.Result;
            }
            catch (Exception)
            {
                hit = null;
            }
            waitSeconds = watch.Elapsed.TotalSeconds;
            if (hit == null)
            {
                return true;
            }
            active = true;
            skipPatchCleanup = true;
            __result = new List<LoadableXmlAsset>();
            return false;
        }

        public static bool CombinePrefix(Dictionary<XmlNode, LoadableXmlAsset> assetlookup, ref XmlDocument __result)
        {
            if (!active)
            {
                return true;
            }
            var i = 0;
            for (var child = hit.Document.DocumentElement.FirstChild; child != null; child = child.NextSibling, i++)
            {
                var owner = hit.Owners[i];
                if (owner >= 0)
                {
                    assetlookup[child] = hit.Assets[owner];
                }
            }
            __result = hit.Document;
            return false;
        }

        public static bool ApplyPatchesPrefix() => !active;

        public static void ApplyPatchesPostfix(XmlDocument xmlDoc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup)
        {
            if (active || armed || pendingOwners != null)
            {
                return;
            }
            var watch = Stopwatch.StartNew();
            try
            {
                Directory.CreateDirectory(Dir);
                File.Delete(MetaPath);
                var mods = LoadedModManager.RunningModsListForReading;
                var assetIndex = new Dictionary<LoadableXmlAsset, int>();
                var assets = new List<string>();
                var owners = new int[xmlDoc.DocumentElement.ChildNodes.Count];
                var i = -1;
                for (var child = xmlDoc.DocumentElement.FirstChild; child != null; child = child.NextSibling)
                {
                    owners[++i] = -1;
                    if (!assetlookup.TryGetValue(child, out var asset) || asset == null)
                    {
                        continue;
                    }
                    if (!assetIndex.TryGetValue(asset, out var index))
                    {
                        index = assets.Count;
                        assetIndex[asset] = index;
                        assets.Add($"asset {mods.IndexOf(asset.mod)}\t{asset.fullFolderPath}\t{asset.name}");
                    }
                    owners[i] = index;
                }
                var settings = new XmlWriterSettings { Indent = false, CheckCharacters = false, NewLineHandling = NewLineHandling.Entitize, Encoding = new UTF8Encoding(false) };
                var temp = XmlPath + ".tmp";
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                using (var writer = XmlWriter.Create(stream, settings))
                {
                    xmlDoc.Save(writer);
                }
                File.Move(temp, XmlPath, true);
                pendingAssets = assets;
                pendingOwners = owners;
                writeSeconds = watch.Elapsed.TotalSeconds;
            }
            catch (Exception e)
            {
                state = $"miss, the cache could not be written ({e.GetType().Name}: {e.Message})";
                pendingOwners = null;
            }
        }

        public static bool ClearCachedPatchesPrefix()
        {
            if (!skipPatchCleanup)
            {
                return true;
            }
            skipPatchCleanup = false;
            foreach (var mod in LoadedModManager.RunningModsListForReading)
            {
                mod.ClearPatchesCache();
            }
            return false;
        }

        private static Dictionary<string, int> Counts() => LoadedModManager.RunningModsListForReading
            .GroupBy(m => m.PackageId)
            .ToDictionary(g => g.Key, g => g.Sum(m => m.AllDefs.Count()));

        public static void ParseAndProcessXmlPostfix()
        {
            if (active)
            {
                active = false;
                var counts = Counts();
                var patched = LoadedModManager.PatchedDefsForReading.Count;
                var wrong = counts.Where(c => !hit.Counts.TryGetValue(c.Key, out var expected) || expected != c.Value).Select(c => c.Key).ToList();
                if (wrong.Count > 0 || counts.Count != hit.Counts.Count || patched != hit.Patched)
                {
                    state = $"hit, but the def counts differ ({string.Join(", ", wrong.Take(5))}{(patched != hit.Patched ? ", defs added by patches" : "")}); cache deleted, the next start loads normally";
                    Log.Warning("[RLO] " + Feature + ": " + state);
                    TryDelete();
                }
                else
                {
                    state = $"hit, {counts.Values.Sum() + patched} defs, counts per mod match";
                }
                hit = null;
                return;
            }
            if (pendingOwners == null)
            {
                return;
            }
            try
            {
                var sb = new StringBuilder();
                sb.Append(Format).Append('\n').Append("fingerprint ").Append(fingerprint.Result).Append('\n');
                foreach (var asset in pendingAssets)
                {
                    sb.Append(asset).Append('\n');
                }
                sb.Append("owners ").Append(string.Join(" ", pendingOwners)).Append('\n');
                foreach (var count in Counts())
                {
                    sb.Append("count ").Append(count.Key).Append('\t').Append(count.Value).Append('\n');
                }
                sb.Append("patched ").Append(LoadedModManager.PatchedDefsForReading.Count).Append('\n');
                File.WriteAllText(MetaPath + ".tmp", sb.ToString());
                File.Move(MetaPath + ".tmp", MetaPath, true);
                state = state.StartsWith("miss") ? state + $"; new cache written in {writeSeconds:F2} s" : $"miss; new cache written in {writeSeconds:F2} s";
            }
            catch (Exception e)
            {
                state = $"miss, the cache could not be written ({e.GetType().Name}: {e.Message})";
                TryDelete();
            }
            pendingOwners = null;
            pendingAssets = null;
        }

        private static void TryDelete()
        {
            try
            {
                File.Delete(MetaPath);
                File.Delete(XmlPath);
            }
            catch (Exception)
            {
            }
        }

        public static string Stats() =>
            $"XML cache: {state}; key over {files:N0} files in {fingerprintSeconds:F2} s on a worker; cache read {readBytes >> 20} MB in {readSeconds:F2} s on a worker, LoadModXML waited {waitSeconds:F2} s for it; patch files of {patchFilesServed} mods read ahead on workers in {patchSeconds:F2} s";
    }
}
#endif
