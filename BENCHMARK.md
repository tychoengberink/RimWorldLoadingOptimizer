# RimWorld Loading Optimizer: test setup and results

The numbers on the Workshop page and in the README come from **one specific setup**, described here so anyone can check them or compare with their own. Your results will differ with other mods, other hardware and other settings.

RimWorld 1.6 and 1.7 run different builds of RimWorld Loading Optimizer with different speedups (see the [changelog](CHANGELOG.md)), so their results are listed separately and should not be compared with each other.

## Hardware

| | Mac (RimWorld 1.6 and 1.7) | Windows PC (RimWorld 1.7) |
|---|---|---|
| CPU | Apple M5 Pro | AMD Ryzen 7 5800X3D (8 cores) |
| Memory | 24 GB unified (shared with the GPU) | 32 GB |
| GPU | Apple M5 Pro (Metal on 1.6, Vulkan through MoltenVK on 1.7) | NVIDIA GeForce RTX 5080, 16 GB (Vulkan) |
| Storage | internal SSD | Samsung 970 EVO 1 TB (NVMe SSD) |
| OS | macOS | Windows 11 (10.0.26200) |

## Method (both versions)

- RimWorld 1.6 was measured on the Mac, RimWorld 1.7 on the Windows PC and the Mac (see above).

- Time to the main menu is taken from RimWorld Loading Optimizer's own log line `main menu reached at … s`, counted from process start.
- "Off" is the same mod list with every RimWorld Loading Optimizer feature switched off (`LEANMEMORY_ALLOFF=1`), so only RimWorld Loading Optimizer differs between the two columns.
- Warm = Missile Girl's XML cache was used (a normal start). Cold = it was not (the first start after a mod update or a change to the mod list).
- One start per scenario unless noted, so expect a spread of several seconds.
- Mod counts include Core and the DLCs. RimWorld Loading Optimizer was loaded directly after Harmony in every list.
- Correctness: every speedup has a verification mode (`LEANMEMORY_VERIFY=1`) that recomputes the result the original way.

## RimWorld 1.6

### Setup

- RimWorld 1.6 with all five DLCs.
- RimWorld Loading Optimizer 5.0 with its default settings, measured on 2026-10-12. 5.1 does not change anything on 1.6, so these results still apply.

Mod lists:

