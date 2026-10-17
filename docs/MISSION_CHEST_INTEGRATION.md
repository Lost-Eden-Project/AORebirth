# Mission Chest Integration

Date: 2026-10-17 (America/Chicago). Source input: `749f819fdf764ce0cc9debd17304607e71bbf363`. Branch: `codex/mission-chests`.

## Status

Implemented candidate; original AO client acceptance remains **UNVERIFIED**.
The final focused run compiled successfully and passed all 56 chest cases.
All 34 disposable real-DAO checks passed. Two inventory
fixture failures reproduce unchanged on exact public source; they are recorded
below. No production deployment is included.

Mike subsequently authorized a local Windows rebuild and restart for client
testing. The full Windows graph additionally needs the existing shared source
inventories regenerated from the canonical Interfaces/Database projects.
DatabasePreflight uses Dapper 1.13, so the two chest existence checks use LINQ
`Any()` rather than the newer Dapper `AsList()` extension. SQL, ownership checks
and transaction boundaries are unchanged.

Implementation is isolated from the original checkout and its unrelated dirty
work. Original capture/research artifacts and protected item data were read only.

## 1. Existing architecture reused

- `QuestDungeonService`, `DungeonLayoutGenerator`, `QuestDungeonPlayfield`
  and `PlayfieldManager` retain accepted missions, frozen layouts, entry,
  reconnect, unload and completion ownership.
- `ItemTemplateCatalog`, `HashItemCatalog` and `ItemBuilder` own item
  definitions, explicit QL families, interpolation and instance allocation.
- `Chest`, `LootableDynel`, `Container`, `InventoryMoveService` and
  `InventoryActionService` retain world/container interactions and inventory
  publication.
- `IMissionDao.Execute`, `MySqlMissionDao`,
  `MySqlCharacterPersistenceDao`, `SharedCharacterPersistence` and
  `MySqlCharacterQuestDao` retain transaction and persistence ownership.

No parallel mission/inventory database, NPC loot table, reward-item selector,
runtime capture reader, migration framework or navigation service was added.
Retained typed-world generated-mission tables were not activated.
`InventoryGrantPlan` is not used to copy already durable chest items.

## 2. Chest spawning and placement

The accepted layout and stored mission QL are passed through both entry and
login reconstruction. Count is a single random roll around
`roomCount * RoomDensity`; default density is 0.3 with a 0.2 variation fraction.
With variation disabled, 10/20/30/40/50 rooms produce 3/6/9/12/15 targets.

Placement uses the existing transformed room geometry and district anchors.
Random candidate coordinates are sampled throughout rooms, rather than selecting
predetermined chest rooms. Existing doors and exact floor-transition endpoints
connect the room graph. Floor projection, sampled walking support, bidirectional
sphere casts and usable approaches establish conservative reachability from the
accepted entrance. Chest/player extents come from the existing selected GameData
collision metadata. Doors, transitions, entry, objectives and other selected
chests have explicit clearances. Multiple chests may occupy one room.

Adding a chest must leave every selected chest with a reachable interaction
approach. Unsafe candidates are rejected. If verified capacity is insufficient,
the planner returns fewer positions instead of inserting unchecked objects.
The generated identities, poses, appearance and collision radii are stored once.
Unavailable placement inputs leave the population uninitialized, allowing a
later materialization to retry after the inputs are restored. A legitimate
zero-count roll or exhausted safe capacity can still be sealed as empty.

**PROVEN automated scope:** all six representative real layouts achieved their
exact requested count: styles 321, 324, 341 and 351, including two- and five-floor
341 instances. This is not an assertion that every possible generated seed was
exhaustively tested.

## 3. Loot eligibility and rarity

Each chest rolls one through three items. Three is an absolute maximum.
The eligible integer QL window is:

`[max(1, MissionQL - 5), min(250, MissionQL + 5)]`

Each supported quality is intersected with that window before rarity grouping.
Known variable-quality families retain their original template identities and
use existing item interpolation; fixed-quality records remain fixed.

Mike approved this **AORebirth gameplay policy**, not a claim about the original
meaning or ordering of stat 688:

| Rarity | Classification | Group weight |
| --- | --- | ---: |
| Missing | Common | 70 |
| 1 | Common | 20 |
| 2 | Uncommon | 6 |
| 3 | Rare | 2.5 |
| 4 | Very Rare | 1 |
| 5 | Extremely Rare | 0.5 |

Missing and value 1 remain separate groups. Empty groups are excluded and
positive configured weights renormalize. Selection chooses a group, then an
eligible item uniformly within that group, then one of its valid qualities.
Existing builder semantics determine effective stats, including endpoints with
missing Rarity. Missing Rarity is never an exclusion.

Catalog presence and the existing Carry restriction establish supported item
definitions. No arbitrary weapon/armor/utility/NoDrop category exclusion is
introduced. Unique checks remain in inventory transfer. NPC ItemTables, boss
loot, mission reward catalogs and mission rare-drop catalogs are not inputs.

