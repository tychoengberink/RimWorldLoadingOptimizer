using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class AvSkins
    {
        private static readonly MethodInfo OriginalGet = AccessTools.Method(typeof(ContentFinder<Texture2D>), nameof(ContentFinder<Texture2D>.Get));
        private static readonly MethodInfo LookupGet = AccessTools.Method(typeof(TextureLookup), nameof(TextureLookup.Get));
        private static readonly string[] Names = { "ValidTexPathOrDefault", "IsValidTexPath" };
        private static int methods;

        public static void Install(Harmony harmony)
        {
            var manager = ModTypes.Find("AV_MechanoidSkins.MechSkinManager");
            if (manager == null || !TextureLookup.Installed)
            {
                return;
            }
            var targets = AccessTools.GetDeclaredMethods(manager).Where(m => Names.Contains(m.Name) && !m.ContainsGenericParameters && IlScan.CallsAny(m, OriginalGet)).ToList();
            if (targets.Count != 3)
            {
                VersionCheck.Skip("AV Mechanoid Skins texture checks", $"expected 3 texture check methods that call ContentFinder.Get, found {targets.Count}");
                return;
            }
            foreach (var method in targets)
            {
                harmony.Patch(method, transpiler: new HarmonyMethod(typeof(AvSkins), nameof(Transpiler)));
                methods++;
            }
            Ctors2Mod.Reports.Add(() => $"AV Mechanoid Skins texture checks: {methods} methods redirected to RLO's texture lookup");
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => IlScan.Replace(instructions, OriginalGet, LookupGet);
    }
}
