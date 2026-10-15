#if RW17
using System;
using System.Runtime;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class GcLatency
    {
        private static GCLatencyMode? saved;
        private static int gen2AtStart;
        private static double setAt;
        private static string outcome = "not changed";

        public static void Install()
        {
            var mode = GCSettings.LatencyMode;
            if (mode != GCLatencyMode.Interactive)
            {
                outcome = $"left at {mode} (only Interactive, the concurrent GC, is changed)";
                Log.Message($"[RLO] GC latency: the runtime runs in {mode} mode, not Interactive; left alone");
                LeanFixes.Reports.Add(Stats);
                return;
            }
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            if (GCSettings.LatencyMode != GCLatencyMode.SustainedLowLatency)
            {
                outcome = $"the runtime kept {GCSettings.LatencyMode}";
                Log.Message($"[RLO] GC latency: the runtime did not accept SustainedLowLatency and stays at {GCSettings.LatencyMode}");
                LeanFixes.Reports.Add(Stats);
                return;
            }
            saved = mode;
            gen2AtStart = GC.CollectionCount(2);
            setAt = Now();
            outcome = $"SustainedLowLatency from {setAt:F1} s until the main menu (was {mode})";
            Log.Message($"[RLO] GC latency: {mode} -> SustainedLowLatency until the main menu, so the runtime avoids blocking full collections while loading");
            MainMenu.Then(Restore);
            LeanFixes.Reports.Add(Stats);
        }

        private static void Restore()
        {
            if (saved == null)
            {
                return;
            }
            var mode = saved.Value;
            saved = null;
            var current = GCSettings.LatencyMode;
            var counts = Counts();
            if (current != GCLatencyMode.SustainedLowLatency)
            {
                outcome += $"; someone else changed it to {current} before the main menu, left at that";
                Log.Message($"[RLO] GC latency: changed to {current} by someone else before the main menu; left at that. {counts}");
                return;
            }
            GCSettings.LatencyMode = mode;
            outcome += $", restored at {Now():F1} s";
            Log.Message($"[RLO] GC latency: back to {GCSettings.LatencyMode} at the main menu. {counts}");
        }

        private static string Counts()
        {
            var gen2 = GC.CollectionCount(2) - gen2AtStart;
            if (!GcFallback.Counting)
            {
                return $"Full collections while it was set: {gen2} (turn on the startup report to see how many were blocking)";
            }
            var (full, blocking) = GcFallback.FullCollections();
            return $"Full collections while it was set: {gen2}; since RLO started {full}, of which {blocking} blocking";
        }

        private static string Stats() => $"GC latency: {outcome}; {Counts()}";

        private static double Now() => (DateTime.Now - (Measurement.ProcessStart ?? DateTime.Now)).TotalSeconds;
    }
}
#endif
