# Council notes (work in progress, not for publishing)

Setup: RimWorld 1.7.4977 rev1248 (Waylith, CoreCLR 10 fork, Harmony 3.0.0-rc.2), 1483 mods, Windows (5800X3D, 32 GB).
Baseline: cold start 226.6 s to main menu (RLO 5.1 on), see RLO phase table in Logs/Player-prev.log.

## Implemented in this round

| Feature | Target | Evidence | Expected |
|---|---|---|---|
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
| R12 | all rounds, cold start (simulated changed file) on 1.7.4979 | **163.9 s** vs 226.6 s cold baseline (−28%); same cold-start error count as earlier cold runs (161 vs 162) |

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
- HarmonyBatchLoad and the `Krafs.Publicizer` build dependency were removed again after the council review (no gain).

- Policy: RLO 5.0 removed a Missile Girl hash-pruning workaround on purpose. `GagarinCacheKeep` brings that back (using Missile Girl's own Dump methods). Better long-term: report upstream to Missile Girl (Context.IsUsingCache setter deletes ModList.xml; stale hashes are never pruned when the cache is rebuilt).
- Report upstream to Adaptive Storage Framework: `PatchQueue.ApplyXmlInheritanceOnDocument` could use `ReplaceChild`-free swapping or rebuild the root once; quadratic today.
- Report upstream to Harmony 3: load generated assemblies from a file (or cache) on Windows because of AMSI; also an index for dependency binding.
- `FasterGameLoading` and `LoadThemLast` Mod constructors throw TargetInvocationException on 1.7 (seen in the log) — Faster Game Loading may not be doing anything on 1.7.
- Generated Harmony assemblies now have a non-empty `Assembly.Location` (temp file). Harmony itself only uses Location in its debug log and in a fallback that re-reads the file (works). Mods that iterate all assemblies and read `Location` could now see these files.
- Temp files: `%TEMP%/RimWorldLoadingOptimizer/HarmonyGenerated/<pid>-<ticks>/`, a few tens of MB, deleted on the next start.
- Linkable Groups facility links still off (mod's static constructor changed); small gain, not re-verified.
- The rimmods benchmark uses a separate save-data folder: no game TextureCache, so its cold starts are slower than real ones.


# Besluit van de RLO-raad: 685425b..896f671

## 1. Besluit

**Vrijgeven met wijzigingen.** De raad stemde unaniem, 5 van 5: gebruiker, dotnet, ludeon, modder en perf. De winst is gemeten op de lijst met 1484 mods: warm van 201,2 naar 154,0 s en koud van 226,6 naar 163,9 s. De verify-runs gaven 0 mismatches, de foutaantallen bleven gelijk en de vingerafdrukken werkten na de update naar 1.7.4979. Een publieke release mag pas als de blokkers hieronder zijn opgelost.

## 2. Waar de raad het over eens is

- SiblingCursor (19,45 â†’ 0,37 s) en HarmonyDependencyIndex zijn de beste wijzigingen: veel winst en weinig risico.
- De vingerafdrukken werken in de praktijk. Elke Install loopt via `RLOMod.Try` en schakelt zichzelf netjes uit als iets misgaat.
- MeshPool geeft de grootste winst, maar kan de schermgeheugen-limiet overschrijden: er is geen WITHIN_BUDGET-vlag. Hij heeft ook geen eigen schakelaar en geen bescherming als de allocator opnieuw wordt aangemaakt.
- CctorDeferral werkt anders dan vanilla. Als de tail faalt, gooit hij Ã©Ã©n keer een TIE, maar het type blijft daarna half geÃ¯nitialiseerd.
- HarmonyBatchLoad en Krafs.Publicizer gaven geen winst. De verzonden DLL bevat nu `IgnoresAccessChecksTo(0Harmony)`.
- CHANGELOG en README missen de meeste functies die standaard aan staan, en COUNCIL-NOTES spreekt zichzelf tegen over HarmonyFileLoads.

## 3. Meningsverschillen en hoe de raad ze oplost

| Punt | Standpunten | Oplossing |
|---|---|---|
| MeshPool na het hoofdmenu | ludeon: afkoppelen; perf, modder en dotnet: houden met budgetcontrole | Budgetcontrole is verplicht. MeshPool blijft de hele sessie actief als de speeltest slaagt, anders koppelt hij af bij het hoofdmenu zoals MeshStaging. |
| Bewijs | gebruiker: speelsessie van 30+ min; perf: een machine met weinig VRAM | Allebei, zie blokker 3. |
| Uitzetten per GPU-merk | gebruiker: voor; dotnet: tegen | Afgewezen. De beslissing hangt af van het VMA-budget, niet van het merk. |
| GagarinCacheKeep | modder: standaard uit tot er een upstream-melding is; ludeon: eigen schakelaar plus documentatie is genoeg | Eigen schakelaar, documentatie en een verify-run na een echte mod-update. Zonder die run gaat hij standaard uit. |
| GcLatency herstellen | dotnet en perf: risico; ludeon en dotnet in ronde 2: geen blokker | Volgende stap, geen blokker. |
| ILSpy-controle | modder: niet nodig na verwijdering | Eerst verwijderen, daarna kort controleren (dotnet vond het attribuut). |
| Assembly.Location | perf: zorg | Achterhaald en door perf ingetrokken. Alleen de notities moeten worden opgeschoond. |

## 4. Blokkers voor een publieke release

1. **MeshPool-veiligheid** (`Source/MeshPool.cs`): WITHIN_BUDGET of een controle via `vmaGetHeapBudgets`. Als `allocatorOf(__instance) != poolAllocator`, de pool leegmaken en vernietigen, of de functie blijvend uitzetten.
2. **Eigen schakelaars** (`Source/RLOMod.cs:247-249`): MeshPool, MeshStaging en AtlasStamps los van `startupPhases`, met een eerlijke tooltip.
3. **Testen in het spel** (MeshPool, GcLatency): op de lijst met 1484 mods een save laden, een nieuwe kaart maken, 30+ min spelen met 0 VMA/Vulkan-fouten en netjes afsluiten. Daarnaast Ã©Ã©n start op een tweede machine (ander GPU-merk, 8 GB VRAM of minder, 16 GB RAM) met LEANMEMORY_VERIFY.
4. **HarmonyBatchLoad en Publicizer uit de release** (`Source/HarmonyBatchLoad.cs`, `Source/RimWorldLoadingOptimizer.csproj:55-61`). Daarna `1.7/Assemblies/RimWorldLoadingOptimizer.dll` controleren.
5. **GagarinCacheKeep** (`Source/GagarinCacheKeep.cs`, `Source/RLOMod.cs:278`): eigen schakelaar, documentatie en de verify-run uit sectie 3.
6. **Standaard uit** (`Source/RLOMod.cs:174, 286`): ResourceSoundReadAhead en SeedIconReadAhead, totdat een log laat zien dat ze items verwerken en tijd besparen.
7. **Documentatie** (`CHANGELOG.md`, `README.md`, `COUNCIL-NOTES.md`): een regel per functie die standaard aan staat, met een toelichting voor Cherry Picker en Missile Girl. Daarnaast de tegenstrijdigheid over HarmonyFileLoads oplossen.

## 5. Volgende stappen

1. CctorDeferral: Cherry Picker met naam in de foutmelding zetten en de TIE ook bij latere toegang opnieuw gooien.
2. Ruisbanden meten: 3 runs van R10 en 3 van R11, en daarna elke functie uit ronde 3 een keer uitzetten. VTE en de Vehicle Framework-decodes standaard uitzetten als ze niets opleveren.
3. Piek-werkgeheugen en piek-heap per fase in het rapport opnemen. GcLatency ook herstellen als laden mislukt of het spel afsluit.
4. Het tragere atlas-bakken (12,1 s) onderzoeken met minder read-ahead-workers.
5. Het herschrijven van ModList in GagarinCacheKeep verplaatsen naar het moment dat GagarinAsync klaar is.
6. Een opstartregel die per mod-functie meldt: geÃ¯nstalleerd, opzij gestapt of uitgeschakeld.
7. De vingerafdrukken van 1.7.4979 opnemen in een verify-script, en de BCL-members van GagarinParallelRead ook een vingerafdruk geven.
8. Upstream melden bij Waylith (VMA-pool), Missile Girl, Adaptive Storage en Harmony 3 (AMSI, ~33 s), en daarna de bijbehorende workarounds weghalen.

## Blockers fixed (2026-10-15 evening)

1. Mesh buffer pool: only takes over when the engine's own small mesh allocations are measured to be slow (16 in a row averaging > 100 µs), only until 10 s after the main menu, and switches off if the engine creates a new allocator. The earlier "dedicated allocation per buffer" explanation was wrong (detection by offset 0 / new VkDeviceMemory never fired); the allocations are just slow, and the pool makes them fast (drain 15.7 s → 0.13 s).
2. Own settings: mesh buffer pool, mesh staging, atlas source stamps, Missile Girl cache keep.
3. In-game test: new map, 300 s of play on 1484 mods, clean quit. RLO on: TPS 773, FPS 54, map ready at 194 s. RLO off: TPS 754, FPS 47, map ready at 326 s. Both show ~299 "persistent allocation was not freed before shutdown" warnings (game behaviour, also with RLO off). Not done: a second machine (other GPU vendor / low VRAM).
4. HarmonyBatchLoad and the Krafs.Publicizer dependency removed.
5. Missile Girl cache keep: own setting, off by default.
6. Game sound and PA Dynamic Seed Icons read-ahead: own settings, off by default.
7. CHANGELOG, README and these notes updated.

Same day, Steam updated Harmony to 3.0.0-rc.3 and Load Them Last (which now restarts the game once per start in the benchmark setup). Harmony rc.3 writes exception-handling methods as DynamicMethods, so the assembly path (and its ~33 s of AMSI scans) is gone: Harmony rebuilds 56 s → 26 s. Four RLO Harmony features were re-verified for rc.3 (decompiled diff: only key ordering, a weak key for dynamic replacements and GenerateMethod moving to MethodBodyGenerator). Six "Inconsistent stack depth" errors from rc.3's DynamicMethod writer also appear with RLO completely off: report to Harmony.

| Run (1.7.4979, Harmony rc.3, same mods) | Main menu |
|---|---|
| RLO off, warm | 238.8 s |
| RLO on, warm | **110.7 s (−54%)** |
| RLO on, cold | 143.9 s |
