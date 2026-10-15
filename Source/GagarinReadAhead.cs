#if RW17
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class GagarinReadAhead
    {
        private const int Chunk = 8 << 20;

        private static string state = "not started";
        private static int files;
        private static long bytes;
        private static double seconds;
        private static double finishedAt;

        public static void Start()
        {
            if (!RLOMod.Settings.Supports("missilegirl") || RLOMod.Running("vr.missilegirl") == null)
            {
                return;
            }
            LeanFixes.Reports.Add(Stats);
            var folder = Path.Combine(Directory.GetParent(GenFilePaths.ConfigFolderPath).FullName, "MissileGirl", "Cache");
            var paths = new[] { "AssetsHash.xml", "AssetsHashInt.xml", "Unified.xml" }.Select(name => Path.Combine(folder, name)).ToArray();
            if (!paths.All(File.Exists))
            {
                state = "skipped, Missile Girl has no cache yet";
                return;
            }
            if (!SameModList(Path.Combine(folder, "ModList.xml")))
            {
                state = "skipped, the mod list changed so Missile Girl rebuilds its cache";
                return;
            }
            state = "running";
            new Thread(() => Read(paths)) { IsBackground = true, Name = "RLO cache read-ahead", Priority = ThreadPriority.BelowNormal }.Start();
        }

        private static bool SameModList(string path)
        {
            try
            {
                var saved = new List<string>();
                using (var reader = XmlReader.Create(path, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true, CheckCharacters = false }))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1 && reader.Name == "Mod")
                        {
                            saved.Add(reader.GetAttribute("packageId"));
                        }
                    }
                }
                return saved.SequenceEqual(LoadedModManager.RunningMods.Select(m => m.PackageId));
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void Read(string[] paths)
        {
            var watch = Stopwatch.StartNew();
            var buffer = new byte[Chunk];
            try
            {
                foreach (var path in paths)
                {
                    bytes += ReadFile(path, buffer);
                    files++;
                }
                state = "done";
            }
            catch (Exception e)
            {
                state = "stopped early (" + e.GetType().Name + ")";
            }
            seconds = watch.Elapsed.TotalSeconds;
            finishedAt = Measurement.ProcessStart is DateTime start ? (DateTime.Now - start).TotalSeconds : 0;
        }

        private static long ReadFile(string path, byte[] buffer)
        {
            long offset = 0;
            while (true)
            {
                int read;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
                {
                    if (offset >= stream.Length)
                    {
                        return offset;
                    }
                    stream.Position = offset;
                    read = stream.Read(buffer, 0, buffer.Length);
                }
                if (read <= 0)
                {
                    return offset;
                }
                offset += read;
            }
        }

        public static string Stats() => state == "running" || state == "done" || state.StartsWith("stopped")
            ? $"Missile Girl cache read ahead into the file cache: {state}, {files} files, {bytes >> 20} MB in {seconds:F2} s, finished {finishedAt:F1} s after start"
            : $"Missile Girl cache read ahead: {state}";
    }
}
#endif
