# Five generated solo mission objectives

Base: installed `9f59727bd`; branch: `codex/all-mission-objectives`.

## Implemented together

| Type | Accepted contract and completion event |
|---|---|
| Kill Person | Frozen catalog leaf, owner, exact NPC identity and reachable placement; that actor's server-side death with existing present kill-credit participants earns completion. |
| Find Person | Frozen boss NPC with normal combat behavior; visible exact actor inspection (`InfoRequest`) within interaction range and line of sight earns completion. Ordinary targeting does not. |
| Find Item | Existing owner-bound world-item pickup, exact inventory grant and durable completion remain in place. |
| Repair Machine | Tool and key granted in the same acceptance transaction; exact tool applied to the exact registered machine in the owned dungeon is consumed atomically with earning. |
| Return Item | Exact world-item collection grants the item and persists progress 0 to 1 without earning rewards; use against the exact originating terminal consumes it and earns progress 2. |

All use existing `generatedquests`, `characterquests`, inventory mutations,
mission DAO transactions and reward ledger. No new schema or reward framework.
Capacity failures leave objectives or earned rewards recoverable. Repeated
completion/collection cannot grant or consume twice. Earned rewards retain the
existing reconnect/restart recovery. New machine targets never fall through to
ordinary template spells for invalid users or tools.

Abandonment and expiration retire exact Repair/Return artifacts and keys in one
existing cleanup transaction, preserving historical item rows. Online inventory
publication fences stale queued item locations. Find Item's collected item is
unchanged. Failed NPC death commits retry through the existing player retry
tick; no new persistent queue was introduced.

## Content and evidence

The existing `DungeonGeneration.json` selects references in the existing item
and NPC catalogs: Find Item `URSE` unchanged; Return `COSE`; Repair `SPPR`/`CRCH`;
Kill Person and Find Person use their existing pools referencing the `MFPB`
human boss profile in the full NPC catalog, with existing level interpolation.
Kill Person adds an accepted scale override of 120; Find Person retains the
catalog's current scale. These are editable
AORebirth policies. Cross-QL selection and random NPC target selection are not
claimed as Funcom probability rules or exact retail target-name reproduction.

Inspected the existing acceptance/dungeon/quest/journal/inventory/DAO owners,
the original Find Person accepted action in capture
`mission-20261011-215642-c70bc191`, the completed Repair item pair/action in
`mission-20261012-004902-7174e861`, current roll packet policy and
`docs/evidence/RK_MISSION_ACG_INTERIOR_EVIDENCE_20260728.md`.
Return's action contract uses the established July analysis/current packet
policy; the absent July raw folders were not presented as newly inspected.
Runtime does not read capture folders. Private catalogs are not published.

## Kill Person boss correction

OBSERVED: Mike confirms Kill Person completion works on runtime 615696581, but
requests that its target come from the boss pool and stand out with a modest
size increase. PROVEN: the existing Kill Person NpcHashes pool selected FECI,
while FindPersonNpcHashes selected the authored MFPB human boss profile. The
existing private catalog resolves MFPB at levels 1, 29, 220 and 400 with scale
100, spawn validation passing, NoCombat absent and growth equipment removed.

The smallest pool correction is editable data: NpcHashes now references MFPB.
No new NPC catalog, duplicate boss leaf or hardcoded NPC content is introduced.
The existing item/NPC policy gains optional KillPersonScalePercent=120. The
existing accepted NPC binding freezes a positive optional ScalePercent and
compares it with the exact binding. SpawnMob's existing configure callback
applies the accepted Scale stat before registry/locality publication. Spawn
logging includes the actual scale. Null preserves older bindings and existing
Find Person size; new Kill Person targets are 20 percent larger than this
profile's baseline. This is AORebirth gameplay policy, not a retail scale or
boss-selection probability claim.

Exact accepted NPC identity/hash/QL/placement and kill completion remain the
same owners. Existing accepted ordinary targets are not rewritten. Find Person
inspection, working Find Item pickup/rewards, all destination/QL/layout/key
mechanics and DAO/reward delivery remain unchanged. Team bosses and loot are
separate unfinished work. Boss combat-stat parity is not newly established.

Inspected existing NpcObjectives selection, accepted NPC binding/reconstruction,
QuestDungeonPlayfield spawning, SpawnService's configuration/publication order,
NpcTemplateCatalog resolution and current private NPC/policy data. Changed
DungeonFindItemPolicy, MissionNpcObjectiveBinding, QuestDungeonService,
QuestDungeonPlayfield and existing DungeonGeneration policy, plus task/status/
report notes. No unit/fixture/regression/DAO suites requested or run. New boss
selection and size in the original client remain Mike's next verification.
Approved Windows core compilation, policy JSON validation and git diff --check
passed. Find/Return pickup policy still requests scale 150. Private NPC catalog
content is unchanged; no private catalog is published.

