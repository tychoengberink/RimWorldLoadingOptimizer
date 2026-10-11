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

        public static void Install(Harmony harmony, bool startupPhases, bool modFixes, bool textureLookup, Action<string, bool, Action> tryInstall)
        {
            tryInstall("assembly type cache", startupPhases, () => TypesCache.Install(harmony));
            tryInstall("loading frame budget", startupPhases, () => FrameBudget.Install(harmony));
            tryInstall("fast content lookups", startupPhases, () => FastContent.Install(harmony, textureLookup));
            tryInstall("designator index", startupPhases, () => DesignatorIndex.Install(harmony));
            tryInstall("CombatAI label scan", modFixes, () => CaiLabels.Install(harmony));
            tryInstall("category sets", modFixes, () => CategorySet.Install(harmony));
            tryInstall("Adaptive Storage inheritance", modFixes, () => AdaptiveStorageInheritance.Install(harmony));
            LongEventHandler.ExecuteWhenFinished(() => tryInstall("HugsLib obsolete patch check", modFixes, () => HugsObsolete.Install(harmony)));
            harmony.Patch(AccessTools.Method(typeof(MainMenuDrawer), nameof(MainMenuDrawer.MainMenuOnGUI)), prefix: new HarmonyMethod(typeof(LeanFixes), nameof(OnMainMenu)));
        }

        public static void OnMainMenu()
        {
            FrameBudget.MenuReached = true;
            CaiLabels.Release();
        }

        public static string Stats() => string.Join("; ", Reports.Select(r =>
        {
            try
            {
                return r();
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }).Concat(new[] { CaiLabels.Stats(), CategorySet.Stats(), HugsObsolete.Stats(), AdaptiveStorageInheritance.Stats() }));

        public static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

        public static IEnumerable<(MethodBase, string[])> WithLambdas(MethodBase method, Dictionary<string, string[]> known)
        {
            yield return Fingerprint.Known(method, known[method.Name]);
            var prefix = "<" + method.Name + ">b__";
            foreach (var nested in method.DeclaringType.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
            {
                foreach (var lambda in nested.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(m => m.Name.StartsWith(prefix)))
                {
                    yield return Fingerprint.Known(lambda, known.TryGetValue(lambda.Name, out var hashes) ? hashes : new string[0]);
                }
            }
        }
    }
}
