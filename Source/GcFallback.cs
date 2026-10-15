#if RW17
using System;
using System.Collections.ObjectModel;
using System.Diagnostics.Tracing;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class GcFallback
    {
        private const int ConcurrentMechanism = 0x1;

        private static Listener listener;
        private static readonly object gate = new object();
        private static int fullCollections, background, blocked;
        private static double blockedSeconds, firstBlockedAt = -1, longest;

        public static void Install()
        {
            listener = new Listener();
            MainMenu.Then(Stop);
            LeanFixes.Reports.Add(Stats);
        }

        public static bool Counting => listener != null;

        public static (int full, int blocking) FullCollections()
        {
            lock (gate)
            {
                return (fullCollections, fullCollections - background);
            }
        }

        private static void Stop()
        {
            var running = listener;
            listener = null;
            running?.Dispose();
        }

        private static string Stats()
        {
            lock (gate)
            {
                if (fullCollections == 0)
                {
                    return "full collections: none seen since RLO started";
                }
                var lost = blocked == 0
                    ? ""
                    : $"; {blocked} of them started as background collections but ran blocking (first at {firstBlockedAt:F1} s, {blockedSeconds:F2} s of pause in total, longest {longest:F2} s): the runtime could not run them in the background";
                return $"full collections since RLO started: {fullCollections}, {background} ran in the background{lost}";
            }
        }

        private static double Now() => (DateTime.Now - (Measurement.ProcessStart ?? DateTime.Now)).TotalSeconds;

        private sealed class Listener : EventListener
        {
            private bool announcedBackground;
            private bool currentBlocked;
            private DateTime suspendedAt;

            protected override void OnEventSourceCreated(EventSource source)
            {
                if (source.Name == "Microsoft-Windows-DotNETRuntime")
                {
                    EnableEvents(source, EventLevel.Informational, (EventKeywords)1);
                }
            }

            protected override void OnEventWritten(EventWrittenEventArgs e)
            {
                try
                {
                    var name = e.EventName;
                    if (name == null)
                    {
                        return;
                    }
                    if (name.StartsWith("GCSuspendEEBegin", StringComparison.Ordinal))
                    {
                        suspendedAt = e.TimeStamp;
                        currentBlocked = false;
                    }
                    else if (name.StartsWith("GCStart", StringComparison.Ordinal))
                    {
                        announcedBackground = Convert.ToUInt32(Value(e, "Depth")) == 2 && Convert.ToUInt32(Value(e, "Type")) == 1;
                    }
                    else if (name.StartsWith("GCGlobalHeapHistory", StringComparison.Ordinal))
                    {
                        if (Convert.ToUInt32(Value(e, "CondemnedGeneration")) != 2)
                        {
                            return;
                        }
                        var concurrent = (Convert.ToUInt32(Value(e, "GlobalMechanisms")) & ConcurrentMechanism) != 0;
                        lock (gate)
                        {
                            fullCollections++;
                            if (concurrent)
                            {
                                background++;
                            }
                            else if (announcedBackground)
                            {
                                blocked++;
                                currentBlocked = true;
                                if (firstBlockedAt < 0)
                                {
                                    firstBlockedAt = Now();
                                }
                            }
                        }
                        announcedBackground = false;
                    }
                    else if (name.StartsWith("GCRestartEEEnd", StringComparison.Ordinal) && currentBlocked)
                    {
                        var seconds = (e.TimeStamp - suspendedAt).TotalSeconds;
                        lock (gate)
                        {
                            blockedSeconds += seconds;
                            longest = Math.Max(longest, seconds);
                        }
                        currentBlocked = false;
                    }
                }
                catch (Exception)
                {
                }
            }

            private static object Value(EventWrittenEventArgs e, string field)
            {
                ReadOnlyCollection<string> names = e.PayloadNames;
                var index = names == null ? -1 : names.IndexOf(field);
                return index < 0 ? 0u : e.Payload[index];
            }
        }
    }
}
#endif
