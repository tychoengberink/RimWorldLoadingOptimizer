# RimWorld Loading Optimizer

Load-time and memory optimizer for RimWorld 1.6 with very large mod lists. It patches the base game and other mods' code paths; it does not change any def content.

Measured on a 661-mod list (Apple M5 Pro, 24 GB), same mod list, start to the main menu, every feature off versus the 4.0 defaults:

| | Off | 4.0 | Change |
|---|---|---|---|
| Warm start to main menu | 186.2 s | **77.2 s** | −58.5% |
| Cold start (after a mod update) | 308.8 s | **109.2 s** | −64.6% |
| Peak memory while loading (warm) | 40.0 GB | **28.5 GB** | −11.5 GB |
| Static constructors (sum) | 27.7 s | 9.4 s | −66% |

Every accelerated answer has a verification mode that recomputes it the vanilla way; all of them reported 0 mismatches on the full list.

## Benchmarks

All numbers below come from one setup: textures pre-converted to DDS with todds (RimSort), Graphics Settings+, Faster Game Loading, Loading Progress and Missile Girl active, and a 661-mod list. See [BENCHMARK.md](BENCHMARK.md) for the complete setup, method, caveats and mod list.

Same 661-mod list on both machines (668 with Core and DLCs). Warm = Missile Girl's XML cache valid (normal start); cold = first start after a mod update or a change to the mod list. "Off" = every RimWorld Loading Optimizer feature off. Time to the main menu comes from the log line `main menu reached at … s`.

### Mac: Apple M5 Pro, 24 GB unified memory, internal SSD, macOS (Metal)

| | Off | RimWorld Loading Optimizer 4.0 | Change |
|---|---|---|---|
| Warm start to main menu | 186.2 s | **77.2 s** | −58.5% |
| Cold start to main menu | 308.8 s | **109.2 s** | −64.6% |
| Peak memory while loading (warm) | 40.0 GB | **28.5 GB** | −11.5 GB |
| Static constructors (sum) | 27.7 s | **9.4 s** | −66% |
| Texture loading on the main thread | ~66 s | **~10 s** | DDS read-ahead |
| Atlas baking | 12–14 s | **1.3 s** | atlas cache hit |
| Melee Animation load event | 29.4 s | **2.5 s** | retexture index |
| XML patching (cold, XML Extensions counter) | 149.1 s | **8.2 s** | FastXPath |

Real error lines identical with and without RimWorld Loading Optimizer; every verification counter 0 mismatches; all 12 cached atlases byte-identical to a fresh bake.

### Mac, scenario 1 again with RimWorld Loading Optimizer 4.0.5 (2026-10-10)

| | Off | RimWorld Loading Optimizer 4.0.5 | Change |
|---|---|---|---|
| Normal start to main menu | 315.6 s | **80.3 s** | −74.6% |
| Cold start to main menu | 315.6 s | **89.0 s** | −71.8% |
| Peak memory while loading | 41.5 GB | **28.8–37.6 GB** | up to −12.7 GB |

Without RimWorld Loading Optimizer, Missile Girl's cache never stays valid on this list, so every start is cold; details in [BENCHMARK.md](BENCHMARK.md).

### Mac, scenario 2: Progression packs 1 to 3 (1,490 mods)

| | Off | RimWorld Loading Optimizer 4.0.5 | Change |
|---|---|---|---|
| Warm start to main menu | 332.4 s | **158.2 s** | −52.4% |
| Cold start to main menu | 713.8 s | **206.9 s** | −71.0% |
| XML patching (cold) | 371.1–394.9 s | **34.5 s** | −91% |
| Peak memory while loading | 53.7 GB | **34.4 GB** | −19.3 GB |

