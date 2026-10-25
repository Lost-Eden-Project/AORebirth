# Mission Chest Master Integration

Date: 2026-10-24 (America/Chicago); automated runs completed 2026-10-25 UTC.

## Result and exact provenance

**PROVEN: the chest implementation is integrated and published on the feature branch. Overall validation is PASS_WITH_PROVEN_BASELINE_EXCEPTIONS, with documented cave placement limitations. Deployment readiness is NOT ESTABLISHED.**

- Branch: `codex/mission-chests-master-integration`.
- Public destination, explicitly corrected by Mike: `https://github.com/subarumike/AORebirth`.
- Starting master, fetched master, and last supplied deployed baseline: `4f66c4c20d0e6f5f6e300082d81482944b028680`.
- Chest source: `72e2ee62a49ff0a72d953aa43fc9c99c376ace8f`.
- Final tested implementation/merge SHA: `752a0d1bdbb28a590fecca23002befa6080d9618`.
- Merge parents: the exact starting master and chest source above. A report-only follow-up records this result; it does not change the tested implementation. The completion message records that documentation commit and the verified final remote tip.
- Integration implementation push: successful; the remote feature ref was independently read back at the exact implementation SHA.
- Public master and the original chest branch were preserved. No merge into master, deployment, database migration, AO client control, or live capture occurred.

All five unique chest commits are ancestors of the integration:

| Commit | Integrated scope |
| --- | --- |
| `a0f0751bfae7aadc6bbb99396a0e41c8c025b735` | Initial durable chest implementation |
| `c1b12373035a8ea0e2a25244e2fda1a228ba2990` | Full Windows project/source wiring |
| `d0d94b5f039168ae69964f0fd8d5a994e6a2c83c` | Wall-adjacent room placement |
| `5cc8a4f95ed508ac9013f41983e7a2822174c510` | Placement correction and lifecycle validation |
| `72e2ee62a49ff0a72d953aa43fc9c99c376ace8f` | Final cave/publication validation changes |

Commit-scope labels above summarize the integrated series; behavior classifications below rely on inspected source and actual runs, not commit titles. The typed mission content branch was excluded.

## Integrated behavior and important source paths

| Area | Source and preserved behavior |
| --- | --- |
| Placement | `AORebirth/Server/ZoneEngine_New/Core/Quests/Dungeons/MissionChestPlacement.cs`: existing generated room metadata, collision geometry, wall candidates, floor support, chest/approach clearance and reachable route witnesses; bounded recovery; unsuitable candidates rejected. One chest per selected eligible room, room/floor separation preferences and randomized wall positions remain intact. No replacement navigation system or dungeon generator. |
| Configuration and loot | `AORebirth/GameData/DungeonGeneration.json`, `MissionChestPolicy.cs`, `MissionChestLootService.cs`: density 0.3, variation 0.2, 1–3 items, mission QL ±5 eligibility, valid generated item QL, two-stage rarity-group/item selection. Weights remain Missing=70, 1=20, 2=6, 3=2.5, 4=1, 5=0.5. The complete Chests object matches the feature source; all other dungeon configuration matches master. |
| Population and reconstruction | `MissionChestPopulation.cs`, `Core/Quests/QuestService.Chests.cs`, `Core/Playfield/QuestDungeonPlayfield.cs`, `SpawnService.cs`, `PlayfieldManager.cs`: freeze identities, transforms, appearance and contents once; read durable seals before generation policy; preserve saved empty populations; defer when prerequisites are absent; never relocate/remint an initialized population. |
| DAO | `AORebirth/Libraries/Source/AORebirth.Database/Domain/Missions/MySqlMissionDao.Chests.cs`, `Domain/Quests/MySqlCharacterQuestDao.cs`, `Domain/Characters/MySqlCharacterPersistenceDao.Inventory.cs`, `MySqlCharacterPersistenceDao.Transactions.cs`, and `AORebirth.Interfaces/Persistence/Missions/IMissionChestPopulationTransaction.cs`: use existing mission flags, item rows and transaction boundaries. No schema change or migration was added. |
| Transfers and ownership | `Core/Entities/Chest.cs`, `LootableDynel.cs`, `Core/Inventory/InventoryActionService.cs`, `InventoryMoveService.cs`, `Core/Data/MySqlInventoryMutationPersistence.cs`: durable source validation and atomic transfer before publication; one opener; reject non-opener, stale/duplicate/other-owner takes. Master Unique checks, overflow checks, unopened-bag hydration, equipment validation and container retirement locks were retained. |
| Protocol | `Chest.cs`, `ChestSpawnDefinition.cs`, `MissionChestProtocolTests.cs`: original chest-family spawn contract and separate contents/take messages retained. Byte fixture and parented-container control pass. The integration-specific fix uses `Playfield.ClientPlayfieldId` so chests obey master's proxy-playfield protocol contract. No packet field or appearance was invented. |
| Routing/lifecycle/objectives | `Core/Quests/Dungeons/QuestDungeonService.cs`: retain master WorldEngine ownership/remote routing, login reconstruction, relocation, objective bindings and cleanup/broadcast paths; add frozen population context at both entry and login construction sites. Current NPC/door registration and visibility code was retained. |
| Build inventories | Database/Interfaces project files, `SharedBuild/source-inventory/*.CompileItems.props`, `ZoneEngine_New.csproj`, and `WindowsBuildNet10/Projects/AORebirth.Interfaces.ZoneNew.WinNet10.csproj`: include chest implementation without restoring retired source owners. |

