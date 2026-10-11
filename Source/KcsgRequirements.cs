using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class KcsgRequirements
    {
        private static AccessTools.FieldRef<object, List<string>> requirements;
        private static PropertyInfo loaded;
        private static HashSet<string> active;
        private static int calls, checks, mismatches;
        public static bool MenuReached;

        public static void Install(Harmony harmony)
        {
            var layout = ModTypes.Find("KCSG.StructureLayoutDef");
            var method = layout == null ? null : AccessTools.Method(layout, "ResolveModRequirements");
            var field = layout == null ? null : AccessTools.Field(layout, "modRequirements");
            loaded = layout == null ? null : AccessTools.Property(layout, "RequiredModLoaded");
            if (method == null || field?.FieldType != typeof(List<string>) || loaded?.PropertyType != typeof(bool) || loaded.GetSetMethod(true) == null)
            {
                return;
            }
            if (!Fingerprint.Matches("KCSG mod requirements", Fingerprint.Known(method, "5C1904C68736C1FA")) || !Ctors2Mod.OnlyOurs(method))
            {
                return;
            }
            requirements = AccessTools.FieldRefAccess<object, List<string>>(field);
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(KcsgRequirements), nameof(Prefix)));
            Ctors2Mod.Reports.Add(Stats);
        }

        public static bool Prefix(object __instance)
        {
            if (MenuReached || !UnityData.IsInMainThread)
            {
                return true;
            }
            calls++;
            if (active == null)
            {
                active = new HashSet<string>();
                foreach (var mod in ModsConfig.ActiveModsInLoadOrder)
                {
                    active.Add(mod.PackageIdNonUnique);
                }
            }
            var allLoaded = true;
            var list = requirements(__instance);
            for (var i = 0; i < list.Count; i++)
            {
                if (!active.Contains(list[i].ToLower()))
                {
                    allLoaded = false;
                    break;
                }
            }
            loaded.SetValue(__instance, allLoaded);
            if (Ctors2Mod.Verify)
            {
                checks++;
                var original = true;
                foreach (var requirement in list)
                {
                    var lower = requirement.ToLower();
                    var any = false;
                    foreach (var mod in ModsConfig.ActiveModsInLoadOrder)
                    {
                        if (mod.PackageIdNonUnique == lower)
                        {
                            any = true;
                            break;
                        }
                    }
                    if (!any)
                    {
                        original = false;
                        break;
                    }
                }
                if (original != allLoaded && ++mismatches <= 10)
                {
                    Log.Warning($"[RLO] kcsg verify mismatch for {__instance}");
                }
            }
            return false;
        }

        public static string Stats() => $"kcsg mod requirements: {calls} layouts served from the active-mod set" + (Ctors2Mod.Verify ? $", verify checks {checks}, mismatches {mismatches}" : "");
    }
}
