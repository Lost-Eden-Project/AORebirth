# Final mission chest master integration

Completed 2026-10-25. **PUBLIC MASTER INTEGRATED — WINDOWS VALIDATED WITH PROVEN BASELINE EXCEPTIONS. LINUX PREPARATION NOT VALIDATED.** No deployment, original-client operation, player-database modification or migration was performed.

## Commit identities and ancestry

| Identity | Exact SHA |
| --- | --- |
| Starting public master | `4f66c4c20d0e6f5f6e300082d81482944b028680` |
| Complete chest integration | `2b339363d3aab9eea34fa2bb9b6941357ec558a0` |
| Validated cave implementation | `152a5d2cac40e955b1d085363677477c8140ad0b` |
| Final tested public-master merge | `b63f02bc25761b59744d586d88403695117a8d82` |
| User-reported last deployed master | `4f66c4c2` — live services were not inspected or changed |

The merge has parents `4f66c4c20d0e6f5f6e300082d81482944b028680` and `2b339363d3aab9eea34fa2bb9b6941357ec558a0`. All five original chest commits are retained as ancestors: `a0f0751bf`, `c1b123730`, `d0d94b5f0`, `5cc8a4f95`, `72e2ee62a`. The final `152a5d2c` correction and documentation tip `2b339363` are included. Separate typed-content commit `ccc26fe681abf8aa7c6331201c939c5608776469` is not an ancestor and its implementation was not integrated.

Fetch and ancestry checks found zero master-only commits and nine integration-only commits. There was no newer master functionality to reconcile. The initial merged tree exactly matched the validated integration tree. A no-fast-forward merge preserves the complete chest history. This report and status receipts follow as a documentation-only child; they do not change tested product source. The final report-delivery tip is reported in the task completion message.

## Root cause and merge resolutions

**PROVEN:** No Git merge conflict occurred. The only additional test change corrects a stale reflection adapter exposed by the expanded NPC validation. Four `ActualMissionWorldRegistersConfiguredOneShotPopulationAndSpawnsCatalogNpcStats` cases passed one argument to `HashSpawnSystem.TrySpawn(HashSpawnPoint, bool skipRespawnChance=false)`, causing `TargetParameterCountException` before exercising NPC spawning.

The four failures reproduced on the untouched exact starting integration `2b339363`. The runtime method and its two-argument contract are byte-identical between starting master and integration; this was not treated as an unexplained new baseline exception. `QuestDungeonDestinationTests.cs:459` now passes `[point, true]`, matching its existing stated intent to skip only respawn chance while exercising actual registration. All gameplay, catalog stats and spawn assertions remain. The complete routing/NPC/door cohort then passed 79/79. No production spawning behavior was changed.

## Files inspected and changed

Inspected authority: `AGENTS.md`, `AI_START_HERE.md`, development authority, project state, decisions, subsystem/architecture, workflow, code standards, testing and regression guidance. Required evidence read: `MISSION_CHEST_MASTER_INTEGRATION.md`, `MISSION_CHEST_CAVE_PLACEMENT_FINAL.md`, and the pre-integration local `LOCAL_MISSION_DEVELOPMENT_AUDIT.md`. The last is historical inventory, not authority to import unrelated branches or to classify the corrected cave failures as still current.

Actual source review covered chest placement/population/loot, DAO transaction ownership, item move/use/delete integration, quest dungeon service and playfield construction, World routing branches, spawn initialization, protocol visibility, the NPC reflection contract and affected focused tests. Existing private Linux authority and build documentation were inspected read-only after public-master publication; private paths, source identities and infrastructure remain private.

The accepted 69-path feature delta is integrated intact. Core production paths include `AORebirth/Server/ZoneEngine_New/Core/Quests/Dungeons/MissionChestPlacement.cs`, `MissionChestPopulation.cs`, `MissionChestLootService.cs`, `QuestDungeonService.cs`, `QuestDungeonPlayfield.cs`, and the existing database/interfaces/inventory integration described in [the integration report](MISSION_CHEST_MASTER_INTEGRATION.md). No wholesale source replacement was used to resolve conflicts.

