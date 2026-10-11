# Generated Find Item and quest reward integrity

Date: 2026-10-11. Base: `40caa10b881a93cbf0c9b6d5babac2a666122a96`.
Branch: `codex/find-item-reward-integrity`.

## Scope and evidence

OBSERVED: Mike could roll, accept, receive a key and enter a generated mission,
but could not find the requested objective item. PROVEN in the base source:
Find Item acceptance persisted no selected objective item, dungeon construction
spawned none, and the active runtime registered no ClientGetItem handler.
QuestService marked completion separately from best-effort reward delivery;
inventory capacity and missing content could lose an already completed reward.

Original completed capture packets and authoritative item references are
documented in [FIND_ITEM_PICKUP_EVIDENCE.md](../evidence/FIND_ITEM_PICKUP_EVIDENCE.md)
and its bounded wire fixture. Exact object identities, accepted journal action
fields and pickup acknowledgement are observed. Completing on successful
pickup, placement selection and objective-family selection are explicit
AORebirth gameplay policy; they are not claims about Funcom's unseen algorithms.

## Existing systems reused

- DungeonGeneration.json policy, HashItemMinter/ItemTemplates and the existing
  item builder resolve the objective. No new catalog or runtime capture reader.
- Existing generatedquests JSON freezes the exact objective instance, template
  pair, quality, owner, quest, room and coordinates during atomic acceptance.
- Existing room geometry, district spawn positions and door connectivity choose
  a point in a reachable room away from the entrance.
- WorldItem, DynelRegistry, locality visibility, ClientGetItem/ContainerAddItem
  codecs and main inventory provide the world-to-inventory transfer.
- Existing IMissionDao transaction ownership, item_instances, characterquests,
  missionrewardledger and stat mutation primitives provide durability.
- InventoryGrantPlan, InventoryFlushService and CharacterSnapshotService retain
  their existing authority and publication roles.
- Existing QuestJournal and QuestEnded/key retirement handle completion cleanup.

## Find Item lifecycle

New Find Item acceptance resolves a configured existing item hash and saves its
binding in the existing dungeon JSON before the quest/key transaction commits.
The initial policy references URSE at objective QL1, independently of mission QL
or advertised reward. This maps to the captured Urgent Sensitive Information
template in the authoritative catalog; content remains editable GameData.
Captured objective flags `0x80000003` and QuestInstance marker `1` are policy
fields frozen with the binding. Generic WorldItem flags remain unchanged.

The accepted journal identifies that exact objective instance. Dungeon creation
uses the persisted position and identity. Reconstruction checks durable owner
progress before spawning; a collected objective is never respawned as collectible.

Pickup validates the current live session, owner, active accepted quest,
registered world object, exact stored binding, dungeon and interaction range.
An inventory item with the same template, an unrelated world instance or another
player's objective cannot complete the mission. A full inventory leaves the
uncollected world object intact.

Collection inserts the exact objective inventory row and records full progress
plus a Pending ledger entry in one transaction. Only after commit does the
server publish the captured pickup acknowledgement and parented item update.
The objective receives no second AddTemplate. Visibility removal despawns it
for observers without destroying the collector's newly owned identity.

## Reward integrity and recovery

Earned progress remains Active with a pending reward until the complete reward
can be delivered. All configured reward items, cash, XP progression and mission
bit changes commit together with Completed state and an Applied ledger entry.
The ledger is keyed by owner, quest and assignment epoch, including repeatable
authored quests. Generated quests are not mirrored into authored missionstates.

Missing reward content or inventory capacity leaves the earned reward pending.
The existing player tick retries at five-second intervals; reconnect retries
from stored progress. Alternative reward selection is stable per assignment.
Earned pending rewards survive expiration and cannot be abandoned or purged.
Unearned expired/abandoned missions cannot collect or receive rewards.

Before projecting rewards, the existing character checkpoint writes current
live base stats without releasing online ownership. After payment, its existing
sequence bookkeeping invalidates older queued snapshots. This preserves XP/cash
earned while clearing the dungeon and prevents stale write-behind from undoing
committed rewards. Unknown commits and failed post-commit publication quarantine
the actor and require a fresh durable reload; they never speculate that a reward
failed and pay it again.

Quest XP keeps the existing cap, level table and level-up behavior. The reusable
reward plan projects the existing equipment, over-equipping, perk, buff and
timed-effect stat layers without mutating the actor before commit; publication
then uses the existing level-vitals path. Other direct-XP reward rules are unchanged.

Completed lifecycle cleanup runs before fallible reward packet publication and
is replayed from durable closed quests at reconnect. Cached dungeon layouts
cannot override the durable owner's closed state.

The accepted terminal reward definition remains independent of the collected
objective. No token amount, token eligibility rule, boss loot or regular loot
is invented. Items defined as rewards use the same atomic grant path. The
existing generated reward definition has no separate mission-token formula.

## Validation

PROVEN automated results on the candidate:

| Validation | Result |
| --- | --- |
| Approved core-engine build | PASS; existing dependency advisories and source warnings remain |
| Focused mission/dungeon suite | 153 passed, 0 failed, 0 skipped |
| Disposable real-MySQL DAO suite | 320 checks passed, including 45 new completion checks |
| Captured pickup evidence verification | 12 packet hashes, two action slices and both full publication windows match |
| Patch whitespace check | PASS |

The runtime cases exercise real acceptance/journal/world/pickup and inventory
publication with a controllable in-memory DAO capability. Real SQL atomicity,
rollback, concurrency and fresh-DAO recovery are separately tested against
disposable MySQL. Neither is represented as an original-client test.