## Find Person boss correction

OBSERVED: Mike's Find Person client test spawned a regular enemy profile. The
shared NPC objective pool supplied the same unmodified `FECI` to both NPC types.
Find Person now selects a separate boss pool and freezes its resolved leaf using
the unchanged accepted NPC binding. The authored `MFPB`/Mission Contact variant
in the existing full NPC catalog uses original `BEMC` boss bands/stats at levels
1/200/400, existing `FECI` human appearance/equipment and base scale 125 observed
on the retail Find Person target. No invented HP multiplier or stat-growth rule.
Existing catalog interpolation supports the mission QL; completion still
requires exact actor inspection. The initial candidate made the target passive.

No replacement NPC catalog, binding format, schema change or raid-boss substitution. The source
Find Person capture's Monte Laurence is not claimed as an identified catalog
hash. Boss modifier nanos alone were rejected as the permanent solution because
their existing size/glow effects expire after 100 seconds. Persistent appearance
comes from the boss variant's base data. Generic direct OnWear combat-stat
modifiers remain outside this repair. Existing accepted actors are
preserved; test a newly accepted Find Person mission.

Inspected existing `NpcTemplateCatalog`, `NpcCharacter`, `SpawnService`,
`WearCastNano`, `NanoRuntime`, `StatModifierSpells`, objective binding/spawning
and completion owners, private NPC/item catalogs and the original Find Person
capture. Final runtime changes are `DungeonFindItemPolicy`, `QuestDungeonService`
and existing DungeonGeneration policy. Added one private NPC catalog leaf;
private catalogs are not published. Task/status documentation records the change.
Approved core compilation, policy/catalog JSON parsing and `git diff --check`
passed. Existing private NPC records remain byte-identical before the appended
leaf; a local backup preserves the complete original catalog. No suites or AO
client control performed. Actual boss appearance awaits Mike's client pass.

OBSERVED follow-up: Mike reports the boss target is better, but too large and
must be attackable. Its base scale is now 100 and the optional KZ15 growth item
is removed from the existing private leaf. The spawn code no longer overrides
attackability or suppresses the normal NPC brain. Find Person inspection
completion, exact actor binding and other mission objectives remain unchanged;
killing the target is not substituted for the Find Person completion event.
Changed `QuestDungeonPlayfield`, the existing public/private policy documentation
and private `NpcTemplates` data, plus task/status notes. Approved follow-up core
compilation and policy/catalog validation passed; no suites requested or AO client
actions performed. The copied human equipment has an existing SIW1 monster-weapon
bridge covering QL1–400. Inspect before killing: killing alone does not earn
Find Person, and a target killed beforehand returns on dungeon reconstruction.

## Pickup rejection and item visibility

OBSERVED: Shade (character 66) accepted D2086788889, entered PF2088886041 and
interacted with objective Terminal51005:2086788890. The local log records six
ClientGetItem requests between uptime 00:15:45.652 and 00:16:50.083, then
abandonment at 00:17:07.100, without a collection/reward exception. GenericCmd
Use denials also occur, but those are a separate intentionally unsupported
pickup path. These failed requests reproduce the reported symptom.

Historical first repair (`fa5dae8aa`, incomplete): SerializerResolverBuilder registers the custom
ClientGetItemMessageSerializer. It reads only message type and Identity1;
inherited message.Identity remains 0:0. ZoneSession dispatches it unchanged.
ClientGetItemMessageHandler incorrectly compared that absent actor field to the
authenticated player, rejecting every authentic pickup before target lookup.
That repair removed only the comparison. Its claim that the packet itself
lacked the actor was incorrect; see the raw-byte correction below. Session ownership, exact target,
mission binding, owner, dungeon, range, inventory capacity and atomic collection
remain enforced. Retained pickup evidence is in FIND_ITEM_PICKUP_EVIDENCE.md and
FIND_ITEM_PICKUP_WIRE.json. DAO, schema and reward delivery are unchanged.

WorldScalePercent is optional in the existing item policy and accepted binding,
with exact equality and positive-value validation. New Find Item and Return
Item objectives request 150 through the existing SIFU stat array. Scale 360 is
already a percent for placed static dynels (IGameData, GameDataStore and
PlayfieldWorldSimulation); no new packet field or catalog is introduced. The
authoritative items.dat entries URSE/100341 and COSE/100231 contain no Scale
override, so their normal baseline is 100 and the configured 150 adds 50 percent.
The override is omitted from the parented inventory update after collection.
Existing bindings without the field remain valid and keep their original look.
Client rendering of these particular meshes remains UNVERIFIED, as does the
complete repaired pickup/reward sequence; Mike performs live verification.