Additional changes beyond that feature tip:
- `AORebirth/Server/ZoneEngine_New.Tests/QuestDungeonDestinationTests.cs`: one-line reflection argument correction.
- `docs/ai/CURRENT_TASK.md`: active-task completion receipt.
- `docs/project/PROJECT_STATE.md`: public-master chest status.
- `docs/reports/MISSION_CHEST_MASTER_MERGE_FINAL.md`: this report.

No schema/SQL migration, private GameData, credentials, database contents, build artifact, private Linux source/infrastructure or unrelated gameplay change was added. WorldHostedElsewhere routing and both remote-host paths remain; objective/reward source such as QuestJournal and generated rolling remains unchanged where it was not part of the chest integration. Chest-specific inventory changes retain expected-source ownership and Unique/overflow gates. Frozen mission layouts and sealed chest populations are retained, including empty/exhausted populations.

## Windows build and regression evidence

All checks ran from an isolated owning-product merge worktree. The established documented native MSBuild project commands used `/restore /t:Build /p:Configuration=Debug /m:1 /nr:false /v:minimal` and separate OutDir values. The ordinary wrapper would stop globally identified engines through ACTIVE_CHECKOUT_WINS; the isolated build form preserved the explicit instruction to leave running servers unchanged.

| Validation | Final result |
| --- | --- |
| ZoneEngine_New, LoginEngine, ChatEngine, WorldEngine Windows builds | PASS, all four |
| Actual cave campaign | PASS, 104/104 |
| Targeted cohort | PASS, 16/16: eight positive placement rolls, two critical diagnostic cases, two critical population cases, four detailed diagnostics |
| Safety/all-five-objective/captured-cell cohort | PASS, 23/23 |
| Protocol, loot, population, persistence, inventory, Unique, bank transfer, overflow, reward and visibility/reconnect core | 115/117; two proven preexisting inventory failures |
| Dungeon destinations, generated NPCs/catalog stats, doors, transitions and procedural foundation | PASS, 79/79 after fixture correction |
| Isolated real-MySQL production-source chest validation | PASS, 50 checks |
| Connected TCP chest acceptance | PASS_WITH_PROVEN_BASELINE_EXCEPTIONS; cleanup PASS |

Tests ran against the pending merged product tree; commit identity is recorded after validation. The fixture logs can identify the first-parent HEAD while the merge is pending; they do not mean unmodified `4f66c4c2` was tested as the new chest implementation. The validated source tree, exact commands, result files and binary identities are retained under ignored `build-verify/mission-chests-master-merge-final-20261025`. The committed merge retains that product code unchanged. The later reflection change is test-only.

Final merged placement source SHA-256: `688d93b03e2913cd1ed41708a3986c01ad2a5ebe4b0d586bb40ae9a2ba302895`. Windows ZoneEngine_New.dll SHA-256 used for both connected processes: `5b44f0b58b0cf81a3fdbea2e3a1e597884fda59ca15f8a0eac13dbf2324c828e`. Other isolated Windows binary SHA-256 identities: Login `3f2db40c2644c66fba7fdc031a42c6da7753bd95112c53c8d25e211f8fc14051`; Chat `0f7792daae8ab886873b1c35f6d2833f309decda4d50a48220327e07ca31d7b3`; World `09c302198fe7e3934854b41105add9f2eac4443a03fb651e8a33ccf0b6e72ad6`.

Local evidence files:
- `windows-four-engines.log`
- `merged-targeted-results/merged-targeted.trx`
- `merged-caves-104-results/merged-caves-104.trx` and `merged-caves-104.jsonl`
- `merged-core-results/merged-core.trx`
- `merged-safety-objectives-results/merged-safety-objectives.trx`
- `merged-routing-npc-doors-fixed-results/merged-routing-npc-doors-fixed.trx`
- `npc-exact-integration-start-results/npc-exact-integration-start.trx`
- `merged-mysql.log`
- `merged-connected-tcp.log`
- `validated-merge-identity.json`, `campaign-summary.json` and the final execution/publication receipts.

