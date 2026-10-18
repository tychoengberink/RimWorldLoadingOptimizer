using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class Ctors2Mod
    {
        public static readonly bool Verify = RLOMod.Verify || Environment.GetEnvironmentVariable("CTORS2_VERIFY") == "1";
        private static readonly string[] Off = (Environment.GetEnvironmentVariable("CTORS2_OFF") ?? "").Split(',').Select(s => s.Trim().ToLowerInvariant()).ToArray();
        public static Harmony Harmony;
        public static readonly List<Func<string>> Reports = new List<Func<string>>();
        private static bool reported;

        public static bool Enabled(string fix) => !Off.Contains(fix) && !Off.Contains("all");

        private static readonly string[] Mods = { "lunar", "avmechanoidskins", "vehicles" };

        public static bool Needed(RLOSettings settings) => Mods.Any(settings.Supports);

        internal static bool On(string id) => RLOMod.Settings.Supports(id);

        public static void Install(Harmony harmony)
        {
            Harmony = harmony;
            if (Enabled("bracket") || Timing.On)
            {
                CtorBracket.InstallEarly(Harmony);
            }
            Timing.Install();
            MainMenu.Then(Report);
        }

        public static void Try(string fix, Action install) => RLOMod.Try(fix, Enabled(fix), install);

        public static void Report()
        {
            if (reported)
            {
                return;
            }
            reported = true;
            foreach (var report in Reports)
            {
                try
                {
                    Log.Message("[RLO] " + report());
                }
                catch (Exception e)
                {
                    Log.Warning($"[RLO] report failed: {e.Message}");
                }
            }
        }


    }

    [StaticConstructorOnStartup]
    public static class Ctors2Late
    {
        static Ctors2Late()
        {
            if (Ctors2Mod.Harmony == null)
            {
                return;
            }
            Timing.InstallMethods(Ctors2Mod.Harmony);
            if (Ctors2Mod.On("lunar"))
            {
                Ctors2Mod.Try("lunar", () => LunarConflicts.Install(Ctors2Mod.Harmony));
            }
            if (Ctors2Mod.On("avmechanoidskins"))
            {
                Ctors2Mod.Try("av", () => AvSkins.Install(Ctors2Mod.Harmony));
            }
            if (Ctors2Mod.On("vehicles"))
            {
                Ctors2Mod.Try("vehicles", () => VehiclePatternLookups.Install(Ctors2Mod.Harmony));
            }
        }
    }
}
