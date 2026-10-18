using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class DesignatorIndex
    {
        private static readonly MethodInfo Where = AccessTools.FirstMethod(typeof(Enumerable), m => m.Name == nameof(Enumerable.Where) && m.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2).MakeGenericMethod(typeof(BuildableDef));

        private static BuildableDef[] defs;
        private static DesignationCategoryDef[] categories;
        private static Dictionary<DesignationCategoryDef, List<BuildableDef>> groups;
        private static int calls, builds, mismatches;
        private static SelfCheck check;

        public static void Install(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(DesignationCategoryDef), "ResolveDesignators");
            var lambda = AccessTools.Method(typeof(DesignationCategoryDef), "<ResolveDesignators>b__24_0");
            var known = VersionCheck.Known(Fingerprint.Known(target, "449408DE610261C1", "76F6E33AE59F1DC3"), Fingerprint.Known(lambda, "3DAF10F9A009DA50", "8020BE24A816E57E"));
            if (!known && !VersionCheck.Pattern("Designator index", target, code => VersionCheck.Calls(code, Where) == 1, "filters the buildable defs with one Enumerable.Where call"))
            {
                return;
            }
            check = new SelfCheck("Designator index", known, 10);
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(DesignatorIndex), nameof(Transpiler)));
            LeanFixes.Reports.Add(() => $"designator index: {calls} categories, {builds} index builds, {mismatches} verify mismatches");
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var replaced = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(Where))
                {
                    replaced++;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DesignatorIndex), nameof(Matching))).WithLabels(instruction.labels).WithBlocks(instruction.blocks);
                    continue;
                }
                yield return instruction;
            }
            if (replaced != 1)
            {
                Log.Warning($"[RLO] designator index: {replaced} Where calls replaced, expected 1");
            }
        }

        public static IEnumerable<BuildableDef> Matching(IEnumerable<BuildableDef> source, Func<BuildableDef, bool> predicate)
        {
            if (!(predicate.Target is DesignationCategoryDef category) || check.IsOff || !UnityData.IsInMainThread)
            {
                return source.Where(predicate);
            }
            calls++;
            if (!Valid())
            {
                Build();
            }
            var result = (groups.TryGetValue(category, out var list) ? list : new List<BuildableDef>()).Where(predicate);
            if (RLOMod.Verify || check.IsChecking)
            {
                var same = result.SequenceEqual(source.Where(predicate));
                check.Report(same, $"designators of {category.defName}");
                if (!same)
                {
                    if (mismatches++ < 10)
                    {
                        Log.Warning($"[RLO] designator index verify mismatch for {category.defName}");
                    }
                    return source.Where(predicate);
                }
            }
            return result;
        }

        private static bool Valid()
        {
            if (defs == null)
            {
                return false;
            }
            var terrains = DefDatabase<TerrainDef>.AllDefsListForReading;
            var things = DefDatabase<ThingDef>.AllDefsListForReading;
            if (defs.Length != terrains.Count + things.Count)
            {
                return false;
            }
            var i = 0;
            foreach (var def in terrains)
            {
                if (!ReferenceEquals(defs[i], def) || !ReferenceEquals(categories[i], def.designationCategory))
                {
                    return false;
                }
                i++;
            }
            foreach (var def in things)
            {
                if (!ReferenceEquals(defs[i], def) || !ReferenceEquals(categories[i], def.designationCategory))
                {
                    return false;
                }
                i++;
            }
            return true;
        }

        private static void Build()
        {
            builds++;
            var all = DefDatabase<TerrainDef>.AllDefsListForReading.Cast<BuildableDef>().Concat(DefDatabase<ThingDef>.AllDefsListForReading).ToArray();
            defs = all;
            categories = new DesignationCategoryDef[all.Length];
            groups = new Dictionary<DesignationCategoryDef, List<BuildableDef>>();
            for (var i = 0; i < all.Length; i++)
            {
                var category = all[i].designationCategory;
                categories[i] = category;
                if (category == null)
                {
                    continue;
                }
                if (!groups.TryGetValue(category, out var list))
                {
                    groups[category] = list = new List<BuildableDef>();
                }
                list.Add(all[i]);
            }
        }
    }
}