Room eligibility excludes passages/transitions using the existing policy and generated geometry; wall placement and collision checks remain prerequisites. Target density is not permission to force a position. Floor labels in the statistics are signed generator floors.

## Conflict and fixture reconciliation

1. Character inventory DAO imports: retain master `System.Text` for batched bag hydration and chest `System.Linq` for expected-source validation; no older inventory implementation replaced master.
2. `QuestDungeonService`: combine chest population arguments with both current WorldEngine-aware construction paths; retain frozen accepted JSON and retire populations through current cleanup.
3. `Tools/ZoneEngineSchemaValidation/Program.cs`: retain master's login-character-creation cohort and add chest/lifecycle modes without changing default runtime behavior.
4. `docs/ai/CURRENT_TASK.md`: describe this integration; resolve `PROJECT_STATE.md` to committed master rather than importing stale feature state.
5. Repair a clean-merge XML item-group boundary in the focused test project and update its opt-in compile list. Default test inventory was not pruned.
6. Adapt test fakes to current APIs: destination GetItem handler receives the existing action service; reward DAO supplies an authoritative empty population snapshot; authored DAO snapshots clone transactional state. Existing reward assertions remain intact. Old narrow destination/reward fixtures disable unrelated generation policies in memory; the new positive coexistence tests explicitly enable chest generation for all five types.
7. New objective tests use the runtime's exact NPC floor snapping and publish through existing playfield actor dispatch. Initial incorrect height/racing assertions were corrected in tests only; production floor/locality code was not changed to satisfy them.

## Windows build and execution boundaries

**PROVEN: all four Windows engine projects built successfully**, using the documented Windows project graph and standard separate MSBuild `OutDir` outputs. No running build directory was overwritten. The ordinary launcher has a global active-engine stop guard, so it was not executed in a mode that could stop existing servers. Its identify-only invocation stopped no process.

Projects: `AORebirth/Server/ZoneEngine_New/ZoneEngine_New.csproj`, `WindowsBuildNet10/Projects/LoginEngine.WinNet10.csproj`, `WindowsBuildNet10/Projects/ChatEngine.WinNet10.csproj`, and `AORebirth/Server/WorldEngine/WorldEngine.csproj`. Builds used `/restore /t:Build /p:Configuration=Debug /m:1 /nr:false` and isolated output paths. Logs: `windows-zone.log`, `windows-login.log`, `windows-chat-world.log`.

