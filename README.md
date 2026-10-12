# RimWorld Loading Optimizer

Load-time and memory optimizer for RimWorld 1.6 with very large mod lists. It patches the base game and other mods' code paths; it does not change any def content.

Version 5.0, measured on a Mac (Apple M5 Pro, 24 GB) with a 672-mod list and no Graphics Settings+: a cold start goes from **8:16 to 4:52 (−41%)** with only RimWorld Loading Optimizer's own features. With the defaults, which include the extra support for the mods in the list, a warm start of a 661-mod list goes from about **4:10 to 2:19**, and a cold start of a 1,490-mod list from **17:03 to 5:41 (−67%)**.

Every speedup has a verification mode that recomputes the result the original way; all reported 0 mismatches in the 5.0 test starts.

## Benchmarks (5.0)

Mac: Apple M5 Pro, 24 GB unified memory, internal SSD, macOS (Metal). Textures pre-converted to DDS with todds (RimSort). Faster Game Loading, Loading Progress and Missile Girl active. One start per scenario, measured on 2026-10-12. "Off" = every RimWorld Loading Optimizer feature switched off (`LEANMEMORY_ALLOFF=1`). Warm = Missile Girl's XML cache was used; cold = it was not (first start after a mod or mod-list change). Time to the main menu comes from the log line `main menu reached at … s`. Results of earlier versions are in [BENCHMARK.md](BENCHMARK.md).

### The NYz Collection: Sci-Fi & Progression without Graphics Settings+ (672 mods), cold start

| | Off | 5.0, mod support off | Change |
|---|---|---|---|
| Time to main menu | 8:16 (495.7 s) | **4:52 (292.4 s)** | −41% |
| Texture loading | 85.5 s | **30.1 s** | DDS buffers for the game's own loader |
| XML patching | 217.7 s | **12.3 s** | fast XML patching |
| Peak memory | 42.6 GB | **41.1 GB** | −1.5 GB |

The verification start compared all 83,778 textures loaded from RimWorld Loading Optimizer's buffers with what the game's own loader builds: 0 differences.

### The NYz Collection: Sci-Fi & Progression with Graphics Settings+ (661 mods + Loading Progress), warm start

| | Off | 5.0, mod support off | 5.0 defaults (all mod support on) |
|---|---|---|---|
| Time to main menu | 4:05 and 4:19 (2 starts) | 4:14 | **2:19** |
| Texture loading | 74–77 s | 87 s | **29 s** |
| Peak memory | 42.3–42.4 GB | **30.2 GB** | 40.7 GB |

With Graphics Settings+ installed, the texture speedup is part of the Graphics Settings+ support, so with mod support off texture loading is not faster on this list; memory is still 12 GB lower. With the defaults (all support on), the time saved comes mostly from Graphics Settings+, Melee Animation and Research Reinvented: Stepping Stones support.

### Progression packs 1 to 3 (1,490 mods), cold start

| | Off | 5.0, all mod support on | Change |
|---|---|---|---|
| Time to main menu | 17:03 (1023.5 s) | **5:41 (341.2 s)** | −67% |
| XML patching | 528.9 s | **39.7 s** | −92% |
| Texture loading | 99.0 s | **26.7 s** | −73% |
| Peak memory | 54.3 GB | **53.7 GB** | −0.6 GB |

This list does not use Graphics Settings+, so its texture speedup comes from the DDS buffers for the game's own loader.

## Measure it on your own PC

Options → Mod Settings → RimWorld Loading Optimizer → **Start** next to "Measure how much RimWorld Loading Optimizer saves on your PC". The next start runs with every RimWorld Loading Optimizer feature switched off and records the time to the main menu; RimWorld then asks you to restart once more, runs with RimWorld Loading Optimizer on, and shows both times and the difference. Every start is also appended to `RimWorldLoadingOptimizer/measurements.csv` (and shown as a web page in `measurements.html`, with a button in Mod Settings) in the RimWorld save data folder (Windows: `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios`, Mac: `~/Library/Application Support/RimWorld`) with time, XML patching time, warm or cold, peak memory (where the OS reports it), mod count and version.

- Do not change mods or settings between the two starts. If one start was cold (XML patching ran, for example after a mod update) and the other warm, the result window says the comparison is not fair.
- The first start after installing or updating RimWorld Loading Optimizer (or any mod) also builds the atlas cache, so it is slower than later starts. Run the measurement twice and use the second result.
- "Off" means RimWorld Loading Optimizer's features are switched off; the mod itself is still loaded.

## Load order

Put RimWorld Loading Optimizer directly after Harmony (its About.xml asks to load before Core, Faster Game Loading and Loading Progress). It still works further down, but the Harmony, type-name and patching speedups then miss every mod loaded before it.

## Features

