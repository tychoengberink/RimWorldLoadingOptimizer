using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class VehiclePatternLookups
    {
        private static readonly MethodInfo OriginalGet = AccessTools.Method(typeof(ContentFinder<Texture2D>), nameof(ContentFinder<Texture2D>.Get));
        private static readonly MethodInfo LookupGet = AccessTools.Method(typeof(TextureLookup), nameof(TextureLookup.Get));

        public static void Install(Harmony harmony)
        {
            var pattern = ModTypes.Find("Vehicles.PatternDef");
            var recache = pattern == null ? null : AccessTools.Method(pattern, "RecacheTextures");
            if (recache == null || LookupGet == null || !Fingerprint.Matches("Vehicle Framework pattern lookups", Fingerprint.Known(recache, "11EB661ACD87B954")))
            {
                return;
            }
            harmony.Patch(recache, transpiler: new HarmonyMethod(typeof(VehiclePatternLookups), nameof(Transpiler)));
            Ctors2Mod.Reports.Add(() => "vehicle pattern lookups: PatternDef.RecacheTextures redirected to RLO's texture lookup");
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => Ctors2Mod.Replace(instructions, OriginalGet, LookupGet);
    }
}
