using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class Compatibility
    {
        private sealed class Support
        {
            public string Name;
            public string Type;
            public Func<RLOSettings, bool> Enabled;
            public string Feature;
        }

        private static readonly Support[] Supports =
        {
            new Support { Name = "Melee Animation", Type = "AM.Retexture.RetextureUtility", Enabled = s => s.compatFixes },
            new Support { Name = "Giddy-Up", Type = "GiddyUp.TextureUtility", Enabled = s => s.compatFixes, Feature = "Giddy-Up draw offsets" },
            new Support { Name = "Vehicle Framework", Type = "Vehicles.PatternDef", Enabled = s => s.compatFixes, Feature = "Vehicle Framework pattern rotation" },
            new Support { Name = "Lunar", Type = "LunarFramework.Patching.HarmonyInliningFixer", Enabled = s => s.compatFixes, Feature = "Lunar inlining fixer" },
            new Support { Name = "What's That Mod", Type = "WhatsThatMod.ModCore", Enabled = s => s.compatFixes, Feature = "What's That Mod patch scan" },
            new Support { Name = "Children Wear Anything", Type = "ChildClothingUnrestricted.TextureRouting.TexturePathResolver", Enabled = s => s.compatFixes, Feature = "Children Wear Anything texture checks" },
            new Support { Name = "Research Reinvented: Stepping Stones", Type = "PeteTimesSix.ResearchReinvented_SteppingStones.PreregRebuilders.PreregRebuilder", Enabled = s => s.compatFixes, Feature = "Research Reinvented: Stepping Stones recipe users" },
            new Support { Name = "Colony Groups", Type = "TacticalGroups.GenMapUIOptimized", Enabled = s => s.compatFixes && ColonyGroupsTexture.Repaired },
            new Support { Name = "Loading Progress", Type = "ilyvion.LoadingProgress.StaticConstructorOnStartupUtilityReplacement", Enabled = s => s.compatFixes },
            new Support { Name = "Missile Girl", Type = "Gagarin.CachedDefHelper", Enabled = s => s.gagarinFast, Feature = "Faster Missile Girl cache load" },
            new Support { Name = "Graphics Settings+", Type = "GraphicSetter.DDSLoader", Enabled = s => s.ddsPrefetch, Feature = "Graphics Settings+ texture buffers (falls back to read-ahead)" },
            new Support { Name = "CombatAI", Type = "CombatAI.Patches.LabelSuppressor_Patch", Enabled = s => s.modFixes, Feature = "cai" },
            new Support { Name = "HugsLib", Type = "HugsLib.Utils.HarmonyUtility", Enabled = s => s.modFixes, Feature = "hugslib" },
            new Support { Name = "Adaptive Storage", Type = "PostInheritanceOperation.PatchQueue", Enabled = s => s.modFixes, Feature = "Adaptive Storage inheritance" },
            new Support { Name = "Vanilla Trading Expanded", Type = "VanillaTradingExpanded.Utils", Enabled = s => s.startupCodeFixes, Feature = "Vanilla Trading Expanded craftable check" },
            new Support { Name = "TD Find Lib", Type = "TD_Find_Lib.ModStartup", Enabled = s => s.startupCodeFixes, Feature = "TD Find Lib mod names" },
            new Support { Name = "Linkable Groups", Type = "LinkableGroups.LinkableGroupsLoader", Enabled = s => s.startupCodeFixes, Feature = "Linkable Groups facility links" },
            new Support { Name = "KCSG", Type = "KCSG.StructureLayoutDef", Enabled = s => s.startupCodeFixes, Feature = "KCSG mod requirements" },
            new Support { Name = "AV Mechanoid Skins", Type = "AV_MechanoidSkins.MechSkinManager", Enabled = s => s.startupCodeFixes, Feature = "AV Mechanoid Skins texture checks" },
        };

        private static readonly string[] Optimizers = { "Faster Game Loading", "Performance Optimizer", "Yet another Optimizer", "WOWGAG's Performance Patch", "RocketMan", "Performance Fish", "Hyperdrive", "RimThreaded", "Performance Esmolas", "Image Opt" };

        public static bool LazyTextures;

        private static List<Support> present;
        private static List<string> alongside;

        public static void Detect(RLOSettings settings, ModContentPack self)
        {
            present = Supports.Where(s => s.Enabled(settings) && (ModTypes.Find(s.Type) != null || AccessTools.TypeByName(s.Type) != null)).ToList();
            alongside = LoadedModManager.RunningModsListForReading
                .Where(m => m != self && m.Name != null && Optimizers.Any(w => m.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                .Select(m => m.Name)
                .Distinct()
                .ToList();
            Log.Message("[RLO] " + Stats());
        }

        public static IEnumerable<string> ExtraSupport()
        {
            if (present == null)
            {
                return Enumerable.Empty<string>();
            }
            List<string> changed;
            lock (Fingerprint.Changed)
            {
                changed = Fingerprint.Changed.ToList();
            }
            return present.Where(s => s.Feature == null || !changed.Contains(s.Feature)).Select(s => s.Name);
        }

        public static IEnumerable<string> Alongside() => alongside ?? Enumerable.Empty<string>();

        public static string Stats() => $"extra support for: {string.Join(", ", ExtraSupport())}; running alongside: {string.Join(", ", Alongside())}";
    }
}
