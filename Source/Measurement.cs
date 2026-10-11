using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
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
        private static DateTime modStart;

        [DllImport("libSystem.dylib")]
        private static extern int proc_pid_rusage(int pid, int flavor, long[] info);

        private static bool MacFootprint(out long peakMb, out long nowMb)
        {
            peakMb = nowMb = 0;
            if (Application.platform != RuntimePlatform.OSXPlayer)
            {
                return false;
            }
            var info = new long[64];
            if (proc_pid_rusage(Process.GetCurrentProcess().Id, 4, info) != 0)
            {
                return false;
            }
            nowMb = info[9] >> 20;
            peakMb = info[30] >> 20;
            return peakMb > 0;
        }

        public static string CsvPath => Path.Combine(GenFilePaths.SaveDataFolderPath, "RimWorldLoadingOptimizer", "measurements.csv");

        public static void Install(Harmony harmony, RLOMod owner, RLOSettings saved, bool off)
        {
            mod = owner;
            persisted = saved;
            featuresOff = off;
            modStart = DateTime.Now;
            try
            {
                processStart = Process.GetCurrentProcess().StartTime;
            }
            catch
            {
            }
            harmony.Patch(AccessTools.Method(typeof(LoadedModManager), nameof(LoadedModManager.ApplyPatches)),
                prefix: new HarmonyMethod(typeof(Measurement), nameof(PatchingStart)),
                finalizer: new HarmonyMethod(typeof(Measurement), nameof(PatchingEnd)));
            harmony.Patch(AccessTools.Method(typeof(MainMenuDrawer), nameof(MainMenuDrawer.MainMenuOnGUI)),
                prefix: new HarmonyMethod(typeof(Measurement), nameof(OnMainMenu)));
        }

        public static void PatchingStart()
        {
            patching.Start();
        }

        public static void PatchingEnd()
        {
            patching.Stop();
        }

        private static bool Cold(double patchSeconds) => patchSeconds >= 2.0;

        public static void OnMainMenu()
        {
            if (done)
            {
                return;
            }
            done = true;
            try
            {
                Record();
            }
            catch (Exception e)
            {
                Log.Warning($"[RLO] measurement failed: {e.Message}");
            }
        }

        private static void Record()
        {
            var seconds = (DateTime.Now - (processStart ?? modStart)).TotalSeconds;
            var patchSeconds = patching.Elapsed.TotalSeconds;
            long peakMb = 0, workingMb = 0;
            try
            {
                if (!MacFootprint(out peakMb, out workingMb))
                {
                    var process = Process.GetCurrentProcess();
                    peakMb = process.PeakWorkingSet64 >> 20;
                    workingMb = process.WorkingSet64 >> 20;
                }
            }
            catch
            {
            }
            var mode = featuresOff ? "off" : "on";
            var from = processStart == null ? "since RimWorld Loading Optimizer loaded" : "since start";
            Log.Message($"[RLO] main menu reached at {seconds:F1} s {from} (RimWorld Loading Optimizer {mode}, XML patching {patchSeconds:F1} s, {(Cold(patchSeconds) ? "cold" : "warm")} start{(peakMb > 0 ? $", peak memory {peakMb} MB" : "")})");
            AppendCsv(mode, seconds, patchSeconds, peakMb, workingMb);
            if (persisted.measureStep == MeasureOff && featuresOff)
            {
                persisted.measureStep = MeasureOn;
                persisted.measureOffSeconds = (float)seconds;
                persisted.measureOffPatchSeconds = (float)patchSeconds;
                persisted.measureOffPeakMb = peakMb;
                mod.WriteSettings();
                Find.WindowStack.Add(new Dialog_MessageBox($"RimWorld Loading Optimizer measurement, step 1 of 2 done.\n\nWith RimWorld Loading Optimizer switched off, RimWorld needed {seconds:F1} s to reach the main menu.\n\nNow quit RimWorld and start it again. The next start runs with RimWorld Loading Optimizer switched on and shows the difference.\n\nDo not change mods or settings in between."));
                return;
            }
            if (persisted.measureStep == MeasureOn && !featuresOff)
            {
                persisted.measureStep = Idle;
                var off = persisted.measureOffSeconds;
                var saved = off - seconds;
                var percent = off > 0 ? saved / off * 100 : 0;
                var fair = Cold(persisted.measureOffPatchSeconds) == Cold(patchSeconds);
                var memory = persisted.measureOffPeakMb > 0 && peakMb > 0 ? $"\nPeak memory: {persisted.measureOffPeakMb:N0} MB off, {peakMb:N0} MB on." : "";
                var warning = fair ? "" : "\n\nWarning: one start was cold (XML patching ran) and the other was warm, for example because a mod updated in between. This comparison is not fair; measure again.";
                persisted.lastMeasurement = $"{DateTime.Now:yyyy-MM-dd HH:mm}: off {off:F1} s, on {seconds:F1} s, {saved:F1} s faster ({percent:F0}%){(fair ? "" : " (not comparable: warm vs cold)")}";
                mod.WriteSettings();
                Find.WindowStack.Add(new Dialog_MessageBox($"RimWorld Loading Optimizer measurement result\n\nTime to the main menu:\n  RimWorld Loading Optimizer off: {off:F1} s\n  RimWorld Loading Optimizer on: {seconds:F1} s\n  Difference: {saved:F1} s ({percent:F0}%){memory}\n\nThis is one pair of starts on your PC with your mods; repeat it a few times for a reliable number. The first start after installing or updating RimWorld Loading Optimizer (or any mod) also builds its texture atlas cache and is slower than later starts. All measurements are saved in:\n{CsvPath}{warning}"));
            }
        }

        private static void AppendCsv(string mode, double seconds, double patchSeconds, long peakMb, long workingMb)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CsvPath));
                var exists = File.Exists(CsvPath);
                using (var writer = new StreamWriter(CsvPath, true))
                {
                    if (!exists)
                    {
                        writer.WriteLine("date,lean_memory,seconds_to_main_menu,xml_patching_seconds,start,peak_memory_mb,memory_at_menu_mb,active_mods,lean_memory_version,os");
                    }
                    var inv = CultureInfo.InvariantCulture;
                    writer.WriteLine(string.Join(",",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", inv),
                        mode,
                        seconds.ToString("F1", inv),
                        patchSeconds.ToString("F1", inv),
                        Cold(patchSeconds) ? "cold" : "warm",
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
    }
}