Run counts and the per-phase breakdown are in [BENCHMARK.md](BENCHMARK.md#scenario-2-mac-progression-packs-1-to-3-1490-mods).

## Measure it on your own PC

Options → Mod Settings → RimWorld Loading Optimizer → **Start** next to "Measure how much RimWorld Loading Optimizer saves on your PC". The next start runs with every RimWorld Loading Optimizer feature switched off and records the time to the main menu; RimWorld then asks you to restart once more, runs with RimWorld Loading Optimizer on, and shows both times and the difference. Every start is also appended to `RimWorldLoadingOptimizer/measurements.csv` in the RimWorld save data folder (Windows: `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios`, Mac: `~/Library/Application Support/RimWorld`) with time, XML patching time, warm or cold, peak memory (where the OS reports it), mod count and version.

- Do not change mods or settings between the two starts. If one start was cold (XML patching ran, for example after a mod update) and the other warm, the result window says the comparison is not fair.
- The first start after installing or updating RimWorld Loading Optimizer (or any mod) also builds the atlas cache, so it is slower than later starts. Run the measurement twice and use the second result.
- "Off" means RimWorld Loading Optimizer's features are switched off; the mod itself is still loaded.

## Load order

Put RimWorld Loading Optimizer directly after Harmony (its About.xml asks to load before Core, Faster Game Loading and Loading Progress). It still works further down, but the Harmony, type-name and patching speedups then miss every mod loaded before it.

## Features

| Feature | What it patches | Effect |
|---|---|---|
| Fast XML patching | Every method that calls `SelectNodes`/`SelectSingleNode` in vanilla PatchOperations and in any mod assembly that defines PatchOperations (XML Extensions included) | XPaths such as `/Defs/ThingDef[defName="X"]/…` are answered from an exact def index that updates itself |
| Def XML release | `TKeySystem.MarkTreatAsList` / `BuildMappings`, and `LoadedModManager.ApplyPatches` (XML Extensions `MainDocument`) | Stops vanilla and XML Extensions from pinning the whole merged def XML for the session (−390 MB live heap warm, −286 MB cold) |
| Shadowed texture release | Mod content holders after texture load | Frees textures overridden by a later mod. By default only from mods without code, which can't look their own textures up; a setting extends this to code mods |
| Recipe index | `ThingDef.AllRecipes` | One reverse pass instead of a scan of every recipe per item |
| Category cache | `ThingCategoryDef.DescendantThingDefs` | Flattened, read-only lists for recipe and stockpile filters |
| Texture lookup index | `ContentFinder<Texture2D>.Get` call sites inside `Graphic` classes | One dictionary lookup instead of scanning every mod, with a cache of misses |
| DDS fallback | `ModDdsLoader.TryLoadDds` | Loads the PNG when a shipped DDS is unusable (for example, sizes that are not a multiple of 4) |
| Audio | `ShouldStreamAudioClipFromFile`, `Thread.Sleep` (only on the flagged audio-load path) | Streams .ogg files of 100 KB or more, and yields instead of sleeping 1 ms per clip (audio load 13 s to 7 s) |
| Read textures ahead | With Graphics Settings+: its `DDSLoader.LoadDDS` (only while its signature still matches). Without it: nothing is replaced; progress is read from `ModDdsLoader.TryLoadDds` and `ModContentPack.ReloadContentInt` | With Graphics Settings+: four background threads read the DDS files into native memory (at most 768 MB ahead) in load order and the main thread builds the texture from that buffer instead of page-faulting a memory-mapped file (texture loading ~66 s to ~10 s, measured). Without it: the same threads read at most 512 MB ahead into the OS file cache, so the game's own loader finds the data in memory; the game's code is unchanged (not yet measured) |
| Faster early content loading | Faster Game Loading's early loader budget | While defs still load, mod content may use 250 ms per frame instead of 50 ms, and no frames are skipped |
| Atlas cache | `GlobalTextureAtlasManager.BakeStaticAtlases` | Stores the baked static atlases (pixels, rects, masks) on disk, keyed by every input texture (mod, path, size, format, file size and time) and the relevant settings, and loads them on the next start instead of baking (~12 s and ~10 GB of peak memory saved; ~0.8 GB on disk under `RimWorldLoadingOptimizer/atlas`). If the baked atlases are not readable on a system (seen on Windows), caching switches itself off until the game, mods, settings or RimWorld Loading Optimizer change |
| Think tree keys | `ThinkTreeKeyAssigner.AssignSingleKey` | Vanilla re-probes every taken key for each copied node (258 M probes); resuming each probe chain gives the identical keys with 119 k probes (−4.4 s) |
| Harmony module cache | `AccessTools.GetMethodByModuleAndToken` | Caches the module scan Harmony repeats for every existing patch whenever a method is patched again |
| Faster Harmony patching | MonoMod `MMReflectionImporter.ImportReference`, `ReflectionHelper._ResolveReflection` | Harmony turns every method, field and type a patch uses into Mono.Cecil and back; RimWorld Loading Optimizer remembers the object it came from, so the way back is a lookup. Same generated code (verified: 497,711 lookups, 0 mismatches). Scenario 2 warm: −7 s |
| Fast content lookups | `ContentFinder` call sites in Graphic classes, Def `PostLoad`, `MaterialPool`, `AudioGrain_Folder` | Texture and sound lookups no longer walk the asset-bundle list of every mod (almost none have bundles) and folder lookups skip mods that have nothing under that path. Same results (verified) |
| Assembly type cache | `Assembly.GetTypes()` | Mono rebuilds the type array through a slow hash table on every call; RimWorld Loading Optimizer returns a copy of the first result. Assemblies that throw are never cached |
| Designator index | `DesignationCategoryDef.ResolveDesignators` | One pass over all buildable defs instead of one per category |
| Loading frame budget | `LongEventHandler` | During startup the loading screen runs 0.25 s of work per frame instead of 0.1 s; same work, same order, fewer redraws |
| CombatAI, HugsLib, category fixes | CombatAI label scan, HugsLib obsolete-patch check, `ThingCategoryDef` contains checks | Same answers from indexed lookups instead of full scans; fingerprint-guarded |
| Type name index | `AccessTools.TypeByName` | Lookups that miss the fast paths are answered from an index in Harmony's exact order instead of listing all types of all assemblies per call |
| Missile Girl cache load | Gagarin `CachedDefHelper.Load`, `ModContentPack.LoadPatches` | Parses the 117 MB def cache once, straight into the def document, and reuses the patch files Gagarin already parsed (CombineIntoUnifiedXML 4.3 s to 1.7 s) |
| Speed fixes for other mods | Melee Animation (retexture scan index, −27 s), Children Wear Anything (−2.5 s), Lunar inlining fixer (−3 s), Giddy-Up offsets (−1 s), What's That Mod (−3 s), Research Reinvented: Stepping Stones, Vehicle Framework | Each one patches only that mod's own non-generic helper methods, is output-identical (verify mode), and is skipped when the mod is absent or its code changed |
| Startup report | `RuntimeHelpers.RunClassConstructor` | Logs the slowest mod static constructors |

## Safety rules the code follows

- **Generic code:** it never patches methods of generic classes instantiated over reference types. Mono shares that code across instantiations, so such patches break `typeof(T)` and other instantiations. Call sites are redirected instead.
- **XPath scope:**
  - The XPath index is active only inside `LoadedModManager.ApplyPatches`, on the thread that patches, and detaches its document events afterwards.
  - Anything it cannot answer exactly falls back to real XPath: position predicates, numeric predicates, `//` and `..` paths, unions and namespaces.
- **Cache invalidation:**
  - Caches are invalidated by content signatures and on `ClearAllPlayData` / `ClearDestroy`, which cover language changes and hot reload.
  - Hits are re-validated against the owning source.
- **Mod and game updates:** every speedup that re-implements another mod's, Harmony's or the game's method checks a fingerprint (SHA-256 of the method's IL) against the versions it was verified on. If the original changed, that speedup stays off, the original code runs, and the log says which one (`[RLO] … has changed since this RLO version was verified`). Melee Animation and Lunar also check the exact code pattern they patch.
- **Atlas cache files:** each cached atlas carries a 64-bit checksum that is verified while loading; a damaged or truncated file is rebaked instead of shown.
- **Transpiler priority:** transpilers run with the lowest priority, so other mods' transpilers still see the original IL.

