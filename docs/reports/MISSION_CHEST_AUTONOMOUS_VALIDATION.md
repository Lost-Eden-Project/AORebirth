# Autonomous mission chest validation and correction

2026-10-17. Windows feature branch `codex/mission-chests`, continuing from
`d0d94b5f039168ae69964f0fd8d5a994e6a2c83c`. No public-master merge or deployment.
Automated server/protocol results do not establish original-client acceptance.

## Proven causes and corrections

1. **Count denominator.** The planner rolled density over all generated cells,
   before excluding main halls, passages, entrance/start and floor-transition
   cells. It now applies the existing style/role/name/transition eligibility
   first and rolls once over actual eligible rooms. Density 0.3 and variation
   0.2 are unchanged. Eligible rooms remain distinct from physically safe capacity.
2. **Overconstrained doorway sampling.** The prior connectivity check required
   every peripheral standing sample around a door to survive. These samples
   are alternatives at one door side. The planner now preserves at least one
   reachable member of each initially reachable door-side group, while retaining
   individual requirements for spawn and supported platform landings. Existing
   door clearance, collision, floor support and chest approaches remain required.
   Real cave regressions recovered poses while losing a peripheral sample and
   preserving independently replayed routes to every required group.
3. **Repeated wall-section draws.** Sampling with replacement could repeatedly
   choose unsuitable wall sections within the bounded attempt budget. The first
   search retains the existing random policy. Only after a shortfall, a second
   bounded search visits wall sections in a length-weighted random permutation.
   It keeps the better complete validated population, never merges unchecked
   poses, rerolls the requested count or allocates extra persisted identities.
   A permutation-only trial regressed some layouts, which is why the original
   first search is retained.
4. **Unverified intermediate pocket support.** One complete 576-layout run
   produced 575 passes and one failed room-side support check. Exact replay of
   cave 341 / 4 floors / seed 68937720 / size 14 showed a projected intermediate
   sample at Y=133.07385 while own-room ground was Y=133; no source surface
   triangle matched the projected height within 0.03. This does not prove a
   physical hole or the cause of the query disagreement. Pocket endpoints were
   checked against room ground, but intermediate steps relied on global queries.
   They now require same-room ground throughout the pocket using the existing
   OwnFloor check. Cross-room graph walking is unchanged. The exact regression
   passes with 11/11 chests, three populated floors and all 129 access groups.
5. **Captured transfer acknowledgement.** Original ordinary chest take records
   1141/1142 and records 1105/1106 echo automatic bag marker 0x6F (111), while the
   runtime acknowledged resolved slot 64. Chest-source automatic transfers now
   echo the requested marker after committing ownership to the physical slot.
   Explicit slots, other sources and all persistence behavior are unchanged.

The planner continues to slice actual transformed collision walls, validate
own-room ground and inward interaction pockets, exclude passage/door/transition
areas, select at most one chest per room, and prefer floor balance and room
separation. Diagnostics are internal and optional; production does not emit
routes or consume capture files. No new generator or navigation system exists.

Saved populations are loaded before planning. Existing identities, poses,
appearance, contents, item QL and empty populations retain their durable values.
Loot policy, JSON rarity weights, mission QL +/-5, one to three items, database
schema and original spawn/open/container codecs are unchanged.

## Final real-layout corpus: PROVEN 576/576 passed