Coverage includes full inventory before pickup and before payout; missing
objective/reward templates; dead/wrong-owner/wrong-instance pickups; repeated
requests; abandonment; unearned expiry; pending rewards past expiry; service
reconstruction and fresh actor reload; uncertain commit outcomes; post-commit
socket failures; completed-key recovery; and old queued stat checkpoints.
XP tests compare every planned durable value before publication to the existing
quest award path, including a level-up with perk, buff and timed modifiers.

Existing destination/QL/five-offer/fee, accepted entrance, key and dungeon cases
remain in the focused run. The two-file destination catalogs are unchanged,
retaining 2,242 placements, 812 supported entrances and 45 QL pools.
The ordinary unfiltered test project has a previously documented unrelated
compile limitation and was not rerun or repaired; this is not a full-suite pass.

Local ignored evidence: `build-verify/find-item-build-final.log`,
`build-verify/mission-destination-focused-tests.log`,
`build-verify/find-item-dao-final.log` and the focused TRX result.

## Limits and client acceptance

UNVERIFIED: original AO client completion of this new Find Item flow. Automated
runtime/DAO validation and retained retail wire evidence do not replace Mike's
client test. The agent does not launch or control the client.

Existing accepted Find Item quests lacking an objective binding are retained
unchanged. Roll and accept a new Find Item mission to test this implementation.
Historical already-completed rewards cannot be reconstructed from missing
ledger history. Missing or unrepresentable configured rewards stay pending for
content repair instead of being discarded.

The other four generated objective types, team acceptance/boss rules, NPC
catalog gaps, boss loot and ordinary dungeon loot remain separate work. Mission
rolling, QL calculation, two-file destinations, room generation and advertised
reward selection are retained. No database schema change or Linux deployment.

## Files changed

- `AORebirth/GameData/DungeonGeneration.json`
- `AORebirth/Libraries/Source/AORebirth.Database/AORebirth.Database.csproj`
- `AORebirth/Libraries/Source/AORebirth.Database/Domain/Missions/MySqlMissionDao.CharacterQuests.cs`
- `AORebirth/Libraries/Source/AORebirth.Database/Domain/Missions/MySqlMissionDao.cs`
- `AORebirth/Libraries/Source/AORebirth.Database/Domain/Quests/MySqlCharacterQuestDao.cs`
- `AORebirth/Libraries/Source/AORebirth.Interfaces/AORebirth.Interfaces.csproj`
- `AORebirth/Libraries/Source/AORebirth.Interfaces/Persistence/Missions/ICharacterQuestCompletionTransaction.cs`
- `AORebirth/Server/ZoneEngine_New.Tests/FindItemRewardTests.cs`
- `AORebirth/Server/ZoneEngine_New.Tests/QuestDungeonDestinationTests.cs`
- `AORebirth/Server/ZoneEngine_New.Tests/ZoneEngine_New.Tests.csproj`
- `AORebirth/Server/ZoneEngine_New/Core/Characters/CharacterSnapshotService.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Entities/Character.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Entities/MissionObjectiveItem.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Entities/Player.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Entities/WorldItem.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Inventory/WearBonusApplier.cs`
- `AORebirth/Server/ZoneEngine_New/Core/MessageHandlers/ClientGetItemMessageHandler.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Missions/DirectXpRewardPlan.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Playfield/Locality/LocalityVisibility.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Playfield/Locality/PlayfieldLocality.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Playfield/PlayfieldManager.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Playfield/QuestDungeonPlayfield.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Quests/Dungeons/DungeonFindItemPolicy.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Quests/Dungeons/DungeonLayoutGenerator.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Quests/Dungeons/DungeonObjectivePlacement.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Quests/Dungeons/MissionItemObjectiveBinding.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Quests/Dungeons/QuestDungeonParameters.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Quests/Dungeons/QuestDungeonService.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Quests/PlayerQuest.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Quests/QuestJournal.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Quests/QuestService.Completion.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Quests/QuestService.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Quests/QuestTemplate.cs`
- `AORebirth/Server/ZoneEngine_New/Program.cs`
- `AORebirth/Server/ZoneEngine_New/ZoneEngine_New.csproj`
- `docs/ai/CURRENT_TASK.md`
- `docs/evidence/FIND_ITEM_PICKUP_EVIDENCE.md`
- `docs/evidence/FIND_ITEM_PICKUP_WIRE.json`
- `docs/project/PROJECT_STATE.md`
- `docs/reports/FIND_ITEM_REWARD_INTEGRITY.md`
- `Tools/MissionDaoValidation/CharacterQuestCompletionChecks.cs`
- `Tools/MissionDaoValidation/MissionDaoValidation.csproj`
- `Tools/MissionDaoValidation/ParallelContractChecks.cs`
- `Tools/MissionDaoValidation/Program.cs`
- `WindowsBuildNet10/Projects/AORebirth.Interfaces.ZoneNew.WinNet10.csproj`

## Additional files inspected

- Current repository authority and workflow documents.
- Existing InventoryGrantPlan, InventoryFlushService, ICharacterRepository, IStatRepository, MySqlStatRepository and character persistence implementation.
- Existing item templates/hash resolution, item builders, StaticDynel/WorldItem, packet codecs and visibility ownership.
- Existing authored reward/transaction code, mission ledger validation, character XP/vital and equipment bonus mechanisms.
- The exact original packet sources and authoritative content references linked by the evidence report.
