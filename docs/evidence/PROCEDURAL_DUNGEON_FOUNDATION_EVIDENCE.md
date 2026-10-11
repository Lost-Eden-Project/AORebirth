# Procedural dungeon foundation: retail evidence

Date: 2026-10-11. Scope: the shared layout and floor-transition foundation.
This document records observations and implementation inputs, not Funcom's
unrecovered random-selection algorithm or a claim of completed team gameplay.

## Evidence inputs and validation

Original recordings remain under `D:\AOTools\ReadyToUse\Plugins\captures`.
Completed offline analyses remain under
`C:\Users\Mike\Documents\AO stripdown\.local\mission_dungeon_capture`.
These are research inputs only. The runtime must not read them. No source from
the other project was imported or modified.

The existing `mission-dungeon.json`, `layout-object-review.json`, and readable
`detailed-capture-report.md` files were inspected. Original `raw-packets.csv`,
`mission-rolls.csv`, and the analysis source manifests were checked directly.
Reentry into the same dynamic-playfield/generator pair was not counted as a new
instance; duplicated `*-level-analysis` projections were excluded.

PROVEN in this audit:

- 23 independent dynamic-playfield/generator pairs, containing 349 unique room
  records. Every retained ordered room record was compared with the original
  packet's six bytes (`>HbBBB`: template index, signed floor, grid X, grid Z,
  quarter turns), including repeated entry layouts.
- 184 source-manifest file hashes match their original recording files:
  166 checks for the recent analysis set and 18 for the earlier PF321 recording.
- 36 first full-update observations of transition platforms/buttons match the
  original packet SHA256, typed identity, and all three serialized float32
  position values exactly.
- No room-record or source-hash mismatch was found.

The prescribed repository-relative `AOSharpMissionCaptureAnalyzer.exe` was
attempted first. It was absent from both the feature worktree and authorized
primary AORebirth checkout. Its prescribed MSBuild command failed because the
feature worktree lacks AOSharp/SmokeLounge references. No unrelated analyzer
repair was made. Consequently there is no new `mission-flow.replay.log` result
from that analyzer. The existing completed analyses were independently checked
against their original bytes as described above; this is not an analyzer pass.

## Complete independent layout inventory

Capture stems below resolve uniquely beneath the original recording directory.
The corresponding analysis is `<stem>-analysis/mission-dungeon.json`, except
the September recording, which uses `pf321-final-20261010/mission-dungeon.json`.
Expected QL is the capture's established lookup result, not a newly decoded
serialized mission QL. `UNVERIFIED` is not an inferred value.

| Capture stem | Initial packet | Dynamic PF | Generator instance | Template PF | Expected QL | Rooms | Serialized floors |
| --- | ---: | ---: | ---: | ---: | --- | ---: | --- |
| Andromeda, 20260916-005649 | 51 | 2151072 | 14594607 | 321 | UNVERIFIED | 15 | 0 |
| mission-20261010-221548 | 292 | 2151502 | 14678854 | 324 | 2 | 21 | 0 |
| mission-20261010-223213 | 121 | 2151626 | 14678856 | 324 | 2 | 19 | 0 |
| mission-20261010-233245 | 367 | 2151603 | 14678865 | 341 | 2 | 11 | 0 |
| mission-20261010-235333 | 366 | 2151624 | 14678866 | 324 | 2 | 17 | 0 |
| mission-20261011-000406 | 136 | 2151495 | 14678867 | 324 | 2 | 10 | 0 |
| mission-20261011-001616 | 385 | 2151400 | 14678870 | 351 | 2 | 15 | 0 |
| mission-20261011-003911 | 165 | 2151551 | 14678872 | 324 | 2 | 9 | 0 |
| mission-20261011-004128 | 429 | 2151612 | 14678873 | 324 | 2 | 18 | 0 |
| mission-20261011-004618 | 167 | 2151523 | 14678874 | 341 | 2 | 14 | 0 |
| mission-20261011-010014 | 38940 | 2151576 | 14674420 | 320 | 2 | 12 | 0 |
| mission-20261011-023454 | 42143 | 2151606 | 14674421 | 320 | 2 | 13 | 0 |
| mission-20261011-025629 | 3513 | 2151396 | 14674422 | 321 | 2 | 12 | 0 |
| mission-20261011-031432 | 6213 | 2151603 | 14674426 | 320 | 2 | 11 | 0 |
| mission-20261011-033010 | 5253 | 2151473 | 14674430 | 351 | 2 | 15 | 0 |
| mission-20261011-053902 | 313 | 2151400 | 14678897 | 341 | 2 | 9 | 0, -1 |
| mission-20261011-055250 | 308 | 2151400 | 14678899 | 324 | 2 | 17 | 0, 1 |
| mission-20261011-055815 | 196 | 2151523 | 14678900 | 341 | 2 | 11 | 0, -1 |
| mission-20261011-060321 | 308 | 2151613 | 14678901 | 324 | 2 | 9 | 0, 1 |
| mission-20261011-060635 | 155 | 2151552 | 14678902 | 324 | 2 | 10 | 0, 1 |
| mission-20261011-060635 | 1379 | 2151617 | 14678903 | 324 | 2 | 17 | 0, 1 |
| mission-20261011-062013 | 244 | 2151400 | 14678904 | 324 | 3 | 17 | 0, 1 |
| mission-20261011-074410 | 1349 | 2151628 | 14674438 | 341 | 220 | 47 | 0, -1, -2, -3, -4 |