| Feature | What it patches | Effect |
|---|---|---|
| Fast XML patching | Every method that calls `SelectNodes`/`SelectSingleNode` in vanilla PatchOperations and in any mod assembly that defines PatchOperations (XML Extensions included) | XPaths such as `/Defs/ThingDef[defName="X"]/…` are answered from an exact def index that updates itself |
| Def XML release | `TKeySystem.MarkTreatAsList` / `BuildMappings`, and XML Extensions' `MainDocument` (released after the startup static constructors, and only when no XML Extensions setting reads it) | Stops vanilla and XML Extensions from pinning the whole merged def XML for the session (−390 MB live heap warm, −286 MB cold) |
| Shadowed texture release | Mod content holders after texture load | Frees textures overridden by a later mod. By default only from mods without code, which can't look their own textures up; a setting extends this to code mods |
| Recipe index | `ThingDef.AllRecipes` | One reverse pass instead of a scan of every recipe per item |
| Category cache | `ThingCategoryDef.DescendantThingDefs` | Flattened, read-only lists for recipe and stockpile filters |
| Texture lookup index | `ContentFinder<Texture2D>.Get` call sites inside `Graphic` classes | One dictionary lookup instead of scanning every mod, with a cache of misses |
| DDS fallback | `ModDdsLoader.TryLoadDds` | Loads the PNG when a shipped DDS is unusable (for example, sizes that are not a multiple of 4) |
| Audio | `ShouldStreamAudioClipFromFile`, `Thread.Sleep` (acts only on the flagged audio-load path; removed at the main menu) | Streams .ogg files of 100 KB or more the way the game already streams files over 300 KB, and yields instead of sleeping 1 ms per clip (audio load 13 s to 7 s) |
| Read textures ahead | Without Graphics Settings+: `ModDdsLoader.TryLoadDds` (fingerprint-checked). With Graphics Settings+: nothing unless its support is switched on | Four to eight background threads read the DDS files into native memory (at most 768 MB ahead) in load order, and the texture is built from that buffer instead of the main thread page-faulting a memory-mapped file (texture loading 85 s to 30 s, verified identical). Anything unusual falls back to the game's loader. With Graphics Settings+ and its support off, the files are only read ahead into the OS file cache |
| Faster early content loading | Faster Game Loading's early loader budget | While defs still load, mod content may use 250 ms per frame instead of 50 ms, and no frames are skipped |
| Atlas cache | `GlobalTextureAtlasManager.BakeStaticAtlases` | Stores the baked static atlases (pixels, rects, masks) on disk, keyed by every input texture (mod, path, size, format, file size and time) and the relevant settings, and loads them on the next start instead of baking (~12 s and ~10 GB of peak memory saved; ~0.8 GB on disk under `RimWorldLoadingOptimizer/atlas`). If the baked atlases are not readable on a system (seen on Windows), caching switches itself off until the game, mods, settings or RimWorld Loading Optimizer change |
| Think tree keys | `ThinkTreeKeyAssigner.AssignSingleKey` | Vanilla re-probes every taken key for each copied node (258 M probes); resuming each probe chain gives the identical keys with 119 k probes (−4.4 s) |
| Harmony module cache | `AccessTools.GetMethodByModuleAndToken` | Caches the module scan Harmony repeats for every existing patch whenever a method is patched again |
| Faster Harmony patching | MonoMod `MMReflectionImporter.ImportReference`, `ReflectionHelper._ResolveReflection` | Harmony turns every method, field and type a patch uses into Mono.Cecil and back; RimWorld Loading Optimizer remembers the object it came from, so the way back is a lookup. Same generated code (verified: 497,711 lookups, 0 mismatches). Scenario 2 warm: −7 s |
| Fast content lookups | `ContentFinder` call sites in Graphic classes, Def `PostLoad`, `MaterialPool`, `AudioGrain_Folder` | Texture and sound lookups no longer walk the asset-bundle list of every mod (almost none have bundles) and folder lookups skip mods that have nothing under that path. Same results (verified) |
| Assembly type cache | `Assembly.GetTypes()` | Mono rebuilds the type array through a slow hash table on every call; RimWorld Loading Optimizer returns a copy of the first result. Assemblies that throw are never cached |
| Designator index | `DesignationCategoryDef.ResolveDesignators` | One pass over all buildable defs instead of one per category |
| Loading frame budget | `LongEventHandler` | During startup the loading screen runs 0.25 s of work per frame instead of 0.1 s; same work, same order, fewer redraws |
| Category sets | `ThingCategoryDef.DescendantThingDefs` results | `Contains` checks on category lists answered from a hash set; follows the Category cache setting |
| Type name index | `AccessTools.TypeByName` | Lookups that miss the fast paths are answered from an index in Harmony's exact order instead of listing all types of all assemblies per call |
| Patch file reuse | `ModContentPack.LoadPatches`, `DirectXmlLoader.XmlAssetsInModFolder` | Patch files parsed during def loading are reused instead of parsed a second time |
| Startup report | `RuntimeHelpers.RunClassConstructor` | Logs the slowest mod static constructors |

