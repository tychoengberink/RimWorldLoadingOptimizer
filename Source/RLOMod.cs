using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using HarmonyLib;
using KTrie;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Verse;

namespace RimWorldLoadingOptimizer
{
    public class RLOSettings : ModSettings
    {
        public bool fastXPath = true;
        public bool shadowedTextures = true;
        public bool recipeIndex = true;
        public bool categoryCache = true;
        public bool textureLookup = true;
        public bool ddsFallback = true;
        public bool audioStreaming = true;
        public bool startupReport = true;
        public bool xmlRelease = true;
        public bool shadowedCodeMods = false;
        public bool ddsPrefetch = true;
        public bool earlyLoadBoost = true;
        public bool compatFixes = true;
        public bool thinkKeys = true;
        public bool harmonyModuleCache = true;
        public bool harmonyIdentityMap = true;
        public bool startupPhases = true;
        public bool modFixes = true;
        public bool loadingBanner = true;
        public bool startupCodeFixes = true;
        public bool defLoadingFixes = true;
        public bool gagarinFast = true;
        public bool typeIndex = true;
        public bool atlasCache = true;
        public bool fasterModConstructors = true;
        public int measureStep;
        public float measureOffSeconds;
        public float measureOffPatchSeconds;
        public long measureOffPeakMb;
        public string lastMeasurement = "";

        public override void ExposeData()
        {
            Scribe_Values.Look(ref fastXPath, "fastXPath", true);
            Scribe_Values.Look(ref shadowedTextures, "shadowedTextures", true);
            Scribe_Values.Look(ref recipeIndex, "recipeIndex", true);
            Scribe_Values.Look(ref categoryCache, "categoryCache", true);
            Scribe_Values.Look(ref textureLookup, "textureLookup", true);
            Scribe_Values.Look(ref ddsFallback, "ddsFallback", true);
            Scribe_Values.Look(ref audioStreaming, "audioStreaming", true);
            Scribe_Values.Look(ref startupReport, "startupReport", true);
            Scribe_Values.Look(ref xmlRelease, "xmlRelease", true);
            Scribe_Values.Look(ref shadowedCodeMods, "shadowedCodeMods", false);
            Scribe_Values.Look(ref ddsPrefetch, "ddsPrefetch", true);
            Scribe_Values.Look(ref earlyLoadBoost, "earlyLoadBoost", true);
            Scribe_Values.Look(ref compatFixes, "compatFixes", true);
            Scribe_Values.Look(ref thinkKeys, "thinkKeys", true);
            Scribe_Values.Look(ref harmonyModuleCache, "harmonyModuleCache", true);
            Scribe_Values.Look(ref harmonyIdentityMap, "harmonyIdentityMap", true);
            Scribe_Values.Look(ref startupPhases, "startupPhases", true);
            Scribe_Values.Look(ref modFixes, "modFixes", true);
            Scribe_Values.Look(ref loadingBanner, "loadingBanner", true);
            Scribe_Values.Look(ref startupCodeFixes, "startupCodeFixes", true);
            Scribe_Values.Look(ref defLoadingFixes, "defLoadingFixes", true);
            Scribe_Values.Look(ref gagarinFast, "gagarinFast", true);
            Scribe_Values.Look(ref typeIndex, "typeIndex", true);
            Scribe_Values.Look(ref atlasCache, "atlasCache", true);
            Scribe_Values.Look(ref fasterModConstructors, "fasterModConstructors", true);
            Scribe_Values.Look(ref measureStep, "measureStep", 0);
            Scribe_Values.Look(ref measureOffSeconds, "measureOffSeconds", 0f);
            Scribe_Values.Look(ref measureOffPatchSeconds, "measureOffPatchSeconds", 0f);
            Scribe_Values.Look(ref measureOffPeakMb, "measureOffPeakMb", 0L);
            Scribe_Values.Look(ref lastMeasurement, "lastMeasurement", "");
        }
    }

