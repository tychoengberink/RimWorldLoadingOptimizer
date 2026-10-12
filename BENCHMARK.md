# RimWorld Loading Optimizer: test setup and results

The numbers on the Workshop page and in the README come from **one specific setup**, described here so anyone can check them or compare with their own. Your results will differ with other mods, other hardware and other settings.

## Mod lists

- **The NYz Collection: Sci-Fi & Progression**: the Steam collection **[The NYz Collection: Sci-Fi & Progression (1.6)](https://steamcommunity.com/sharedfiles/filedetails/?id=3618911061)** plus **[Loading Progress](https://steamcommunity.com/sharedfiles/filedetails/?id=3535481557)**: 667 mods without Graphics Settings+, 668 with it.
- **Progression packs 1 to 3**: the three Progression pack collections ([1/3](https://steamcommunity.com/sharedfiles/filedetails/?id=3521297585), [2/3](https://steamcommunity.com/sharedfiles/filedetails/?id=3521319712), [3/3](https://steamcommunity.com/sharedfiles/filedetails/?id=3637541646)), 1,490 mods, Prepatcher active. The list was saved in the order Load Them Last wants, so the game does not restart itself during the run.

Mod counts include Core and the DLCs. RimWorld Loading Optimizer was loaded directly after Harmony in every list.

## Hardware

| | Mac (all measurements) |
|---|---|
| CPU | Apple M5 Pro |
| Memory | 24 GB unified (shared with the GPU) |
| GPU | Apple M5 Pro (Metal) |
| Storage | internal SSD |
| OS | macOS |

RimWorld 1.6 with all five DLCs.

## What else was in place (important)

These are part of the setup and strongly affect the results:

1. **All mod textures were pre-converted to DDS** with todds (the converter bundled with RimSort), and non-UI textures larger than 2048 px were downscaled to 2048. Command used, run on the Workshop content folder:
   ```
   todds -f BC1 -af BC7 -on -vf -fs -ss /Textures/ -t <steamapps/workshop/content/294100>
   ```
   RimSort exposes the same conversion in its texture optimisation settings. Reading textures ahead, one of the biggest gains, only applies to DDS textures.
2. **Other performance mods were active**: Faster Game Loading, Loading Progress and Missile Girl (its XML cache is what makes a warm start warm). RimWorld Loading Optimizer's numbers are measured **on top of** these, not instead of them.
3. In-game texture compression on (default).

## Method

- RimWorld Loading Optimizer 5.0 with its default settings, measured on 2026-10-12.
- Time to the main menu is taken from RimWorld Loading Optimizer's own log line `main menu reached at … s`, counted from process start.
- "Off" is the same mod list with every RimWorld Loading Optimizer feature switched off (`LEANMEMORY_ALLOFF=1`), so only RimWorld Loading Optimizer differs between the two columns.
- Warm = Missile Girl's XML cache was used (a normal start). Cold = it was not (the first start after a mod update or a change to the mod list).
- One start per scenario unless noted, so expect a spread of several seconds.
- Correctness: every speedup has a verification mode that recomputes the result the original way; all reported 0 mismatches in these test starts. A verification start compared all 83,778 textures loaded from RimWorld Loading Optimizer's buffers with what the game's own loader builds: 0 differences.

## Results (Mac, 5.0)

### The NYz Collection: Sci-Fi & Progression without Graphics Settings+ (667 mods), cold start

| | Off | 5.0 defaults | Change |
|---|---|---|---|
| Time to main menu | 8:16 (495.7 s) | **2:47 (166.9 s)** | −66% |
| Texture loading | 85.5 s | **26.8 s** | DDS buffers for the game's own loader |
| XML patching | 217.7 s | **9.1 s** | fast XML patching |
| Peak memory | 42.6 GB | **41.4 GB** | −1.2 GB |

### The NYz Collection: Sci-Fi & Progression with Graphics Settings+ (668 mods), warm start

| | Off | 5.0 defaults | Change |
|---|---|---|---|
| Time to main menu | 4:05 and 4:19 (2 starts) | **2:19 (138.5 s)** | about −45% |
| Texture loading | 74–77 s | **29 s** | Graphics Settings+ support |
| Peak memory | 42.3–42.4 GB | **40.7 GB** | about −1.6 GB |

Most of the time saved on this list comes from the extra support for Graphics Settings+, Melee Animation and Research Reinvented: Stepping Stones.

### Progression packs 1 to 3 (1,490 mods), cold start

| | Off | 5.0 defaults | Change |
|---|---|---|---|
| Time to main menu | 17:03 (1023.5 s) | **5:41 (341.2 s)** | −67% |
| XML patching | 528.9 s | **39.7 s** | −92% |
| Texture loading | 99.0 s | **26.7 s** | −73% |
| Peak memory | 54.3 GB | **53.7 GB** | −0.6 GB |

Warm starts could not be measured on this list: Missile Girl resets its own XML cache on every start here ("Total number of files changed. Reseting cache"), with or without RimWorld Loading Optimizer.

## Measure it yourself

RimWorld Loading Optimizer can run the same A/B comparison on any PC: Mod Settings → RimWorld Loading Optimizer → "Measure how much RimWorld Loading Optimizer saves on your PC". It needs two restarts and writes every start to `RimWorldLoadingOptimizer/measurements.csv` in the RimWorld save data folder. See the README for details.

## Not verified yet

- Other mod lists, other hardware, Windows, Linux.