Inspected the registered pickup codec/resolver, ZoneSession dispatch, pickup
handler, MissionObjectiveItem, WorldItem/StaticDynel, binding/acceptance/spawning,
QuestService.Completion, existing DAO contracts and static-dynel scale owners,
original pickup evidence and current local mission logs. Changed only the
handler, item entity, existing policy/binding/acceptance and DungeonGeneration
policy, plus task/status/report notes. Private catalogs are not published.
Approved Windows core compilation, policy JSON parsing and git diff --check
passed. No unit/fixture/regression/DAO suites requested or run. The first
read-only local mission-row lookup failed before returning a row; it made no
database changes and is not used as evidence of persisted mission state. The
registered codec/handler mismatch independently establishes the pickup defect.

## Pickup codec follow-up

OBSERVED after fa5dae8aa: Shade accepted D2086798944, entered PF2088896096 and
made four ClientGetItem requests at uptime 00:14:18.867 through 00:14:43.842.
An ordinary Use denial identifies objective Terminal51005:2086798945. No
collection, completion or DAO exception follows in the inspected log window.

PROVEN from original retained OUT820 and OUT3878 in FIND_ITEM_PICKUP_WIRE.json:
after the 16-byte transport header, ClientGetItem is 4-byte type, 8-byte player
Identity, 1-byte Unknown, then 8-byte item Identity1. Both players are
50000:1248653930; item targets are 51005:1482214240 and 51005:1482014915.
The custom codec consumed only type and the next identity, assigning the player
to Identity1, leaving inherited Identity unset and nine bytes unread. This
caused the original actor-check rejection and, after fa5dae8aa removed that
check, a lookup for the player instead of the item. The previous wire prose was
incorrect; captured bytes and their hashes are unchanged.

Correct only ClientGetItem's serializer/deserializer to read/write type,
Identity, Unknown, Identity1, following the existing N3 contract and observed
bytes. Restore actor equality with the authenticated session. Add low-frequency
pickup logging for decoded actor/target, exact quest, distance and outcome, or
unavailable target. Retain every owner/binding/dungeon/range/inventory and atomic
completion validation. Preserve the 150-percent size policy, exact accepted
identities and all reward/DAO mechanics. No schema or content changes.

Inspected current local pickup logs, retained original wire fixture, N3Message,
custom codec/resolver/dispatch, handler and objective transaction gates. A
read-only mission-row lookup failed on a missing connector logging assembly
before opening a connection; no DB changes were made. The raw-byte/codec
mismatch establishes this repair independently of that unavailable lookup.
No unit/fixture/regression/DAO suites requested or run. Original-client
collection/completion/reward verification remains Mike's next step.
Approved Windows core compilation and git diff --check passed. Offline byte
inspection confirms both retained request lengths and actor/Unknown/target
offsets; this is evidence analysis, not a runtime codec test suite. The raw
wire fixture and installed size/content policy remain unchanged.

## Find Item original-client acceptance

OBSERVED on installed runtime 61569658195c66af8a6f93bd0474a0b5708c8158: Mike
reports "picked up and got reawrd now". The local ZoneEngine log corroborates:

- Line 120374, uptime 00:05:09.463: actor CanbeAffected:66, target
  Terminal:2086798945, quest D2086798944, edge distance 0.5487016531552542,
  collected=True.
- Line 120376: advertised reward item creation, Head Implant: Chemistry,
  Shiny, IDs 105497/105498, QL30.
- Line 120378: dungeon quest ended, keys=1, evicted=False.
- Line 120384: "Quest completed and rewards committed char=66
  quest=D2086798944".

This is original-client Find Item pickup/completion/reward acceptance with
corroborating server events. It supersedes the pending Find Item acceptance
notes above. It does not establish exact payout amounts, separately confirm
the visual size increase, accept other objective types, or exercise every
duplicate/capacity/disconnect/recovery case. This documentation-only update
changes no runtime, content or running service and requires no rebuild.

## Validation and limits

- PROVEN: approved `cmd /d /c tools\build_aorebirth_debug.cmd` succeeded for
  ChatEngine, LoginEngine and ZoneEngine_New, including framework DAO preflight
  compilation. Compiler/package warnings remain; no compile errors.
- PROVEN: `git diff --check` and policy JSON parsing succeeded.
- No unit, fixture, regression, fault-injection or DAO test suites run for this
  task. Previous Find Item test counts are not this candidate's validation.
- Find Item pickup/completion/rewards are client-accepted as recorded above.
  Original-client all-five gameplay is not fully verified. Test newly accepted missions;
  older unbound accepted contracts are not rewritten.
- Repair/Return use requires main inventory; move the item out of a backpack.
- An uncommitted kill event lost during a database outage plus disconnect or
  restart can require killing the reconstructed target again. Committed earned
  rewards retain the existing ledger recovery.
- Team acceptance/rosters/bosses, independent dungeon loot and complete modern
  retail NPC appearance/population parity remain separate unfinished work.