## 4. Configuration

The existing selected `DungeonGeneration.json` has a `Chests` section:

- `RoomDensity`, `CountVariationFraction`, `MinItems`, `MaxItems`.
- `RarityWeights`: the six groups above, editable without gameplay code.
- `SpawnDefinition`: exact appearance/common-prefix fields from the verified
  world-chest packet; the confirmed Locked flag is cleared for ordinary chests.
- `Placement`: bounded candidate counts, floor/walking checks, collision
  padding, and door/objective/chest/entry clearances.

The existing `AO_REBIRTH_GAMEDATA_PATH` selection mechanism remains unchanged.
A missing chest section preserves compatibility with existing external configs.
Sealed populations reconstruct from their snapshots even after policy changes.
Policy data is deep-copied before generation.

The same validated section was added to the selected private runtime policy.
A private rollback copy is retained under ignored build-verify. All other
private policy properties were verified unchanged; private data is not committed.

## 5. Item-generation integration and metadata coverage

The current full item dataset has 131,384 definitions; 127,936 omit Rarity.
This supports the need for group selection before per-item selection.

Existing explicit family metadata is incomplete: 69,694 carryable templates
have no family entry, and inspected Relations collections are empty. Such
templates remain eligible at their actual stored QL; unsupported ranges are
never fabricated by matching display names. Distinct family definitions may
overlap in template IDs. Identical ID-set aliases are deduplicated first; after
QL and rarity filtering, identical eligible low-ID/high-ID/QL choice arrays
also receive one selection ticket. Different eligible identities or ranges
remain distinct.

The selector supports known variable ranges and every valid unlinked record's
known quality. Complete variable-range recovery for unlinked records is a
remaining metadata limitation, rather than authority to invent relationships.

## 6. DAO and transactions

The optional `IMissionChestPopulationTransaction` capability runs inside the
existing owner-locked `IMissionDao.Execute`. It validates exact mission owner,
assigned run, active/nonexpired state and frozen generator JSON.

One transaction writes compact per-chest mission flags, initial
`item_instances` rows and the population seal. Each snapshot is at most 1,024
characters. An existing seal returns load authority; partial unsealed records,
identity collisions and mismatched snapshots fail closed. Zero-count
populations can also be sealed. No schema migration is required.

Ownership is the existing container type/instance/placement tuple. Chest
contents use Container identities and initial slots 0-2. The client container
capacity remains the existing 21 slots; this does not change the maximum of
three generated items.

For an already durable chest item, the existing inventory mutation optionally
locks and verifies the exact source row before normal location updates. The
same row moves to the player. Shared chest and player gates protect live
projection changes. Stale/duplicate claims fail; occupied destinations cannot
swap player items back into a chest. Unique/capacity restrictions and commit
before acknowledgement remain in the existing inventory services.

## 7. Persistence, restart and cleanup

On materialization, a seal loads frozen identities, poses and appearance.
Remaining items come exclusively from current container rows with
`IsPersisted=true`; initial descriptors never remint consumed loot.
An uncertain initialization commit is reconciled from the seal before any
publication, without re-running inserts.

Every new opener refreshes from durable row ownership under the shared chest
gate. This reconciles an uncertain earlier transfer before another player can
see its old projection. The existing inventory quarantine still applies to
the affected looter. Session handles and viewers are transient.

Completion preserves the existing occupied-dungeon policy; abandonment and
expiry retire access and evict through the existing lifecycle. Eventual
generated-quest purge, inside its existing transaction, removes only rows
still owned by the exact persisted chest containers and their flags.
Items already transferred to players are outside that delete predicate.
Empty contents remain authoritative across reconstruction.

## 8. Original-client protocol evidence

**PROVEN:** the recovered world-chest fixture is original session
`mission-20261011-004128-b8e7bf2e`, raw packet ordinal 480,
identity `51017:196766495`, template `162447`. The complete packet is
155 bytes, SHA-256:

`E86620C76173EE0D943B94C144C40E4EDBA87B5F27B406CD93E62884EB42FD31`

The 139-byte message body SHA-256 is:

`D2332D057A7BABBBDE25C77E726685654D0456838E310DB11C43106A822E96AD`

The existing AOtomation codec exactly reproduces the positioned owner-none
FullUpdate prefix, transform, playfield, state-machine/stat block, empty
optional blob, lockable marker/value/empty scaled trap list and chest marker.
There is no added custom tail and no corpse/backpack substitution.

| Field | Captured value | Source | Confidence |
| --- | --- | --- | --- |
| Identity | 51017:196766495 | session ordinal 480 | PROVEN |
| Template stats 23/702/703 | 162447 | same exact packet | PROVEN |
| State machine | 1000015:7 | same exact packet | PROVEN |
| Header field / lockable integer | 111 / 15 | same exact packet | PROVEN |
| Item level / count stats | 1 / 1 | same exact packet | PROVEN |
| Ordinary flags | captured 0x20001841 with Locked 0x40 cleared | exact packet plus recovered original setter and existing ItemFlags.Locked | PROVEN transformation |

