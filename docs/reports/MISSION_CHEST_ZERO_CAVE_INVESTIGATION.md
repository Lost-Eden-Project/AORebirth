# Zero-chest cave investigation

2026-10-24, Windows feature branch `codex/mission-chests`, starting from
`5cc8a4f95ed508ac9013f41983e7a2822174c510`. Automated protocol observations
do not establish original-client visual or walking acceptance.

## Actual player mission: proven build omission

Read-only inspection of the current local mission database and current server
log identifies `D2086979044` as the newest entered cave mission. It was accepted
at 19:31:46 and materialized at 19:32:01 on 2026-10-24 (server log time).
Its frozen generator version is 3, QL 125, style 341, seed 1775745370,
one floor, size 12, height 64, descending, without a boss floor. The quest
completed at 19:34:56 and its world was released at 19:35:08.

**OBSERVED:** the database contains zero chest snapshots and no chest population
seal for this accepted run. Its token census was committed when the dungeon
was built, proving that materialization occurred. The frozen mission JSON was
retained privately for an exact-layout regression; the player mission was not
changed.

**PROVEN:** the running canonical ZoneEngine binary lacks the
`MissionChestPopulation` type entirely. Its SHA-256 is
`eb417bb7de7cfdc13e3187d71fbed9a0e993af3dd4288c64412c4b2206e822ae`.
Current master also lacks the chest population path; the chest feature remains
on the unmerged feature branch. This is a recently built master without the
feature, rather than evidence that the corrected planner ran and rejected every
position. The binary's exact source SHA is not independently established.

| Stage | Result for the actual mission |
| --- | --- |
| Accepted and frozen | Yes |
| Dungeon materialized | Yes, log and token census |
| Chest initialization | Not present in running build |
| Requested | Not calculated |
| Generated | 0: generation path absent |
| Persisted | 0: observed snapshots; seal absent |
| Instantiated / published | 0 follows from absent implementation and population; no historical packet trace |
| Visible | Player reports 0; no agent client observation |
| Chests on inaccessible floors | None persisted; this mission has one floor |

Cell and eligible-room counts attributed to this layout in the CSV come from its frozen reconstruction, not a historical chest plan.

The first missing link is **chest generation in the installed build**. No
deployment, merge into master, migration, mission regeneration or player-data
mutation is authorized or performed by this investigation.

## Feature-branch corrections and validation

**PROVEN current-data compatibility blocker:** the first isolated cave run
correctly failed its nonempty-population assertion. The old feature branch's
item model could not deserialize the current full-RDB `items.dat` export,
logged a serialization exception, and loaded zero templates. Consequently,
placement deferred for unavailable chest collision data rather than sealing
an empty population. This is independent of the actual player's installed
build omission.

The correction copies only `Dat/DatItemTemplate.cs` from accepted master
`6f700c3092c762ccef7a923b3d7a4a22a0937fbf`, where it was introduced in
`a47f11612598512f198cc8fa6d9e7ae76ffec55f`: the existing MessagePack member 14
and its typed RDB models. The mapper and reader already match master exactly;
no item mechanics, loot probabilities or private catalog bytes are changed.
After reconciliation the bounded exact-layout/cave preflight cohort passed
9/9 against the current complete dataset.

Private raw evidence stays under ignored
`build-verify/zero-chests-20261024`; schema snapshots, connection settings and
player records are not published.

## Corrected feature behavior

