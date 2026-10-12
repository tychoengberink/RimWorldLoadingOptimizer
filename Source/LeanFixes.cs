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

        public static IEnumerable<(MethodBase, string[])> WithLambdas(MethodBase method, Dictionary<string, string[]> known)
        {
            yield return Fingerprint.Known(method, known.TryGetValue(method.Name, out var own) ? own : new string[0]);
            var prefix = "<" + method.Name + ">b__";
            foreach (var nested in method.DeclaringType.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
            {
                foreach (var lambda in nested.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(m => m.Name.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    yield return Fingerprint.Known(lambda, known.TryGetValue(lambda.Name, out var hashes) ? hashes : new string[0]);
                }
            }
        }
    }
}
