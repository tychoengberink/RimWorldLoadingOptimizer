using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
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
        public bool thinkKeys = true;
        public bool harmonyModuleCache = true;
        public bool harmonyIdentityMap = true;
        public bool startupPhases = true;
        public bool loadingBanner = true;
        public bool defLoadingFixes = true;
        public bool earlyContent = true;
#if RW17
        public bool backgroundGc = true;
        public bool soundReadAhead = false;
        public bool precompilePatched = true;
        public bool backgroundJitProfile = true;
        public bool harmonyRebuildCaches = true;
        public bool shadowedUploads = true;
        public bool harmonyCoalesce = false;
        public bool atlasPageBuffers = true;
        public bool meshPool = true;
        public bool atlasStamps = true;
        public bool gagarinCacheKeep = false;
        public bool gameSoundReadAhead = false;
        public bool seedIconReadAhead = false;
        public bool defXmlCache = true;
        public bool atlasBundleKeys = true;
        public bool atlasPagePrefetch = true;
#endif
#if !RW17
        public bool atlasFlush = true;
        public bool atlasPacking = true;
#endif
        public bool gagarinFast = true;
        public bool typeIndex = true;
        public bool atlasCache = true;
        public bool fasterModConstructors = true;
        public int measureStep;
        public float measureOffSeconds;
        public float measureOffPatchSeconds;
        public bool measureOffCold = true;
        public long measureOffPeakMb;
        public string lastMeasurement = "";
        public List<string> modSupport = new List<string>();

        public bool Supports(string id) => modSupport != null && modSupport.Contains(id);

        public void SetSupport(string id, bool on)
        {
            if (on && !modSupport.Contains(id))
            {
                modSupport.Add(id);
            }
            else if (!on)
            {
                modSupport.Remove(id);
            }
        }

        private static readonly FieldInfo[] Flags = typeof(RLOSettings).GetFields()
            .Where(f => f.FieldType == typeof(bool) && f.Name != nameof(measureOffCold))
            .ToArray();

        private static readonly RLOSettings Defaults = new RLOSettings();

        public static RLOSettings AllOff()
        {
            var off = new RLOSettings();
            foreach (var flag in Flags)
            {
                flag.SetValue(off, false);
            }
            off.startupReport = true;
            off.loadingBanner = true;
            return off;
        }

        public override void ExposeData()
        {
            foreach (var flag in Flags)
            {
                var value = (bool)flag.GetValue(this);
                Scribe_Values.Look(ref value, flag.Name, (bool)flag.GetValue(Defaults));
                flag.SetValue(this, value);
            }
            Scribe_Values.Look(ref measureStep, "measureStep", 0);
            Scribe_Values.Look(ref measureOffSeconds, "measureOffSeconds", 0f);
            Scribe_Values.Look(ref measureOffPatchSeconds, "measureOffPatchSeconds", 0f);
            Scribe_Values.Look(ref measureOffCold, "measureOffCold", true);
            Scribe_Values.Look(ref measureOffPeakMb, "measureOffPeakMb", 0L);
            Scribe_Values.Look(ref lastMeasurement, "lastMeasurement", "");
            Scribe_Collections.Look(ref modSupport, "modSupport", LookMode.Value);
            if (modSupport == null)
            {
                modSupport = new List<string>();
            }
        }
    }

    public class RLOMod : Mod
    {
        private readonly RLOSettings settings;
        private readonly RLOSettings persisted;

        public static RLOSettings Settings { get; private set; }

        public static string HarmonyId { get; private set; }

        public RLOMod(ModContentPack content) : base(content)
        {
            settings = persisted = GetSettings<RLOSettings>();
            var allOff = DevSwitches.AllOff || persisted.measureStep == Measurement.MeasureOff;
            if (allOff)
            {
                settings = RLOSettings.AllOff();
            }
            Settings = settings;
            if (ThreadTimeline.Wanted)
            {
                ThreadTimeline.Install();
            }
#if RW17
            Try("GC latency until the main menu", settings.backgroundGc, GcLatency.Install);
            Try("background JIT profile", settings.backgroundJitProfile, JitProfile.Install);
            Try("pre-compile patched methods", settings.precompilePatched, PrecompilePatched.Install);
#endif
            var harmony = new Harmony(content.PackageIdPlayerFacing);
            HarmonyId = harmony.Id;
#if RW17
            Try("Harmony dependency name index", settings.harmonyRebuildCaches, () => HarmonyDependencyIndex.Install(harmony));
#endif
            MainMenu.Install(harmony);
#if RW17
            if (GraphicsProfile.Wanted)
            {
                Try("graphics profile", true, () => GraphicsProfile.Install(harmony));
            }
            Try("sound read-ahead", settings.soundReadAhead, AudioReadAhead.Install);
            Try(ResourceSoundReadAhead.Feature, settings.gameSoundReadAhead, () => ResourceSoundReadAhead.Install(harmony));
            if (DevSwitches.Verify)
            {
                MainMenu.Then(AudioReadAhead.LogClipHash);
            }
#endif
            Compatibility.LazyTextures = Running("sz.yaopt") != null && Compatibility.Prepatcher;
            var xmlCache = Running("wowgag.guiperformancepatch") != null;
            if (xmlCache)
            {
                RLOLog.Message("WOWGAG's Performance Patch caches the whole XML stage itself; RimWorld Loading Optimizer's fast XML patching and its Missile Girl cache helpers stay off so the two caches do not compete.");
            }
            if (Compatibility.LazyTextures)
            {
                RLOLog.Message("Yet another Optimizer can load textures lazily with Prepatcher; texture lookups go through the game's own loader, and the atlas cache, DDS read-ahead and DDS fallback stay off.");
            }
#if RW17
            Try(XmlCache.Feature, settings.defXmlCache && !xmlCache, () => XmlCache.Install(harmony));
            Try("Missile Girl cache read-ahead", settings.gagarinFast && !xmlCache && !(settings.defXmlCache && XmlCache.CacheFileExists), GagarinReadAhead.Start);
#endif
            ModConstructorSpeedups.On = settings.fasterModConstructors;
#if RW17
            Try("Harmony patch state reuse", settings.harmonyRebuildCaches, () => PatchInfoReuse.Install(harmony));
            Try("Harmony assembly name cache", settings.harmonyRebuildCaches, () => HarmonyAssemblyNames.Install(harmony));
            Try("Harmony rebuild timing", settings.startupReport, () => HarmonyRebuildTiming.Install(harmony));
#endif
            Try("reflection cache", ModConstructorSpeedups.Enabled("reflcache"), () => ReflCache.Install(harmony));
#if !RW17
            Try("assembly list cache", ModConstructorSpeedups.Enabled("asmcache"), () => AsmCache.Install(harmony));
#endif
            Try("assembly type cache", ModConstructorSpeedups.Enabled("earlytypes"), () => TypesCache.Install(harmony));
            if (settings.fasterModConstructors)
            {
#if RW17
                StartupPhaseFixes.Reports.Add(IlScan.Stats);
#else
                StartupPhaseFixes.Reports.Add(() => IlScan.Stats() + "\n" + LoadFromFix.Stats());
#endif
            }
            Try("load time measurement", true, () => Measurement.Install(harmony, this, persisted, allOff));
            Try("loading banner", persisted.loadingBanner, () => LoadingBanner.Install(harmony, allOff));
            var parallelXmlLoader = Running("vopaga.hyperdrive");
            var xpathCompetitor = parallelXmlLoader ?? LoadedModManager.RunningModsListForReading.FirstOrDefault(m =>
                (m.Name ?? "").IndexOf("Yet Another Optimizer", StringComparison.OrdinalIgnoreCase) >= 0);
            if (xpathCompetitor != null && settings.fastXPath)
            {
                RLOLog.Message($"{xpathCompetitor.Name} also accelerates XML patching; RimWorld Loading Optimizer's fast XML patching stays off to avoid conflicts.");
            }
            Try("Harmony identity map", settings.harmonyIdentityMap, () => HarmonyIdentityMap.Install(harmony));
#if RW17
            Try("Harmony rebuild coalescing", settings.harmonyCoalesce, () => Coalesce.Install(harmony));
#endif
            Try("mod constructor speedups", EarlyModSupport.Needed(settings), () => EarlyModSupport.InstallEarly(harmony));
#if !NATIVE_ASSETS
            Try("atlas cache", settings.atlasCache && !Compatibility.LazyTextures && !DevSwitches.NoAtlasCache, () => AtlasCache.Install(harmony));
#endif
            Try("type name index", settings.typeIndex, () => TypeIndex.Install(harmony));
            Try("Harmony module cache", settings.harmonyModuleCache, () => ModuleCache.Install(harmony));
            Try("think tree keys", settings.thinkKeys, () => ThinkKeys.Install(harmony));
            if (parallelXmlLoader != null && settings.gagarinFast && !xmlCache)
            {
                RLOLog.Message($"{parallelXmlLoader.Name} loads mod XML in parallel; RimWorld Loading Optimizer leaves LoadModXML to it and skips its own patch-file reuse.");
            }
            Try("Missile Girl cache load", settings.gagarinFast && !xmlCache, () => GagarinFast.Install(harmony, parallelXmlLoader != null));
            Try("fast XML patching", settings.fastXPath && xpathCompetitor == null && !xmlCache, () => FastXPath.Install(harmony));
#if !NATIVE_ASSETS
            Try("audio streaming", settings.audioStreaming, () => AudioStreaming.Install(harmony));
#endif
            Try("startup report", settings.startupReport, () => StartupProfiler.Install(harmony));
#if RW17
            Try("GC per phase", settings.startupReport, () => GcPhases.Install(harmony));
            Try("atlas cache miss report", settings.startupReport, () => AtlasMisses.Install(harmony));
            Try("atlas page buffers", settings.atlasPageBuffers, () => AtlasPageBuffers.Install(harmony));
            Try("atlas source stamps", settings.atlasStamps, () => AtlasStamps.Install(harmony));
            Try(AtlasBundleKeys.Feature, settings.atlasBundleKeys, () => AtlasBundleKeys.Install(harmony));
            Try(AtlasPagePrefetch.Feature, settings.atlasPagePrefetch, () => AtlasPagePrefetch.Install(harmony));
            Try("mesh upload timing", settings.startupReport, () => MeshUploadTiming.Install(harmony));
            Try("mesh buffer pool", settings.meshPool && !DevSwitches.NoMeshPool, () => MeshPool.Install(harmony));
            Try("GC fallback report", settings.startupReport, GcFallback.Install);
#endif
#if !RW17
            Try("atlas flush", settings.atlasFlush && !DevSwitches.NoAtlasFlush, () => AtlasFlush.Install(harmony));
            Try("atlas packing lookups", settings.atlasPacking && !DevSwitches.NoAtlasPacking, () => AtlasPacking.Install(harmony));
#endif
            Try("category cache", settings.categoryCache, () => CategoryCache.Install(harmony));
            Try("recipe index", settings.recipeIndex, () => RecipeIndex.Install(harmony));
            Try("texture lookup index", settings.textureLookup && !Compatibility.LazyTextures, () => TextureLookup.Install(harmony));
            var imageOpt = Application.platform != RuntimePlatform.OSXPlayer && (Running("dev.soeur.imageopt") != null || AccessTools.TypeByName("ImageOpt.TextureLoadPatch") != null);
            if (imageOpt)
            {
                RLOLog.Message("Image Opt loads textures itself on this system; DDS read-ahead and the DDS fallback stay off, shadowed textures are released after Image Opt's texture swap.");
            }
            Try("DDS fallback", settings.ddsFallback && !imageOpt && !Compatibility.LazyTextures, () => DdsFallback.Install(harmony));
            Try("early load boost", settings.earlyLoadBoost && !DevSwitches.NoBoost, () => EarlyLoadBoost.Install(harmony));
#if !NATIVE_ASSETS
            Try("DDS prefetch", settings.ddsPrefetch && !imageOpt && !Compatibility.LazyTextures && !DevSwitches.NoPrefetch, () => DdsPrefetch.Install(harmony));
#endif
            Try("faster mod startup code", LateModSupport.Needed(settings), () => LateModSupport.Install(harmony));
            Try("early content loading", settings.earlyContent, () => EarlyContent.Install(harmony));
#if RW17
            Try("skip load-end memory clean-up", settings.backgroundGc, () => BackgroundGc.Install(harmony));
#endif
            Try("XML child list cursor", settings.defLoadingFixes, () => ChildCursor.Install(harmony));
#if RW17
            Try("XML previous-sibling cursor", settings.defLoadingFixes, () => SiblingCursor.Install(harmony));
#endif
            Try("surgery index", settings.defLoadingFixes, () => SurgeryIndex.Install(harmony));
            Try(ThingFilterDisallow.Feature, settings.defLoadingFixes, () => ThingFilterDisallow.Install(harmony));
            Try(ResearchCoordinates.Feature, settings.defLoadingFixes, () => ResearchCoordinates.Install(harmony));
            Try("type name lookups", settings.defLoadingFixes, () => TypeNameIndex.Install(harmony));
            Try("vanilla facility link lookup", settings.defLoadingFixes, () => FacilityFast.Install(harmony));
            Try("Missile Girl background cache write", settings.defLoadingFixes && settings.gagarinFast && settings.Supports("missilegirl") && !xmlCache, () => GagarinAsync.Install(harmony));
#if RW17
            Try("Missile Girl cache kept after a rebuild", settings.gagarinCacheKeep && settings.gagarinFast && settings.Supports("missilegirl") && !xmlCache, () => GagarinCacheKeep.Install(harmony));
#endif
            Try("startup phase fixes", true, () => StartupPhaseFixes.Install(harmony, settings, Try));
            Try("optimizer overlap check", true, () => OptimizerOverlap.Install(harmony));
#if RW17
            Try("Giddy-Up draw offsets", settings.Supports("giddyup"), () => GiddyUpOffsets.Install(harmony));
            Try("Worksites Expanded trader count", settings.Supports("miningoutpost"), () => WorksitesTraders.Install(harmony));
            Try("Vehicle Framework pattern decodes", settings.Supports("vehicles"), () => VehiclePatternDecodes.Install(harmony));
            Try(RecipeScans.TradingFeature, settings.Supports("vanillatrading"), () => RecipeScans.InstallTrading(harmony));
            Try(RecipeScans.FindFeature, settings.Supports("tdfindlib"), () => RecipeScans.InstallFind(harmony));
            Try(SeedIconReadAhead.Feature, settings.seedIconReadAhead, () => SeedIconReadAhead.Install(harmony));
            Try(CherryPickerSetup.Feature, settings.Supports("cherrypicker"), () => CherryPickerSetup.Install(harmony));
#endif
            Try("def XML release", settings.xmlRelease, () => XmlRelease.Install(harmony));
#if RW17
            Try("shadowed textures not uploaded", settings.shadowedTextures && settings.shadowedUploads, () => ShadowedUploads.Install(harmony));
#endif
            LongEventHandler.ExecuteWhenFinished(() =>
            {
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

        public static ModContentPack Running(string packageId) => LoadedModManager.RunningModsListForReading.FirstOrDefault(m => string.Equals(m.PackageIdPlayerFacing, packageId, StringComparison.OrdinalIgnoreCase));

        internal static void Try(string feature, bool enabled, Action install)
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
                RLOLog.Error($"{feature} could not be enabled and was skipped: {e}");
            }
        }

        public override string SettingsCategory() => "RimWorld Loading Optimizer";

        private static Vector2 settingsScroll;
        private static float settingsHeight = 1600f;
        private static bool? htmlExists;

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
                    : $"Measured {Measurement.Duration(persisted.measureOffSeconds)} with RimWorld Loading Optimizer off: restart RimWorld to measure with it switched on.");
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
            if ((htmlExists ?? (htmlExists = File.Exists(Measurement.HtmlPath)).Value) && list.ButtonText("Open the measurement history (every start, in your browser)"))
            {
                Application.OpenURL(new Uri(Measurement.HtmlPath).AbsoluteUri);
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
#if RW17
            list.CheckboxLabeled("   ...and do not load overridden textures onto the graphics card", ref settings.shadowedUploads, "At the end of loading the game puts every texture on the graphics card, including overridden ones that can never be shown. Skip those. If a mod still asks for one, it is loaded the first time it is drawn. Same pictures, less memory, shorter end of loading.");
#endif
            list.CheckboxLabeled("Recipe index", ref settings.recipeIndex, "Build ThingDef.AllRecipes from one reverse index.");
            list.CheckboxLabeled("Category cache", ref settings.categoryCache, "Cache flattened item category lists used by recipe and stockpile filters.");
            list.CheckboxLabeled("Texture lookup index", ref settings.textureLookup, "Answer graphic texture lookups from one index instead of scanning every mod.");
#if !NATIVE_ASSETS
            list.CheckboxLabeled("Read textures ahead", ref settings.ddsPrefetch, "Read DDS texture files on background threads before the game needs them, so the main thread does not wait for the disk. Without Graphics Settings+ they are read into memory for the game's own loader; with Graphics Settings+ only when its support is switched on below, otherwise just into the file cache.");
#endif
            list.CheckboxLabeled("Faster early content loading", ref settings.earlyLoadBoost, "While the game is still loading defs, let Faster Game Loading load mod textures for up to 250 ms per frame instead of 50 ms.");
            list.CheckboxLabeled("Faster think tree keys", ref settings.thinkKeys, "Assign the same think tree save keys without re-probing every taken key (quadratic in vanilla).");
#if !NATIVE_ASSETS
            list.CheckboxLabeled("Cache baked texture atlases", ref settings.atlasCache, "Save the baked static texture atlases to disk and load them on the next start when none of their textures changed, instead of baking them again (about 0.8 GB on disk). Only works when the game compresses atlases on the processor, as on macOS; most Windows graphics cards compress them on the card and then this cache stays empty.");
#endif
            list.CheckboxLabeled("Type name index", ref settings.typeIndex, "Answer AccessTools.TypeByName lookups for types that are not found directly from one index, instead of listing every type of every assembly per call (mods probing for absent mods).");
            list.CheckboxLabeled("Faster def loading", ref settings.defLoadingFixes, "Faster reading of long XML lists, one-pass surgery recipe checks, indexed type name lookups, and Missile Girl's cache file written in the background. Same results.");
            list.CheckboxLabeled("Load sounds and textures during XML loading", ref settings.earlyContent, "Load every mod's sounds and textures while the game reads and patches the XML, instead of waiting until the XML is done. Same order, same content.");
#if RW17
            list.CheckboxLabeled("Skip the forced memory clean-up at the end of loading", ref settings.backgroundGc, "Just before the main menu the game forces a full memory clean-up and waits for it (several seconds with many mods). Skip it; the runtime cleans up as usual while you play. Same game; memory is freed a little later.");
            list.CheckboxLabeled("Decode mod sounds ahead", ref settings.soundReadAhead, "While mod data loads, a background thread already opens and decodes the mods' sound files, so the game finds them ready instead of loading them one by one. Uses at most 256 MB of extra memory during loading. Sounds are identical.");
            list.CheckboxLabeled("Pre-compile patched methods", ref settings.precompilePatched, "Remembers which methods were patched by mods at the main menu and compiles them on spare CPU cores early in the next start, so less compiling happens on the loading thread. Only the timing of compilation changes.");
            list.CheckboxLabeled("Background JIT profile", ref settings.backgroundJitProfile, "Records which game and mod code gets compiled during startup and lets the .NET runtime compile it again on a background thread from the next start on. Only the timing of compilation changes.");
            list.CheckboxLabeled("Faster Harmony patch rebuilds", ref settings.harmonyRebuildCaches, "Every time a mod patches a method, Harmony rebuilds that method from scratch. Skip three repeated steps of that rebuild: decoding the stored patch list a second time, working out the name of every loaded assembly again, and comparing the generated code's references with every loaded assembly (an index by name answers that). Same patches, same generated code.");
            list.CheckboxLabeled("Merge repeated Harmony rebuilds (test)", ref settings.harmonyCoalesce, "When a mod patches the same game method from several patch classes, Harmony rebuilds and recompiles that method once per class. Build it once, at the end of that mod's patching, instead. Patch errors still appear exactly where they would otherwise, and the final code is identical. Off by default until it is verified in a full start.");
            list.CheckboxLabeled("Reuse buffers for cached atlas pages", ref settings.atlasPageBuffers, "When the game loads its baked texture atlases from its cache, reuse one buffer for every page instead of allocating a new 85 MB block per page. Less memory churn and fewer long memory clean-ups during atlas baking. Same atlases.");
            list.CheckboxLabeled("Pool small mesh buffers while graphics memory is full", ref settings.meshPool, "With many mod textures the graphics card's memory budget is used up, and the engine's memory allocator then gives every small mesh buffer its own allocation from the driver (tens of thousands, about 15 s in the frame before the main menu, plus wasted graphics memory). While that is the case, and only until shortly after the main menu, small mesh buffers are placed in shared 32 MB blocks instead. Same buffers, same rendering; only where they sit in graphics memory changes. Switch this off if you see graphics problems.");
            list.CheckboxLabeled("Read atlas source file details in parallel", ref settings.atlasStamps, "Before the texture atlases are baked, read the size and date of every source texture file on worker threads instead of one by one on the main thread. The game's atlas cache keys come out the same.");
            list.CheckboxLabeled("Cache atlas pages with textures from asset bundles", ref settings.atlasBundleKeys, "The game only saves a baked texture atlas page when every texture on it comes from a file, so pages with DLC or mod textures from asset bundles are baked again on every start. Give those textures a cache key from their bundle file (path, size, date and the texture's place in the bundle), the same kind of key the game uses for texture files, so these pages are saved and loaded too.");
            list.CheckboxLabeled("Read cached atlas pages ahead", ref settings.atlasPagePrefetch, "Atlas pages loaded from the game's cache are read and unpacked on the main thread, one after another. Read and unpack the pages the last start used on worker threads while the main thread prepares the atlases, a few pages ahead at a time. A page that is not ready or not listed is loaded the normal way. Same atlases.");
            list.CheckboxLabeled("Cache the patched def XML", ref settings.defXmlCache, "Saves the def XML after all patches are applied. On the next start with the same mods, files, mod settings and game version, mod XML files are not read and patches are not applied; the saved XML is read on a background thread while the mods start. Missile Girl's own XML cache stays off on those starts. Any change to a Defs, Patches or Assemblies file, the mod list or the game rebuilds it once.");
            list.CheckboxLabeled("Keep Missile Girl's rebuilt cache usable (test)", ref settings.gagarinCacheKeep, "When Missile Girl rebuilds its XML cache (for example after a mod or game update), it deletes its own mod list file and keeps hashes of files that are no longer loaded, so the next start is slow again. After a rebuild, write the mod list again and drop those hashes with Missile Girl's own functions. Missile Girl still checks every file on the next start. Off by default until it is verified after real mod updates; this works around a Missile Girl issue.");
            list.CheckboxLabeled("Prepare the game's own sounds ahead (test)", ref settings.gameSoundReadAhead, "Open the game's built-in sound files on worker threads while mod data loads, so they are ready when the sound definitions are resolved. Uses up to 128 MB of extra memory during loading. Off by default until it shows a gain.");
            list.CheckboxLabeled("Prepare PA Dynamic Seed Icons' images ahead (test)", ref settings.seedIconReadAhead, "Read and decode PA Dynamic Seed Icons' cached seed icon images on worker threads; its own code still makes every texture on the main thread. Uses up to 160 MB of extra memory during loading. Off by default until it shows a gain.");
#endif
#if !RW17
            list.CheckboxLabeled("Free atlas baking memory after every atlas", ref settings.atlasFlush, "While the texture atlases are baked, send the graphics commands after every atlas, so the graphics driver can free that atlas's temporary buffers right away instead of keeping all of them until loading is done.");
            list.CheckboxLabeled("Faster atlas packing", ref settings.atlasPacking, "When the texture atlases are packed, find each texture's place from an index instead of searching the whole texture list for every texture. The atlases come out the same.");
#endif
            list.CheckboxLabeled("Faster mod constructors", ref settings.fasterModConstructors, "Makes the first part of startup, where every mod sets itself up, faster by remembering answers that mods ask the game for again and again. Part of it needs Prepatcher. Same results.");
            list.CheckboxLabeled("Show the RimWorld Loading Optimizer banner while loading", ref settings.loadingBanner, "A bar at the top of the loading screen that shows RimWorld Loading Optimizer is active, live counters, and the last measured result on this PC.");
            list.CheckboxLabeled("Faster content lookups and loading screen", ref settings.startupPhases, "Skips mods without asset bundles when the game looks up textures and sounds, caches Assembly.GetTypes, indexes designators per category, and lets the loading screen run 0.25 s of work per frame instead of 0.1 s. Same results, verified.");
            list.CheckboxLabeled("Faster Harmony patching", ref settings.harmonyIdentityMap, "When a mod patches a method, Harmony converts every referenced method, field and type to Mono.Cecil and back. RimWorld Loading Optimizer remembers the original object so the way back is a lookup instead of a search. The generated code is identical.");
            list.CheckboxLabeled("Harmony module lookup cache", ref settings.harmonyModuleCache, "Cache Harmony's module lookups that otherwise scan every loaded assembly for each patch.");
            list.CheckboxLabeled("Reuse parsed patch files", ref settings.gagarinFast, "Parse each mod's patch files once per start and reuse them. With Missile Girl support switched on below, also load Missile Girl's def cache straight into the def document.");
            list.CheckboxLabeled("DDS fallback to PNG", ref settings.ddsFallback, "Load the PNG when a shipped DDS file cannot be loaded.");
#if !NATIVE_ASSETS
            list.CheckboxLabeled("Stream medium .ogg sounds", ref settings.audioStreaming, "Stream sound files of 100 KB and up instead of decoding them into memory.");
#endif
            list.CheckboxLabeled("Release def XML after loading", ref settings.xmlRelease, "Stop vanilla's unused translation-key bookkeeping from keeping the whole merged def XML in memory.");
            list.CheckboxLabeled("Startup report in log", ref settings.startupReport, "Log cache statistics and the slowest mod static constructors.");
            list.GapLine();
            list.Label("Extra support for other mods. Each one speeds up startup code of one specific mod and is off by default. It relies on that mod's internals: it switches itself off when the mod changes and may need a RimWorld Loading Optimizer update after the mod updates. Switch one on here if you want it.");
            foreach (var support in Compatibility.Supports)
            {
                var on = settings.Supports(support.Id);
                list.CheckboxLabeled(support.Name, ref on, support.Description);
                settings.SetSupport(support.Id, on);
            }
            settingsHeight = list.CurHeight + 20f;
            list.End();
            Widgets.EndScrollView();
        }
    }
}