    public class RLOMod : Mod
    {
        private readonly RLOSettings settings;
        private readonly RLOSettings persisted;

        public static RLOSettings Settings { get; private set; }

        internal static readonly bool Verify = Environment.GetEnvironmentVariable("LEANMEMORY_VERIFY") == "1";

        public RLOMod(ModContentPack content) : base(content)
        {
            settings = persisted = GetSettings<RLOSettings>();
            var allOff = Environment.GetEnvironmentVariable("LEANMEMORY_ALLOFF") == "1" || persisted.measureStep == Measurement.MeasureOff;
            if (allOff)
            {
                settings = new RLOSettings
                {
                    fastXPath = false, shadowedTextures = false, recipeIndex = false, categoryCache = false, textureLookup = false,
                    ddsFallback = false, audioStreaming = false, xmlRelease = false, ddsPrefetch = false, earlyLoadBoost = false, compatFixes = false, thinkKeys = false, harmonyModuleCache = false, harmonyIdentityMap = false, startupPhases = false, modFixes = false, startupCodeFixes = false, defLoadingFixes = false, gagarinFast = false, typeIndex = false, atlasCache = false, fasterModConstructors = false
                };
            }
            Settings = settings;
            var harmony = new Harmony(content.PackageId);
            var running = LoadedModManager.RunningModsListForReading.Select(m => m.PackageId.ToLowerInvariant()).ToList();
            Compatibility.LazyTextures = running.Contains("sz.yaopt") && running.Contains("zetrith.prepatcher");
            var xmlCache = running.Contains("wowgag.guiperformancepatch");
            if (xmlCache)
            {
                Log.Message("[RLO] WOWGAG's Performance Patch caches the whole XML stage itself; RimWorld Loading Optimizer's fast XML patching and its Missile Girl cache helpers stay off so the two caches do not compete.");
            }
            if (Compatibility.LazyTextures)
            {
                Log.Message("[RLO] Yet another Optimizer can load textures lazily with Prepatcher; texture lookups go through the game's own loader, and the atlas cache, DDS read-ahead and DDS fallback stay off.");
            }
            FasterCtors.On = settings.fasterModConstructors;
            Try("reflection cache", FasterCtors.Enabled("reflcache"), () => ReflCache.Install(harmony));
            Try("assembly list cache", FasterCtors.Enabled("asmcache"), () => AsmCache.Install(harmony));
            Try("assembly type cache", FasterCtors.Enabled("earlytypes"), () => TypesCache.Install(harmony));
            if (settings.fasterModConstructors)
            {
                LeanFixes.Reports.Add(() => IlScan.Stats() + "; " + LoadFromFix.Stats());
            }
            Try("load time measurement", true, () => Measurement.Install(harmony, this, persisted, allOff));
            Try("loading banner", persisted.loadingBanner, () => LoadingBanner.Install(harmony, allOff));
            var xpathCompetitor = LoadedModManager.RunningModsListForReading.FirstOrDefault(m =>
                string.Equals(m.PackageIdPlayerFacing, "vopaga.hyperdrive", StringComparison.OrdinalIgnoreCase)
                || (m.Name ?? "").IndexOf("Yet Another Optimizer", StringComparison.OrdinalIgnoreCase) >= 0);
            if (xpathCompetitor != null && settings.fastXPath)
            {
                Log.Message($"[RLO] {xpathCompetitor.Name} also accelerates XML patching; RimWorld Loading Optimizer's fast XML patching stays off to avoid conflicts.");
            }
            Try("Harmony identity map", settings.harmonyIdentityMap, () => HarmonyIdentityMap.Install(harmony));
            Try("mod constructor speedups", settings.compatFixes, () => CtorFixes.InstallEarly(harmony));
            Try("atlas cache", settings.atlasCache && !Compatibility.LazyTextures && Environment.GetEnvironmentVariable("LEANMEMORY_NOATLASCACHE") != "1", () => AtlasCache.Install(harmony));
            Try("type name index", settings.typeIndex, () => TypeIndex.Install(harmony));
            Try("Harmony module cache", settings.harmonyModuleCache, () => ModuleCache.Install(harmony));
            Try("think tree keys", settings.thinkKeys, () => ThinkKeys.Install(harmony));
            Try("Missile Girl cache load", settings.gagarinFast && !xmlCache, () => GagarinFast.Install(harmony));
            Try("fast XML patching", settings.fastXPath && xpathCompetitor == null && !xmlCache, () => FastXPath.Install(harmony));
            Try("audio streaming", settings.audioStreaming, () => AudioStreaming.Install(harmony));
            Try("startup report", settings.startupReport, () => StartupProfiler.Install(harmony));
            Try("category cache", settings.categoryCache, () => CategoryCache.Install(harmony));
            Try("recipe index", settings.recipeIndex, () => RecipeIndex.Install(harmony));
            Try("texture lookup index", settings.textureLookup && !Compatibility.LazyTextures, () => TextureLookup.Install(harmony));
            var imageOpt = Application.platform != RuntimePlatform.OSXPlayer && AccessTools.TypeByName("ImageOpt.TextureLoadPatch") != null;
            if (imageOpt)
            {
                Log.Message("[RLO] Image Opt loads textures itself on this system; DDS read-ahead and the DDS fallback stay off, shadowed textures are released after Image Opt's texture swap.");
            }
            Try("DDS fallback", settings.ddsFallback && !imageOpt && !Compatibility.LazyTextures, () => DdsFallback.Install(harmony));
            Try("early load boost", settings.earlyLoadBoost && Environment.GetEnvironmentVariable("LEANMEMORY_NOBOOST") != "1", () => EarlyLoadBoost.Install(harmony));
            Try("DDS prefetch", settings.ddsPrefetch && !imageOpt && !Compatibility.LazyTextures && Environment.GetEnvironmentVariable("LEANMEMORY_NOPREFETCH") != "1", () => DdsPrefetch.Install(harmony));
            Try("faster mod startup code", settings.startupCodeFixes, () => Ctors2Mod.Install(harmony));
            Try("XML child list cursor", settings.defLoadingFixes, () => ChildCursor.Install(harmony));
            Try("surgery index", settings.defLoadingFixes, () => SurgeryIndex.Install(harmony));
            Try("type name lookups", settings.defLoadingFixes, () => TypeNameIndex.Install(harmony));
            Try("facility links", settings.defLoadingFixes, () => FacilityFast.Install(harmony));
            Try("Missile Girl background cache write", settings.defLoadingFixes && settings.gagarinFast && !xmlCache, () => GagarinAsync.Install(harmony));
            Try("startup phase fixes", true, () => LeanFixes.Install(harmony, settings.startupPhases, settings.modFixes, settings.textureLookup, Try));
            Try("def XML release", settings.xmlRelease, () => XmlRelease.Install(harmony));
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                Try("Colony Groups texture repair", settings.compatFixes, ColonyGroupsTexture.Repair);
                Try("compatibility list", true, () => Compatibility.Detect(settings, content));
                if (settings.shadowedTextures)
                {
                    ShadowedTextures.IncludeCodeMods = settings.shadowedCodeMods;
                    if (imageOpt)
                    {
                        LongEventHandler.ExecuteWhenFinished(ShadowedTextures.Release);
                    }
                    else
                    {
                        ShadowedTextures.Release();
                    }
                }
                if (settings.startupReport)
                {
                    LongEventHandler.ExecuteWhenFinished(StartupProfiler.Report);
                }
            });
        }

        private static void Try(string feature, bool enabled, Action install)
        {
            if (!enabled)
            {
                return;
            }
            try
            {
                install();
            }
            catch (Exception e)
            {
                Log.Error($"[RLO] {feature} could not be enabled and was skipped: {e}");
            }
        }

        public override string SettingsCategory() => "RimWorld Loading Optimizer";

        private static Vector2 settingsScroll;
        private static float settingsHeight = 1600f;

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var view = new Rect(0f, 0f, inRect.width - 20f, settingsHeight);
            Widgets.BeginScrollView(inRect, ref settingsScroll, view);
            var list = new Listing_Standard();
            list.Begin(view);
            if (persisted.measureStep == Measurement.Idle)
            {
                if (list.ButtonTextLabeled("Measure how much RimWorld Loading Optimizer saves on your PC (two restarts)", "Start"))
                {
                    persisted.measureStep = Measurement.MeasureOff;
                    WriteSettings();
                    Find.WindowStack.Add(new Dialog_MessageBox("Measurement prepared.\n\nQuit RimWorld and start it again: that start runs with RimWorld Loading Optimizer switched off. When the main menu appears you will be asked to restart once more, with RimWorld Loading Optimizer switched on. Then the difference is shown.\n\nDo not change mods or settings in between."));
                }
            }
            else
            {
                list.Label(persisted.measureStep == Measurement.MeasureOff
                    ? "Measurement prepared: restart RimWorld to measure with RimWorld Loading Optimizer switched off."
                    : $"Measured {persisted.measureOffSeconds:F1} s with RimWorld Loading Optimizer off: restart RimWorld to measure with it switched on.");
                if (list.ButtonText("Cancel measurement"))
                {
                    persisted.measureStep = Measurement.Idle;
                    WriteSettings();
                }
            }
            if (!string.IsNullOrEmpty(persisted.lastMeasurement))
            {
                list.Label("Last result: " + persisted.lastMeasurement);
            }
            if (settings != persisted)
            {
                list.Label("This session runs with every feature switched off (measurement run); changes below are not saved.");
            }
            list.GapLine();
            list.Label("Changes apply after restarting RimWorld.");
            list.GapLine();
            list.CheckboxLabeled("Fast XML patching", ref settings.fastXPath, "Answer PatchOperation XPaths from an exact def index. Biggest load-time win.");
            list.CheckboxLabeled("Release shadowed textures", ref settings.shadowedTextures, "Free textures that a later mod overrides and that can never be shown.");
            list.CheckboxLabeled("   ...also from mods that ship code", ref settings.shadowedCodeMods, "Frees more memory, but a mod that reads its own overridden texture in code would get nothing back (missing icon).");
            list.CheckboxLabeled("Recipe index", ref settings.recipeIndex, "Build ThingDef.AllRecipes from one reverse index.");
            list.CheckboxLabeled("Category cache", ref settings.categoryCache, "Cache flattened item category lists used by recipe and stockpile filters.");
            list.CheckboxLabeled("Texture lookup index", ref settings.textureLookup, "Answer graphic texture lookups from one index instead of scanning every mod.");
            list.CheckboxLabeled("Read textures ahead", ref settings.ddsPrefetch, "Read DDS texture files on background threads before the game needs them, so the main thread does not wait for the disk (Graphics Settings+ loader).");
            list.CheckboxLabeled("Faster early content loading", ref settings.earlyLoadBoost, "While the game is still loading defs, let Faster Game Loading load mod textures for up to 250 ms per frame instead of 50 ms.");
            list.CheckboxLabeled("Speed fixes for other mods", ref settings.compatFixes, "Output-identical speedups for slow startup code in specific mods (for example Melee Animation's retexture scan). Only loaded when those mods are active.");
            list.CheckboxLabeled("Faster think tree keys", ref settings.thinkKeys, "Assign the same think tree save keys without re-probing every taken key (quadratic in vanilla).");
            list.CheckboxLabeled("Cache baked texture atlases", ref settings.atlasCache, "Save the baked static texture atlases to disk and load them on the next start when none of their textures changed, instead of baking them again (about 0.8 GB on disk). Only works when the game compresses atlases on the processor, as on macOS; most Windows graphics cards compress them on the card and then this cache stays empty.");
            list.CheckboxLabeled("Type name index", ref settings.typeIndex, "Answer AccessTools.TypeByName lookups for types that are not found directly from one index, instead of listing every type of every assembly per call (mods probing for absent mods).");
            list.CheckboxLabeled("Faster def loading", ref settings.defLoadingFixes, "Faster reading of long XML lists, one-pass surgery recipe checks, indexed type name lookups, and Missile Girl's cache file written in the background. Same results.");
            list.CheckboxLabeled("Faster mod constructors", ref settings.fasterModConstructors, "Makes the first part of startup, where every mod sets itself up, faster by remembering answers that mods ask the game for again and again. Part of it needs Prepatcher. Same results.");
            list.CheckboxLabeled("Faster mod startup code", ref settings.startupCodeFixes, "Faster versions of slow startup code in Vanilla Trading Expanded, TD Find Lib, Lunar, Linkable Groups, KCSG, AV Mechanoid Skins and Vehicle Framework. Same results; each one switches itself off when that mod changes.");
            list.CheckboxLabeled("Show the RimWorld Loading Optimizer banner while loading", ref settings.loadingBanner, "A bar at the top of the loading screen that shows RimWorld Loading Optimizer is active, live counters, and the last measured result on this PC.");
            list.CheckboxLabeled("Faster content lookups and loading screen", ref settings.startupPhases, "Skips mods without asset bundles when the game looks up textures and sounds, caches Assembly.GetTypes, indexes designators per category, and lets the loading screen run 0.25 s of work per frame instead of 0.1 s. Same results, verified.");
            list.CheckboxLabeled("Speed fixes for CombatAI, HugsLib and category lists", ref settings.modFixes, "Faster versions of CombatAI's label scan, HugsLib's obsolete patch check and ThingCategoryDef lookups. Each one switches itself off when the mod's code changes.");
            list.CheckboxLabeled("Faster Harmony patching", ref settings.harmonyIdentityMap, "When a mod patches a method, Harmony converts every referenced method, field and type to Mono.Cecil and back. RimWorld Loading Optimizer remembers the original object so the way back is a lookup instead of a search. The generated code is identical.");
            list.CheckboxLabeled("Harmony module lookup cache", ref settings.harmonyModuleCache, "Cache Harmony's module lookups that otherwise scan every loaded assembly for each patch.");
            list.CheckboxLabeled("Faster Missile Girl XML cache load", ref settings.gagarinFast, "Load Missile Girl's def cache straight into the def document and reuse already parsed patch files.");
            list.CheckboxLabeled("DDS fallback to PNG", ref settings.ddsFallback, "Load the PNG when a shipped DDS file cannot be loaded.");
            list.CheckboxLabeled("Stream medium .ogg sounds", ref settings.audioStreaming, "Stream sound files of 100 KB and up instead of decoding them into memory.");
            list.CheckboxLabeled("Release def XML after loading", ref settings.xmlRelease, "Stop vanilla's unused translation-key bookkeeping from keeping the whole merged def XML in memory.");
            list.CheckboxLabeled("Startup report in log", ref settings.startupReport, "Log cache statistics and the slowest mod static constructors.");
            settingsHeight = list.CurHeight + 20f;
            list.End();
            Widgets.EndScrollView();
        }
    }

    public static class ShadowedTextures
    {
        private static readonly AccessTools.FieldRef<ModContentHolder<Texture2D>, StringTrieSet> Trie =
            AccessTools.FieldRefAccess<ModContentHolder<Texture2D>, StringTrieSet>("contentListTrie");

        public static bool IncludeCodeMods;

        public static void Release()
        {
            try
            {
                ReleaseInternal();
            }
            catch (Exception e)
            {
                Log.Error($"[RLO] shadowed texture release failed, nothing was freed: {e}");
            }
        }

        private static void ReleaseInternal()
        {
            var winners = new HashSet<string>();
            var released = new List<Texture2D>();
            var keptForCode = new List<Texture2D>();
            var meleePaths = MeleeWeaponPaths();
            var keptForMelee = 0;
            var mods = LoadedModManager.RunningModsListForReading;
            for (var i = mods.Count - 1; i >= 0; i--)
            {
                var holder = mods[i].GetContentHolder<Texture2D>();
                var hasCode = mods[i].assemblies.loadedAssemblies.Count > 0;
                foreach (var path in holder.contentList.Keys.ToList())
                {
                    if (winners.Add(path))
                    {
                        continue;
                    }
                    if (hasCode && !IncludeCodeMods)
                    {
                        keptForCode.Add(holder.contentList[path]);
                        continue;
                    }
                    if (meleePaths != null && IsUnder(meleePaths, path))
                    {
                        keptForMelee++;
                        continue;
                    }
                    released.Add(holder.contentList[path]);
                    holder.contentList.Remove(path);
                    Trie(holder).Remove(path);
                }
            }

            var kept = new HashSet<Texture2D>(mods.SelectMany(m => m.GetContentHolder<Texture2D>().contentList.Values));
            long bytes = 0;
            foreach (var texture in released.Where(t => t != null && t != BaseContent.BadTex && !kept.Contains(t)).Distinct())
            {
                bytes += EstimatedBytes(texture);
                UnityEngine.Object.Destroy(texture);
            }
            long keptBytes = keptForCode.Where(t => t != null).Distinct().Sum(t => EstimatedBytes(t));
            Log.Message($"[RLO] released {released.Count} shadowed textures (~{bytes / (1 << 20)} MB), kept {keptForCode.Count} overridden textures of code mods (~{keptBytes / (1 << 20)} MB) and {keptForMelee} melee weapon textures for Melee Animation; streamed {AudioStreaming.Streamed} extra sound files ({AudioStreaming.StreamedBytes >> 20} MB on disk) instead of decoding them; {MonoHeap.Stats()}");
        }

        private static HashSet<string> MeleeWeaponPaths()
        {
            if (AccessTools.TypeByName("AM.Retexture.RetextureUtility") == null)
            {
                return null;
            }
            var paths = new HashSet<string>();
            foreach (var def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                var texPath = def.graphicData?.texPath;
                if (!string.IsNullOrEmpty(texPath) && def.IsMeleeWeapon)
                {
                    paths.Add(texPath);
                }
            }
            return paths;
        }

        private static bool IsUnder(HashSet<string> roots, string path)
        {
            for (var current = path; ; )
            {
                if (roots.Contains(current))
                {
                    return true;
                }
                var slash = current.LastIndexOf('/');
                if (slash <= 0)
                {
                    return false;
                }
                current = current.Substring(0, slash);
            }
        }

        private static long EstimatedBytes(Texture2D texture)
        {
            long total = 0;
            for (var mip = 0; mip < texture.mipmapCount; mip++)
            {
                total += (long)GraphicsFormatUtility.ComputeMipmapSize(Math.Max(1, texture.width >> mip), Math.Max(1, texture.height >> mip), texture.graphicsFormat);
            }
            return total;
        }
    }

    public static class MonoHeap
    {
        private delegate UIntPtr GetSize();

        [DllImport("/usr/lib/libSystem.dylib")]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        public static string Stats()
        {
            try
            {
                return $"Mono heap {Size("GC_get_heap_size") >> 20} MB, free {Size("GC_get_free_bytes") >> 20} MB";
            }
            catch (Exception)
            {
                return "Mono heap stats unavailable on this platform";
            }
        }

        private static ulong Size(string symbol)
        {
            var ptr = dlsym(new IntPtr(-2), symbol);
            if (ptr == IntPtr.Zero)
            {
                throw new EntryPointNotFoundException(symbol);
            }
            return ((GetSize)Marshal.GetDelegateForFunctionPointer(ptr, typeof(GetSize)))().ToUInt64();
        }
    }
}