| Tested binary | SHA256 |
| --- | --- |
| ZoneEngine_New.dll | `57fa5022287164e7eafaa929904495bafa601789dcc30252aee474792e8e890e` |
| LoginEngine.dll | `0dda807d8a321ad451b65cfff9a41d11bc60aafd1195a6076a0b6991b6a9a84d` |
| WorldEngine.dll | `98646d7a4bd8af9fbd5549c8796a8fb1637b8b86df05e91fccf9193097d4e4bf` |
| Final connected acceptance tool | `d27c21e0cb40decb87bbcdb9d06f99fdcabfe5f7fc86cbb0686fc909fdad611a` |

Builds emitted existing dependency/obsolete/nullability warnings; successful build does not assert a warning-free or security-audited dependency graph. Linux build/package/deployment: **NOT RUN**.

All log/result references below are relative to ignored `build-verify/mission-chests-master-integration/` in the integration worktree. Private schema/frozen-input artifacts, logs, binaries and credentials are not committed. The tested-file manifest is `tested-source-manifest.json`, SHA256 `bce192b1b14592c41a82481b9cabad895e78546e87521234349934df8f22ffa8`.

Validation processes had explicit overall bounds (360/480/900 seconds according to cohort), per-test hang bounds and exact-owned-PID process-tree cleanup. No unlimited retry loop was used. Initial native-child environment failures were resolved by restoring normal Windows profile/ProgramFiles/PATH variables, without changing repository build architecture. One agent restore-only diagnostic incorrectly ran under PowerShell (exit 0, no fixture/server/database interaction); subsequent execution used CMD. This workflow deviation is recorded rather than hidden.

## Focused automated results

Final disjoint cohorts executed **128 tests: 126 passed and 2 failed**. Both failures are independently reproduced on exact starting master; this is not a plain PASS.

| Final core class | Passed | Failed |
| --- | ---: | ---: |
| MissionChestProtocolTests | 12 | 0 |
| MissionChestLootTests | 20 | 0 |
| MissionChestPersistenceTests | 11 | 0 |
| MissionChestPopulationTests (saved/deferred subset) | 17 | 0 |
| InventoryActionTests | 23 | 2 |
| PlayerInventoryOverflowTests | 13 | 0 |
| FindItemRewardTests (selected reward/XP/overflow/fault cases) | 10 | 0 |
| ExactSpawnVisibilityTests | 7 | 0 |
| ReconnectVisibilityTests | 1 | 0 |
| **Core total** | **114** | **2** |
| Positive five-type coexistence and objective actions | 8 | 0 |
| Critical real cave layouts | 4 | 0 |

Core evidence: `focused-core-final.log`, `core-final-results/core-final.trx`. Objective evidence: `focused-objectives-final.log`, `objective-final-results/objectives-final.trx`. Cave evidence: `focused-critical-caves.log`, `critical-cave-results/critical-caves.trx`.

Coverage includes real-body spawn byte equality and proxy ID, 40,000 rarity-group draws, 1–3 items/fresh identities/QL bounds, full inventory, Unique already held in main/overflow/unopened bag, commit-before-ack, duplicate/stale claims, unknown commit quarantine, frozen remaining contents, saved empty populations and deferred initialization. Reward cases check exact objective/reward identity, overflow delivery, recoverable missing template, uncertain/committed packet faults, and existing XP semantics. These are separate assertions; unit/in-memory fixtures are not described as real MySQL.

### Exact-master failures and blocked/incomplete validation

