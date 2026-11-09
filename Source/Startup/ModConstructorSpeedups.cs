using System;
using System.Linq;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class ModConstructorSpeedups
    {
        private static readonly string[] off = DevSwitches.ModConstructorSpeedupsOff;
        private static bool? passOne;

        public static bool On;

        public static bool Off(string fix) => off.Contains("all") || off.Contains(fix);

        public static bool Enabled(string fix) => On && !Off(fix);

        public static bool PassOne(string fix)
        {
            if (Off(fix) || DevSwitches.AllOff)
            {
                return false;
            }
            if (passOne == null)
            {
                try
                {
                    var pack = LoadedModManager.RunningModsListForReading.FirstOrDefault(m => m.assemblies.loadedAssemblies.Contains(typeof(ModConstructorSpeedups).Assembly))
                        ?? RLOMod.Running("aruclya.rimworldloadingoptimizer");
                    if (pack == null)
                    {
                        UnityEngine.Debug.Log("[RLO] pass 1: could not find RimWorld Loading Optimizer's mod folder to read its settings, free patches stay off");
                        passOne = false;
                        return false;
                    }
                    var settings = LoadedModManager.ReadModSettings<RLOSettings>(pack.FolderName, nameof(RLOMod));
                    passOne = settings.fasterModConstructors && settings.measureStep != Measurement.MeasureOff;
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.Log("[RLO] pass 1: could not read settings, free patches stay off: " + e);
                    passOne = false;
                }
            }
            return passOne.Value;
        }
    }
}