The routing cohort uses existing tests and intentionally excludes only `EveryConfiguredProfileBoundaryBuildsConnectedLayoutsAcrossSixteenSeeds`, the separate broader profile-boundary matrix. It verifies actual generated-world doors/NPC registration and both directions of same-zone floor controls. It is not a full NPC combat, closed-door occlusion or multi-Zone cross-process travel acceptance. World-mediated mission entrance resolution and two-player access are exercised by TCP; no source change was made to invent a missing retired transfer API or force the historical unfiltered fixtures to compile.

## Cave results and remaining placement limits

**PROVEN:** 104 complete artifact rows, 104 unique actual generated layouts, 104/104 tests passed, zero positive-target empty populations and no timeout. Requested/placed counts match the prior validated campaign on every layout: **442 requested, 413 placed**, across 4,396 cells and 1,467 eligible rooms. Fourteen cases retain 29 total shortfalls; 90 meet their rolled target. Maximum case was 19.632 seconds against the 60-second case limit; outer cap was 40 minutes. No unbounded retry or forced coordinate was introduced.

| Required case | Eligible rooms | Requested | Placed | Floor distribution |
| --- | ---: | ---: | ---: | --- |
| 341 / seed34928853 / 2floors / size10 | 2 | 1 | 1 | 0:1 |
| 341 / seed82433732 / 5floors / size12 | 24 | 8 | 5 | 0:2, -1:3 |
| Original seed1775745370 / 1floor / size12 | 8 | 3 | 3 | 0:3 |
| Seed42 / 5floors / size10 | 17 | 6 | 6 | Four floors |

Every returned chest has a distinct per-mission room, actual-wall/collision/support validation, an independently replayed interaction approach and entrance route. All 413 approaches and 413 entrance routes validate. Required reachable anchor groups remain **7,735/7,735**. There are 93 lost peripheral samples; group preservation is not preservation of every optional anchor point. Wall center distances remain 0.66085356–0.7103574, sphere-edge gaps 0.05048555–0.099989414. Fifty-nine layouts place across multiple floors.

Per-layout counts and wall/floor details are in [the prior full table and CSV](MISSION_CHEST_CAVE_PLACEMENT_FINAL.md); the requested/placed pairs were independently compared against this merged run. New merged timing and rejection details remain in the ignored JSONL. No population-persistence claim is inferred from that geometry campaign: its rows explicitly say `Persistence=NOT_EXERCISED` and `Publication=ROUTE_WITNESSES_ONLY`.

The bounded planner does not prove every physically safe location is found. In the five-floor regression case it finds five of eight requested chests on the fixed roll. Layouts503763222,122465018,389134278 have eligible rooms on more than one floor but only one floor with reachable candidates/placements in the selected search. Full reachability completeness and original-client walking/interaction are unverified. Preserve safe rejection rather than weakening collision or route checks to fill a target.

## Persistence, multiplayer and baseline exceptions

The 50 isolated real-MySQL checks exercise durable initialization, rollback/no seal, concurrent initialization, frozen layout/key reload, expected-source transfer, occupied-target and ownership rejection, single-winner concurrent take, commit-outcome recovery and retained looted rows. No existing player database was used.

Connected acceptance imports an empty fixture schema (`MIGRATIONS_EXECUTED=0`), starts owned disposable World/Login/Zone processes and performs post-start writes through TCP. Entry, reconnect and distinct-process restart all report requested/generated/persisted/instantiated/published **1/1/1/1/1** for the accepted test mission. Opening, partial take/reopen, frozen restart population, empty reopen, player retention, two players, opener handoff, nonopener/duplicate rejection and peer loot passed. Both Zone process instances used the same merged binary hash. Container/network residue is NONE and disposable cleanup is PASS. These are synthetic test accounts, not production gameplay/client acceptance.