- **PROVEN BASELINE FAILURE:** `InventoryActionTests.DeleteRetiresCorrectInstanceBeforeAcknowledgingAndDuplicateIsNoOp` and `UnsupportedOnUseAloneDoesNotCountAsExecuted`. Exact `4f66c4c20d0e6f5f6e300082d81482944b028680` run: 23 passed, same 2 failed. Evidence: `baseline-inventory.log`, `baseline-inventory-results/baseline-inventory.trx`. No implementation/assertion was changed to hide them.
- **BLOCKED BY PROVEN BASELINE COMPILE ERRORS:** the default/full test project and `PlayfieldTransferTests` refer to removed `ZoneHandoffTicket`, `ZoneHandoffStore` and `ZoneHandoffClaim` APIs (plus other obsolete team/handoff fixture uses). Exact-master reproduction: `baseline-transfer-compile.log`; integrated compile showed the same transfer errors. These tests were excluded only from the opt-in chest cohort; obsolete runtime APIs were not restored.
- The exact-master mission-destination focused build also exposed its old parameterless GetItem handler fixture (`baseline-mission-compile.log`); the integration repairs that test construction, not production behavior.
- An initial broad expanded run hit its 720-second outer bound and its owned process tree was terminated. `focused-integrated-r3.log` has no completed TRX and is **INCOMPLETE**, not PASS. Final bounded cohorts above replace any assumption that this broad run completed. The complete historical layout matrix/default suite was not rerun.
- Earlier core execution had 6 reward fixture exceptions from unsupported seal reads; the small DAO fake change fixed them and final reward cases all pass. Initial strict objective execution was 5/8 before the exact floor/actor fixture corrections; final strict execution is 8/8.

## Real generated cave results

These use the private full dataset through the existing GameData selector and actual generated geometry, not only rectangular fixtures. Runtime population cases verify generation, durable snapshot, world registration and real locality delivery to a test session as distinct stages. Their transaction implementation is the existing in-memory test DAO; real MySQL is covered separately below.

| Style / seed / floors / size | Total layout cells | Eligible rooms | Requested | Placed / rooms with chests | G / P / I / session publication | Wall min / mean / max | Chest floor distribution |
| --- | ---: | ---: | ---: | --- | --- | --- | --- |
| 341 / 1775745370 / 1 / 12, QL 125 | 20 | 8 | 3 | 3 / 3 | 3 / 3 / 3 / 3 | 0.6913981 / 0.699948 / 0.70569575 | 0:3 |
| 341 / 42 / 5 / 10, QL 125 | 67 | 17 | 5 | 5 / 5 | 5 / 5 / 5 / 5 | 0.6710846 / 0.6846411 / 0.69585216 | 0:1, -1:1, -2:2, -3:1 |
| 341 / 34928853 / 2 / 10 | 17 | 2 | 1 | 0 / 0 | 0 / not exercised / not exercised / not exercised | N/A | none |
| 341 / 82433732 / 5 / 12 | 65 | 24 | 8 | 0 / 0 | 0 / not exercised / not exercised / not exercised | N/A | none |

G/P/I are generated/persisted/instantiated. Total cells includes passages/transitions and is not the eligible-room denominator. Wall values are test-reported distances in engine units. Positive cases assert distinct rooms, valid walls/collision/support/approach/reachability and unchanged saved reconstruction. The five-floor case explicitly requires positive populations on more than one floor. It does not require a chest on an unsuitable floor.

The original player seed was reconstructed from the supplied frozen parameters and asserted as style341/floors1/size12/QL125. Its placement seed is 602659656 for this test mission identity/run, producing 19 candidates and 3 selected positions. Chest count/positions need not match a different newly initialized mission identity. Existing saved populations are never rerolled.

**Remaining cave limitation, OBSERVED:** with one placement roll at placementSeed7919 and the existing four bounded internal passes, seed34928853 has zero candidates; seed82433732 has 49 geometric candidates but zero validated reachable candidates. Both remain zero. The tests pass because they validate/report safe rejection and route preservation, not because density was achieved. Physical impossibility is UNVERIFIED; these results do not prove that a more complete search could never find a safe position. No unsafe fallback or density force was introduced.

### Positive coexistence with every mission type

