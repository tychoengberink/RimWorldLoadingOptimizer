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
        private static int state;
        private static long calls, ticks, mismatches;

        public static void Install(Harmony harmony)
        {
            target = AccessTools.Method(typeof(CompProperties_Facility), nameof(CompProperties_Facility.ResolveReferences));
            getComp = AccessTools.Method(typeof(ThingDef), nameof(ThingDef.GetCompProperties));
            if (!Fingerprint.Matches("Facility link lookup", Fingerprint.Known(target, "4049308670CED946", "5B71AA1F8A5BD7EB"), Fingerprint.Known(getComp, "215F91C53DE88854", "0960DBC5C1F68151")))
            {
                return;
            }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(FacilityFast), nameof(Prefix)) { priority = Priority.Low });
            LeanFixes.Reports.Add(Stats);
        }

        public static bool Prefix(CompProperties_Facility __instance, ThingDef parentDef, bool __runOriginal)
        {
            if (!__runOriginal)
            {
                return false;
            }
            if (state == 0)
            {
                state = Fingerprint.OnlyOurs(target) && Fingerprint.OnlyOurs(getComp) && Fingerprint.OnlyOurs(getComp.MakeGenericMethod(typeof(CompProperties_AffectedByFacilities))) ? 1 : 2;
            }
            if (state != 1)
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
            if (RLOMod.Verify)
            {
                var slow = new List<ThingDef>();
                foreach (var def in defs)
                {
                    var props = def.GetCompProperties<CompProperties_AffectedByFacilities>();
                    if (props?.linkableFacilities != null && props.linkableFacilities.Exists(f => f == parentDef))
                    {
                        slow.Add(def);
                    }
                }
                var same = slow.Count == result.Count;
                for (var k = 0; same && k < slow.Count; k++)
                {
                    same = slow[k] == result[k];
                }
                if (!same)
                {
                    mismatches++;
                    result = slow;
                }
            }
            __instance.linkableBuildings = result;
            ticks += Stopwatch.GetTimestamp() - start;
            return false;
        }

        public static string Stats() => $"facility link lookup: {calls} calls in {LeanFixes.Seconds(ticks):F2} s" + (state == 2 ? " (left to vanilla: other patches present)" : "") + (RLOMod.Verify ? $", verify mismatches {mismatches}" : "");
    }
}