### Extra support for other mods (on by default)

Each of these targets one mod, can be switched off separately in Mod Settings, and only acts when that mod is active. None of them re-implements the mod's own code: the mod's code still runs, and RimWorld Loading Optimizer only adds an index, a cache or a redirect around it. Each checks a fingerprint of the code it touches and switches itself off when that mod changes.

| Mod | What RimWorld Loading Optimizer does |
|---|---|
| Graphics Settings+ | Hands its own DDS loader the file bytes that were read ahead into memory (texture loading ~66 s to ~10 s on the 661-mod list) |
| Missile Girl | Lets its own cache loader read the cache straight into the def document instead of copying every def, and skips an unused second read of the cache file |
| Melee Animation | Indexes its retexture scan (−27 s) |
| Research Reinvented: Stepping Stones | Indexed recipe-user lookups |
| Lunar | Faster inlining fixer and patch conflict check |
| What's That Mod | Runs its patch scan at a point where it is cheaper |
| Children Wear Anything, Vehicle Framework, AV Mechanoid Skins | Route their texture lookups through RimWorld Loading Optimizer's texture index |
| CombatAI | Faster label scan, and its label patches are applied on the main thread |
| Linkable Groups | Indexed facility links |
| Loading Progress | Installs the support above before Loading Progress runs the static constructors |

Patches on .NET methods that every mod calls (`AppDomain.GetAssemblies`, `Assembly.FullName`, `Assembly.GetTypes`, `Type.ContainsGenericParameters`, `Type.GetGenericArguments`, `XmlChildNodes.Item`, `Thread.Sleep`) only exist during startup and are removed when the main menu opens.

## Safety rules the code follows

- **Generic code:** it never patches methods of generic classes instantiated over reference types. Mono shares that code across instantiations, so such patches break `typeof(T)` and other instantiations. Call sites are redirected instead.
- **XPath scope:**
  - The XPath index is active only inside `LoadedModManager.ApplyPatches`, on the thread that patches, and detaches its document events afterwards.
  - Anything it cannot answer exactly falls back to real XPath: position predicates, numeric predicates, `//` and `..` paths, unions and namespaces.
- **Cache invalidation:**
  - Caches are invalidated by content signatures and on `ClearAllPlayData` / `ClearDestroy`, which cover language changes and hot reload.
  - Hits are re-validated against the owning source.
- **Mod and game updates:** every speedup that touches another mod's, Harmony's or the game's method checks a fingerprint (SHA-256 of the method's IL) against the versions it was verified on. If the original changed, that speedup stays off, the original code runs, and the log says which one (`[RLO] <feature>: … is not the version RimWorld Loading Optimizer was checked against …`). Re-implemented game getters also step aside when another mod patches the same method. Melee Animation and Lunar also check the exact code pattern they patch.
- **Atlas cache files:** each cached atlas carries a 64-bit checksum that is verified while loading; a damaged or truncated file is rebaked instead of shown.
- **Transpilers:** they only redirect specific calls and leave the rest of the method intact, so other mods' transpilers still find their anchors. A transpiler that does not find the exact calls it expects leaves the method unchanged.

## Diagnostics

Environment variables for testing; none are needed for normal play.

| Variable | Effect |
|---|---|
| `LEANMEMORY_VERIFY=1` | Runs the original code next to every speedup and logs mismatches |
| `LEANMEMORY_ALLOFF=1` | Starts with every feature off, like the "off" step of the measurement |
| `LEANMEMORY_IGNORE_FINGERPRINTS=1` | Uses speedups even when the original method changed (logged as a warning) |
| `LEANMEMORY_NOATLASCACHE=1`, `LEANMEMORY_NOBOOST=1`, `LEANMEMORY_NOPREFETCH=1` | Switch off the atlas cache, faster early content loading or texture read-ahead |
| `LEANMEMORY_PROFILE=1` | Adds a startup profile and phase timeline to the startup report |
| `LEANMEMORY_STRINGSTATS=1`, `LEANMEMORY_OBJECTSTATS=1`, `LEANMEMORY_XMLSTATS=1`, `LEANMEMORY_HEAPSTATS=1` | Memory statistics in the log |
| `CTORS2_OFF=a,b` | Switches off single startup-code fixes: `lunar`, `av`, `linkable`, `vehicles`, `bracket`, or `all` |
| `CTORS2_VERIFY=1` | Verify mode for the startup-code fixes only |
| `CTORS2_TIMING=1` | Times selected mod static constructors |
| `PREXML2_OFF=a,b` | Switches off single mod-constructor speedups: `reflcache`, `asmcache`, `earlytypes`, `loadfrom`, `ilscan`, `cai`, or `all` |
| `MAINPHASES_FRAME=0.4` | Seconds of loading work per frame during startup (default 0.25) |

## Changelog

See [CHANGELOG.md](CHANGELOG.md).