The later original folders `mission-20261011-080142-c7cf9e68`,
`mission-20261011-082207-d9763c53`, and
`mission-20261011-082602-e35e6543` contain no additional complete ACG layout:
the first has an 86-byte exterior PlayfieldAnarchyF packet, and the others have
no PlayfieldAnarchyF packets. No additional captures are requested.

## Layout and floor observations

OBSERVED: all 23 layouts use serialized floor separation 64. Both ascending
and descending floor indices occur. Floor indices must be read as signed;
`0xFF` in the PF341 room record means -1. The serialized field is separation,
not an instruction to place every floor beside the previous floor in X/Z.

The QL220 packet already contains all 47 rooms on first entry, distributed
12/9/10/15/1 over floors 0/-1/-2/-3/-4. The final room is PF341 template 94,
`Mine_bossroom2`, without planar doors. Only floors 0, -1, and -2 have observed
player traversal in this incomplete mission. Initial full-layout reception
does not establish that every NPC, object, or container was visited.

The QL3 team packet contains 16 regular rooms on floor 0 plus PF324 template
45, `clan_bossroom1`, on floor 1. QL2 team instances also contain two floors.
Neither high QL nor a large human party is necessary for a second floor.

Geometric door adjacency must be restricted to the same floor. In the QL220
analysis, the same-floor reconstruction gives 46 door pairs and 44 distinct
room pairs; X/Z-only comparisons produce aliases across floors. These are
geometric candidates, not independent retail collision or navigation proof.

Observed low-QL single-floor layouts contain 9-21 rooms. The captures establish
examples, not mandatory room-count distributions or universal level limits.
Configurable room budgets, template selection, placement attempts, and floor
counts are implementation policy, as authorized for this foundation.

## Transition anchors and reusable item templates

Each listed platform is identity type 51005 and uses SimpleItemFullUpdate.
Its colocated button is a separate identity. Stat 23 and template stats
702/703 identify the same item template; stat 701 is 1 in these observations.
The exact original identities are evidence references, not runtime constants.
All 36 checked first platform/button observations have serialized quaternion
XYZW `(0, 0, 0, 1)`, irrespective of the containing room's quarter turns.
The observed world rotation therefore should not inherit the room rotation.

| Capture / platform packet | PF / room template | Turns | Derived room origin XYZ | Exact serialized platform XYZ | Platform / button templates |
| --- | --- | ---: | --- | --- | --- |
| 062013 / 303 | 324 / 6 | 3 | 234, 5, 136 | 238.79998779296875, 5.0100016593933105, 131.5 | 159867 / 159864 (boss) |
| 062013 / 968 | 324 / 45 | 0 | 146, 64, 156 | 136.00999450683594, 66.01000213623047, 163.70001220703125 | 159867 / 159863 (down) |
| 074410 / 1564 | 341 / 11 | 0 | 66, 261, 56 | 67.21405029296875, 261.010009765625, 57.258819580078125 | 159865 / 159863 (down) |
| 074410 / 2519 | 341 / 52 | 2 | 284, 197, 44 | 297.70001220703125, 197.00999450683594, 56.899993896484375 | 159865 / 159869 (up) |
| 074410 / 3768 | 341 / 52 | 0 | 26, 133, 36 | 12.29998779296875, 133.00999450683594, 23.100006103515625 | 159865 / 159869 (up) |
| 053902 / 724 | 341 / 93 | 0 | 146, 0.0002627521753311157, 156 | 152.1240692138672, 0.010999999940395355, 151.77520751953125 | 159865 / 159869 (up) |

Facing-zero offsets derived from these exact observations can be kept in the
existing editable room metadata. Example offsets relative to the derived
room origin, using the existing quarter-turn transform:

