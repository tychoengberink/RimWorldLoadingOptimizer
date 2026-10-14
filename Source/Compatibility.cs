using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public static class Compatibility
    {
        public sealed class Support
        {
            public string Id;
            public string Name;
            public string Type;
            public string Feature;
            public string Description;
            public Func<bool> Listed = () => true;
        }

        public static readonly Support[] Supports =
        {
            new Support { Id = "meleeanimation", Name = "Melee Animation", Description = "Index for Melee Animation's retexture scan.", Type = "AM.Retexture.RetextureUtility", Feature = "Melee Animation retexture scan index" },
            new Support { Id = "vehicles", Name = "Vehicle Framework", Description = "Routes Vehicle Framework's pattern texture lookups through RimWorld Loading Optimizer's texture index.", Type = "Vehicles.PatternDef", Feature = "Vehicle Framework pattern lookups" },
            new Support { Id = "lunar", Name = "Lunar", Description = "Faster Lunar inlining fixer and patch conflict check.", Type = "LunarFramework.Patching.HarmonyInliningFixer", Feature = "Lunar inlining fixer" },
            new Support { Id = "whatsthatmod", Name = "What's That Mod", Description = "Precomputed patch scan for What's That Mod.", Type = "WhatsThatMod.ModCore", Feature = "What's That Mod patch scan" },
            new Support { Id = "childrenwearanything", Name = "Children Wear Anything", Description = "Routes Children Wear Anything's texture lookups through RimWorld Loading Optimizer's texture index.", Type = "ChildClothingUnrestricted.TextureRouting.TexturePathResolver", Feature = "Texture lookup index" },
            new Support { Id = "steppingstones", Name = "Research Reinvented: Stepping Stones", Description = "Indexed recipe-user lookups for Stepping Stones.", Type = "PeteTimesSix.ResearchReinvented_SteppingStones.PreregRebuilders.PreregRebuilder", Feature = "Research Reinvented: Stepping Stones recipe users" },
            new Support { Id = "loadingprogress", Name = "Loading Progress", Description = "Installs the fixes above before Loading Progress runs the static constructors.", Type = "ilyvion.LoadingProgress.StaticConstructorOnStartupUtilityReplacement" },
            new Support { Id = "missilegirl", Name = "Missile Girl", Description = "Missile Girl's own cache loader reads its cache straight into the def document instead of copying every def.", Type = "Gagarin.CachedDefHelper", Feature = "Faster Missile Girl cache load" },
            new Support { Id = "graphicssettings", Name = "Graphics Settings+", Description = "Hands Graphics Settings+'s own DDS loader file bytes that were read ahead in memory (otherwise only the file cache is warmed).", Type = "GraphicSetter.DDSLoader", Feature = "Graphics Settings+ texture buffers (falls back to read-ahead)" },
            new Support { Id = "combatai", Name = "CombatAI", Description = "Faster CombatAI label scan; also moves its label patches to the main thread.", Type = "CombatAI.Patches.LabelSuppressor_Patch", Feature = "CombatAI label scan" },
            new Support { Id = "linkablegroups", Name = "Linkable Groups", Description = "Indexed facility links for Linkable Groups.", Type = "LinkableGroups.LinkableGroupsLoader", Feature = "Linkable Groups facility links" },
#if RW17
            new Support { Id = "giddyup", Name = "Giddy-Up 2", Description = "Remembers Giddy-Up's rider draw offsets between starts, keyed on the exact texture content, instead of reading every animal texture back from the graphics card on every start.", Type = "GiddyUp.TextureUtility", Feature = GiddyUpOffsets.Feature },
            new Support { Id = "miningoutpost", Name = "Worksites Expanded", Description = "Answers Worksites Expanded's tome-trader check directly while no game is loaded, where the original check can only fail with an error that is caught, tens of thousands of times per start.", Type = "MiningOutpost.Loot.LootRarity", Feature = WorksitesTraders.Feature },
            new Support { Id = "alienworlds", Name = "Alien Worlds Framework", Description = "Reuses the Worldbuilder preset images Alien Worlds Framework wrote last start when their source textures are unchanged, instead of rendering and encoding them again.", Type = "AlienWorlds.WorldbuilderCompat", Feature = AlienWorldsPresets.Feature },
#endif
            new Support { Id = "avmechanoidskins", Name = "AV Mechanoid Skins", Description = "Faster texture checks for AV Mechanoid Skins.", Type = "AV_MechanoidSkins.MechSkinManager", Feature = "AV Mechanoid Skins texture checks" },
        };

        private static readonly string[] Optimizers = { "Faster Game Loading", "Performance Optimizer", "Yet another Optimizer", "WOWGAG's Performance Patch", "RocketMan", "Performance Fish", "Hyperdrive", "RimThreaded", "Performance Esmolas", "Image Opt" };

        public static bool LazyTextures;

        private static List<Support> present;
        private static List<string> alongside;

        public static void Detect(RLOSettings settings, ModContentPack self)
        {
            present = Supports.Where(s => settings.Supports(s.Id) && s.Listed() && (ModTypes.Find(s.Type) != null || AccessTools.TypeByName(s.Type) != null)).ToList();
            alongside = LoadedModManager.RunningModsListForReading
                .Where(m => m != self && m.Name != null && Optimizers.Any(w => m.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                .Select(m => m.Name)
                .Distinct()
                .ToList();
            MainMenu.Then(LogSupport);
        }

        private static void LogSupport()
        {
            var supported = ExtraSupport().ToList();
            Log.Message($"[RLO] extra support for {supported.Count} mods: {(supported.Count == 0 ? "none" : string.Join(", ", supported))}");
            Log.Message($"[RLO] running alongside {alongside.Count} other optimizers: {(alongside.Count == 0 ? "none" : string.Join(", ", alongside))}");
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
    }
}