- **Minimum target:** positive density and at least one eligible room now request at least one chest. Empty room sets and explicitly disabled density still request zero. Cave 341 / 2 floors / seed 34719395 / size 8 has one eligible room: all four original count rolls requested zero, while four independently checked density-one probes found a valid reachable chest. The corrected target produces one validated chest on every included roll. This changes the target only; it never forces a pose.
- **Bounded sampling recovery:** five previously empty rolls in three caves had proven safe witnesses under other placement seeds. Only when both existing searches return zero, the planner repeats that existing bounded pair once, stopping when it finds a positive validated population. Maximum four searches; the count is rolled once, candidates are never merged, and no identities are allocated until planning finishes. All five misses now recover. Wall, collision, support, doorway, approach and preserved-route checks are unchanged.
- **Deferred initialization:** population reports exact missing-input reasons without a seal. Existing destination admission retries data prerequisites in an already built dungeon before visibility activates. Successful materialization runs once. A failed partial registration retires access and requires fresh reconstruction; admission errors are logged while ordinary world visibility continues. Missing whole-world geometry/simulation still requires ordinary fresh reconstruction; this change does not rebind a live world.
- **Diagnostics:** generation records the accepted run, style, seed, QL, floors, placement random seed, eligible/requested/generated/candidate counts and rejection reasons. Completed materialization records persisted/instantiated counts and floor distribution. Compact runtime diagnostics omit stored route witnesses; an independent test proves they produce the same nonempty population as full diagnostics. These logs do not claim client publication.
- **Durability:** seal-first reconstruction precedes current policy and geometry. Existing identities, positions, appearance, remaining item instances and sealed empty populations retain their values. The new minimum cannot reroll an existing seal of zero. Loot selection, JSON rarity weights, QL rules, ownership, DAO transactions and original chest packet builders are unchanged. No new navigation system, generator, database schema or chest implementation is introduced.

## Bounded real-layout runtime matrix

All nine cases use actual generated collision/layout data, the production population, registry, locality and original spawn builder. They commit through the existing in-memory transaction fixture and publish real messages to a bound test session after visiting each chest's locality. They then reconstruct with generation policy unavailable and require byte-identical spawn messages, the same item ownership and no second initialization transaction. This matrix is not a TCP or real-MySQL claim; those are separate checks below.

| Style / floors / seed / size | Cells | Eligible | Requested | Candidates | Persisted / instantiated / published | Rooms | Wall center distance, m | Chest floors |
| --- | ---: | ---: | ---: | ---: | --- | ---: | --- | --- |
| 321 / 1 / 7 / 11 | 20 | 13 | 3 | 75 | 3 / 3 / 3 | 3 | 0.67441–0.69336 | 0:3 |
| 321 / 1 / 42 / 9 | 12 | 6 | 2 | 36 | 2 / 2 / 2 | 2 | 0.67959–0.69595 | 0:2 |
| 324 / 1 / 42 / 9 | 11 | 9 | 2 | 53 | 2 / 2 / 2 | 2 | 0.67637–0.70390 | 0:2 |
| 324 / 2 / 7 / 10 | 16 | 5 | 1 | 27 | 1 / 1 / 1 | 1 | 0.68346–0.68346 | 0:1 |
| 341 / 1 / 7 / 11 | 17 | 7 | 2 | 13 | 2 / 2 / 2 | 2 | 0.66085–0.69905 | 0:2 |
| 341 / 1 / 1775745370 / 12 | 20 | 8 | 2 | 26 | 2 / 2 / 2 | 2 | 0.67715–0.70888 | 0:2 |
| 341 / 5 / 42 / 10 | 67 | 17 | 5 | 22 | 5 / 5 / 5 | 5 | 0.66744–0.69855 | 0:1, -3:3, -1:1 |
| 351 / 1 / 7 / 11 | 21 | 15 | 5 | 112 | 5 / 5 / 5 | 5 | 0.66604–0.70564 | 0:5 |
| 351 / 1 / 42 / 9 | 20 | 14 | 4 | 57 | 4 / 4 / 4 | 4 | 0.67897–0.70520 | 0:4 |

Total: **26 requested, 26 generated, 26 persisted, 26 instantiated, 26 published**; one chest per occupied room. Wall distances are center-to-wall distances, not collider-edge gaps. Independent physical assertions verify collision clearance. The actual failed mission's geometry is the 1775745370 row; these are isolated reconstructed results, not mutations or historical counts for the player mission.

## Bounded cave diagnosis and regression results

