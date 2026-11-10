using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class FacilityFast
    {
        private static MethodBase target;
        private static MethodInfo getComp;
        private static Fingerprint.Guard guard;
        private static SelfCheck check;

        [ThreadStatic]
        private static bool bypass;
        private static long calls,
            ticks;
        private static readonly MismatchLog mismatches = new MismatchLog("facility link lookup");

        public static void Install(Harmony harmony)
        {
            target = AccessTools.Method(
                typeof(CompProperties_Facility),
                nameof(CompProperties_Facility.ResolveReferences)
            );
            getComp = AccessTools.Method(typeof(ThingDef), nameof(ThingDef.GetCompProperties));
            if (target == null || getComp == null)
            {
                VersionCheck.Skip(
                    "Facility link lookup",
                    "CompProperties_Facility.ResolveReferences or ThingDef.GetCompProperties no longer exists"
                );
                return;
            }
            check = new SelfCheck("Facility link lookup", VersionCheck.Code(target, getComp), 20);
            harmony.Patch(
                target,
                prefix: new HarmonyMethod(typeof(FacilityFast), nameof(Prefix)) { priority = Priority.Low }
            );
            StartupPhaseFixes.Reports.Add(Stats);
        }

        public static bool Prefix(CompProperties_Facility __instance, ThingDef parentDef, bool __runOriginal)
        {
            if (!__runOriginal)
            {
                return false;
            }
            if (bypass || check.IsOff)
            {
                return true;
            }
            if (
                !(
                    guard ??= new Fingerprint.Guard(
                        target,
                        getComp,
                        getComp.MakeGenericMethod(typeof(CompProperties_AffectedByFacilities))
                    )
                ).Ours
            )
            {
                return true;
            }
            var start = Stopwatch.GetTimestamp();
            calls++;
            var result = new List<ThingDef>();
            var defs = DefDatabase<ThingDef>.AllDefsListForReading;
            for (var i = 0; i < defs.Count; i++)
            {
                var comps = defs[i].comps;
                CompProperties_AffectedByFacilities props = null;
                for (var c = 0; c < comps.Count; c++)
                {
                    if (comps[c] is CompProperties_AffectedByFacilities found)
                    {
                        props = found;
                        break;
                    }
                }
                if (props == null || props.linkableFacilities == null)
                {
                    continue;
                }
                for (var j = 0; j < props.linkableFacilities.Count; j++)
                {
                    if (props.linkableFacilities[j] == parentDef)
                    {
                        result.Add(defs[i]);
                        break;
                    }
                }
            }
            if (DevSwitches.Verify || check.IsChecking)
            {
                bypass = true;
                try
                {
                    __instance.ResolveReferences(parentDef);
                }
                finally
                {
                    bypass = false;
                }
                var slow = __instance.linkableBuildings ?? new List<ThingDef>();
                var same = slow.Count == result.Count;
                for (var k = 0; same && k < slow.Count; k++)
                {
                    same = slow[k] == result[k];
                }
                check.Report(same, $"facility {parentDef?.defName}");
                if (!same)
                {
                    mismatches.Report(
                        $"facility {parentDef?.defName}: fast {result.Count} buildings, original {slow.Count}"
                    );
                    result = slow;
                }
            }
            __instance.linkableBuildings = result;
            ticks += Stopwatch.GetTimestamp() - start;
            return false;
        }

        public static string Stats() =>
            $"facility link lookup: {calls} calls in {StartupPhaseFixes.Seconds(ticks):F2} s"
            + (guard?.Blocked == true ? " (left to vanilla: other patches present)" : "")
            + (DevSwitches.Verify ? $", verify mismatches {mismatches.Count}" : "");
    }
}
