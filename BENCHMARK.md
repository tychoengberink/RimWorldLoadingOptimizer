# RimWorld Loading Optimizer: test setup and results

All numbers come from the Progression packs 1–3 mod list, described below, so anyone can check them or compare with their own. Your results will differ with other mods, other hardware and other settings. RimWorld 1.6 and 1.7 run different builds with different speedups (see the [changelog](CHANGELOG.md)), so their results are kept separate and should not be compared with each other.

## Summary

### RimWorld 1.6

| Machine | Start | Off | On | Change | Peak memory (off → on) |
|---|---|---|---|---|---|
| Mac | cold | 17:03 | **5:41** | −67% | 54.3 → 53.7 GB |

### RimWorld 1.7

| Machine | Start | Off | On | Change | Peak memory (off → on) |
|---|---|---|---|---|---|
| Windows | cold | 6:37 | **4:11** | −37% | 19.0 → 18.8 GB |
| Windows | warm | – | **3:33** | – | – → 17.1 GB |
| Mac ¹ | warm | 3:42 | **2:19** | −37% | 26.9 → 27.5 GB |
| Mac ¹ | cold | – | **2:35** | – | – → 27.1 GB |

Times are to the main menu. "–" means not measured.

¹ Test runs, not a clean benchmark: a test build, time until the save starts loading instead of the main menu, and macOS swapping because the list needs about 27 GB on a 24 GB Mac. See [Mac, 1.7](#mac-17).

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
- Other performance mods were active in every list: Faster Game Loading, Loading Progress and Missile Girl. The numbers are measured **on top of** these.

## RimWorld 1.6 (Mac, 5.0)

- RimWorld 1.6 with all five DLCs, RimWorld Loading Optimizer 5.0 with default settings, 2026-10-12. Later versions do not change anything on 1.6.
- Mod list: the Progression pack collections [1/3](https://steamcommunity.com/sharedfiles/filedetails/?id=3521297585), [2/3](https://steamcommunity.com/sharedfiles/filedetails/?id=3521319712) and [3/3](https://steamcommunity.com/sharedfiles/filedetails/?id=3637541646) with Prepatcher, saved in Load Them Last's order so the game does not restart itself.
- **All mod textures were pre-converted to DDS** with todds (bundled with RimSort), non-UI textures above 2048 px downscaled: `todds -f BC1 -af BC7 -on -vf -fs -ss /Textures/ -t <steamapps/workshop/content/294100>`. Reading textures ahead, one of the biggest gains, only applies to DDS. In-game texture compression on (default).
- All speedups reported 0 mismatches in verification starts, including all 83,778 textures compared with what the game's own loader builds.

- Cold start phases: XML patching 528.9 → **39.7 s**, texture loading 99.0 → **26.7 s**.
- No warm start was measured: after a switch from another mod list, Missile Girl needs three starts before its XML cache is used again (the first logs "Mod list changed", the second "Total number of files changed. Reseting cache", the third "Mod list changed" again), with or without RimWorld Loading Optimizer. The 5.0 starts on this list all fell inside that cycle, so only the cold result is listed.

## RimWorld 1.7

On 1.7 the atlas cache and audio streaming switch themselves off, because the game does that work itself.

### Windows, 5.1

- RimWorld 1.7.4976 (rev1043) with all five DLCs, RimWorld Loading Optimizer 5.1 development build with default settings, 2026-10-14. Numbers from `measurements.csv`.
- Progression packs 1–3 in Load Them Last's order, no Prepatcher, Graphics Settings+ or Melee Animation. Extra support used: Giddy-Up 2 and Alien Worlds Framework.
- Cold: average of 2 starts per column. XML patching went from 9.4 to 11.7 s, most likely because mod sounds and textures now load at the same time. The first "on" start after the "off" starts took 5:18 and is left out: pre-compiling patched methods and the JIT profile only help from the second start.
- Warm: average of 3 starts. No warm "off" start was measured; compared with the cold "off" starts it is −46%, which includes Missile Girl's cache.

### Mac, 1.7

These starts tested correctness and TPS rather than load time, so they differ from the method above:

- RimWorld 1.7.4977 (rev926), Progression packs 1–3 plus three test mods (def hashes, TPS, running with Steam closed), 2026-10-13.
- A 5.1 development build with every feature compiled in (the ones that do not apply to 1.7 switch themselves off), not the exact build in this repository.
- The game went straight into a save, so the time is the `startup finished … at … s` line, up to the moment the save starts loading.
- Warm: off 225.6, 221.1, 219.5 and 223.8 s; on 147.1, 137.7, 140.2, 135.5 and 136.6 s. Cold: one start with RimWorld Loading Optimizer on.
- TPS on a test save (60 s at speed 4): 350 off, 502 on on average, but single starts ranged from 207 to 569 because of the memory pressure, so this only shows it does not get slower.
- Correctness: all ~118,000 defs were compared field by field with the optimizer on and off. Apart from 32 defs that also differ between two starts with the same setting (random values), they were identical, and so was the set of error messages. An earlier build gave 601 defs fewer random texture variants than vanilla; that was fixed before the last two "on" starts.

## Measure it yourself

Mod Settings → RimWorld Loading Optimizer → "Measure how much RimWorld Loading Optimizer saves on your PC" runs the same A/B comparison. It needs two restarts and writes every start to `RimWorldLoadingOptimizer/measurements.csv` in the RimWorld save data folder. See the README.

## Not verified yet

- RimWorld 1.6 on Windows and Linux; RimWorld 1.7 on Linux.
- RimWorld 1.7 on the Mac with the exact build in this repository, timed to the main menu.
- RimWorld 1.7 warm starts with RimWorld Loading Optimizer off on Windows.
- Other mod lists and other hardware.
