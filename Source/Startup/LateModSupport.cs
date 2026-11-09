using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class LateModSupport
    {
        public static readonly bool Verify = DevSwitches.Verify || DevSwitches.LateModSupportVerify;
        private static readonly string[] Off = DevSwitches.LateModSupportOff;
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
            if (Enabled("bracket") || StaticConstructorTiming.On)
            {
                StaticConstructorTracker.InstallEarly(Harmony);
            }
            StaticConstructorTiming.Install();
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
                    RLOLog.Detail(report());
                }
                catch (Exception e)
                {
                    RLOLog.Warning($"report failed: {e.Message}");
                }
            }
        }


    }

    [StaticConstructorOnStartup]
    public static class LateModSupportInstaller
    {
        static LateModSupportInstaller()
        {
            if (LateModSupport.Harmony == null)
            {
                return;
            }
            StaticConstructorTiming.InstallMethods(LateModSupport.Harmony);
            if (LateModSupport.On("lunar"))
            {
                LateModSupport.Try("lunar", () => LunarConflicts.Install(LateModSupport.Harmony));
            }
            if (LateModSupport.On("avmechanoidskins"))
            {
                LateModSupport.Try("av", () => AvSkins.Install(LateModSupport.Harmony));
            }
            if (LateModSupport.On("vehicles"))
            {
                LateModSupport.Try("vehicles", () => VehiclePatternLookups.Install(LateModSupport.Harmony));
            }
        }
    }
}