Rolling, mission QL, destination pools, room generation and advertised rewards
were not redesigned. No other objective frameworks or content catalogs added.

## Kill Person completion delay and level-up reward persistence

OBSERVED on installed runtime e65160c865c86c6de79c3b3ef74bc3c62c59419b:
Mike killed the objective but saw completion only after killing another mob.
The existing ZoneEngine log for char 66 / D2086818891 records:

| Log line | Uptime | Event |
|---|---|---|
| 124904 | 11:07.790 | Exact objective 50000:2086818892, MFPB Mission Contact, scale 120. |
| 125758 | 11:55.011 | Advertised reward item materialization begins at target death. |
| 125760-125762 | 11:55.035 | Death requests identify the exact accepted objective. |
| 125814 | 12:00.041 | Reward delivery retry. |
| 125820 | 12:05.041 | Reward delivery retry. |
| 125853 | 12:07.589 | Player receives NewLevelMessage during ordinary mob combat. |
| 125869 | 12:10.063 | Reward delivery retry. |
| 125877 | 12:10.112 | Quest completed and rewards committed. |
| 125881 | 12:10.112 | Ordinary SH10 mob 1000021 becomes a corpse. |

The correct objective was recognized before the second kill. A read-only lookup
confirms the completed assignment has progress 1/1 and its frozen reward is
1731 XP, 7770 credits and item 110392/110393 QL31. This lookup did not modify any
rows. No additional captures were made.

PROVEN defect: DirectXpRewardPlan.CreateQuestReward includes Health, MaxHealth,
CurrentNano and MaxNanoEnergy only when its XP causes a level-up. Completion
incorrectly included those values in the DAO's exact durable stat expectations,
mutations and saved baseline. CharacterSnapshotService correctly excludes all
four using StatCollection.IsRuntimeOnly; their durable representation is the
existing health/nano percentage stats. Shade's stored runtime-vital rows remain
34/34/28/28, so they are not a synchronized source for reward comparisons.
MySqlMissionDao rejects any stale expected value without paying the reward.

INFERRED incident relationship: an ordinary kill leveling the character can
make the subsequent mission reward stop crossing a level boundary, removing
those runtime-only comparisons and permitting delivery. This fits the recorded
retry/level-up/commit sequence. The original log records neither DAO rejection
reasons nor numeric player XP/vitals, so it cannot independently prove the exact
failed stat comparison from those attempts.

Correction: filter completion's durable projection with the existing
StatCollection.IsRuntimeOnly policy. Keep the full XP projection for publication
after commit, including the level-up refill, and persist health/nano percentages.
Log non-applied completion status/reason instead of silently returning. Keep
the atomic inventory/stats/quest/reward-ledger transaction, expected durable
stat comparisons, publication fence and duplicate-payment handling unchanged.
No DAO/schema, content, objective, XP calculation or target-selection changes.

Inspected: local engine log; QuestService and QuestService.Completion;
PlayerQuest and Player tick; Character kill/death ordering; DirectXpRewardPlan;
InventoryGrantPlan; StatCollection; CharacterSnapshotService; MaxHealthCalculator;
MySqlMissionDao.CharacterQuests and its ReadStatValue; completion contracts;
existing project/task/workflow documentation. Changed: QuestService.Completion
and these current task/state/report documents. No private catalog changes.

Validation: approved Windows core compilation and git diff --check succeeded.
No fixture, unit, regression or DAO suites requested or run. Mike's
original-client verification of a mission
reward that causes a level-up remains pending; do not describe it as accepted.

## Immediate Kill Person original-client acceptance

OBSERVED on installed runtime 08f17b3e4ee2e79f388a574b92fd9aad50e9affd:
Mike reports "worked got reawrd on kill". Current ZoneEngine log corroborates:

- Line 127392: Shade / char 66 accepts D2086828889.
- Lines 127928-127929: exact objective 50000:2086828890, MFPB Mission Contact,
  level 32, scale 120.
- Line 128941, uptime 29:13.072: one advertised reward item materialization,
  Chest Implant: Stamina, Shiny, IDs 109080/109081, QL32.
- Lines 128947/128953, uptime 29:13.136: one key retired and quest completion
  with rewards committed; exact-target Die requests follow at the same timestamp.
- Lines 128965/128967, uptime 29:15.532: target corpse and despawn.

No reward-delivery warning, completion exception, repeated reward materialization
or NewLevelMessage appears between acceptance and the immediate aftermath.
This is original-client acceptance of immediate exact-target Kill Person
completion and reward receipt. It does not demonstrate a mission-reward level-up
or establish the prior attempt's unlogged rejection reason.

Inspected the current engine log and task/state/report documents. Changed only
these three documents. No runtime/content/DAO/schema changes, build, restart or
test suites; local engines remain running. Documentation diff validation only.
