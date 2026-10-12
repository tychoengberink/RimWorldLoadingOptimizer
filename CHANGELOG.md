# Changelog

## 5.0

### Changed
- Extra support for other mods now has one setting per mod (all on by default), so you can switch off the support for a single mod in Mod Settings. Each one relies on that mod's internals, switches itself off when the mod changes, and may need an RLO update after the mod updates.
- Graphics Settings+ and Missile Girl support no longer replace those mods' loaders: their own code runs, and RimWorld Loading Optimizer only hands Graphics Settings+ bytes it read ahead, or lets Missile Girl read its cache straight into the def document.
- Children Wear Anything and Vehicle Framework support only route those mods' texture lookups through RimWorld Loading Optimizer's texture index.
- Category sets now follow the "Category cache" setting.
- The startup report shows one line per feature; feature names say what they act on (for example "vanilla facility link lookup").
- When a speedup is skipped because its target changed, the log says why it can happen (another version of the mod, or another mod such as Performance Fish rewriting the method through Prepatcher) and that nothing is broken.
- Times are shown as minutes:seconds, and "cold start" / "warm start" say whether an XML cache was used.
- With Hyperdrive, RimWorld Loading Optimizer leaves `LoadModXML` to it, so Hyperdrive's parallel XML loading is no longer switched off.
- Image Opt is recognised by its packageId (original and fork).
- Warm and cold starts are told apart by whether Missile Girl actually used its XML cache, so a measurement is no longer marked unfair just because fast XML patching made patching quick.
- The atlas cache really switches itself off on systems where it cannot work, instead of hashing every texture and throwing the result away on every start.
- More speedups check the original code first (recipe index, category cache, content lookups, Melee Animation, Faster Game Loading, Research Reinvented) and step aside when another mod patches the same method.
- Patches on .NET methods are removed once the main menu opens, and the supported-mods list is logged at that point.

### Added
- Without Graphics Settings+, DDS textures are read into memory on background threads for the game's own loader (texture loading 85 s to 30 s on a 672-mod list, verified identical for 83,778 textures).
- The time to start is also recorded when the game goes straight into a save without showing the main menu.
- The measurement history is also saved as a web page, with a button in Mod Settings.
- Peak memory and memory at the main menu are recorded on Windows and Linux.
- The log says when CombatAI's startup code ran before RimWorld Loading Optimizer could speed it up.

### Fixed
- XML Extensions settings that read text from the def XML keep working.

### Removed
- Support for Giddy-Up, Vehicle Framework pattern rotation, TD Find Lib, Vanilla Trading Expanded, KCSG, Adaptive Storage and HugsLib, and the Colony Groups texture repair: these re-implemented parts of those mods' own code.
- Pruning of Missile Girl's stale file hashes. This worked around a problem in Missile Girl; on some mod lists Missile Girl's cache now resets more often.

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