**PROVEN existing inventory failures:** `DeleteRetiresCorrectInstanceBeforeAcknowledgingAndDuplicateIsNoOp` and `UnsupportedOnUseAloneDoesNotCountAsExecuted`. The merged core run reports exactly the same two failures as retained starting-integration/master evidence. No inventory expectations or production behavior were altered to hide them.

**PROVEN existing shutdown exception:** owned Zone stop exits3 with ordered `NotSupportedException,ObjectDisposedException`, matching the retained exact-master/starting-integration baseline. The existing explicit chest-only baseline opt-in accepts only that known signature; other failures remain fatal. Login and World stop normally. TCP therefore reports `PASS_WITH_PROVEN_BASELINE_EXCEPTIONS`, not plain PASS. Graceful Zone shutdown is not claimed repaired.

## Linux preparation

**NOT VALIDATED — no native Linux build or release acceptance is claimed.** Linux preparation began only after public-master publication and verification of `PUBLIC_MASTER_SHA=b63f02bc25761b59744d586d88403695117a8d82`. The existing private role and its private-output/public-input boundary were verified read-only. Its source has not been reconciled to this new public merge, and its documented native isolated builder was not dispatched with an unreconciled candidate. No private Git push, private source/configuration change, deployment-tool execution or production connection was made.

The currently documented public development-only command `LinuxBuild\build-linux.cmd` was attempted in the isolated merged checkout. It failed before source compilation because `LinuxBuild/global.json` pins SDK **10.0.302** with `latestPatch`, while the available Windows SDK is **10.0.401**. No SDK installation or global.json/build-system change was made. This is an environment prerequisite failure, not evidence of a chest Linux source defect or a native Linux compile result.

Static review found no new chest-specific Windows API, OS conditional, P/Invoke, absolute-drive dependency or native package. Derived Linux source/inventory reconciliation must verify inclusion of the new transaction interface and DAO partial; the public Linux inventories omit them. Private wiring was not compiled in this task. Exact GameData filename case, editable chest policy and protected catalogs remain packaging prerequisites. Do not substitute the stale public source inventories or the older private source for an accepted current Linux candidate.

Linux status: `LINUX_SOURCE_SHA=NOT_CREATED_FOR_THIS_PUBLIC_SHA`; `PACKAGE_SOURCE_SHA=NOT_BUILT`; `PUBLIC_BASELINE_FAILURES` retain the Windows baseline evidence above; `PUBLIC_BASELINE_ARCHITECTURE_FINDINGS=NOT_EVALUATED_BY_THIS_PREPARATION`; `LINUX_PORT_FAILURES=NOT_EVALUATED`; `NEW_LINUX_FAILURES=UNKNOWN`. No Linux synchronization, zero-new-failures gate, package or deployment readiness is asserted. Private checkout identities, remotes, build/deployment details and artifacts remain outside this public report.

## Public GitHub verification and preservation

`git push origin HEAD:refs/heads/master` succeeded without force. `git ls-remote origin refs/heads/master` returned the exact tested merge SHA `b63f02bc25761b59744d586d88403695117a8d82`; ancestry verification proves it contains `2b339363d3aab9eea34fa2bb9b6941357ec558a0`. Remote master was fetched again before committing; no newer master had appeared. The original feature branch remains unchanged at2b339363. The separate typed-content branch was not merged.

Original working-state preservation uses verified private local backups and exact hashes. Configuration files and the five original untracked files remain in place; only the two dirty documents that overlap the final master update need temporary Git preservation. Their original bytes are restored unstaged after fast-forward, so these local working copies intentionally differ from the new committed status receipts. Existing 15 stash OIDs, other worktrees, private GameData, player databases, accepted binaries and running services are preserved. No private backup or ignored build output is staged for publication.

## Remaining deployment requirements

Public-master chest integration is complete and the requested Windows candidate checks are satisfied with the two inventory and shutdown exceptions above. Linux requires a separately reconciled exact-public-source candidate, its supported isolated native build/validation and packaging, private content/case verification, and explicit deployment authorization. This merge introduces no database migration; future normal deployment must use the then-current approved schema/readiness process. Mike performs any original-client acceptance. No master integration result here authorizes service restart or deployment.