| Style / floors / seed / size | Rolls | Cells / eligible | Requested | Placed | Candidates |
| --- | ---: | --- | --- | --- | --- |
| 341 / 1 / 1775745370 / 12 | 8 | 20 / 8 | 2–3 | 2–3 | 21–31 |
| 341 / 1 / 19024074 / 9 | 4 | 14 / 4 | 1–1 | 1–1 | 1–3 |
| 341 / 1 / 20071364 / 11 | 4 | 11 / 2 | 1–1 | 1–1 | 2–4 |
| 341 / 1 / 22480131 / 10 | 4 | 16 / 6 | 2–2 | 1–2 | 6–9 |
| 341 / 2 / 34719395 / 8 | 8 | 13 / 1 | 1–1 | 1–1 | 3–8 |
| 341 / 2 / 34928853 / 10 | 4 | 17 / 2 | 1–1 | 0–0 | 0–0 |
| 341 / 2 / 38384910 / 8 | 4 | 11 / 2 | 1–1 | 1–1 | 1–3 |
| 341 / 5 / 82433732 / 12 | 4 | 65 / 24 | 7–8 | 0–0 | 47–52 |

These 40 diagnostic rolls include the repeated minimum regression. Every returned pose has independent wall, own-room floor, collision, inward pocket, objective/door clearance and entrance/required-route witnesses. Zero-result rows are diagnostics, **not successful populated runtime cases**. Layout 22480131 still has one shortfall (target 2, placed 1); recovery stops at its first positive result. Layout 34928853 has no validated candidates; 82433732 has 47–52 candidates but none reachable in the sampled graph. Their physical impossibility is **UNVERIFIED**. Four bounded searches do not prove there is no safe physical route, and no safety threshold was weakened to conceal these limits.

[All per-layout statistics](MISSION_CHEST_ZERO_CAVE_LAYOUTS.csv) include wall distances, floor distributions, candidate rejections and explicit NOT_EXERCISED/NOT_RECORDED fields. Supplemental positive geometry statistics come from the broader cohort; the zero-only extension does not change their positive search paths. Geometry-only rows do not claim persistence or client publication.

| Check | Actual result |
| --- | --- |
| Broader bounded chest + inventory cohort | 123 passed, 2 failed, 0 skipped; 125 total |
| Final recovery / real population / geometry cohort | 25 passed, 0 failed, 0 skipped |
| Final strict recovered-layout and saved-zero assertions | 8 passed, 0 failed, 0 skipped |
| Real MySQL chest DAO checks | 50 / 50 passed |
| Required secret scan | Passed using the documented CPython wrapper |

Both broad-cohort failures are already documented on feature base 5cc8a4f95: DeleteRetiresCorrectInstanceBeforeAcknowledgingAndDuplicateIsNoOp and UnsupportedOnUseAloneDoesNotCountAsExecuted. Their test and inventory mutation source bytes are unchanged by this task. They are reported as known feature-baseline exceptions, not suppressed or represented as a clean overall pass. The full item-catalog loot smoke, captured chest spawn/open/take protocol fixtures, persistence, retry, reconstruction and chest ownership checks passed. Real MySQL checks include concurrent initialization, concurrent two-player loot with one winner, lost commit acknowledgements, restart ownership and retention.

## Connected cave acceptance

**PASS on the final bounded-recovery binary.** A genuine TCP-accepted cave, style 341 / one floor / seed 1424198395, had 17 cells, one eligible room, one requested chest and one candidate. The chest naturally held three items. Placement seed: 1372756057.

| Phase | Requested / generated | Persisted | Instantiated on load | Expected active | Unique TCP chest spawns |
| --- | --- | ---: | ---: | ---: | ---: |
| Entry | 1 / 1 | 1 | 1 | 1 | 1 |
| Authenticated reconnect after one item taken | Frozen 1 / 1 | 1 | 1 | 1 | 1 |
| Distinct ZoneEngine process reconstruction | Frozen 1 / 1 | 1 | 1 | 1 | 1 |