All five actual acceptance/reconstruction cases use a test-local existing style324/one-floor/size12 profile, enabled chests and current objective/token plumbing. Each requires a positive population and compares frozen layout, exact chest spawn bodies and item counts after reconstruction. Find Person, Repair and Return actions additionally reject wrong owners/targets and preserve their completion/consumption rules.

| Mission type ID | Accepted seed | Requested | Generated / persisted / instantiated / session-published |
| --- | ---: | ---: | --- |
| 0 | 1329818276 | 2 | 2 / 2 / 2 / 2 |
| 1 | 1161270034 | 4 | 4 / 4 / 4 / 4 |
| 2 | 710871956 | 3 | 3 / 3 / 3 / 3 |
| 3 | 865189345 | 3 | 3 / 3 / 3 / 3 |
| 4 | 1112836236 | 3 | 3 / 3 / 3 / 3 |

These five logs emit population counts, not separate wall-distance statistics. The critical geometry table above records the measured wall/floor statistics. Source: `AORebirth/Server/ZoneEngine_New.Tests/MissionChestObjectiveIntegrationTests.cs`.

## Real isolated MySQL

`Tools/run_mission_dao_validation.cmd --chests-only` completed with `MISSION_CHEST_MYSQL=PASS`, `MISSION_CHEST_CHECKS=50` (`mysql-chests-run.log`). It used only its owned disposable MySQL fixture, not player databases.

`Tools/MissionDaoValidation/ChestPopulationChecks.cs` covers exact owner/run/frozen-layout fencing, atomic seal+item rollback, concurrent single initialization, ambiguous COMMIT readback, original-instance transfer, occupied-target rollback, concurrent two-player single-winner claims, committed/lost-ack reload and cleanup that retains carried rows. Disposable DAO container/network/volume were subsequently absent. No database migration was run.

## Real connected TCP acceptance

Final frozen-tool run: `connected-chests-source-final.log`, exit 0, no timeout, successful disposable cleanup. Result: **PASS_WITH_PROVEN_BASELINE_EXCEPTIONS**.

The existing fixture now owns a real WorldEngine on a unique loopback endpoint, with a fixture-only token and disabled status listener. It starts owned World/Login/Zone and imports a verified empty current-schema snapshot only into its new labelled database. The log records `MIGRATIONS_EXECUTED=0`. Post-start gameplay writes occur through network clients; SQL checks are read-only. Normal running services and database credentials are not used.

- Acknowledged roll/accept through current mission code found style341 on bounded attempt6: seed1523721924, one floor, questD2086989733.
- Entry, authenticated reconnect and restarted reconstruction each independently reported: 16 cells, 5 eligible rooms, requested2, generated2, persisted2, instantiated2, published-unique2.
- Exact key copy/trade across two accounts, World-assigned entrance, chest open, partial take/reopen, authenticated reconnect, empty reopen and retained carried items all passed.
- Zone PID65636 restarted as distinct PID44404 with the exact same Zone binary hash above; saved identities/transforms/contents stayed frozen.
- Two actual TCP players passed non-opener rejection, opener handoff, duplicate-take rejection and peer looting of the same selected chest.
- Login and World stopped with exit0/no shutdown markers. Final container/network residue was NONE; cleanup PASS.

The fixture sends at most one additional real entrance Use after positively observing its owned World assignment and waiting beyond the existing use interval; it still requires real ACK/teleport/redirection messages. The exact internal explanation for the first-use timing failure is INFERRED, not claimed as a proven runtime root cause. No lease/teleport was faked in SQL and no runtime routing bypass was added.

### Proven inherited shutdown outcome

Exact-master Zone/Login/World were independently built (`windows-exact-baseline.log`). The strict `--lifecycle-only` mode starts them on its owned empty fixture and stops before gameplay. Exact4f66 Zone hash `d02785434036b42cc2f9d55caea7ff90ad9de45745f7f63d575b1c8e847ae2f5` exited3 with ordered types `NotSupportedException,ObjectDisposedException`; cleanup passed and the strict fixture correctly failed. Evidence: `baseline-world-lifecycle.log`.