- PF324 room 6: `[-4.5, 0.0100016593933105, -4.79998779296875]`.
- PF324 room 45: `[-9.9900054931640625, 2.01000213623046875, 7.70001220703125]`.
- PF341 room 11: `[1.21405029296875, 0.010009765625, 1.258819580078125]`.
- PF341 room 52: `[-13.70001220703125, 0.0099945068359375, -12.899993896484375]`.
- PF341 room 93: `[6.1240692138671875, 0.01073724776506424, -4.22479248046875]`.

INFERRED transformation: local offsets are obtained by inverse-rotating the
observed world-position difference. Using one such valid anchor as a reusable
generation policy is not proof that retail always uses it. In particular,
PF341 room 93 and PF324 room 45 each have different platform positions in
other captures. PF341 room 94 has no observed platform anchor in the QL220
capture because the player did not reach that floor. Room 93 has direct anchor
evidence and is a defensible initial configured boss-room choice instead.

No ordinary upper-floor arrival anchor is observed for PF324: all five captured
PF324 team instances have two floors. PF341 provides ordinary arrival/departure
anchors as well as a separate boss room, supporting an initial configurable
multi-floor implementation without claiming the same retail pairing algorithm.
Departure platforms are not restricted to rooms named `elevator`: room 6 in
PF324 and room 11 in PF341 are directly observed examples.

## Activation and mission mode

PROVEN capture-specific transitions in `mission-20261011-062013-e3af7cc7`:

- GenericCmd Use OUT 934, matching reply IN 936, Teleport IN 978 moves from
  room 12/floor 0 to the exact room 16/floor 1 platform position. Button
  identity is 51005:1482207384; destination platform is 51005:1482207383.
- OUT 1360, IN 1362, Teleport IN 1399 returns to the exact floor-0 platform
  position. Button is 51005:1482207385; platform is 51005:1482207382.

The QL220 capture supplies four same-instance paths through floors
0 -> -1 -> -2 -> -1 -> -2. Three Teleport positions equal the destination
platform's serialized XYZ exactly. Teleport 4678 differs only in Z by -2^-21;
the cause of that measured rounding difference is unknown. Floor travel does
not require a replacement dungeon layout in these captured visits.

OBSERVED request-mode association across the complete corpus:

- All 15 captured roll requests in the 14 recent single-floor instance
  recordings have byte 41 equal to 1.
- All eight captured roll requests associated with the eight user-annotated
  team instances have byte 41 equal to 2.
- The older PF321 recording starts after rolling, so no selector is observed.

The existing AOtomation `QuestAlternativeMessage` maps that byte to
`Unknown5`; the packet format need not change to preserve this distinction.
For example, team requests 053902/42 and 062013/51 reference terminal
56001:3221684790, and team request 074410/28 references 56001:3221291663.
Single-floor request 233245/35 references 56001:3221619254. No exact terminal
name was found in those recordings' memory rows. Mode should not be guessed
from a faction or a terminal display name.

Accepted team quest updates contain flags 258 and serialized participant lists.
The original native flag consumer was not reverified; prior bit-8/bit-9 labels
remain documented mappings rather than new reverse-engineering proof. The
QL3 capture has four serialized participants but only one observed interior
player. The QL220 capture has two serialized participants and one observed
interior player; human party size was not supplied. Roster membership and
actual instance presence must remain separate when completion eligibility is
implemented.

## Boundaries for later increments

- A room named `bossroom` and a floor-layout record do not identify a boss NPC.
  Actual boss population and objective-specific boss completion remain separate
  from reserving and connecting the final room in this foundation.
- MonsterData, NPCFamily, appearance, and NPC level observations do not make
  the older NpcTemplates catalog identical to the current retail catalog.
- A complete room layout does not guarantee exhaustive NPC/object/loot
  observations. Unopened containers remain unobserved.
- The QL220 team visit is explicitly incomplete. Its title and advertised
  rewards are not completion or payout evidence.
- Terminal-advertised rewards, ordinary corpse/container loot, and boss loot
  are separate systems. Layout configuration must not copy an advertised
  mission reward into either loot category.
- Captured arrangements inform configurable generation rules; none establish
  destination probabilities, mandatory template weights, universal floor counts,
  or the complete original procedural algorithm.

This audit made no capture, runtime, DAO, schema, or other-project changes.
The only tracked change by this evidence subtask is this report. Runtime and
automated generation validation are reported separately by the implementation
task; original-client acceptance of the new foundation remains a separate step.
