#if RW17
using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Runtime;
using System.Text;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class GcPhases
    {
        private struct Sample
        {
            public double At;
            public long Allocated;
            public int Gen0;
            public int Gen1;
            public int Gen2;
            public TimeSpan Pause;
            public TimeSpan Jit;
            public bool Taken;
        }

        private static readonly string[] Names =
        {
            "engine + mods until RLO",
            "mod constructors",
            "LoadModXML",
            "CombineIntoUnifiedXML",
            "ApplyPatches",
            "ParseAndProcessXML",
            "defs to databases",
            "resolve references",
            "ExecuteWhenFinished",
            "static constructors",
            "atlas baking",
            "GC + end",
        };

        private const int FirstMainThreadPhase = 8;
        private static readonly Sample[] marks = new Sample[Names.Length + 1];
        private static readonly object gate = new object();
        private static int current;
        private static bool menuReported;
        private static Sample atReport;

        public static void Install(Harmony harmony)
        {
            var start = AccessTools.Method(typeof(DeepProfiler), nameof(DeepProfiler.Start));
            if (!Fingerprint.Matches("GC per phase", Fingerprint.Known(start, "4027DCB158962411")))
            {
                return;
            }
            marks[1] = Take();
            harmony.Patch(start, prefix: new HarmonyMethod(typeof(GcPhases), nameof(OnStart)));
            MainMenu.UnpatchAfterStartup(start);
            MainMenu.Then(OnMainMenu);
            LeanFixes.Reports.Add(Report);
        }

        private static Sample Take() => new Sample
        {
            At = (DateTime.Now - (Measurement.ProcessStart ?? DateTime.Now)).TotalSeconds,
            Allocated = GC.GetTotalAllocatedBytes(false),
            Gen0 = GC.CollectionCount(0),
            Gen1 = GC.CollectionCount(1),
            Gen2 = GC.CollectionCount(2),
            Pause = GC.GetTotalPauseDuration(),
            Jit = System.Runtime.JitInfo.GetCompilationTime(false),
            Taken = true,
        };

        private static int PhaseOf(string label)
        {
            switch (label)
            {
                case "LoadModXML()": return 2;
                case "CombineIntoUnifiedXML()": return 3;
                case "ApplyPatches()": return 4;
                case "ParseAndProcessXML()": return 5;
                case "Copy all Defs from mods to global databases.": return 6;
                case "Resolve references.": return 7;
                case "ExecuteToExecuteWhenFinished()": return 8;
                case "StaticConstructorOnStartupUtility.CallAll()":
                case "StaticConstructorOnStartupUtilityReplacement.CallAll()":
                case "Static constructor calls": return 9;
                case "Atlas baking.": return 10;
                case "Garbage Collection": return 11;
                default: return -1;
            }
        }

        public static void OnStart(string label)
        {
            if (label == null || current >= Names.Length - 1)
            {
                return;
            }
            var phase = PhaseOf(label);
            if (phase <= current)
            {
                return;
            }
            lock (gate)
            {
                if (phase <= current || (phase >= FirstMainThreadPhase && current < FirstMainThreadPhase - 1))
                {
                    return;
                }
                marks[phase] = Take();
                current = phase;
            }
        }

        private static string Row(string name, Sample from, Sample to)
        {
            return $"{name,-28}{from.At,7:F1}{to.At - from.At,7:F1}{(to.Allocated - from.Allocated) / 1048576.0,9:F0}{to.Gen0 - from.Gen0,7}{to.Gen1 - from.Gen1,6}{to.Gen2 - from.Gen2,5}{(to.Pause - from.Pause).TotalSeconds,7:F2}{(to.Jit - from.Jit).TotalSeconds,7:F2}";
        }

        private static string Settings()
        {
            var info = GC.GetGCMemoryInfo();
            GC.GetConfigurationVariables().TryGetValue("GCGen0MaxBudget", out var budget);
            var env = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                .Select(e => e.Key as string)
                .Where(k => k != null && (k.StartsWith("DOTNET_GC", StringComparison.OrdinalIgnoreCase) || k.StartsWith("COMPlus_GC", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(k => k)
                .Select(k => $"{k}={Environment.GetEnvironmentVariable(k)}")
                .ToList();
            return $"GC settings: {(GCSettings.IsServerGC ? "server" : "workstation")}, latency {GCSettings.LatencyMode}, gen0 max budget {(budget is IConvertible b ? $"{Convert.ToInt64(b) / 1048576.0:F1} MB" : "n/a")}, GCgen0size {AppContext.GetData("GCgen0size") ?? "unset"}, GCHeapHardLimit {AppContext.GetData("GCHeapHardLimit") ?? AppContext.GetData("System.GC.HeapHardLimit") ?? "unset"}, available memory {info.TotalAvailableMemoryBytes / 1048576.0:F0} MB, environment {(env.Count == 0 ? "no DOTNET_GC* variables" : string.Join(" ", env))}";
        }

        public static string Report()
        {
            Sample[] taken;
            lock (gate)
            {
                atReport = Take();
                current = Names.Length - 1;
                taken = (Sample[])marks.Clone();
            }
            taken[taken.Length - 1] = atReport;
            var sb = new StringBuilder(Settings());
            sb.Append($"\nGC per phase (process-wide; allocated is approximate, includes all threads running at the time):\n{"phase",-28}{"start",7}{"secs",7}{"alloc MB",9}{"gen0",7}{"gen1",6}{"gen2",5}{"pause",7}{"JIT s",7}");
            var seen = 0;
            for (var i = 0; i < Names.Length; i++)
            {
                if (!taken[i].Taken && i > 0)
                {
                    continue;
                }
                var next = Enumerable.Range(i + 1, taken.Length - i - 1).First(j => taken[j].Taken);
                seen++;
                sb.Append('\n').Append(Row(Names[i] + (next == i + 1 ? "" : " (+next)"), taken[i], taken[next]));
            }
            sb.Append('\n').Append(Row("total until report", taken[0], atReport));
            if (RLOMod.Verify)
            {
                sb.Append($"\nGC per phase verify: {seen - 1} of {Names.Length - 1} phase boundaries seen in order, nothing to compare (counters only, no behaviour change)");
            }
            return sb.ToString();
        }

        public static void OnMainMenu()
        {
            if (menuReported || !atReport.Taken)
            {
                return;
            }
            menuReported = true;
            Log.Message("[RLO] GC per phase, " + Row("report until main menu", atReport, Take()));
        }
    }
}
#endif