- **The NYz Collection: Sci-Fi & Progression**: the Steam collection **[The NYz Collection: Sci-Fi & Progression (1.6)](https://steamcommunity.com/sharedfiles/filedetails/?id=3618911061)** plus **[Loading Progress](https://steamcommunity.com/sharedfiles/filedetails/?id=3535481557)**: 667 mods without Graphics Settings+, 668 with it.
- **Progression packs 1 to 3**: the three Progression pack collections ([1/3](https://steamcommunity.com/sharedfiles/filedetails/?id=3521297585), [2/3](https://steamcommunity.com/sharedfiles/filedetails/?id=3521319712), [3/3](https://steamcommunity.com/sharedfiles/filedetails/?id=3637541646)), 1,490 mods, Prepatcher active. The list was saved in the order Load Them Last wants, so the game does not restart itself during the run.

What else was in place (important). These are part of the setup and strongly affect the results:

1. **All mod textures were pre-converted to DDS** with todds (the converter bundled with RimSort), and non-UI textures larger than 2048 px were downscaled to 2048. Command used, run on the Workshop content folder:
   ```
   todds -f BC1 -af BC7 -on -vf -fs -ss /Textures/ -t <steamapps/workshop/content/294100>
   ```
   RimSort exposes the same conversion in its texture optimisation settings. Reading textures ahead, one of the biggest gains, only applies to DDS textures.
2. **Other performance mods were active**: Faster Game Loading, Loading Progress and Missile Girl (its XML cache is what makes a warm start warm). RimWorld Loading Optimizer's numbers are measured **on top of** these, not instead of them.
3. In-game texture compression on (default).

All speedups reported 0 mismatches in the verification starts. A verification start compared all 83,778 textures loaded from RimWorld Loading Optimizer's buffers with what the game's own loader builds: 0 differences.

### Results (Mac, 1.6, 5.0)

#### The NYz Collection: Sci-Fi & Progression without Graphics Settings+ (667 mods), cold start

| | Off | 5.0 defaults | Change |
|---|---|---|---|
| Time to main menu | 8:16 (495.7 s) | **2:47 (166.9 s)** | −66% |
| Texture loading | 85.5 s | **26.8 s** | DDS buffers for the game's own loader |
| XML patching | 217.7 s | **9.1 s** | fast XML patching |
| Peak memory | 42.6 GB | **41.4 GB** | −1.2 GB |

#### The NYz Collection: Sci-Fi & Progression with Graphics Settings+ (668 mods), warm start

| | Off | 5.0 defaults | Change |
|---|---|---|---|
| Time to main menu | 4:05 and 4:19 (2 starts) | **2:19 (138.5 s)** | about −45% |
| Texture loading | 74–77 s | **29 s** | Graphics Settings+ support |
| Peak memory | 42.3–42.4 GB | **40.7 GB** | about −1.6 GB |

Most of the time saved on this list comes from the extra support for Graphics Settings+, Melee Animation and Research Reinvented: Stepping Stones.

#### Progression packs 1 to 3 (1,490 mods), cold start

| | Off | 5.0 defaults | Change |
|---|---|---|---|
| Time to main menu | 17:03 (1023.5 s) | **5:41 (341.2 s)** | −67% |
| XML patching | 528.9 s | **39.7 s** | −92% |
| Texture loading | 99.0 s | **26.7 s** | −73% |
| Peak memory | 54.3 GB | **53.7 GB** | −0.6 GB |

Warm starts could not be measured on this list: Missile Girl resets its own XML cache on every start here ("Total number of files changed. Reseting cache"), with or without RimWorld Loading Optimizer.

## RimWorld 1.7

### Setup

- RimWorld 1.7.4976 (rev1043) with all five DLCs (Royalty, Ideology, Biotech, Anomaly, Odyssey), on the Windows PC above. These numbers come from different hardware than the 1.6 ones.
- RimWorld Loading Optimizer 5.1 (development build) with its default settings, measured on 2026-10-14. "Merge repeated Harmony rebuilds" is a test option and was off, as in the defaults.
- Mod list: 1,484 mods, saved in the order Load Them Last wants. Other performance mods active: Faster Game Loading, Loading Progress and Missile Girl. Prepatcher, Graphics Settings+ and Melee Animation are not in this list. Mods with extra 1.7 support in the list: Giddy-Up 2 and Alien Worlds Framework (with its world packs).
- On 1.7 the atlas cache and audio streaming switch themselves off, because the game does that work itself.
- Numbers come from `measurements.csv`. The per-phase texture loading times were not kept for these starts, so they are not listed.

### Results (Windows, 1.7, 5.1)

#### 1,484-mod list, cold start

Average of 2 starts per column.

| | Off | 5.1 defaults | Change |
|---|---|---|---|
| Time to main menu | 6:37 (397.2 s) | **4:11 (250.6 s)** | −37% |
| XML patching | 9.4 s | 11.7 s | +2.3 s |
| Peak memory | 19.0 GB | **18.8 GB** | −0.2 GB |

The first start with RimWorld Loading Optimizer on after the off starts took 5:18 (318.2 s). Pre-compiling patched methods and the background JIT profile only help from the second start on, so that start is left out of the table.

XML patching is about 2 s slower with RimWorld Loading Optimizer on, most likely because mod sounds and textures now load at the same time. The total time to the main menu still drops by about 2.5 minutes.

#### 1,484-mod list, warm start

Average of 3 starts.

| | Off | 5.1 defaults | Change |
|---|---|---|---|
| Time to main menu | not measured | **3:33 (212.7 s)** | −46% compared with the cold "off" starts |
| Peak memory | not measured | **17.1 GB** | |

No warm start with RimWorld Loading Optimizer off was measured, so the change in this table compares with the cold "off" starts above and also includes Missile Girl's XML cache.

### Results (Mac, 1.7, 5.1 test build)

These starts were made to test correctness and TPS, not as a clean benchmark, so they differ from the method above:

- RimWorld 1.7.4977 (rev926), Progression packs 1 to 3 (1,477 mods) plus three test mods: one that logs a hash of every def, one that loads a save and measures TPS, and one that lets the game run with Steam closed (all mods were local copies).
- A 5.1 development build with every feature compiled in. The features that do not apply to 1.7 switch themselves off. This is not the exact build in this repository.
- The game went straight into a save instead of stopping at the main menu, so the time is RimWorld Loading Optimizer's `startup finished … at … s` line (up to the moment the save starts loading), not the main menu.
- The Mac has 24 GB of memory shared with the GPU and this list needs about 27 GB, so macOS swaps during every start. Expect a larger spread than on the Windows PC.
- Measured on 2026-10-13.

#### Progression packs 1 to 3, warm start

| | Off | 5.1 test build | Change |
|---|---|---|---|
| Startup time | 3:42 (222.5 s, 4 starts) | **2:19 (139.4 s, 5 starts)** | −37% |
| Peak memory | 26.9 GB | 27.5 GB | +0.6 GB |
| TPS on a 1.6 test save (60 s at speed 4) | 350 | **502** | not slower |

Single starts: off 225.6, 221.1, 219.5 and 223.8 s; on 147.1, 137.7, 140.2, 135.5 and 136.6 s. TPS varied between 207 and 428 with the optimizer off and between 415 and 569 with it on, because of the memory pressure, so the TPS row only shows that it does not get slower.

One cold start with RimWorld Loading Optimizer on took 2:35 (154.8 s). No cold start with it off was measured.

Correctness: every def (about 118,000) was compared field by field between the starts with the optimizer on and off. The first builds gave 601 defs with fewer random texture variants than vanilla; that was fixed, and with the fixed build (the last two "on" starts) everything was identical apart from 32 defs that also differ between two starts with the same setting (random values in some ThingDefs and ThingSetMakerDefs), they were identical. The set of distinct error messages was identical too.

## Measure it yourself

RimWorld Loading Optimizer can run the same A/B comparison on any PC: Mod Settings → RimWorld Loading Optimizer → "Measure how much RimWorld Loading Optimizer saves on your PC". It needs two restarts and writes every start to `RimWorldLoadingOptimizer/measurements.csv` in the RimWorld save data folder. See the README for details.

## Not verified yet

- RimWorld 1.6 on Windows and Linux; RimWorld 1.7 on Linux.
- RimWorld 1.7 on the Mac with the exact build in this repository, timed to the main menu.
- RimWorld 1.7 warm starts with RimWorld Loading Optimizer off on Windows.
- Other mod lists and other hardware.
