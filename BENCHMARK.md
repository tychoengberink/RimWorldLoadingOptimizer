# RimWorld Loading Optimizer: test setup and results

All numbers come from the Progression packs 1–3 mod list, described below, so anyone can check them or compare with their own. Your results will differ with other mods, other hardware and other settings. RimWorld 1.6 and 1.7 run different builds with different speedups (see the [changelog](CHANGELOG.md)), so their results are kept separate and should not be compared with each other.

## Summary

RimWorld Loading Optimizer 5.2 with its default settings, switched on and off.

### RimWorld 1.6

| Start | Off | On | Change | Peak memory (off → on) |
|---|---|---|---|---|
| cold | 16:36 | **3:04** | −81% | 80.8 → 40.6 GB |
| warm | – | **2:20** | – | – → 40.2 GB |

### RimWorld 1.7

| Start | Off | On | Change | Peak memory (off → on) |
|---|---|---|---|---|
| cold | 2:19 | **1:26** | −38% | 24.0 → 22.0 GB |
| warm | 1:50 | **1:11** | −36% | 24.3 → 24.3 GB |

Times are to the main menu. "–" means not measured. Details per version below.

## Hardware

| | Mac |
|---|---|
| CPU | Apple M5 Pro |
| Memory | 24 GB unified (shared with the GPU) |
| GPU | Apple M5 Pro (Metal on 1.6, Vulkan through MoltenVK on 1.7) |
| Storage | internal SSD |
| OS | macOS |

## Method

- Every start ran through the RimMods benchmark runner, one game at a time, with Steam closed. Each start loads to the main menu and closes the game.
- Time is RimWorld Loading Optimizer's own log line `main menu reached at … s`, counted from process start.
- "Off" is the same mod list with every speedup switched off (`LEANMEMORY_ALLOFF=1`), so only the speedups differ. The mod stays loaded for the measurement and the log reports, which run in both columns.
- Warm = Missile Girl's XML cache was used (a normal start). Cold = it was not (first start after a mod update or a mod list change); for a cold start Missile Girl's mod list file was removed first.
- Loading Progress, Missile Girl and Performance Optimizer were active in every start. The numbers are measured **on top of** these.
- Correctness: every speedup has a verification mode (`LEANMEMORY_VERIFY=1`) that recomputes the result the original way.

## RimWorld 1.6 (Mac, 2026-10-18)

- RimWorld 1.6.4871 (rev597) with all five DLCs. RimWorld Loading Optimizer 5.2 with default settings (support for other mods off).
- Mod list: the current Progression pack collections [1/3](https://steamcommunity.com/sharedfiles/filedetails/?id=3521297585), [2/3](https://steamcommunity.com/sharedfiles/filedetails/?id=3521319712) and [3/3](https://steamcommunity.com/sharedfiles/filedetails/?id=3637541646), 1,486 mods. Mods the packs removed are disabled; four newly added mods could not be downloaded offline.
- **All mod textures were pre-converted to DDS** with todds (bundled with RimSort), non-UI textures above 2048 px downscaled: `todds -f BC1 -af BC7 -on -vf -fs -ss /Textures/ -t <steamapps/workshop/content/294100>`. In-game texture compression on (default).
- Off was measured earlier the same day, with the same game build and mod list, using the development build just before 5.2 with every speedup switched off.

| | Off | On |
|---|---|---|
| cold | 1,024.3 / 967.5 s | 184.1 s |
| warm | – | 139.3 / 140.0 / 141.5 / 142.4 s |
| peak memory | 80.8 GB | 39.9–40.6 GB |
| XML patching (cold) | 459.6–517.0 s | 30.1 s |
| static constructors | – | 28.3–29.6 s |

- The first start after installing took 192.1 s: it baked the atlases and wrote them to the atlas cache (peak memory 45.6 GB). Later starts load them.
- Off cannot start warm on this list: without RimWorld Loading Optimizer, Missile Girl resets its XML cache on every start ("Total number of files changed. Reseting cache").

## RimWorld 1.7 (Mac, 2026-10-18)

- RimWorld 1.7.4981 (rev136, Steam's unstable branch) with all five DLCs. Harmony mod 3.0.1.0 (library 3.0.0-rc.3). RimWorld Loading Optimizer 5.2 with default settings (support for other mods off).
- The Progression packs 1–3 list as on 1.6, 1,482 mods: Prepatcher, CAI 5000, Only If Tolerance Below and Better Prerequisites have no 1.7 version, and VE Base Generation and four newly added pack mods could not be downloaded offline.
- Load Them Last restarted the game once on every start; the times are from the restarted game.
- On 1.7 the atlas cache and audio streaming switch themselves off, because the game does that work itself.

| | Off | On |
|---|---|---|
| cold | 138.8 / 161.1 s | 86.1 s |
| warm | 109.6 / 110.2 / 110.7 / 111.1 s | 70.6 / 70.6 / 71.3 / 71.7 s |
| peak memory (cold) | 23.8–24.0 GB | 22.0 GB |
| peak memory (warm) | 22.6–24.4 GB | 24.3–24.4 GB |
| XML patching (cold) | 8.1 / 10.7 s | 7.8 s |
| static constructors (cold) | 31.0 s | 17.9 s |
| error lines (cold / warm) | 1,380 / 1,160 | 1,380 / 1,160 |

- The summary compares the first cold start of each (138.8 vs 86.1 s, −38%) and the warm averages (−36%).
- The error log is identical with RimWorld Loading Optimizer on and off.
- On this list peak memory is close to the Mac's 24 GB either way; the 1.7 game keeps most of the loaded content resident.

## Measure it yourself

Mod Settings → RimWorld Loading Optimizer → "Measure how much RimWorld Loading Optimizer saves on your PC" runs the same A/B comparison. It needs two restarts and writes every start to `RimWorldLoadingOptimizer/measurements.csv` in the RimWorld save data folder. See the README.