The existing generator and complete GameData produced 576 distinct primary
cases: 64 seeds per supported style/floor configuration, cycling each profile's
supported floor sizes. Repeated experimental runs are not counted as new layouts.
All 576 metric keys match 576 unique passing TRX results. See
[every layout's statistics](MISSION_CHEST_AUTONOMOUS_LAYOUTS.csv) for counts,
wall distances, occupied rooms, floor distribution, access groups and outcome.

| Style | Layouts | Generated cells | Eligible actual rooms | Requested chests | Placed chests / occupied rooms | Weighted coverage |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 321 | 64 | 1258 | 719 | 216 | 216 | 100.00% |
| 324 | 128 | 2457 | 1312 | 394 | 394 | 100.00% |
| 341 | 320 | 13316 | 4569 | 1373 | 1297 | 94.46% |
| 351 | 64 | 1534 | 1107 | 337 | 337 | 100.00% |
| Total | 576 | 18565 | 7707 | 2320 | 2244 | 96.72% |

Requests are 30.103% of eligible rooms; actual placements are 29.116%. The mean
per-layout coverage for positive targets is 97.612%; minimum/worst coverage is
0%. Styles 321, 324 and 351 have 100% coverage. Style 341 averages 95.683% per positive
layout and 94.465% weighted. Three zero-target layouts have blank CSV coverage
and are excluded from these per-layout averages.

Every placed chest passed wall adjacency, finite static-triangle overlap,
room membership, supported inward pocket, doorway/transition exclusions,
orientation, one-per-room, independent entrance-route replay and preservation
of required doorway/platform access groups. Origin-to-wall distance min /
chest-weighted mean / max is 0.659463 / 0.686708 / 0.711158 world units. Existing
floating-point tangency and floor tolerances remain; this is not an exhaustive
continuous-space navigation or collision proof.

Cave counts by requested floor configuration (64 layouts each):

| Floors | Requested | Placed | Weighted coverage |
| --- | ---: | ---: | ---: |
| 1 | 121 | 117 | 96.69% |
| 2 | 98 | 96 | 97.96% |
| 3 | 247 | 244 | 98.79% |
| 4 | 391 | 374 | 95.65% |
| 5 | 516 | 466 | 90.31% |

Every case met the spread requirement across its initially reachable candidate
floors when the requested count permitted it. There are 43 adjacent-room pairs
in 30 layouts (maximum three pairs in one layout), all caves. Separation is a
preference. Eighty-eight layouts omit at least one metadata-eligible floor
despite sufficient requested count; these omissions do not prove physical
inaccessibility. The CSV distinguishes metadata/actual count from sampled
reachability and records placed floors for each mission.

Thirty-five cave layouts have a target shortfall, including five positive-target
zero-placement cases. All 35 have fewer initially reachable candidate rooms
than requested. The largest deficit includes 341 / 5 floors / seed 82433732 /
size 12: 24 eligible rooms, 7 requested, 0 placed, 14 rooms with candidates but
none initially reachable in the selected bounded graph. Another example is
seed 84214125 / size 14: 12 requested, 5 placed. These are unresolved search or
geometry/connectivity limitations, not proven impossibility. No positions were
forced to improve these statistics.

Fallback ran in 58 layouts, won in 39 and added 68 chests over those runs'
primary populations. Compared with the earlier count-corrected control on 575
common completed metric cases, 67 layouts improve, none regress and 132 chests
are added. Cave coverage improves from 1165/1373 (84.85%) to 1297/1373 (94.46%);
shortfall layouts decrease from 78 to 35. This control already had the count
correction and is not untouched d0d source. Compared with the separate
permutation-only experiment, total placements improve by 47 with nine individual
regressions; the runtime guarantee is against its own primary pass, not another
RNG stream. The pocket correction changes no per-layout counts versus the
preceding 575-pass/1-fail run and removes its unverified pocket placement.

Observed Plan timing mean / p95 / max is 1160.875 / 4446 / 7279 ms. This includes
both searches when used and excludes generation, world construction and
independent test replay; it is not an isolated performance benchmark.
Diagnostics describe the selected complete pass, rather than summed rejection
counters for both passes.

| Investigation run | Completed test result |
| --- | --- |
| Count-corrected control | 556 passed / 20 failed: 19 spread assertions and one non-reproduced constructor exception |
| Permutation-only experiment | 576 passed / 0 failed; individual placement-count regressions motivated retaining the primary search |
| Initial best-of-two attempt | Host aborted; 97 recorded passes and 100 metrics, not a completed pass |
| Best-of-two before pocket correction | 575 passed / 1 failed; pocket case independently reproduced |
| Final pocket-corrected corpus | 576 passed / 0 failed |

## Investigation limits

A local-height wall-seed experiment produced zero accepted off-reference-height
poses across 80 diagnostic rolls and was reverted. No height-policy change is
claimed. For the five-floor cave seed 7, wall candidates exist below floor -2,
but none had a validated entrance route in the tested graphs. Six further rolls
with 8/16/32 candidates per room and 256 attempts retained this result; all 124
initially reachable access groups survived. This does not prove physical
impossibility and does not justify inventing a teleport or relaxing collision.

Bounded sampling can still return fewer chests. Reported candidate-room counts
are opportunities in a sampled graph, not proof that all candidates can safely
coexist. Adjacent selected rooms remain possible when safer alternatives are
exhausted. Automated inward-heading checks do not establish visual mesh facing.

## Interaction, persistence and protocol results

**PROVEN completed focused cohort:** 102 cases, 100 passed and two failed.
All 77 chest cases passed; inventory is 23 passed / 2 unchanged failures:
`DeleteRetiresCorrectInstanceBeforeAcknowledgingAndDuplicateIsNoOp` and
`UnsupportedOnUseAloneDoesNotCountAsExecuted`. Exact public-input reproduction
on `749f819fdf764ce0cc9debd17304607e71bbf363` is recorded in
[the integration report](../MISSION_CHEST_INTEGRATION.md) and
[the prior placement report](MISSION_CHEST_PLACEMENT_CORRECTION.md).
The unchanged inventory tests and unaffected delete/use production paths were
reviewed. Completed-cohort result: **PASS_WITH_PROVEN_PUBLIC_BASELINE_EXCEPTIONS**.
Four initial prerequisite-fixture failures were repaired by giving their
synthetic repaired world valid style/generator metadata; no production behavior
was changed to accommodate null test input.

**PROVEN disposable real MySQL:** 50 checks passed. Coverage includes offer
acceptance and key links, one-time chest materialization, uncertain commits,
fresh DAO loads, exact partial contents and player ownership, concurrent
population creation, parallel two-player claims with one winner, stale claim
rejection, failed-transfer rollback, retirement and retention cleanup. Focused
runtime tests additionally verify opener exclusion and handoff between two
players, with no unauthorized writes or acknowledgements.

**PROVEN synthetic TCP lifecycle:** the existing isolated connected fixture
performed actual LoginEngine authentication and character selection, terminal
roll and acceptance, mission-entrance use, dungeon entry, chest discovery/open,
three-item chest partial loot, close/reopen, authenticated reconnect, a distinct
Zone process restart, reconstruction of frozen identities/positions/remaining
items, retained original carried item and looted item, final loot and empty
reopen. Zone PIDs 49364 and 3300 used the identical runtime DLL SHA256
`0de6065e4138ba92279da13f59eda5ee3f67aaf0d3fe23ddd93c75a32a488ac2`.
All connected-lifecycle setup SQL finished before its engine processes started.
Subsequent mutations used TCP; DAO/SQL
assertions were read-only. Disposable process/container/network cleanup passed.

This was one TCP character. Multiplayer concurrency is proven by the focused
runtime and real-DAO tests, not by two simultaneous TCP clients. Direct-control
movement packets position the synthetic character; geometry routes are verified
separately. The fixture reports an explicit limitation if a natural population
contains no multi-item chest; it does not force loot. The old default connected
cohort retains its inherited removed-API compilation failures; the new current
chest path uses the existing opt-in focused-cohort mechanism.

**PROVEN protocol fixtures:** all 11 chest protocol cases passed. Exact original
world chest spawn bytes, ordinary open/close sequences, separate contents,
ordinary automatic takes and stack counts are revalidated using real handlers.
The automatic-take regression failed with expected111/actual64 before the scoped
acknowledgement fix and passed afterward. Durable physical slot ownership and
commit-before-ack ordering are asserted. Inventory contents remain separate from
ChestFullUpdate. No packet fields, appearance or backpack spawn substitution was
introduced. A separate pre-existing lockpick activation/ack difference was not
promoted into ordinary unlocked-chest behavior.

**UNVERIFIED original client:** no AO client or capture tools were launched or
controlled. Visual facing, rendering and original-client gameplay acceptance are
not claimed and were not requested from Mike during this autonomous task.

## Build and environment preservation

Windows ChatEngine, LoginEngine and final ZoneEngine_New project builds passed
using the existing SDK project graph, single-node MSBuild and separate standard
OutDir locations. The normal core wrapper stops running engines, so it could not
satisfy the explicit requirement to preserve this session's running environment.
No replacement build/lifecycle system was introduced. One candidate build was
stopped after targeting a loaded disposable-fixture output; it was rebuilt
successfully into a fresh output directory. The successful TCP run used that
fixed final binary.

Read-only status verifies the same running local engine PIDs and listeners as
before this task. Running engine DLL hashes, the retained database-backup hash,
rollback manifest and all 464 accepted rollback files are unchanged. No actual
player database connection or migration was used. Existing GameData was read
without changes. Candidate outputs remain separate; no deployment occurred.

The initial final-corpus attempt aborted with a fatal host AccessViolation after
97 recorded passes and 100 metric writes. An earlier count-only run also saw
one non-reproduced null-reference failure in a vector constructor. The fault
sites do not establish a production cause. Frozen runtime DLL identity and
independent hardlink checks excluded a current shared output hardlink for the
inspected assemblies; a JIT/runtime or dependency-memory fault remains
**UNVERIFIED**. The aborted artifacts are retained, not counted as a completed
pass. No speculative physics/vector/JIT change was made.

One trial statistics aggregation used a disallowed PowerShell analysis script.
It was removed and report statistics were regenerated with native Node tooling;
no test/build, service, database or product source action used that script.

## Representative original shortfall cases

These use the original floor size 10 and placement RNG seed 7919. Counts change
because actual rooms, rather than all cells, now determine the requested target.
Each placed chest occupies a different room. Negative floors descend; terminal
boss/transition cells are not ordinary eligible rooms.

| Style / floors / layout seed | Total cells | Eligible rooms | Old requested / placed | Corrected requested / placed | Wall distance min / mean / max | Chests by floor |
| --- | ---: | ---: | --- | --- | --- | --- |
| 341 / 5 / 42 | 67 | 17 | 23 / 7 | 6 / 6 | 0.67731 / 0.69108 / 0.70388 | -3:2, -2:1, -1:1, 0:2 |
| 341 / 5 / 1 | 65 | 14 | 22 / 5 | 5 / 5 | 0.66359 / 0.68258 / 0.69888 | -2:2, -1:1, 0:2 |
| 341 / 5 / 7 | 76 | 25 | 26 / 13 | 8 / 8 | 0.67727 / 0.69341 / 0.70891 | -2:4, -1:3, 0:1 |

All three corrected runs have zero adjacent selected-room pairs. Seed 42 covers
all four ordinary floors. Seeds 1 and 7 cover three; other tested search rolls
can reach an additional floor in seed 1. A single bounded graph is not complete
navigation knowledge, and the policy does not claim a global optimum for floor
coverage. Existing saved missions keep their original populations.

## Files inspected and changed

Inspected current authority/workflow/testing documents; QuestDungeonService,
DungeonLayoutGenerator, QuestDungeonPlayfield, placement/policy/population/loot
owners; generated style/role/transition metadata and real collision meshes;
Chest/LootableDynel, InventoryMoveService, PlayerInventory, world simulation,
movement and locality publication; MySqlMissionDao, MySqlCharacterQuestDao,
MySqlCharacterPersistenceDao and current fixture SQL; original packet evidence,
focused chest/inventory tests and existing DAO/connected helpers. GameData and
capture-derived evidence were read as inputs, not published as runtime corpora.

Changed product files (all under AORebirth/Server/ZoneEngine_New/Core):

- Quests/Dungeons/MissionChestPlacement.cs: eligible count, alternative doorway
  access groups, bounded fallback, intermediate pocket support and internal diagnostic witnesses.
- Quests/Dungeons/MissionChestPolicy.cs: count-basis documentation.
- Inventory/InventoryMoveService.cs: chest automatic-marker acknowledgement.

Changed tests and validation tooling:

- AORebirth/Server/ZoneEngine_New.Tests/MissionChestPlacementTests.cs
- AORebirth/Server/ZoneEngine_New.Tests/MissionChestPopulationTests.cs
- AORebirth/Server/ZoneEngine_New.Tests/MissionChestPersistenceTests.cs
- AORebirth/Server/ZoneEngine_New.Tests/MissionChestProtocolTests.cs
- Tools/MissionDaoValidation/ChestPopulationChecks.cs
- Tools/ZoneEngineSchemaValidation/ConnectedMissionChestAcceptance.cs (new)
- Tools/ZoneEngineSchemaValidation/ConnectedAcceptanceSmoke.cs
- Tools/ZoneEngineSchemaValidation/ConnectedCharacterPersistence.cs
- Tools/ZoneEngineSchemaValidation/ConnectedEngineProcess.cs
- Tools/ZoneEngineSchemaValidation/ConnectedWireClient.cs
- Tools/ZoneEngineSchemaValidation/Program.cs
- Tools/ZoneEngineSchemaValidation/ZoneEngineSchemaValidation.csproj
- Tools/run_newengine_connected_acceptance.cmd

Documentation: docs/ai/CURRENT_TASK.md, docs/ai/WORKFLOW.md,
docs/project/PROJECT_STATE.md, this report and the companion per-layout CSV.
No GameData, migration, production DAO, chest entity or codec file changed.

The requested validation used native CMD through existing wrappers/project
commands: focused dotnet test with MissionChestFocusedTests=true; the geometry
corpus and targeted/search categories; Tools/run_mission_dao_validation.cmd
--chests-only; Tools/run_newengine_connected_acceptance.cmd --chests-only with
explicit isolated engine paths; SDK project builds with /m:1 /nr:false and
absolute isolated OutDir; status-engines.cmd. Logs, TRX, diagnostic JSONL and
artifact hashes are retained under ignored build-verify/mission-chest-autonomous
and named build-verify/mission-chest-autonomous-* logs. Their SOURCE_SHA records
the starting HEAD; the corrections were uncommitted during execution. The
runtime DLL hash above identifies the exact tested product bytes.

## Publication and final checks

Publication target: origin/codex/mission-chests. The final handoff identifies the
verified commit and push. Public master and running servers remain unchanged.
Required secret scan and scoped diff/peer review passed.
