# Generated mission chest placement correction

2026-10-17. AORebirth Windows feature branch `codex/mission-chests`, based on
`c1b12373035a8ea0e2a25244e2fda1a228ba2990`. Original-client testing belongs to Mike.

## Root cause - PROVEN

The previous planner sampled open-floor AABB coordinates for every generated
cell, including passages. Selection shuffled individual positions, without a
used-room limit or room-separation preference, and chose random yaw. Existing
checks proved collision/support/count properties but did not assert natural
wall placement, distinct rooms or floor distribution.

Cave doorway metadata can use a template origin below the walkable ground.
Using that Y for approach probes and clearance hid valid door connections and
could omit the actual doorway exclusion. The previous occupancy probe also
swept the sphere centre by another radius, doubling its horizontal envelope;
that could reject physically clear narrow approaches. Both are placement-time
checks; character movement and dungeon generation are unchanged.

## Placement strategy

Reuse QuestDungeonService, the frozen DungeonLayoutGenerator result,
QuestDungeonPlayfield and the existing placement run. Slice each placed cell's
actual transformed collision triangles at chest height; do not derive walls
from rectangular room bounds. Require standing-height wall continuation and a
bounded outward scan to the occupied-floor perimeter; reject usable floor
behind the surface before accepting a room-boundary wall.
The scan uses the existing walking-sample spacing.

Room roles, authored MainHalls, actual transition endpoint cells and editable
`ExcludedRoomNameParts` exclude passages, entrances, intersections and platform
rooms. Supported room-side width/depth probes reject narrow pockets. Wall
sections are sampled by length with randomized positions and a preference for
suitable corners. Engine heading faces the verified inward approach.

Select at most one chest per eligible room. Prefer nonadjacent rooms, balance
floors, then prefer room-graph and spatial separation, with randomized first
selection/ties. The existing density 0.3 and variation 0.2 remain unchanged.
CandidateAttemptsPerRoom increases 48 -> 128 for the more restrictive wall search;
CandidatesPerRoom remains 8. No successful room is selected again.

Chest/player radii come from existing item/GameData metadata. Validate actual
chest-sphere and padded-envelope triangle clearance, own-cell ground support,
objective/entry/door/transition clearance and a floor-followed approach with
line of sight. Correct door approach heights from own ground triangles and
check alternative positions across the existing doorway. Keep the existing
bounded connectivity check, supported interior walking anchors, and existing
floor-use semantics with supported opposite landings; no runtime navigation
mesh or generator is introduced. Every accepted chest preserves previously
reachable door/transition anchors and all selected chest approaches.

Reject unverified candidates instead of forcing the count. Saved populations
are read before the planner and retain their identities, XYZ, yaw, appearance
and remaining items, including old placements that would fail the new policy.
Loot, rarity weights, mission QL +/-5, ownership, synchronization, DAO/schema and
original-client packet code are unchanged.

## Validation performed

Final existing focused cohort: 90 total, 88 passed, 2 failed, 0 skipped. All 65 chest
cases passed. Inventory is 23 passed / 2 unchanged PROVEN_PUBLIC_BASELINE_FAILURES:
`DeleteRetiresCorrectInstanceBeforeAcknowledgingAndDuplicateIsNoOp` and
`UnsupportedOnUseAloneDoesNotCountAsExecuted`. Both were previously executed
on untouched exact public input 749f819fd; this correction changes no inventory
production or fixture source. NEW_INVENTORY_FAILURES=0. Overall automated result:
PASS_WITH_PROVEN_PUBLIC_BASELINE_EXCEPTIONS.

Real layouts use the existing generator with private complete GameData and
actual ground/surface triangles, including partial DCGA room footprints.
Independent checks cover wall distance, exact chest/static triangle overlap,
own-room support, grounded door exclusions, floor-followed yaw-facing pockets,
one chest per room, randomized poses and multi-floor distribution. The cave
door regression found 10 grounded door heights above raw Y by more than the
configured probe lift. A real partial mainhall has 4 empty DCGA blocks within
its AABB and receives no chest.

Two additional reconstruction cases change/remove current policy and change
layout geometry/seed, then assert byte-identical saved flags, frozen IDs and
poses, remaining item identity/slot/QL, zero writes and no item reminting.
Existing loot, inventory/persistence and exact original spawn/contents packet
tests remain in the cohort. The real item smoke rolls 100 chests at each mission
QL 10/50/100/200/250. No AO client or capture tools were used for this correction.

Command (from the feature-worktree root, native CMD environment):

```text
dotnet test AORebirth\Server\ZoneEngine_New.Tests\ZoneEngine_New.Tests.csproj -p:MissionChestFocusedTests=true --no-restore --logger "trx;LogFileName=mission-chest-wall-final-r3.trx" --verbosity quiet --disable-build-servers
```

TRX/log/statistics remain in the ignored local TestResults/build-verify paths.
The normal Windows build/start workflow remains `restart-engines.cmd`.

## Real dungeon statistics

Distances are chest-origin XZ distance to an actual finite collision-wall
section at chest-centre height, in world units. Chest collision radius is
0.610368; configured padding is 0.05, giving a nominal origin-to-wall target
0.660368..0.710368. Rooms counts include generated hallway/transition cells,
matching the existing count-roll basis. Negative floor numbers are descending
floors. Boss platform rooms are excluded. The 324/12345 row deliberately uses
density 1 / variation 0 to prove the one-room cap and safe shortfall.

