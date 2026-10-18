using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class LeanFixes
    {
        public static readonly List<Func<string>> Reports = new List<Func<string>>();

        public static void Install(Harmony harmony, RLOSettings settings, Action<string, bool, Action> tryInstall)
        {
            tryInstall("assembly type cache", settings.startupPhases, () => TypesCache.Install(harmony));
            tryInstall("loading frame budget", settings.startupPhases, () => FrameBudget.Install(harmony));
            tryInstall("fast content lookups", settings.startupPhases, () => FastContent.Install(harmony, settings.textureLookup));
            tryInstall("designator index", settings.startupPhases, () => DesignatorIndex.Install(harmony));
            tryInstall("CombatAI label scan", settings.Supports("combatai"), () => CaiLabels.Install(harmony));
            tryInstall("category sets", settings.categoryCache, () => CategorySet.Install(harmony));
            MainMenu.Then(OnMainMenu);
        }

        public static void OnMainMenu()
        {
            FrameBudget.MenuReached = true;
            CaiLabels.Release();
        }

        public static string Stats() => string.Join("\n", Reports.Select(r =>
        {
            try
            {
                return r();
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }).Concat(new[] { CaiLabels.Stats(), CategorySet.Stats() }));

        public static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;
    }
}
