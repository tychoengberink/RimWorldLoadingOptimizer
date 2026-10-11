using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class Timing
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("CTORS2_TIMING") == "1";
        private static readonly string[] Types =
        {
            "TD_Find_Lib.ModStartup", "AV_MechanoidSkins.OnStartUp", "LinkableGroups.LinkableGroupsLoader", "KCSG.StartupActions",
            "MiningOutpost.KcsgSymbolBootstrap", "Vehicles.VehicleHarmony"
        };
        private static readonly string[][] Methods =
        {
            new[] { "LunarFramework.Patching.PatchGroup", "CheckForConflicts" },
            new[] { "VanillaTradingExpanded.Utils", "Initialize" },
            new[] { "LunarFramework.Bootstrap.Entrypoint", "OnPlayDataLoadFinished" }
        };
        private static readonly Dictionary<string, double> totals = new Dictionary<string, double>();
        private static readonly Dictionary<Type, long> started = new Dictionary<Type, long>();
        private static readonly Dictionary<MethodBase, string> names = new Dictionary<MethodBase, string>();

        public static void Install()
        {
            if (!On)
            {
                return;
            }
            var types = new HashSet<string>(Types);
            CtorBracket.Started += t => { if (types.Contains(t.FullName)) started[t] = Stopwatch.GetTimestamp(); };
            CtorBracket.Ended += t =>
            {
                if (started.TryGetValue(t, out var begin))
                {
                    Add(t.FullName, Stopwatch.GetTimestamp() - begin);
                    started.Remove(t);
                }
            };
            Ctors2Mod.Reports.Add(Stats);
        }

        public static void InstallMethods(Harmony harmony)
        {
            if (!On)
            {
                return;
            }
            foreach (var pair in Methods)
            {
                var type = ModTypes.Find(pair[0]);
                if (type == null)
                {
                    continue;
                }
                foreach (var method in AccessTools.GetDeclaredMethods(type).Where(m => m.Name == pair[1] && !m.ContainsGenericParameters))
                {
                    names[method] = $"{pair[0]}.{pair[1]}({method.GetParameters().Length})";
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(Timing), nameof(Begin)) { priority = Priority.First }, finalizer: new HarmonyMethod(typeof(Timing), nameof(End)) { priority = Priority.Last });
                }
            }
        }

        public static void Begin(out long __state) => __state = Stopwatch.GetTimestamp();

        public static void End(long __state, MethodBase __originalMethod)
        {
            if (names.TryGetValue(__originalMethod, out var name))
            {
                Add(name, Stopwatch.GetTimestamp() - __state);
            }
        }

        private static void Add(string name, long ticks)
        {
            totals.TryGetValue(name, out var total);
            totals[name] = total + ticks * 1000.0 / Stopwatch.Frequency;
        }

        public static string Stats() => "timing (ms): " + string.Join(", ", totals.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value:F0}"));
    }
}