Both separately authenticated accounts entered using the original key and its current-protocol copied/traded counterpart. Opening, first take, partial close/reopen, durable two-item remainder, logout/reconnect, distinct-process reconstruction, second-player opener handoff, stale duplicate rejection, final empty reopen and retained player inventory passed. Every saved chest identity had to reach the synthetic client; a generated/instantiated population with zero initial publication fails this gate. The initial and restarted Zone binaries have the same SHA-256: 5e5d56eb68834081f1868295ac67c493347b51173378ba1b7946aa4805382aa7. The Login binary SHA-256 is ef7f1065fb8849792dabc8ad115996b55e5ef59e7ddb7fda22ef92914a79395f. The private tested-source manifest binds the validated working-tree C# bytes to these builds. All test-owned processes and labelled MySQL/network resources were cleaned successfully.

The final fixture uses a schema-only snapshot and existing allocator/ledger metadata in its own newly created database; MIGRATIONS_EXECUTED=0. Setup creates only fixture accounts/characters. After startup, mutations use the existing network protocol; SQL/DAO reads verify durability. Its twelve-acceptance cave-selection bound is unchanged. Empty looted chests retain the existing live-world retirement behavior: only a positively observed exhausted identity can be excluded from same-process reconnect publication; entry and fresh reconstruction require every saved identity. The final three-item run exercised full partial-loot coverage; its single-item retirement branch was not exercised in that final run.

Indoor test positioning uses existing direct-control packets. This verifies server/DAO/wire behavior, not original-client rendering or player walking. Multi-floor acceptance is covered by the production population/locality matrix; the current ordinary TCP acceptance path configures solo missions with one floor, and no invented team hook was added.

## Files changed

- Core/Quests/Dungeons/MissionChestPolicy.cs, MissionChestPlacement.cs and MissionChestPopulation.cs: minimum, bounded recovery, diagnostics and initialization result.
- Core/Playfield/QuestDungeonPlayfield.cs and SpawnService.cs: existing admission retry and visibility-safe failure handling.
- Core/Inventory/Dat/DatItemTemplate.cs: exact accepted-master format model.
- MissionChestLootTests.cs, MissionChestPlacementTests.cs, MissionChestPopulationTests.cs and MissionChestProtocolTests.cs: bounded actual-layout, durability and publication checks; reuse the existing byte serializer helper.
- Tools/ZoneEngineSchemaValidation/ConnectedMissionChestAcceptance.cs, ConnectedEngineProcess.cs, ConnectedWireClient.cs and Program.cs: cave/two-account acceptance, counts, empty-retirement handling, decode diagnostics and migration-free disposable schema import.
- docs/ai/WORKFLOW.md and this report/statistics CSV: scoped instructions and results.

## Environment protection and execution corrections

The running player servers, character/inventory/mission rows, private GameData, accepted rollback build and backups were not changed by the task. Builds use separate output directories; test processes and labelled databases are owned and cleaned by the existing fixtures. The branch remains unmerged and is not deployed. The running local master still lacks this chest feature.

Initial native environment/argument forwarding and two newly edited test-tool compile errors were corrected before validation. A malformed Windows rg glob, an agent literal-regex search and an agent CMD environment-capture command were execution errors; targeted rg directory/-g or fixed-string forms and native CMD argument handling corrected them. Read-only SQL and an initial binary-as-JSON inspection also failed without mutation and were corrected from the actual schema/reader source. These are agent execution corrections, not project blockers. A bounded TCP attempt found no cave in twelve acceptances; another reached two published chests but exposed the fixture's former multi-item-only assumption. That assumption is corrected to cover natural one-to-three-item contents. One intermediate run hit an unclassified decoder exception after materialization; its packet bytes were not retained, so its exact cause remains UNVERIFIED. Value-free packet/field-path diagnostics now preserve a future failure's context without skipping or changing protocol messages.

No AO client or capture tool was launched or controlled. Original-client visual/walking acceptance remains UNVERIFIED and is not requested as a manual step by this task.
