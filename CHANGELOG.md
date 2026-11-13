# Changelog

## 5.4

### Changed
- Three former tests are on by default: preparing the game's own sounds ahead, preparing PA Dynamic Seed Icons' images ahead, and keeping Missile Girl's rebuilt cache usable (the last one only acts with the Missile Girl support switched on). Each keeps its own switch in Mod Settings. "Merge repeated Harmony rebuilds" stays a test and stays off.

### Fixed
- Loading screen: the mod content bar also counts mods whose content loads after the XML, so it always fills. Before, it stopped where early loading stopped, for example at a third on a slow disk.
- Humanoid Alien Races: race mods (Ratkin, Nearmare and others) no longer lose body, head and addon textures. Humanoid Alien Races looks up each race's textures as soon as its own textures are loaded; with "Load sounds and textures during XML loading" that was before the race mods' textures were loaded, so it found none. It now waits until every mod's textures are loaded.

### RimWorld 1.7
- Harmony replacements are built at the first call of the patched method instead of when the patch is applied, for methods whose exact patches were already built on the last start. Most patched methods are never called before the main menu. Patches with a transpiler, and methods known to be called during loading, are built right away as before. The first 200 are compared with Harmony's own build after every game, Harmony or RimWorld Loading Optimizer update. It follows the "Harmony rebuild caches" setting and steps aside when "Merge repeated Harmony rebuilds" is on. The first start after a mod list change builds everything as before.
- When the game restarts itself (after a mod list change, or with a mod that restarts the game on every start), the restarted game runs the server garbage collector, which collects on several cores. It uses more memory (about 1.6 GB on the Progression packs list) and is only passed on with at least 15 GB of memory. It follows the "Skip the forced memory clean-up" setting.

## 5.3

### Changed
- Loading screen: with Loading Progress, one line "RimWorld Loading Optimizer enabled" at the bottom of its window instead of the green bar; without it, a small panel in the game's window style.
- The log only shows the load time and problems. The per-feature startup report is now the setting "Detailed startup report in log" (off by default).
- Diagnostic environment variables start with `RLO_` (for example `RLO_ALL_OFF`, `RLO_VERIFY`, `RLO_PROFILE`); see the README.
- Research tab layout: the node positions are relaxed per research tab instead of over all tabs at once.
- Thing filters: the loops that remove disallowed things walk only the things still allowed instead of every thing. It steps aside when another mod patches thing filters.
- Thing filters: allowing a whole category adds its things in one step from a set kept per category, instead of one call per thing (11 million calls on the Progression packs list, mostly from recipes whose default ingredient filter allows a top category). It follows the Category cache setting, compares its first 50 results with the game's own code, and leaves a call to the game when a thing was already allowed without its variants or when another mod patches `ThingFilter.SetAllow`.

### Fixed
- RimWorld Loading Optimizer starts its own background work (pre-compiling patched methods, the def XML cache key, the Missile Girl and DDS read-ahead) only after it has installed all its patches. Before, those threads could run a method at the moment it was being patched, which crashed the game in rare cases while the mods were starting.
- When startup goes straight into a game instead of the main menu, the main menu steps now run once that game has loaded: startup-only patches are removed and (1.7) the GC latency mode is set back. Before, they stayed until the main menu was opened.
- Self-checks run again after every RimWorld Loading Optimizer update, not only after a game or mod update. All self-checks run once on the first start with this version.
- The category cache, recipe index, facility link lookup and thing filter loops also step aside when another mod patches the same method later during startup, not only when it was patched before their first use.
- Reading textures ahead: when the self-check of the DDS buffers switches them off, the buffers already read are freed and the remaining files are read into the file cache. Before, read-ahead stopped and up to 768 MB stayed in use until the main menu.
- Measurement: the "off" start no longer turns on the detailed startup report, so both starts log the same amount. A measurement start that goes straight into a game is cancelled instead of leaving every later start switched off.
- The optimizer overlap check no longer removes its own hook while the game is loading; removing it while other threads ran the same method caused a rare crash just before the main menu (seen once on 1.7).

