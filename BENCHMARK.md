# RimWorld Loading Optimizer: test setup and results

All numbers come from the Progression packs 1–3 mod list, described below, so anyone can check them or compare with their own. Your results will differ with other mods, other hardware and other settings. RimWorld 1.6 and 1.7 run different builds with different speedups (see the [changelog](CHANGELOG.md)), so their results are kept separate and should not be compared with each other.

## Summary

"Current" is the version in this repository; 5.0 and 5.1.0 are the previous releases, measured on the same list in the same session.

### RimWorld 1.6

| Machine | Start | Off | 5.0 | Current | Change (off → current) | Peak memory (off / 5.0 / current) |
|---|---|---|---|---|---|---|
| Mac ¹ | cold | 16:36 | 4:58 | **2:59** | −82% | 80.8 / 80.3 / 40.5 GB |
| Mac ¹ | warm | – | 3:47 | **2:12** | – | – / 76.2 / 40.3 GB |

¹ RimWorld 1.6.4871, 2026-10-18, 1,486 mods: the current Progression packs 1–3 list, DDS textures, Steam closed. Off cannot start warm on this list (see [RimWorld 1.6](#rimworld-16-mac-2026-10-18)). The first start after an install or mod change takes about a minute longer (4:07) because it writes the atlas cache.

### RimWorld 1.7

| Machine | Start | 5.1.0 | Current | Current, all mod support on | Peak memory (5.1.0 / current) |
|---|---|---|---|---|---|
| Mac ² | cold | 1:38 | **1:22** | – | 22.8 / 22.1 GB |
| Mac ² | warm | 1:05 | 1:11 | **1:03** | 23.2 / 22.9 GB |

² RimWorld 1.7.4981, 2026-10-18, 1,482 mods: the same Progression packs list, Steam closed. Both with default settings: 5.1.0 has the support for other mods on by default, the current version has it off. With the same settings the current version is about 2 s faster warm. No start with the optimizer off was measured on this build; see [Mac, 2026-10-18](#mac-2026-10-18).

| Machine | Start | Off | On | Change | Peak memory (off → on) |
|---|---|---|---|---|---|
| Windows ³ | cold | 4:40 | **2:11** | −53% | 18.7 → 17.4 GB |
| Windows ³ | warm | 4:06 | **1:54** | −54% | 17.3 → 16.4 GB |

³ 5.1 development build on RimWorld 1.7.4979, 2026-10-15, with 1,481 mods: the Progression packs list after removing a few slow mods. Earlier results on the full list are under [Windows, 5.1 (2026-10-14)](#windows-51-2026-10-14).

Times are to the main menu. "–" means not measured.

## Hardware

| | Mac | Windows PC |
|---|---|---|
| CPU | Apple M5 Pro | AMD Ryzen 7 5800X3D (8 cores) |
| Memory | 24 GB unified (shared with the GPU) | 32 GB |
| GPU | Apple M5 Pro (Metal on 1.6, Vulkan through MoltenVK on 1.7) | NVIDIA GeForce RTX 5080, 16 GB (Vulkan) |
| Storage | internal SSD | Samsung 970 EVO 1 TB (NVMe SSD) |
| OS | macOS | Windows 11 (10.0.26200) |

## Method

- Time is RimWorld Loading Optimizer's own log line `main menu reached at … s`, counted from process start.
- "Off" is the same mod list with every speedup switched off (`LEANMEMORY_ALLOFF=1`), so only the speedups differ. The mod stays loaded for the measurement and the log reports, which run in both columns.
- Warm = Missile Girl's XML cache was used (a normal start). Cold = it was not (first start after a mod update or a mod list change).
- One start per scenario unless noted. RimWorld Loading Optimizer sits directly after Harmony.
- Correctness: every speedup has a verification mode (`LEANMEMORY_VERIFY=1`) that recomputes the result the original way.
- Other performance mods were active in every list: Loading Progress and Missile Girl, plus Performance Optimizer in the 2026-10-18 lists and Faster Game Loading in the earlier ones. The numbers are measured **on top of** these.

## RimWorld 1.6 (Mac, 2026-10-18)

- RimWorld 1.6.4871 (rev597) with all five DLCs, 2026-10-18. Default settings: mod support switched off (the default), except Missile Girl support (see warm starts below).
- Mod list: the current Progression pack collections [1/3](https://steamcommunity.com/sharedfiles/filedetails/?id=3521297585), [2/3](https://steamcommunity.com/sharedfiles/filedetails/?id=3521319712) and [3/3](https://steamcommunity.com/sharedfiles/filedetails/?id=3637541646), 1,486 mods, Steam closed. Mods the packs removed are disabled; four newly added mods could not be downloaded offline. Loading Progress, Missile Girl and Performance Optimizer are in the packs.
- **All mod textures were pre-converted to DDS** with todds (bundled with RimSort), non-UI textures above 2048 px downscaled: `todds -f BC1 -af BC7 -on -vf -fs -ss /Textures/ -t <steamapps/workshop/content/294100>`. In-game texture compression on (default).
- 5.0 and off ran on the same list in the same session, so all three columns are directly comparable.

| Start | Off | 5.0 | Current |
|---|---|---|---|
| cold | 1,024.3 / 967.5 s | 296.1 / 300.5 s | 247.1 ⁴ / 178.6 s |
| warm | – | 226.3 / 227.6 s | 132.6 / 131.6 s |
| peak memory | 80.8 GB | 75.9–80.3 GB | 40.1–45.3 GB |
| XML patching (cold) | 459.6–517.0 s | 26.7–27.2 s | 28.1–30.0 s |
| atlas baking | – | 33.4–37.0 s | 3.9–4.2 s |
| TPS, colony save ⁵ | – | 509.2 / 528.7 | 538.0 / 517.1 |

⁴ First start after the install: it bakes the atlases and writes them to the atlas cache (~3.5 GB on disk). Later starts load them (24 of 25 atlases).

⁵ A new colony on this list, game speed 4, 60 s measured after a 120 s warm-up. Performance Optimizer keeps patching in the background for the first ~90 s of play, so shorter warm-ups measure that instead. 5.0 and the current version are equal within noise, as expected: RimWorld Loading Optimizer only changes loading.

- What the current version adds on 1.6 compared with 5.0:
  - The atlas cache now covers textures from asset bundles and Resources: 24 of 25 atlases cached (5.0: 2 of 25).
  - The graphics driver gets each baked atlas right away instead of collecting all of them, which roughly halves peak memory.
  - Atlas packing finds textures by reference.
  - Mod content loads while the XML loads (1,486 of 1,486 mods).
- Correctness:
  - Two verification starts reported 0 mismatches.
  - Every atlas loaded from the cache was identical to a fresh bake.
  - The error log was the same as with 5.0.
  - The colony played normally.
- Stability: 0 crashes in about 30 starts with the current version. 5.0 crashed twice in 9 starts (once while loading textures, once in the pawn renderer during play); neither crash happened with the current version.
- Warm starts: on this list Missile Girl resets its XML cache on every start ("Total number of files changed. Reseting cache"). It only starts warm when RimWorld Loading Optimizer's Missile Girl support is switched on, which is why off has no warm result.

## RimWorld 1.7

On 1.7 the atlas cache and audio streaming switch themselves off, because the game does that work itself.

### Mac, 2026-10-18

- RimWorld 1.7.4981 (rev136) with all five DLCs, 2026-10-18, Steam closed. Harmony mod 3.0.1.0 (library 3.0.0-rc.3).
- The Progression packs 1–3 list as on 1.6, 1,482 mods: Prepatcher, CAI 5000, Only If Tolerance Below and Better Prerequisites have no 1.7 version, and VE Base Generation and four newly added pack mods could not be downloaded offline.
- Load Them Last restarted the game once on every start; the times are from the restarted game.
- 5.1.0 runs with its defaults (support for all other mods on). The current version runs with its defaults (support for other mods off), with only Missile Girl support on, and with all 18 mod supports on.

| Start | 5.1.0 | Current (default) | Current, Missile Girl support on | Current, all mod support on |
|---|---|---|---|---|
| cold | 98.0 s | 81.7 s | 78.5 / 77.7 s | – |
| warm | 64.4 / 64.8 s | 71.3 / 70.9 / 71.4 s | 69.0 / 68.7 s | 64.7 ⁴ / 61.4 / 64.1 s |
| peak memory | 22.5–23.2 GB | 22.1–22.9 GB | 22.3–22.9 GB | 22.8–23.0 GB |
| static constructors (warm) | 11.9 s | 18.3 s | 18.5 s | 11.8 s |
| TPS, colony save ⁵ | 864.4 / 868.8 | 893.6 | 893.4 / 891.3 | – |
| longest frame while playing ⁵ | 1,754 / 1,816 ms | 271 ms | 205 / 220 ms | – |

⁴ First start with Giddy-Up 2 and Alien Worlds Framework support on: it writes their caches.

⁵ A new colony on this list, game speed 4, 60 s measured after a 120 s warm-up.

- Cold starts are 16–20 s faster than 5.1.0: on this game build 5.1.0 switches eight of its speedups off, because the game code they were checked against changed (sound read-ahead, atlas page buffers, mesh staging buffer, mesh buffer pool, XML previous-sibling cursor, fast content bundle lookups, Missile Girl's multi-threaded cache read and Linkable Groups facility links). The current version knows this game build, and checks code it does not know by what that code does instead of by its exact version, so all of them except Linkable Groups stay on.
- Missile Girl's cache is used on 1.7 with default settings too; Missile Girl support only makes reading it faster (5.4 → 1.6 s).
- Warm with default settings the current version is about 6.5 s slower than 5.1.0. The support for other mods is off by default now, so the static constructors take 6.4 s longer (mostly Giddy-Up 2, Alien Worlds Framework, Vehicle Framework and Cherry Picker) and Missile Girl's cache is read more slowly. With all mod support on, the current version is about 2 s faster than 5.1.0.
- Correctness:
  - A verification start reported 0 mismatches for every speedup.
  - A start that forced every version-free check passed all of them.
  - The error log was identical to 5.1.0's, at the main menu and while playing.
  - The colony played normally.
- Linkable Groups support switches itself off on this list, because that mod changed; its setup takes 137 ms anyway.
- The first start after the game was installed took 254.8 s (with an earlier build): the game's own atlas cache was still empty and it prepared every texture for the first time. Later starts load 15 of its 22 atlas pages from that cache.

### Windows, 5.1 (2026-10-15)

- RimWorld 1.7.4979 (rev109) with all five DLCs, Harmony 3.0.0-rc.3, RimWorld Loading Optimizer 5.1 development build (develop at e9494e2) with default settings, 2026-10-15. Each start loads to the main menu and closes the game.
- 1,481 mods: the Progression packs 1–3 list after removing a few slow mods, so these numbers cannot be compared with the 2026-10-14 ones below. No Prepatcher, Graphics Settings+ or Melee Animation.
- Load Them Last reorders this list and restarts the game once on every start. It puts the mods in the same order each time, so Missile Girl's cache stays valid; the times are from the restarted game, and the first start adds about 30 s of wall time.
- "Support off" = RimWorld Loading Optimizer on, but with the support for all 18 other mods switched off in its settings, so only its own speedups for the game and Harmony run.

| Start | Off | Support off | On | Peak memory (off → support off → on) |
|---|---|---|---|---|
| cold | 4:40 | 2:24 | **2:11** | 18.7 → 17.2 → 17.4 GB |
| warm | 4:06 | 2:19 | **1:54** | 17.3 → 16.3 → 16.4 GB |

- Cold: one start per column, each after a mod list change. Warm: off 245.0 and 246.0 s, on 113.1 and 113.9 s, support off one start (138.5 s), each right after a start that filled the cache.
- With a warm cache the support for other mods saves about 25 s: Missile Girl's cache load (CombineIntoUnifiedXML) 10.5 → 2.4 s and the static constructors 32.4 → 21.8 s, mostly Cherry Picker and Vehicle Framework.
- Every warm start logged the same 85 error lines with the optimizer on and off. The Linkable Groups speedup switched itself off because that mod changed.

### Windows, 5.1 (2026-10-14)

- RimWorld 1.7.4976 (rev1043) with all five DLCs, RimWorld Loading Optimizer 5.1 development build with default settings, 2026-10-14. Numbers from `measurements.csv`.
- Progression packs 1–3 in Load Them Last's order, no Prepatcher, Graphics Settings+ or Melee Animation. Extra support used: Giddy-Up 2 and Alien Worlds Framework.
- Cold: average of 2 starts per column. XML patching went from 9.4 to 11.7 s, most likely because mod sounds and textures now load at the same time. The first "on" start after the "off" starts took 5:18 and is left out: pre-compiling patched methods and the JIT profile only help from the second start.
- Warm: average of 3 starts. No warm "off" start was measured; compared with the cold "off" starts it is −46%, which includes Missile Girl's cache.

## Measure it yourself

Mod Settings → RimWorld Loading Optimizer → "Measure how much RimWorld Loading Optimizer saves on your PC" runs the same A/B comparison. It needs two restarts and writes every start to `RimWorldLoadingOptimizer/measurements.csv` in the RimWorld save data folder. See the README.

## Not verified yet

- RimWorld 1.6 on Windows and Linux; RimWorld 1.7 on Linux.
- RimWorld 1.7.4981 with the optimizer off on the Mac.
- RimWorld 1.7 warm starts with RimWorld Loading Optimizer off on Windows on the full Progression packs list.
- Other mod lists and other hardware.