## Changelog

### 4.1.2
- Faster start-up for the code of many popular mods, including Vanilla Trading Expanded, TD Find Lib, Linkable Groups, KCSG, AV Mechanoid Skins and Lunar.
- Faster loading of game definitions.
- Mods start faster when Prepatcher is installed.
- Missile Girl's cache now stays valid after you switch between mod lists, so fewer starts need a full rebuild.
- Works alongside Image Opt (fork), Yet another Optimizer with Prepatcher and WOWGAG's Performance Patch: parts that would overlap with them switch off automatically.
- The loading screen bar now shows how many mods get extra support and which other optimizers are running. The full list is written to the log.
- Fixed: with CombatAI and Colony Groups together, the Colony Groups health overlay could go missing.
- The Mod Settings window now scrolls.
- The texture atlas cache setting now explains that it only works on some systems (it stays empty on most Windows PCs).

### 4.1.0

- Faster start-up with Adaptive Storage, CombatAI and HugsLib.
- Faster setup of the architect menu, item categories and recipes.
- Faster lookups of mod code and mod textures during loading.
- During loading the game spends less time redrawing the loading screen and more on loading itself.
- Fixed: Melee Animation weapon textures stay correct when duplicate textures are released.
- Fixed: the loading screen bar no longer changes the font or colour of other text.
- Frees some extra memory once the main menu is reached.
- The measurement button in Mod Settings now records peak memory correctly on Mac.

### 4.0.5

- Harmony patches from all mods are set up faster.
- Missile Girl's cache now stays valid on very large mod lists.
- A bar on the loading screen shows that the mod is active and what it is doing.

### 4.0.1 to 4.0.3

- Texture read-ahead also works without Graphics Settings+.
- Fixes for Windows: the texture read-ahead keeps up, and the atlas cache switches itself off where it cannot work.
- Protection against mod updates: parts that speed up a specific mod switch themselves off when that mod changes.
- The atlas cache checks every texture before it uses a saved atlas.

### 4.0

- Textures are read from disk ahead of time while the game loads.
- Texture atlases are saved and reused on the next start when nothing changed.
- Faster start-up for Giddy-Up, Vehicle Framework, Children Wear Anything, Research Reinvented: Stepping Stones, Lunar and What's That Mod.
- Faster AI setup and faster lookups of mod code.
- The loading screen stays responsive while definitions load.

### 3.5

- Duplicate textures are only released from mods without code, so mods that read their own textures keep working.
- More XML patches are handled the fast way.

### 3.4

- Frees the memory the game kept for definition XML after loading.

### 3.2

- Large sound files are streamed, so audio loads faster.
- Faster texture, recipe and category setup.
- A report of the slowest mod start-up code in the log.

### 3.1

- Much faster XML patching.
- Faster recipe setup and faster start-up for some mods.

### 2

- Releases textures that several mods ship under the same path, so only the one the game uses stays in memory.
