![RimWorld Loading Optimizer](About/Preview.png)

# RimWorld Loading Optimizer

Load-time and memory optimizer for RimWorld 1.6 and 1.7 with very large mod lists. It patches the base game and other mods' code paths; it does not change any def content.

All numbers below come from the Progression packs 1–3 mod list ([1/3](https://steamcommunity.com/sharedfiles/filedetails/?id=3521297585), [2/3](https://steamcommunity.com/sharedfiles/filedetails/?id=3521319712), [3/3](https://steamcommunity.com/sharedfiles/filedetails/?id=3637541646)) with RimWorld Loading Optimizer's default settings: 5.0 on RimWorld 1.6, 5.1 on RimWorld 1.7.

Every speedup has a verification mode that recomputes the result the original way; all reported 0 mismatches in the 5.0 test starts on 1.6.

## Benchmarks

"Off" = every speedup switched off (`LEANMEMORY_ALLOFF=1`); the mod stays loaded for the measurement and the log reports. "Current" = this version; 5.0 and 5.1.0 are the previous releases on the same list in the same session. Warm = Missile Girl's XML cache was used; cold = it was not (first start after a mod or mod-list change). Loading Progress and Missile Girl were active in every start, plus Performance Optimizer in the 2026-10-18 starts and Faster Game Loading in the earlier ones. RimWorld 1.6 and 1.7 run different builds with different speedups, so their results are kept separate.

- Mac: Apple M5 Pro, 24 GB unified memory, macOS. On 1.6 all textures were pre-converted to DDS with todds (RimSort).
- Windows PC: AMD Ryzen 7 5800X3D, 32 GB, RTX 5080, Windows 11.

### RimWorld 1.6

| Machine | Start | Off | 5.0 | Current | Change (off → current) | Peak memory (off / 5.0 / current) |
|---|---|---|---|---|---|---|
| Mac ¹ | cold | 16:36 | 4:58 | **2:59** | −82% | 80.8 / 80.3 / 40.5 GB |
| Mac ¹ | warm | – | 3:47 | **2:12** | – | – / 76.2 / 40.3 GB |

¹ RimWorld 1.6.4871, 2026-10-18, 1,486 mods: the current Progression packs 1–3 list, DDS textures, Steam closed. Off cannot start warm on this list (see [BENCHMARK.md](BENCHMARK.md#rimworld-16-mac-2026-10-18)). The first start after an install or mod change takes about a minute longer (4:07) because it writes the atlas cache.

### RimWorld 1.7

| Machine | Start | 5.1.0 | Current | Current, all mod support on | Peak memory (5.1.0 / current) |
|---|---|---|---|---|---|
| Mac ² | cold | 1:38 | **1:22** | – | 22.8 / 22.1 GB |
| Mac ² | warm | 1:05 | 1:11 | **1:03** | 23.2 / 22.9 GB |

² RimWorld 1.7.4981, 2026-10-18, 1,482 mods: the same Progression packs list, Steam closed. Both with default settings: 5.1.0 has the support for other mods on by default, the current version has it off. With the same settings the current version is about 2 s faster warm. No start with the optimizer off was measured on this build; see [BENCHMARK.md](BENCHMARK.md#mac-2026-10-18).

| Machine | Start | Off | On | Change | Peak memory (off → on) |
|---|---|---|---|---|---|
| Windows ³ | cold | 4:40 | **2:11** | −53% | 18.7 → 17.4 GB |
| Windows ³ | warm | 4:06 | **1:54** | −54% | 17.3 → 16.4 GB |

³ 5.1 development build on RimWorld 1.7.4979, 2026-10-15, with 1,481 mods: the Progression packs list after removing a few slow mods. Earlier results on the full list are in [BENCHMARK.md](BENCHMARK.md#windows-51-2026-10-14).

Times are to the main menu. "–" means not measured.

The full test setup, the phase times and the single starts are in [BENCHMARK.md](BENCHMARK.md).

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
| Atlas cache | `GlobalTextureAtlasManager.BakeStaticAtlases` | Stores the baked static atlases (pixels, rects, masks) on disk, keyed by every input texture (mod, path, size, format, file size and time; for textures from asset bundles the bundle file's size and time, for the game's built-in textures their resource path) and the relevant settings, and loads them on the next start instead of baking (~12 s and ~10 GB of peak memory saved; about 3.5 GB on disk for the Progression packs list, under `RimWorldLoadingOptimizer/atlas`). If the baked atlases are not readable on a system (seen on Windows), caching switches itself off until the game, mods, settings or RimWorld Loading Optimizer change |
| Atlas baking memory (1.6) | `StaticTextureAtlas.Bake` | Sends the graphics commands after every atlas, so the driver frees each atlas's temporary buffers right away instead of keeping all of them until loading ends (peak memory 80 → 45 GB on the Progression packs list, Mac) |
| Atlas packing (1.6) | The `List.IndexOf` call in `StaticTextureAtlas.CalcRectsForAtlasNew` | Finds each texture's place in the atlas from an index instead of searching the whole texture list; the atlases come out the same |
| Load mod content during XML loading | The queued `ModContentPack.ReloadContent` steps | Mod sounds and textures load on the main thread while the game reads and patches the XML on its loading thread, instead of after it. A bar on the loading screen shows the progress |
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
| Harmony dependency name index (1.7) | Harmony 3 `GeneratedAssemblyLoader` constructor (`AppDomain.GetAssemblies` call) | Each generated method binds its references from an index of loaded assemblies by name instead of comparing them with every loaded assembly; Harmony's own name and hash checks still run (3.5 s to 0.1 s) |
| XML previous-sibling cursor (1.7) | `XmlLinkedNode.PreviousSibling` (startup only) | Walks forward from the last previous sibling found instead of from the first child; removing or inserting many nodes in document order is no longer quadratic (Adaptive Storage Framework's def replacement: ~20 s to 0.4 s on cold starts), same nodes |
| Mesh buffer pool (1.7) | Waylith `MeshUploader.AllocateMeshBuffer` | While the device-local memory budget is used up, and only until 10 s after the main menu, small mesh buffers (up to 256 KB) are placed in a VMA pool with 32 MB blocks instead of one driver allocation each (the frame before the main menu: ~16 s to 0.1 s). Each buffer stays its own VkBuffer; the pool is emptied and destroyed before the allocator. Own setting |
| Mesh upload timing (1.7) | Waylith `MeshUploader` upload methods | Report only: times mesh uploads for the first 10 s after startup (main menu or straight into a game). Changes nothing |
| Atlas source stamps (1.7) | `StaticTextureAtlas.TryWriteSourceStamp` | File size and date of every atlas source texture are read on worker threads before baking; same cache key bytes. Own setting |
| GC latency (1.7) | `GCSettings.LatencyMode` | SustainedLowLatency from RimWorld Loading Optimizer's start until the main menu, so the runtime avoids blocking full memory clean-ups while loading; set back at the main menu. Follows the "skip the forced memory clean-up" setting |
| Startup report | `RuntimeHelpers.RunClassConstructor` | Logs the slowest mod static constructors |
| Game sound read-ahead (1.7, test, off by default) | `UnityEngine.Resources.LoadStubResource` (its `AudioClip.Load` call) | Once the defs are loaded, the game's own sound files that the sub-sounds' grains will ask for are opened on worker threads with the game's `AudioClip.Open` (at most 128 MB waiting); the clip is then built from that on the thread that asks, exactly as `AudioClip.Load` builds it. Anything not ready is opened by the game as before |

### Extra support for other mods (off by default)

Each of these targets one mod, can be switched on separately in Mod Settings, and only acts when that mod is active. None of them re-implements the mod's own code: the mod's code still runs, and RimWorld Loading Optimizer only adds an index, a cache or a redirect around it. When that mod changes, each one either checks that the exact call it changes is still there, or compares its first results with the mod's own code and switches itself off for that start if they differ.

| Mod | What RimWorld Loading Optimizer does |
|---|---|
| Graphics Settings+ | Hands its own DDS loader the file bytes that were read ahead into memory |
| Missile Girl | Lets its own cache loader read the cache straight into the def document instead of copying every def, and skips an unused second read of the cache file; on 1.7 it reads the cache on several threads |
| Melee Animation | Indexes its retexture scan (−27 s) |
| Research Reinvented: Stepping Stones | Indexed recipe-user lookups |
| Lunar | Faster inlining fixer and patch conflict check |
| What's That Mod | Runs its patch scan at a point where it is cheaper |
| Children Wear Anything, Vehicle Framework, AV Mechanoid Skins | Route their texture lookups through RimWorld Loading Optimizer's texture index |
| Vehicle Framework (1.7) | While it builds its pattern materials, each pattern texture is decoded to pixels once instead of six times; the repeats get a copy of the same pixels |
| CombatAI | Faster label scan, and its label patches are applied on the main thread |
| Loading Progress | Installs the support above before Loading Progress runs the static constructors |
| Giddy-Up 2 (1.7) | Remembers its rider draw offsets between starts, keyed on the exact texture content |
| Worksites Expanded (1.7) | Answers its tome-trader check directly while no game is loaded instead of an exception caught tens of thousands of times |
| Alien Worlds Framework (1.7) | Reuses last start's Worldbuilder preset images when their source textures are unchanged |
| Cherry Picker (1.7) | Runs the part of its startup code that removes the picked defs right after its class is initialized instead of while it is still being initialized (1.6 s to 0.3 s). Same code, same order; if that code ever failed, the error is reported for that call instead of on every later use of Cherry Picker |
| TD Find Lib (1.7) | Recipe users read during its startup come from an index, in the same order |
| Vanilla Trading Expanded (1.7) | Its product check and the market value recipe check use an index while it sorts items; steps aside when another mod (such as Performance Optimizer) patches the same method |

Tests, off by default (Mod Settings): keep Missile Girl's rebuilt cache usable (writes its mod list again and drops stale hashes after a rebuild, with Missile Girl's own functions; works around a Missile Girl issue), PA Dynamic Seed Icons image read-ahead, and the game sound read-ahead above.

Patches on .NET methods that every mod calls (`AppDomain.GetAssemblies`, `Assembly.FullName`, `Assembly.GetTypes`, `Type.ContainsGenericParameters`, `Type.GetGenericArguments`, `XmlChildNodes.Item`, `XmlLinkedNode.PreviousSibling`, `Thread.Sleep`) only exist during startup and are removed when the main menu opens.

## Safety rules the code follows

- **Generic code:** it never patches methods of generic classes instantiated over reference types. Mono shares that code across instantiations, so such patches break `typeof(T)` and other instantiations. Call sites are redirected instead.
- **XPath scope:**
  - The XPath index is active only inside `LoadedModManager.ApplyPatches`, on the thread that patches, and detaches its document events afterwards.
  - Anything it cannot answer exactly falls back to real XPath: position predicates, numeric predicates, `//` and `..` paths, unions and namespaces.
- **Cache invalidation:**
  - Caches are invalidated by content signatures and on `ClearAllPlayData` / `ClearDestroy`, which cover language changes and hot reload.
  - Hits are re-validated against the owning source.
- **Mod and game updates:** no speedup depends on a list of known game or mod versions. Every speedup first checks that the methods and fields it needs still exist, then:
  - a speedup that only measures or reports, or only marks a point in time, needs nothing more;
  - a speedup that changes one call checks that the call it needs is still there (and only that);
  - a speedup that replaces a result lets the original run next to it for its first results (200 by default, fewer for expensive ones), compares them, and only takes over when all of them match. On any difference it switches off for that start and the original code runs (`[RLO] <feature>: a result differed from the original code …`);
  - a passed check is remembered for that exact code in `RimWorldLoadingOptimizer/verified-code.txt`, so the comparison runs once after every game or mod update;
  - two speedups also depend on internals a comparison cannot fully cover, so they additionally check that the Harmony or System.Xml methods they build on still make exactly the calls they were written around: Harmony rebuild coalescing (test, off by default), and reading Missile Girl's cache on several threads, which also compares its first read of every new code version with Missile Girl's own read and uses Missile Girl's result for that start;
  - faster mod assembly loading on 1.6 still needs a known version.
  The startup report lists the checks in `code checks:` and `self-checks:` lines. Re-implemented game getters also step aside when another mod patches the same method.
- **Other optimizers:** when another optimizer (for example Performance Fish or Performance Optimizer) replaces or rewrites a method that a speedup also works on, that speedup steps aside and leaves the method to the other optimizer. Patches that only add work before or after a method don't count.
- **Atlas cache files:** each cached atlas carries a 64-bit checksum that is verified while loading; a damaged or truncated file is rebaked instead of shown.
- **Transpilers:** they only redirect specific calls and leave the rest of the method intact, so other mods' transpilers still find their anchors. A transpiler that does not find the exact calls it expects leaves the method unchanged.

## Diagnostics

Environment variables for testing; none are needed for normal play.

| Variable | Effect |
|---|---|
| `LEANMEMORY_VERIFY=1` | Runs the original code next to every speedup and logs mismatches |
| `LEANMEMORY_ALLOFF=1` | Starts with every feature off, like the "off" step of the measurement |
| `LEANMEMORY_IGNORE_FINGERPRINTS=1` | Uses the few speedups that still need exact code even when that code changed (logged as a warning) |
| `LEANMEMORY_SELFCHECK=1` | Ignores `verified-code.txt`, so every speedup that can runs its self-check again (results are not remembered) |
| `LEANMEMORY_NOATLASFLUSH=1`, `LEANMEMORY_NOATLASPACKING=1` | Switch off the atlas baking memory fix or the atlas packing index (1.6) |
| `LEANMEMORY_NOATLASCACHE=1`, `LEANMEMORY_NOBOOST=1`, `LEANMEMORY_NOPREFETCH=1` | Switch off the atlas cache, faster early content loading or texture read-ahead |
| `LEANMEMORY_PROFILE=1` | Adds a startup profile and phase timeline to the startup report |
| `LEANMEMORY_STRINGSTATS=1`, `LEANMEMORY_OBJECTSTATS=1`, `LEANMEMORY_XMLSTATS=1`, `LEANMEMORY_HEAPSTATS=1` | Memory statistics in the log |
| `CTORS2_OFF=a,b` | Switches off single startup-code fixes: `lunar`, `av`, `vehicles`, `bracket`, or `all` |
| `CTORS2_VERIFY=1` | Verify mode for the startup-code fixes only |
| `CTORS2_TIMING=1` | Times selected mod static constructors |
| `PREXML2_OFF=a,b` | Switches off single mod-constructor speedups: `reflcache`, `asmcache`, `earlytypes`, `loadfrom`, `ilscan`, `cai`, or `all` |
| `MAINPHASES_FRAME=0.4` | Seconds of loading work per frame during startup (default 0.25) |

## Changelog

See [CHANGELOG.md](CHANGELOG.md).