Both integrated Zone stops reproduced the identical exit3/ordered signature. A parent-fixture-only explicit opt-in (`AO_REBIRTH_CHEST_ALLOW_PROVEN_SHUTDOWN_BASELINE=1`) permits exactly this Zone outcome so restart assertions can execute. It is cleared before engine child environments, does not alter runtime shutdown, and emits the qualified result. Other roles/exits/signatures remain failures; default and lifecycle modes remain strict. `ConnectedEngineProcess.cs` and `ConnectedMissionChestAcceptance.cs` preserve uncaught gameplay/cleanup failures. The exact shutdown mechanism remains UNVERIFIED because current runtime logs expose types rather than stacks.

## Reproduction and scope limits

Use the documented wrappers and full private GameData selector. The empty schema snapshot and original frozen-layout input remain private; supply their existing environment variables rather than placing them in Git.

- Focused project: `AORebirth/Server/ZoneEngine_New.Tests/ZoneEngine_New.Tests.csproj`, property `MissionChestFocusedTests=true`.
- Core selects protocol, loot, persistence, inventory actions/overflow, exact/reconnect visibility, selected FindItemReward cases and saved/deferred population cases; exact command is retained in ignored `execution-state.json`.
- Objective filter: `TestCategory=MissionChestObjectiveIntegration`.
- Critical cave filter: `TestCategory=MissionChestCriticalCaves`; supply `AO_REBIRTH_CHEST_FROZEN_LAYOUT_PATH`.
- DAO: `Tools/run_mission_dao_validation.cmd --chests-only`.
- TCP: `Tools/run_newengine_connected_acceptance.cmd --engine <Zone.dll> --login-engine <Login.dll> --chests-only --world-engine <World.dll>`; supply `AO_REBIRTH_CHEST_FIXTURE_SCHEMA_PATH` and explicit proven-baseline opt-in only when comparing this established failure.
- Exact-master inventory reproduction uses `FriendlyItemUseFocusedTests=true`, filter `FullyQualifiedName~InventoryActionTests`.

**UNVERIFIED / not exercised:** original AO client visuals, player walking and official-client gameplay acceptance; cross-Zone multi-process transfer; a dedicated closed-door visibility/occlusion acceptance; the obsolete transfer suite; full historical layout matrix; Linux builds/packages. The TCP fixture uses direct-control packets for indoor positioning. Shared exact-spawn/reconnect tests and preserved door source do not constitute a new end-to-end door acceptance.

Direct persisted-chest deletion, a delayed equipment take whose opener/range changes during the delay, and a lost COMMIT acknowledgement injected end-to-end through TCP lack dedicated public-path coverage here. Related retirement/delayed guards were source-reviewed and uncertain-commit behavior is tested separately at DAO/runtime-action levels. These distinctions must not be collapsed into an unsupported full-system PASS.

## Preservation, publication and readiness

PROVEN preservation checks: protected master/chest refs unchanged; all 15 stash SHAs unchanged; 12 recorded pre-existing dirty-file hashes unchanged; the original untracked local audit text unchanged. The original master and chest worktrees, local configuration and earlier validation artifacts were preserved. No private GameData write, production connection, running-server restart or AO client/capture action was performed.

The reviewed implementation contains 66 changed/new source, test, public-policy and documentation paths. The complete incoming commit series plus final diff were checked before public push; no private GameData, configuration, SQL dumps, credentials, raw capture stores, binaries or validation logs were included. No migration/SqlTables change is present.

The feature branch is ready for review and Mike's separately performed Windows client acceptance. **It is not declared deployment-ready.** Cave zero-placement cases, two proven master test failures and inherited shutdown error remain explicit. Linux and original-client acceptance are absent. Any deployment or merge into master requires a separate instruction; neither was performed.
