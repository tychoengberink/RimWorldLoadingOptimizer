using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class LinkableFacilities
    {
        private static Type loader;
        private static Dictionary<ThingDef, List<ThingDef>> buildings;
        private static int windows, calls, served, checks, mismatches;

        public static void Install(Harmony harmony)
        {
            loader = ModTypes.Find("LinkableGroups.LinkableGroupsLoader");
            var resolve = AccessTools.Method(typeof(CompProperties_Facility), nameof(CompProperties_Facility.ResolveReferences), new[] { typeof(ThingDef) });
            if (loader == null || resolve == null)
            {
                return;
            }
            if (!Fingerprint.Matches("Linkable Groups facility links",
                    Fingerprint.Known(loader.TypeInitializer, "4D66BE97D1E8EE98"),
                    Fingerprint.Known(resolve, "5B71AA1F8A5BD7EB")) || !Ctors2Mod.OnlyOurs(resolve))
            {
                return;
            }
            harmony.Patch(resolve, prefix: new HarmonyMethod(typeof(LinkableFacilities), nameof(Prefix)));
            CtorBracket.Started += t => { if (t == loader) { buildings = null; windows++; } };
            CtorBracket.Ended += t => { if (t == loader) buildings = null; };
            Ctors2Mod.Reports.Add(Stats);
        }

        public static bool Prefix(CompProperties_Facility __instance, ThingDef parentDef)
        {
            if (parentDef == null || !CtorBracket.Inside(loader))
            {
                return true;
            }
            calls++;
            if (buildings == null)
            {
                buildings = new Dictionary<ThingDef, List<ThingDef>>();
                foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    var props = def.GetCompProperties<CompProperties_AffectedByFacilities>();
                    if (props?.linkableFacilities == null)
                    {
                        continue;
                    }
                    foreach (var facility in props.linkableFacilities)
                    {
                        if (facility == null)
                        {
                            continue;
                        }
                        if (!buildings.TryGetValue(facility, out var list))
                        {
                            buildings[facility] = list = new List<ThingDef>();
                        }
                        if (list.Count == 0 || !ReferenceEquals(list[list.Count - 1], def))
                        {
                            list.Add(def);
                        }
                    }
                }
            }
            __instance.linkableBuildings = buildings.TryGetValue(parentDef, out var found) ? new List<ThingDef>(found) : new List<ThingDef>();
            served++;
            if (Ctors2Mod.Verify)
            {
                checks++;
                if (!__instance.linkableBuildings.SequenceEqual(Original(parentDef)) && ++mismatches <= 10)
                {
                    Log.Warning($"[RLO] linkable verify mismatch for {parentDef}");
                }
            }
            return false;
        }

        private static List<ThingDef> Original(ThingDef parentDef)
        {
            var result = new List<ThingDef>();
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                var props = def.GetCompProperties<CompProperties_AffectedByFacilities>();
                if (props?.linkableFacilities == null)
                {
                    continue;
                }
                for (var j = 0; j < props.linkableFacilities.Count; j++)
                {
                    if (props.linkableFacilities[j] == parentDef)
                    {
                        result.Add(def);
                        break;
                    }
                }
            }
            return result;
        }

        public static string Stats() => $"linkable groups: {windows} windows, {calls} facility resolves, {served} served from the index" + (Ctors2Mod.Verify ? $", verify checks {checks}, mismatches {mismatches}" : "");
    }
}