Inventory contents are separate. Exact contents/move/acknowledgement fixtures
are ordinals 1094/1105/1106 from the same session. Type 107 already encodes
`(handle << 16) | slot`. Tests cover that original request and response path,
a parented backpack control, and close/reopen/access behavior. Future locks
retain IBreakAndEnterTarget support; traps and locked generation are not enabled.

Authorized read-only research sources were the six supplied AO Stripdown
recovery artifacts and their exact raw capture. No external implementation
or evidence files were modified.

## 9. Validation

- Initial focused chest suite: 43 passed, zero failed/skipped.
- Final combined focused run: **79 passed, two failed, zero skipped** across
  81 cases. All **56 chest cases passed**; the other 25 are the existing
  inventory regression described below. The final run includes retry after
  unavailable placement inputs, identical eligible-family deduplication, and
  excessive/unrepresentable walk-sample rejection.
- Strengthened real-layout/full-item suite: nine passed, zero failed/skipped.
  Its six layout cases require the exact requested count. The additional item
  case rolls 100 chests at each of QL 10/50/100/200/250 using the real item data.
- Protocol tests compare every spawn-body byte with the original packet.
- Pure statistical tests verify group renormalization and configured weights.
- Runtime tests cover persisted transfers, occupied/Unique/capacity rejection,
  uncertain commits, reconstruction and exhausted containers.
- Isolated real-DAO harness: **34 checks passed**, including concurrent first
  initialization/looting, actual MySQL rollback, after-COMMIT acknowledgement
  loss, partial/exhausted reconstruction, and cleanup retaining player items.
- Existing inventory regression: **23 passed, two failed** on both this candidate
  and an untouched worktree at exact public input
  749f819fdf764ce0cc9debd17304607e71bbf363. Both are
  **PROVEN PUBLIC_BASELINE_FAILURES**: the delete fixture sends template IDs
  123/4 for a 1000/1000 item, and the unknown-operator fixture expects acceptance
  where the existing evaluator rejects it. Runtime/tests were not changed to
  hide either failure. New inventory failures: **0**.
- `git diff --check`: passed.
- Local Windows follow-up: the approved `NewZoneEngineBuild\build.cmd` passed
  for DatabasePreflight, ChatEngine, LoginEngine and ZoneEngine_New after the
  compatibility/source-inventory corrections described above. The required
  read-only local database preflight also passed; no schema changes were made.
- Existing objective/reward regression execution is blocked by the unchanged
  `QuestDungeonDestinationTests` fixture at line 716: its parameterless
  `ClientGetItemMessageHandler` construction no longer matches the production
  constructor. The reward tests depend on that fixture. No reward-suite pass
  is claimed, and no production behavior or unrelated fixture was altered to
  satisfy it.

Commands use the existing focused .NET test project and the existing disposable
DAO wrapper `cmd /d /c Tools\run_mission_dao_validation.cmd --chests-only`.
The focused command from this worktree is:

```text
dotnet test AORebirth\Server\ZoneEngine_New.Tests\ZoneEngine_New.Tests.csproj -p:MissionChestFocusedTests=true --no-restore --logger "trx;LogFileName=mission-chest-final-r2.trx" --verbosity quiet --disable-build-servers
```

The DAO wrapper compiles actual DAO sources and provisions only its labelled
disposable database. It does not consume player connection configuration.
The execution host required explicit existing SDK/NuGet environment settings;
no repository build-system redesign was made.

The initial feature implementation performed no AO client/capture startup,
accepted engine restart, player-database mutation, schema migration or deployment.
The later local testing request authorizes core-engine restart. Client acceptance
is **UNVERIFIED**.

## 10. Remaining limitations and client acceptance

- Mike must verify enter -> find chest -> open -> loot -> reconnect -> remaining
  contents in the original client. Automated wire/geometry/DAO results do not
  establish gameplay acceptance.
- Unlinked templates support their stored QL only, as described above.
- Placement uses the existing five-ray sphere-cast approximation and bounded
  candidates; unusual seeds may have fewer verified positions.
- Future locked chests and traps are separate milestones.

## Files inspected and changed

Inspected: repository startup/authority/workflow/code standards; supplied mission
audit; current Chest/LootableDynel/Corpse/Container/InventoryGrantPlan and
inventory owners; generated mission/layout/geometry/world simulation owners;
current mission, character-quest and shared inventory DAOs/schema; item data,
builders, enums and collision metadata; recovered protocol artifacts/raw bytes.

Changed code is confined to chest selection/placement/population/appearance,
the existing catalog enumeration and mission/inventory persistence seams,
current dungeon materialization hooks, focused tests/disposable DAO checks,
and chest JSON configuration. This report and the branch's CURRENT_TASK record
describe the candidate; the original checkout's dirty documents are untouched.
