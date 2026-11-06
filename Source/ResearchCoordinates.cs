using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class ResearchCoordinates
    {
        public const string Feature = "Research tree layout per tab";

        private static AccessTools.FieldRef<ResearchProjectDef, float> x, y;
        private static Action<ResearchProjectDef> clamp;
        private static SelfCheck check;
        private static int projects, groups, longest;
        private static long ticks;

        public static void Install(Harmony harmony)
        {
            var method = AccessTools.Method(typeof(ResearchProjectDef), nameof(ResearchProjectDef.GenerateNonOverlappingCoordinates));
            var clampMethod = AccessTools.Method(typeof(ResearchProjectDef), "ClampInCoordinateLimits");
            var xField = AccessTools.Field(typeof(ResearchProjectDef), "x");
            var yField = AccessTools.Field(typeof(ResearchProjectDef), "y");
            if (method == null || clampMethod == null || xField == null || yField == null || xField.FieldType != typeof(float) || yField.FieldType != typeof(float))
            {
                VersionCheck.Skip(Feature, "a method or field it needs no longer exists");
                return;
            }
            x = AccessTools.FieldRefAccess<ResearchProjectDef, float>(xField);
            y = AccessTools.FieldRefAccess<ResearchProjectDef, float>(yField);
            clamp = AccessTools.MethodDelegate<Action<ResearchProjectDef>>(clampMethod);
            check = new SelfCheck(Feature, VersionCheck.Code(method, clampMethod), 1);
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(ResearchCoordinates), nameof(Prefix)), postfix: new HarmonyMethod(typeof(ResearchCoordinates), nameof(Postfix)));
            LeanFixes.Reports.Add(Stats);
        }

        public static bool Prefix(ref float[] __state)
        {
            if (check.IsOff)
            {
                return true;
            }
            var start = Stopwatch.GetTimestamp();
            var all = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
            var byTab = new Dictionary<ResearchTabDef, List<ResearchProjectDef>>();
            var noTab = new List<ResearchProjectDef>();
            var order = new List<List<ResearchProjectDef>>();
            foreach (var project in all)
            {
                x(project) = project.researchViewX;
                y(project) = project.researchViewY;
                List<ResearchProjectDef> group;
                if (project.tab == null)
                {
                    group = noTab;
                }
                else if (!byTab.TryGetValue(project.tab, out group))
                {
                    group = new List<ResearchProjectDef>();
                    byTab[project.tab] = group;
                    order.Add(group);
                }
                group.Add(project);
            }
            if (noTab.Count > 0)
            {
                order.Add(noTab);
            }
            var gaveUp = false;
            foreach (var group in order)
            {
                gaveUp |= Relax(group);
            }
            if (gaveUp)
            {
                Log.Error("Couldn't relax research project coordinates apart after " + 200 + " passes.");
            }
            projects = all.Count;
            groups = order.Count;
            ticks = Stopwatch.GetTimestamp() - start;
            if (!check.IsChecking)
            {
                return false;
            }
            __state = new float[all.Count * 2];
            for (var i = 0; i < all.Count; i++)
            {
                __state[2 * i] = x(all[i]);
                __state[2 * i + 1] = y(all[i]);
            }
            return true;
        }

        private static bool Relax(List<ResearchProjectDef> group)
        {
            var passes = 0;
            while (true)
            {
                var moved = false;
                for (var i = 0; i < group.Count; i++)
                {
                    var a = group[i];
                    for (var j = 0; j < group.Count; j++)
                    {
                        var b = group[j];
                        if (a == b)
                        {
                            continue;
                        }
                        if (!(Mathf.Abs(x(a) - x(b)) < 0.5f) || !(Mathf.Abs(y(a) - y(b)) < 0.25f))
                        {
                            continue;
                        }
                        moved = true;
                        if (x(a) <= x(b))
                        {
                            x(a) -= 0.1f;
                            x(b) += 0.1f;
                        }
                        else
                        {
                            x(a) += 0.1f;
                            x(b) -= 0.1f;
                        }
                        if (y(a) <= y(b))
                        {
                            y(a) -= 0.1f;
                            y(b) += 0.1f;
                        }
                        else
                        {
                            y(a) += 0.1f;
                            y(b) -= 0.1f;
                        }
                        x(a) += 0.001f;
                        y(a) += 0.001f;
                        x(b) -= 0.001f;
                        y(b) -= 0.001f;
                        clamp(a);
                        clamp(b);
                    }
                }
                if (!moved)
                {
                    longest = Math.Max(longest, passes);
                    return false;
                }
                passes++;
                if (passes > 200)
                {
                    longest = Math.Max(longest, passes);
                    return true;
                }
            }
        }

        public static void Postfix(float[] __state)
        {
            if (__state == null)
            {
                return;
            }
            var all = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
            for (var i = 0; i < all.Count; i++)
            {
                if (__state[2 * i] != x(all[i]) || __state[2 * i + 1] != y(all[i]))
                {
                    check.Report(false, $"{all[i].defName} at ({__state[2 * i]}, {__state[2 * i + 1]}) instead of ({x(all[i])}, {y(all[i])})");
                    return;
                }
            }
            check.Report(true);
        }

        public static string Stats() => $"research tree layout: {projects:N0} projects in {groups} tabs relaxed per tab in {ticks / (double)Stopwatch.Frequency:F2} s, most passes {longest}";
    }
}
