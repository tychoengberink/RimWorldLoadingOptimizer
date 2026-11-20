# RimWorld Loading Optimizer: benchmark

RimWorld Loading Optimizer 5.5 pre-release, measured on 20 November 2026 with one start per scenario, on the mod list in [MODLIST.md](MODLIST.md). "With" had support for seven other mods and the startup report switched on; "without" is the same mod list with every speedup switched off (`RLO_ALL_OFF=1`). A cold start does not use Missile Girl's XML cache, as after a mod list change; a warm start is a normal start.

| Time | Scenario | Platform |
|---|---|---|
| 89.3 s (1:29) | Cold start to the main menu, with RimWorld Loading Optimizer | Mac (Apple M5 Pro, 24 GB), macOS, RimWorld 1.7.5014 with 1,553 mods |
| 246.6 s (4:07) | Cold start to the main menu, without RimWorld Loading Optimizer | Mac (Apple M5 Pro, 24 GB), macOS, RimWorld 1.7.5014 with 1,553 mods |
| 72.6 s (1:13) | Warm start to the main menu, with RimWorld Loading Optimizer | Mac (Apple M5 Pro, 24 GB), macOS, RimWorld 1.7.5014 with 1,553 mods |
| 177.4 s (2:57) | Warm start to the main menu, without RimWorld Loading Optimizer | Mac (Apple M5 Pro, 24 GB), macOS, RimWorld 1.7.5014 with 1,553 mods |
