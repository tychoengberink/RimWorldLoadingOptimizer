# Changelog

## 5.1

### Added
- RimWorld 1.7 support. On 1.6 nothing changes; everything below is for 1.7 only.
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
- The startup report adds memory clean-ups per loading phase, Harmony rebuild timing, atlas cache misses and JIT/GC totals.

### Changed
- On 1.7 the atlas cache and audio streaming switch themselves off: the game now caches baked atlases and loads sounds without the old wait loop itself.

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
