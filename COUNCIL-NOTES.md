# Council notes (work in progress, not for publishing)

Setup: RimWorld 1.7.4977 rev1248 (Waylith, CoreCLR 10 fork, Harmony 3.0.0-rc.2), 1483 mods, Windows (5800X3D, 32 GB).
Baseline: cold start 226.6 s to main menu (RLO 5.1 on), see RLO phase table in Logs/Player-prev.log.

## Implemented in this round

| Feature | Target | Evidence | Expected |
|---|---|---|---|
| Harmony generated assemblies from files (`HarmonyFileLoads`) | `GeneratedAssemblyLoader.LoadCollectible` → `LoadFromStream` | Windows runs an AMSI (antivirus) scan for every assembly loaded from memory: 8.4 ms vs 1.1 ms from a file (microbenchmark, stock .NET 10). In game 3256 loads ≈ 29.5 s in `LoadFromStream` | ~25 s |
| Harmony dependency name index (`HarmonyDependencyIndex`) | `GeneratedAssemblyLoader..ctor` `GetAssemblies()` | 3257 scans × ~6 refs × ~7000 assemblies | ~3–4 s |
| XML previous-sibling cursor (`SiblingCursor`) | `XmlLinkedNode.PreviousSibling` | Adaptive Storage Framework (PostInheritanceOperation) replaces 33k defs under `<Defs>` with InsertAfter+RemoveChild → O(n²). Harness on the real document: 19.45 s → 0.37 s, identical result | ~15–19 s, every start |
| Missile Girl cache kept after a rebuild (`GagarinCacheKeep`) | `Gagarin.StartupHelper.StartUpFinished` | Missile Girl deletes ModList.xml when it turns the cache off and never writes it back; stale hashes after list switches. Each mod update cost 2 cold starts, each list switch 3 | one cold start instead of 2–3 |
| Fingerprints re-verified | Loading Progress `CollectGarbage` (same three calls as vanilla), Lunar `PatchProcessor.GetPatchInfo` / `ReadMethodBody` (Harmony 3, same behaviour) | were off after mod/Harmony updates | ~2.7 s (forced full GC) + Lunar |

## Measurements (rimmods benchmark, warm, 1484 mods, same machine)

| Run | Build | Main menu |
|---|---|---|
| R1 | 5.1 (HEAD before this round) | 201.2 s |
| R2 | round 1 (dependency index, sibling cursor, Missile Girl keep, fingerprints) | 181.0 s (−20.2 s) |
| R3 | round 1 + Harmony rebuild coalescing | 181.7 s (coalescing saves only 47 of 5243 rebuilds: keep off) |
| R4 | round 1 + DOTNET_GCgen0size=256 MB | 181.3 s (3× fewer gen0 GCs, same 13.9 s pause: GC budget is not the problem) |

| R6 | round 2 (fast content, Harmony module/identity, recipes, atlas stamps, GC latency, mesh staging) | 170.2 s |
| R7 | + mesh buffer pool | **155.0 s** (−46.2 s, −23% vs R1) |
| R8 | + batched Harmony loads (test) | 155.4 s (no gain: kept off) |
| R5 | round 1, cold (simulated asset change) | 254.2 s; ParseAndProcessXML 33.5 → 16.5 s (sibling cursor) |
| R9/R10 | final, after Steam updated RimWorld to 1.7.4979 overnight | 199.2 s (1st start, cold file cache: pre-RLO 45.8 s) / 169.3 s (2nd start) |
| R11 | round 3 on 1.7.4979 (Cherry Picker, TD Find Lib, VTE, Vehicle Framework, seed icons, game sounds, parallel Missile Girl cache read) | **154.0 s** (169.3 s without round 3 on the same build; verify run: 0 mismatches) |

Game update 1.7.4977 → 1.7.4979: Mesh staging, Mesh buffer pool and Fast content bundle lookups switched themselves off (new IL tokens). Decompiled both builds: source identical except VmaAllocationExtensions' failure path (more relax stages), which the pool leaves to the engine. New hashes added. Missile Girl cache keep worked in the real case ("Game build changed ... clearing cache" → mod list written again → next start warm).

Benchmark caveats: the rimmods tool cannot close the game with this list (Confirm Quit to OS), every run ends in "Timed out"; the time comes from RLO's log line. Its `cache: cold` does not clear Missile Girl's cache.

## Dead ends (measured)

- Loading Harmony's generated assemblies from temp files: 0.5 ms outside the game but 1.7 s per load inside RimWorld 1.7 (Waylith runtime). Removed.
- Priming Windows Defender's verdict cache across starts: memory-only loads are never cached across processes, also not after writing the file or mapping it as an image. AMSI cost per new generated assembly (~9–12 ms) is fixed; only fewer generated assemblies would help (batching several methods per module inside Harmony; not done).
- Harmony rebuild coalescing (existing test feature): no gain.
- Larger gen0 budget: no gain.

## Not finished

- Round 3 is applied (commit 76d7fbb). Not effective yet on this list: Vanilla Trading Expanded steps aside because Performance Optimizer patches the same method; Vehicle Framework saw no repeat decodes; the game-sound and seed-icon read-ahead report 0 items taken at report time (check after the main menu; atlas baking was slower in R11, 12.1 s, possibly worker contention).
- Remaining big costs (warm, 155 s): AMSI scan of Harmony's ~3,250 generated assemblies (~33 s, only fewer assemblies helps), pre-RLO engine/mod scanning (~21 s, out of RLO's reach), mods' own static-constructor work.

## Issues / to discuss later

- Report upstream to Ludeon (Waylith): with VRAM over budget, VMA gives every small mesh buffer its own vkAllocateMemory (16 s, and wastes VRAM). RLO's pool fixes it, but the engine should use a pool itself.
- `Krafs.Publicizer` is now a build dependency for 1.7 (publicized 0Harmony, needed by HarmonyBatchLoad only). Checked: no other RLO code binds to non-public Harmony members.

- Policy: RLO 5.0 removed a Missile Girl hash-pruning workaround on purpose. `GagarinCacheKeep` brings that back (using Missile Girl's own Dump methods). Better long-term: report upstream to Missile Girl (Context.IsUsingCache setter deletes ModList.xml; stale hashes are never pruned when the cache is rebuilt).
- Report upstream to Adaptive Storage Framework: `PatchQueue.ApplyXmlInheritanceOnDocument` could use `ReplaceChild`-free swapping or rebuild the root once; quadratic today.
- Report upstream to Harmony 3: load generated assemblies from a file (or cache) on Windows because of AMSI; also an index for dependency binding.
- `FasterGameLoading` and `LoadThemLast` Mod constructors throw TargetInvocationException on 1.7 (seen in the log) — Faster Game Loading may not be doing anything on 1.7.
- Generated Harmony assemblies now have a non-empty `Assembly.Location` (temp file). Harmony itself only uses Location in its debug log and in a fallback that re-reads the file (works). Mods that iterate all assemblies and read `Location` could now see these files.
- Temp files: `%TEMP%/RimWorldLoadingOptimizer/HarmonyGenerated/<pid>-<ticks>/`, a few tens of MB, deleted on the next start.
- Linkable Groups facility links still off (mod's static constructor changed); small gain, not re-verified.
- The rimmods benchmark uses a separate save-data folder: no game TextureCache, so its cold starts are slower than real ones.