### RimWorld 1.6
- Releasing overridden textures no longer frees a texture when the mod that overrides it has no usable texture at that path.

### RimWorld 1.7
- Works with RimWorld 1.7.5000.
- The patched def XML is cached. On a start with the same mods, files, mod settings and game version, mod XML files are not read and patches are not applied; the saved XML is read on a background thread while the mods start, and Missile Girl's own XML cache stays off on those starts. Patch files are read on background threads while the mods start. The def counts per mod are checked on every start that uses it. Building the cache on the first start cost nothing measurable on the Progression packs list.
- Cached texture atlas pages that the last start used are read and unpacked on worker threads while the main thread prepares the atlases, a few pages ahead.
- Texture atlas pages with DLC or mod textures from asset bundles are now saved in the game's atlas cache and loaded on the next start, instead of being baked again every start. The bundle textures get a cache key from their bundle file (path, size, date and their place in the bundle). On the Progression packs list all 22 pages load from the cache (was 15); peak memory 14.4 instead of 15.9 GB.
- The def XML cache is rebuilt after any change to any mod's settings (all `Mod_*.xml` and HugsLib settings), not only those of mods with their own patch operations, so a changed mod option can never load defs patched with the old value.
- Graphics with appearance variants look up each texture folder once per graphic instead of once per variant.
- Folder lookups in asset bundles use an index of the folders in each bundle.
- Decoding mod sounds ahead is off by default. 1.7.5000 streams .ogg sounds instead of decoding them, and on the Progression packs list starts were faster without it. It can still be switched on in Mod Settings.
- 1.7.5000: Giddy-Up draw offsets work again (a game type moved).
- 1.7.5000: skipping the forced memory clean-up at the end of loading finds the game's new clean-up call.
- The game's built-in Prepatcher support counts as Prepatcher for the Yet another Optimizer check and the log hint about rewritten methods.
- README: an optional Steam launch option for a larger GC budget while loading.
- Melee Animation support is no longer listed, because it never loaded on 1.7.
- Def XML cache: the cache is not kept on mod lists where patch operations run after the patching phase (XML Extensions' def database operations, PostInheritanceOperation), because a start from the cache could not repeat them. Such lists load the normal way on every start.
- Def XML cache: only the first load of a session writes the cache. A reload after a mod error or a language change could store its result under the key of the original mod list.
- Def XML cache: a mod's name is part of the cache key, and the cached document has the same first node as the original. Existing caches are rebuilt once.
- A start that uses the def XML cache is recorded as a warm start in the measurement.
- Decoding mod sounds ahead: a sound the game was waiting for is used instead of being opened a second time.

## 5.2

### Changed
- Speedups no longer depend on lists of known game, Harmony or mod versions, so a game or mod update no longer switches them off. Each speedup checks that what it needs still exists, then either checks the one call it changes or compares its first results with the original code once per new code version (remembered in `verified-code.txt`). Harmony rebuild coalescing and reading Missile Girl's cache on several threads also check that the Harmony or System.Xml methods they build on still make exactly the calls they were written around. Only faster mod assembly loading on 1.6 still needs a known version.
- Extra support for other mods is now off by default; switch on the mods you want in Mod Settings. Choices saved by 5.0 are not carried over.
- Works with the RimWorld 1.7.4981 beta.
- Where another optimizer replaces or rewrites the same method, the speedup steps aside and leaves that method to it.
- Missile Girl's background cache write now follows the Missile Girl setting.
- The mesh upload timing in the startup report also appears when a save is loaded straight away.

### RimWorld 1.6
- Atlas baking: the graphics commands are sent after every atlas, so each atlas's temporary buffers are freed right away instead of when loading ends (peak memory on the Progression packs list 80 → 45 GB on a 24 GB Mac).
- Atlas packing: each texture's place in the atlas is looked up from an index instead of searching the whole texture list. The atlases come out the same.
- Atlas cache: atlases with textures from asset bundles (DLCs and mods that ship bundles) or from the game's built-in resources can now be cached too; before, only atlases made entirely of mod texture files could be. On the Progression packs list 24 of 25 atlases are cached instead of 2, and baking takes 4 s instead of 39 s.
- Mod sounds and textures load while the game reads and patches the XML, as on 1.7. A bar on the loading screen shows the progress.

### Removed
- Linkable Groups support.
- 1.7 mesh staging buffer and its setting.

## 5.1

### Added
- RimWorld 1.7 support. Everything in this list is for 1.7 only; the changes on 1.6 are under "RimWorld 1.6" below.
- Mod sounds and textures load while the game reads and patches the XML, instead of after it. A second bar on the loading screen shows the progress.
- Mod sound files are opened and decoded on a background thread while mod data loads (at most 256 MB extra memory during loading).
- The forced full memory clean-up just before the main menu is skipped; the runtime cleans up as usual while you play.
- Code that mods patched is compiled on spare CPU cores early in the next start, and a background JIT profile lets the runtime compile startup code ahead of time from the second start on.
- Faster Harmony patching: the stored patch list and loaded assembly names are no longer worked out again for every patched method.
- Merge repeated Harmony rebuilds when one mod patches the same method from several classes (test, off by default).
- Overridden textures that can never be shown are not loaded onto the graphics card; if a mod still asks for one, it is loaded the first time it is drawn.
- Cached texture atlas pages reuse one buffer instead of a new 85 MB block per page.
- Missile Girl's cache files are read ahead from disk on a background thread when the mod list is unchanged.
- Missile Girl's XML cache is read on several threads into the def document, without first loading the whole file into one string; the defs come out the same, node for node.
- Giddy-Up 2: rider draw offsets are remembered between starts, keyed on the exact texture content, instead of being read back from the graphics card every start. Giddy-Up's own code still runs.
- Worksites Expanded: its tome-trader check is answered directly while no game is loaded, instead of failing and being caught tens of thousands of times per start.
- Alien Worlds Framework: Worldbuilder preset images from the last start are reused when their source textures are unchanged.
- Vanilla Trading Expanded: while it sorts tradeable items at startup, its own product check and RimWorld's market value recipe check loop over the one recipe that can match from an index, instead of over every recipe for every item. Its own code still runs.
- TD Find Lib: while its startup code runs, the recipe users it reads come from an index of the ThingDef recipe lists, in the same order, instead of a scan of every ThingDef per recipe.
- Faster Harmony patching: generated methods bind their references from an index of loaded assemblies by name, instead of comparing every reference with every loaded assembly (about 3.5 s to 0.1 s with 1,483 mods). The module lookup Harmony does for existing patches uses an index too, and the reflection lookups made while generating a method are answered from RLO's identity map.
- XML: finding the previous sibling of a node starts from the last one found instead of walking from the first child. Adaptive Storage Framework replaces every inherited def under the def root this way, which took about 20 s on cold starts with 1,483 mods and now takes about 0.4 s; the document is identical.
- Graphics memory: while the graphics card's memory budget is used up, small mesh buffers are placed in shared 32 MB blocks until shortly after the main menu, instead of each getting its own allocation from the driver (the frame before the main menu went from about 16 s to 0.1 s with 1,483 mods). Mesh uploads copy through one staging buffer per batch. Both have their own setting.
- Texture atlases: the size and date of every source texture file are read on worker threads before baking; the cache keys are the same.
- Memory: from RimWorld Loading Optimizer's start until the main menu the runtime avoids blocking full memory clean-ups (GC latency mode SustainedLowLatency); it is set back at the main menu.
- Content lookups: textures that are not in any mod are checked against each asset bundle's list of names before the bundle is asked, folder lookups only visit mods that have files under that folder, and texture-index misses no longer scan every mod again.
- Recipe index and category cache: Alpha Implants' warm-up of every ThingDef's recipes is checked once instead of once per ThingDef, and the category cache checks a flat list instead of hashing the category tree.
- Cherry Picker: the part of its startup code that removes the picked defs runs right after its class is initialized instead of while it is still being initialized, where every read of the class's own fields takes a slower path (about 1.6 s to 0.3 s). Same code, same order, nothing else runs in between. If that code ever failed, the error is reported for that call instead of on every later use of Cherry Picker.
- Vehicle Framework: while it builds its pattern materials, each pattern texture is decoded to pixels once instead of six times.
- Loading Progress and Lunar: the speedups for them are checked against their current versions again (the forced memory clean-up at the end of loading is skipped again, about 2.7 s).
- Tests, off by default: keep Missile Girl's rebuilt cache usable after a rebuild (it deletes its own mod list file and keeps stale hashes, so the next start is slow again; this works around a Missile Girl issue), and read the game's own sounds and PA Dynamic Seed Icons' images ahead.
- The startup report adds memory clean-ups per loading phase, Harmony rebuild timing, atlas cache misses, mesh upload timing and JIT/GC totals.

### Changed
- On 1.7 the atlas cache and audio streaming switch themselves off: the game now caches baked atlases and loads sounds without the old wait loop itself.

### RimWorld 1.6
- Recipe index: Alpha Implants' warm-up of every ThingDef's recipes is checked once instead of once per ThingDef.
- Category cache and texture lookup index: changes are detected by comparing the category tree and the mods' texture lists directly instead of a hash, so a hash collision can no longer hide a change.
- Content lookups: folder lookups only visit mods that have files under that folder, and texture-index misses no longer scan every mod again.
- Category cache, recipe index and Lunar: the speedups also accept the current versions of the methods they replace.
- Missile Girl verification also compares node kinds, text splits, empty elements and attribute order, not only the XML text.

## 5.0

### Changed
- Extra support for other mods now has one setting per mod (all on by default), so you can switch off the support for a single mod in Mod Settings. Each one relies on that mod's internals, switches itself off when the mod changes, and may need an RLO update after the mod updates.
- Graphics Settings+ and Missile Girl support no longer replace those mods' loaders: their own code runs, and RimWorld Loading Optimizer only hands Graphics Settings+ bytes it read ahead, or lets Missile Girl read its cache straight into the def document.
- Children Wear Anything and Vehicle Framework support only route those mods' texture lookups through RimWorld Loading Optimizer's texture index.
- Category sets now follow the "Category cache" setting.
- The startup report shows one line per feature; feature names say what they act on (for example "vanilla facility link lookup"). (thanks @Jointboy)
- When a speedup is skipped because its target changed, the log says why it can happen (another version of the mod, or another mod such as Performance Fish rewriting the method through Prepatcher) and that nothing is broken. (thanks @Jointboy)
- Times are shown as minutes:seconds, and "cold start" / "warm start" say whether an XML cache was used. (thanks @Jointboy)
- With Hyperdrive, RimWorld Loading Optimizer leaves `LoadModXML` to it, so Hyperdrive's parallel XML loading is no longer switched off. (thanks @Jointboy)
- Image Opt is recognised by its packageId (original and fork). (thanks @Jointboy)
- Warm and cold starts are told apart by whether Missile Girl actually used its XML cache, so a measurement is no longer marked unfair just because fast XML patching made patching quick. (thanks @Jointboy)
- The atlas cache really switches itself off on systems where it cannot work, instead of hashing every texture and throwing the result away on every start.
- More speedups check the original code first (recipe index, category cache, content lookups, Melee Animation, Faster Game Loading, Research Reinvented) and step aside when another mod patches the same method.
- Patches on .NET methods are removed once the main menu opens, and the supported-mods list is logged at that point.

### Added
- Without Graphics Settings+, DDS textures are read into memory on background threads for the game's own loader (texture loading 85 s to 30 s on a 667-mod list, verified identical for 83,778 textures).
- The time to start is also recorded when the game goes straight into a save without showing the main menu. (thanks @Jointboy)
- The measurement history is also saved as a web page, with a button in Mod Settings. (thanks @Jointboy)
- Peak memory and memory at the main menu are recorded on Windows and Linux. (thanks @Jointboy)
- The log says when CombatAI's startup code ran before RimWorld Loading Optimizer could speed it up. (thanks @Jointboy)

### Fixed
- XML Extensions settings that read text from the def XML keep working.

### Removed
- Support for Giddy-Up, Vehicle Framework pattern rotation, TD Find Lib, Vanilla Trading Expanded, KCSG, Adaptive Storage and HugsLib, and the Colony Groups texture repair: these re-implemented parts of those mods' own code.
- Pruning of Missile Girl's stale file hashes. This worked around a problem in Missile Girl; on some mod lists Missile Girl's cache now resets more often.

### Thanks
- Thanks @Jointboy on Discord for the bug reports.

## 4.1.2
- Faster start-up for the code of many popular mods, including Vanilla Trading Expanded, TD Find Lib, Linkable Groups, KCSG, AV Mechanoid Skins and Lunar.
- Faster loading of game definitions.
- Mods start faster when Prepatcher is installed.
- Missile Girl's cache now stays valid after you switch between mod lists, so fewer starts need a full rebuild.
- Works alongside Image Opt (fork), Yet another Optimizer with Prepatcher and WOWGAG's Performance Patch: parts that would overlap with them switch off automatically.
- The loading screen bar now shows how many mods get extra support and which other optimizers are running. The full list is written to the log.
- Fixed: with CombatAI and Colony Groups together, the Colony Groups health overlay could go missing.
- The Mod Settings window now scrolls.
- The texture atlas cache setting now explains that it only works on some systems (it stays empty on most Windows PCs).

## 4.1.0

- Faster start-up with Adaptive Storage, CombatAI and HugsLib.
- Faster setup of the architect menu, item categories and recipes.
- Faster lookups of mod code and mod textures during loading.
- During loading the game spends less time redrawing the loading screen and more on loading itself.
- Fixed: Melee Animation weapon textures stay correct when duplicate textures are released.
- Fixed: the loading screen bar no longer changes the font or colour of other text.
- Frees some extra memory once the main menu is reached.
- The measurement button in Mod Settings now records peak memory correctly on Mac.

## 4.0.5

- Harmony patches from all mods are set up faster.
- Missile Girl's cache now stays valid on very large mod lists.
- A bar on the loading screen shows that the mod is active and what it is doing.

## 4.0.1 to 4.0.3

- Texture read-ahead also works without Graphics Settings+.
- Fixes for Windows: the texture read-ahead keeps up, and the atlas cache switches itself off where it cannot work.
- Protection against mod updates: parts that speed up a specific mod switch themselves off when that mod changes.
- The atlas cache checks every texture before it uses a saved atlas.

## 4.0

- Textures are read from disk ahead of time while the game loads.
- Texture atlases are saved and reused on the next start when nothing changed.
- Faster start-up for Giddy-Up, Vehicle Framework, Children Wear Anything, Research Reinvented: Stepping Stones, Lunar and What's That Mod.
- Faster AI setup and faster lookups of mod code.
- The loading screen stays responsive while definitions load.

## 3.5

- Duplicate textures are only released from mods without code, so mods that read their own textures keep working.
- More XML patches are handled the fast way.

## 3.4

- Frees the memory the game kept for definition XML after loading.

## 3.2

- Large sound files are streamed, so audio loads faster.
- Faster texture, recipe and category setup.
- A report of the slowest mod start-up code in the log.

## 3.1

- Much faster XML patching.
- Faster recipe setup and faster start-up for some mods.

## 2

- Releases textures that several mods ship under the same path, so only the one the game uses stays in memory.
