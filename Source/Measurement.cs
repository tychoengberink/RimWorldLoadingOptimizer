using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class Measurement
    {
        public const int Idle = 0;
        public const int MeasureOff = 1;
        public const int MeasureOn = 2;

        private static RLOMod mod;
        private static RLOSettings persisted;
        private static bool featuresOff;
        private static bool done;
        private static readonly Stopwatch patching = new Stopwatch();
        private static DateTime? processStart;
        private static bool processStartRead;
        private static DateTime modStart;

        public static DateTime? ProcessStart
        {
            get
            {
                if (!processStartRead)
                {
                    processStartRead = true;
                    try
                    {
                        using (var process = Process.GetCurrentProcess())
                        {
                            processStart = process.StartTime;
                        }
                    }
                    catch (Exception)
                    {
                        processStart = null;
                    }
                }
                return processStart;
            }
        }

        private const double ColdPatchSeconds = 2.0;

        [DllImport("/usr/lib/libSystem.dylib")]
        private static extern int proc_pid_rusage(int pid, int flavor, long[] info);

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCounters
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
        private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);

        public static string Duration(double seconds)
        {
            var whole = (int)Math.Round(Math.Abs(seconds));
            return $"{(seconds < 0 ? "-" : "")}{whole / 60}:{whole % 60:00}";
        }

        private static bool MacFootprint(out long peakMb, out long nowMb)
        {
            peakMb = nowMb = 0;
            if (Application.platform != RuntimePlatform.OSXPlayer)
            {
                return false;
            }
            var info = new long[64];
            int pid;
            using (var process = Process.GetCurrentProcess())
            {
                pid = process.Id;
            }
            if (proc_pid_rusage(pid, 4, info) != 0)
            {
                return false;
            }
            nowMb = info[9] >> 20;
            peakMb = info[30] >> 20;
            return peakMb > 0;
        }

        private static bool WindowsMemory(out long peakMb, out long nowMb)
        {
            peakMb = nowMb = 0;
            if (Application.platform != RuntimePlatform.WindowsPlayer)
            {
                return false;
            }
            var size = (uint)Marshal.SizeOf(typeof(ProcessMemoryCounters));
            var counters = new ProcessMemoryCounters { cb = size };
            if (!GetProcessMemoryInfo(GetCurrentProcess(), ref counters, size))
            {
                return false;
            }
            peakMb = (long)counters.PeakWorkingSetSize.ToUInt64() >> 20;
            nowMb = (long)counters.WorkingSetSize.ToUInt64() >> 20;
            return peakMb > 0;
        }

        private static bool LinuxMemory(out long peakMb, out long nowMb)
        {
            peakMb = nowMb = 0;
            if (Application.platform != RuntimePlatform.LinuxPlayer || !File.Exists("/proc/self/status"))
            {
                return false;
            }
            foreach (var line in File.ReadAllLines("/proc/self/status"))
            {
                if (line.StartsWith("VmHWM:", StringComparison.Ordinal))
                {
                    peakMb = Kilobytes(line) >> 10;
                }
                else if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                {
                    nowMb = Kilobytes(line) >> 10;
                }
            }
            return peakMb > 0;
        }

        private static long Kilobytes(string statusLine)
        {
            var parts = statusLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        }

        private static void Memory(out long peakMb, out long nowMb)
        {
            if (MacFootprint(out peakMb, out nowMb) || WindowsMemory(out peakMb, out nowMb) || LinuxMemory(out peakMb, out nowMb))
            {
                return;
            }
            using (var process = Process.GetCurrentProcess())
            {
                peakMb = process.PeakWorkingSet64 >> 20;
                nowMb = process.WorkingSet64 >> 20;
            }
        }

        public static string CsvPath => Path.Combine(GenFilePaths.SaveDataFolderPath, "RimWorldLoadingOptimizer", "measurements.csv");

        public static string HtmlPath => Path.Combine(GenFilePaths.SaveDataFolderPath, "RimWorldLoadingOptimizer", "measurements.html");

        public static void Install(Harmony harmony, RLOMod owner, RLOSettings saved, bool off)
        {
            mod = owner;
            persisted = saved;
            featuresOff = off;
            modStart = DateTime.Now;
            _ = ProcessStart;
            harmony.Patch(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.ApplyPatches)),
                prefix: new HarmonyMethod(typeof(Measurement), nameof(PatchingStart)),
                finalizer: new HarmonyMethod(typeof(Measurement), nameof(PatchingEnd)));
            MainMenu.Then(OnMainMenu);
            harmony.Patch(AccessTools.Method(typeof(SavedGameLoaderNow), nameof(SavedGameLoaderNow.LoadGameFromSaveFileNow)),
                prefix: new HarmonyMethod(typeof(Measurement), nameof(OnGameStart)));
            harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.InitNewGame)),
                prefix: new HarmonyMethod(typeof(Measurement), nameof(OnGameStart)));
        }

        public static void PatchingStart()
        {
            patching.Start();
        }

        public static void PatchingEnd()
        {
            patching.Stop();
        }

        private static bool Cold(double patchSeconds)
        {
            var context = AccessTools.TypeByName("Gagarin.Context");
            var usingCache = context == null ? null : AccessTools.PropertyGetter(context, "IsUsingCache");
            if (usingCache != null)
            {
                return !(bool)usingCache.Invoke(null, null);
            }
            return RLOMod.Running("wowgag.guiperformancepatch") == null || patchSeconds >= ColdPatchSeconds;
        }

        private static string StartKind(bool cold) => cold ? "cold start (no XML cache was used)" : "warm start (XML cache used)";

        public static void OnMainMenu() => RecordOnce(false);

        public static void OnGameStart() => RecordOnce(true);

        private static void RecordOnce(bool straightIntoGame)
        {
            if (done)
            {
                return;
            }
            done = true;
            try
            {
                Record(straightIntoGame);
            }
            catch (Exception e)
            {
                Log.Warning($"[RLO] measurement failed: {e.Message}");
            }
        }

        private static void Record(bool straightIntoGame)
        {
            var seconds = (DateTime.Now - (ProcessStart ?? modStart)).TotalSeconds;
            var patchSeconds = patching.Elapsed.TotalSeconds;
            var cold = Cold(patchSeconds);
            long peakMb = 0, workingMb = 0;
            try
            {
                Memory(out peakMb, out workingMb);
            }
            catch
            {
            }
            var mode = featuresOff ? "off" : "on";
            var from = ProcessStart == null ? "since RimWorld Loading Optimizer loaded" : "since start";
            var memory = (peakMb > 0 ? $", peak memory {peakMb:N0} MB" : "") + (workingMb > 0 ? $", {workingMb:N0} MB {(straightIntoGame ? "at that moment" : "at the main menu")}" : "");
            var reached = straightIntoGame ? "startup finished (went straight into a game, no main menu) at" : "main menu reached at";
            Log.Message($"[RLO] {reached} {seconds:F1} s ({Duration(seconds)}) {from} (RimWorld Loading Optimizer {mode}, XML patching {patchSeconds:F1} s, {StartKind(cold)}{memory})");
            AppendCsv(mode, seconds, patchSeconds, cold, peakMb, workingMb);
            WriteHtml();
            if (straightIntoGame)
            {
                return;
            }
            if (persisted.measureStep == MeasureOff && featuresOff)
            {
                persisted.measureStep = MeasureOn;
                persisted.measureOffSeconds = (float)seconds;
                persisted.measureOffPatchSeconds = (float)patchSeconds;
                persisted.measureOffCold = cold;
                persisted.measureOffPeakMb = peakMb;
                mod.WriteSettings();
                Find.WindowStack.Add(new Dialog_MessageBox($"RimWorld Loading Optimizer measurement, step 1 of 2 done.\n\nWith RimWorld Loading Optimizer switched off, RimWorld needed {Duration(seconds)} ({seconds:F0} s) to reach the main menu.\n\nNow quit RimWorld and start it again. The next start runs with RimWorld Loading Optimizer switched on and shows the difference.\n\nDo not change mods or settings in between."));
                return;
            }
            if (persisted.measureStep == MeasureOn && !featuresOff)
            {
                persisted.measureStep = Idle;
                var off = persisted.measureOffSeconds;
                var saved = off - seconds;
                var percent = off > 0 ? saved / off * 100 : 0;
                var fair = persisted.measureOffCold == cold;
                var peakMemory = persisted.measureOffPeakMb > 0 && peakMb > 0 ? $"\nPeak memory: {persisted.measureOffPeakMb:N0} MB off, {peakMb:N0} MB on." : "";
                var warning = fair ? "" : "\n\nWarning: one start used an XML cache and the other did not, for example because a mod updated in between. This comparison is not fair; measure again.";
                var change = saved >= 0 ? $"{Duration(saved)} faster ({percent:F0}%)" : $"{Duration(-saved)} slower ({-percent:F0}%)";
                persisted.lastMeasurement = $"{DateTime.Now:yyyy-MM-dd HH:mm}: off {Duration(off)}, on {Duration(seconds)}, {change}{(fair ? "" : " (not comparable: warm vs cold)")}";
                mod.WriteSettings();
                Find.WindowStack.Add(new Dialog_MessageBox($"RimWorld Loading Optimizer measurement result\n\nTime to the main menu (minutes:seconds):\n  RimWorld Loading Optimizer off: {Duration(off)} ({off:F0} s)\n  RimWorld Loading Optimizer on: {Duration(seconds)} ({seconds:F0} s)\n  Difference: {Duration(saved)} ({percent:F0}%){peakMemory}\n\nThis is one pair of starts on your PC with your mods; repeat it a few times for a reliable number. The first start after installing or updating RimWorld Loading Optimizer (or any mod) also builds its texture atlas cache and is slower than later starts. All measurements are saved in:\n{HtmlPath}\n{CsvPath}{warning}"));
            }
        }

        private static void AppendCsv(string mode, double seconds, double patchSeconds, bool cold, long peakMb, long workingMb)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CsvPath));
                var exists = File.Exists(CsvPath);
                using (var writer = new StreamWriter(CsvPath, true))
                {
                    if (!exists)
                    {
                        writer.WriteLine("date,optimizer,seconds_to_main_menu,xml_patching_seconds,start,peak_memory_mb,memory_at_menu_mb,active_mods,optimizer_version,os");
                    }
                    var inv = CultureInfo.InvariantCulture;
                    writer.WriteLine(string.Join(",",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", inv),
                        mode,
                        seconds.ToString("F1", inv),
                        patchSeconds.ToString("F1", inv),
                        cold ? "cold" : "warm",
                        peakMb.ToString(inv),
                        workingMb.ToString(inv),
                        LoadedModManager.RunningModsListForReading.Count.ToString(inv),
                        mod.Content.ModMetaData.ModVersion ?? "",
                        SystemInfo.operatingSystem.Replace(",", " ")));
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[RLO] could not write {CsvPath}: {e.Message}");
            }
        }

        private static void WriteHtml()
        {
            try
            {
                var rows = File.ReadAllLines(CsvPath).Skip(1).Select(line => line.Split(',')).Where(fields => fields.Length >= 10).Reverse().ToList();
                var inv = CultureInfo.InvariantCulture;
                var longest = rows.Select(fields => double.TryParse(fields[2], NumberStyles.Float, inv, out var value) ? value : 0).DefaultIfEmpty(1).Max();
                var html = new StringBuilder();
                html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>RimWorld Loading Optimizer measurements</title><style>")
                    .Append("body{font-family:system-ui,sans-serif;margin:24px;background:#16181c;color:#e6e6e6}h1{font-size:20px}p{color:#aaa}")
                    .Append("table{border-collapse:collapse;width:100%}th,td{padding:6px 10px;border-bottom:1px solid #333;text-align:left;white-space:nowrap}")
                    .Append("th{color:#aaa;font-weight:600}td.num{text-align:right;font-variant-numeric:tabular-nums}.bar{height:10px;border-radius:3px}")
                    .Append(".on{background:#4caf7d}.off{background:#888}.cold{color:#e0a050}.warm{color:#7fb8e6}</style></head><body>")
                    .Append("<h1>RimWorld Loading Optimizer: time to the main menu</h1>")
                    .Append("<p>Newest first. A cold start used no XML cache (no cache mod, or the first start after a mod or mod-list change); a warm start loaded the defs from an XML cache. Compare cold with cold and warm with warm. Peak memory is the physical footprint on macOS, the peak working set on Windows and VmHWM on Linux, so only compare rows from the same system.</p>")
                    .Append("<table><tr><th>Date</th><th>Optimizer</th><th>Time</th><th></th><th>XML patching</th><th>Start</th><th>Peak memory</th><th>Memory at menu</th><th>Mods</th><th>Version</th><th>System</th></tr>");
                foreach (var fields in rows)
                {
                    double.TryParse(fields[2], NumberStyles.Float, inv, out var seconds);
                    double.TryParse(fields[3], NumberStyles.Float, inv, out var patchSeconds);
                    var width = longest > 0 ? seconds / longest * 240 : 0;
                    html.Append("<tr><td>").Append(Escape(fields[0]))
                        .Append("</td><td>").Append(Escape(fields[1]))
                        .Append("</td><td class=\"num\">").Append(Duration(seconds)).Append(" (").Append(seconds.ToString("F0", inv)).Append(" s)")
                        .Append("</td><td><div class=\"bar ").Append(fields[1] == "off" ? "off" : "on").Append("\" style=\"width:").Append(width.ToString("F0", inv)).Append("px\"></div>")
                        .Append("</td><td class=\"num\">").Append(patchSeconds.ToString("F1", inv)).Append(" s")
                        .Append("</td><td class=\"").Append(fields[4] == "cold" ? "cold" : "warm").Append("\">").Append(Escape(fields[4]))
                        .Append("</td><td class=\"num\">").Append(Megabytes(fields[5]))
                        .Append("</td><td class=\"num\">").Append(Megabytes(fields[6]))
                        .Append("</td><td class=\"num\">").Append(Escape(fields[7]))
                        .Append("</td><td>").Append(Escape(fields[8]))
                        .Append("</td><td>").Append(Escape(string.Join(",", fields.Skip(9))))
                        .Append("</td></tr>");
                }
                html.Append("</table></body></html>");
                File.WriteAllText(HtmlPath, html.ToString());
            }
            catch (Exception e)
            {
                Log.Warning($"[RLO] could not write {HtmlPath}: {e.Message}");
            }
        }

        private static string Megabytes(string field) => long.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? $"{value:N0} MB" : "";

        private static string Escape(string text) => System.Security.SecurityElement.Escape(text) ?? "";
    }
}