| Style | Floors | Layout seed | Rooms | Requested | Placed | Rooms with chests | Wall distance min / mean / max | Chest count by floor | Adjacent pairs |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- | ---: |
| 321 | 1 | 42 | 15 | 5 | 5 | 5 | 0.66300 / 0.68490 / 0.70224 | 0:5 | 1 |
| 324 | 1 | 42 | 12 | 4 | 4 | 4 | 0.66612 / 0.67779 / 0.68856 | 0:4 | 0 |
| 324 | 1 | 12345 | 13 | 13 | 7 | 7 | 0.66635 / 0.67890 / 0.68971 | 0:7 | 1 |
| 341 | 1 | 42 | 12 | 4 | 3 | 3 | 0.67665 / 0.68987 / 0.70232 | 0:3 | 1 |
| 341 | 2 | 42 | 13 | 4 | 3 | 3 | 0.68909 / 0.69308 / 0.69688 | 0:3 | 1 |
| 341 | 5 | 1 | 65 | 22 | 5 | 5 | 0.66359 / 0.68258 / 0.69888 | -2:2,-1:1,0:2 | 0 |
| 341 | 5 | 7 | 76 | 26 | 13 | 13 | 0.66441 / 0.68701 / 0.70646 | -2:8,-1:3,0:2 | 3 |
| 341 | 5 | 42 | 67 | 23 | 7 | 7 | 0.66941 / 0.68877 / 0.70702 | -3:2,-2:1,-1:2,0:2 | 0 |
| 351 | 1 | 42 | 26 | 9 | 9 | 9 | 0.67391 / 0.68983 / 0.70799 | 0:9 | 0 |

Additional randomized rolls on the same real layouts:

| Style / layout seed | Placement roll seed | Rooms | Requested / placed / unique | Wall distance min / mean / max | Floors | Adjacent pairs |
| --- | ---: | ---: | --- | --- | --- | ---: |
| 321 / 42 | 19 | 15 | 5 / 5 / 5 | 0.66098 / 0.67617 / 0.70244 | 0:5 | 0 |
| 321 / 42 | 7919 | 15 | 5 / 5 / 5 | 0.66300 / 0.68490 / 0.70224 | 0:5 | 1 |
| 321 / 42 | 104729 | 15 | 5 / 5 / 5 | 0.66341 / 0.68597 / 0.70757 | 0:5 | 0 |
| 321 / 42 | 32452843 | 15 | 4 / 4 / 4 | 0.66266 / 0.68497 / 0.69528 | 0:4 | 0 |
| 324 / 42 | 19 | 12 | 4 / 4 / 4 | 0.68854 / 0.70070 / 0.70997 | 0:4 | 0 |
| 324 / 42 | 7919 | 12 | 4 / 4 / 4 | 0.66612 / 0.67779 / 0.68856 | 0:4 | 0 |
| 324 / 42 | 104729 | 12 | 4 / 4 / 4 | 0.67285 / 0.67971 / 0.68848 | 0:4 | 0 |
| 324 / 42 | 32452843 | 12 | 3 / 3 / 3 | 0.67001 / 0.67681 / 0.68025 | 0:3 | 0 |

## Files inspected and changed

Inspected: current placement/policy/population and QuestDungeonService;
DungeonLayoutGenerator and QuestDungeonPlayfield; DungeonGenerator, RoomShape,
DungeonEntranceMap; transformed collision builders/room bounds/ground meshes;
world simulation, platform use and movement heading/warp owners; current chest
and inventory/DAO tests; private style metadata and current build/test docs.

Changed runtime/data: MissionChestPlacement.cs, MissionChestPolicy.cs and public
DungeonGeneration.json. Tests: MissionChestPlacementTests.cs and
MissionChestPopulationTests.cs. Documentation: this report,
MISSION_CHEST_INTEGRATION.md, docs/ai/CURRENT_TASK.md and
docs/project/PROJECT_STATE.md. The selected private DungeonGeneration.json
receives only the matching two placement-property changes, with a rollback
copy and semantic comparison preserving every other property.

## Remaining limitations

Bounded sampling and conservative support/collision/reachability checks can
return fewer chests. Metadata-eligible room count is not safe placement capacity:
the five-floor seeds 42/1/7 have 17/14/25 metadata-eligible rooms, already below
requested 23/22/26. Further geometric/route rejection yields 7/5/13 positions.
These shortfalls do not prove every other physical pose is impossible.

Seed 42 covers all 4 ordinary floors; seeds 1 and 7 cover 3 and leave floor -3 empty.
Room separation is preferential: some completed cases retain adjacent pairs
when later alternatives are exhausted/rejected. Door and pocket walking use
the existing bounded samples and bidirectional five-ray sweep approximation;
exact static overlap rejection additionally protects the placed chest sphere
(with 1 mm floating-point tangency tolerance). Future passage names/roles remain
editable data. Visual mesh-front alignment and original-client gameplay remain
Mike's acceptance work; automated engine-forward/+Z and approach checks do not
establish visual acceptance.
