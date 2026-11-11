# Planned dungeon layout (generator version 4) and mission chests

Branch base: public master `5580ba16`. Everything here is game server code; content (GameData) is listed at the end
for context and ships separately through the GameData repository.

## In one paragraph

New quest dungeons are built by a second layout algorithm, the **planned layout**, stamped generator **version 4**.
Instead of growing rooms doorway by doorway, it lays a floor out the way a deck is drawn: its first room, a spine of
main halls running away from it, real rooms hung off the spine, every leftover doorway closed, and each floor's lift
arrival placed over the floor below's departure. Stored version 3 dungeons replay the classic layout byte for byte.
Around it: a per-style spawn chance for planned floors (they have fewer, bigger rooms), loot rows that can drop at a
share of the level (`QualityPercent`), more than one chest per room, and dungeon missions capped at QL 250 so the
hardest dialog choice no longer fails above level 163. The existing mission chest system was reused as it is.

## Why the classic layout had to go

Rendered with the viewer (`Tools/DungeonGeneratorViewer`) and measured over 60 Alien Mothership dungeons (3 floors,
size 13) with the classic generator:

| Per floor, classic | |
|---|---|
| Real rooms (not halls, not connector pieces) | 1.4 |
| One-block connector pieces | 12.1 |
| One-block dead-end caps | 5.6 |
| Longest chain of one-block pieces walked in a row | 8.9 |
| Lift arrival vs. departure, horizontal distance (per dungeon) | 75 m |

Three things in `LayoutBuilder` cause it:

1. **The floor size counts pieces, not area.** A target of 13 is reached fastest with 1x1 pieces; a 9x9 lab counts
   the same as an end block.
2. **Every fitting option is equally likely.** `TryFillSlot` collects every shape, rotation and door alignment that
   fits and picks one uniformly. A 1x1 piece with 2-4 doors fits a dozen ways almost anywhere; a 7x9 room fits once or
   twice, and only before the grid fills. Corridor pieces win on numbers.
3. **`Grow` extends the deepest doorway 70% of the time and `Cap` closes everything with the cheapest terminator.**
   The result is long single-file 1x1 corridors with one-block stubs, and the real rooms (alien rooms, lab, engine
   room, surveillance, hangars) mostly never appear. Floors are also laid out independently, so a lift lands
   anywhere on the floor above.

## How the planned layout builds a floor

Code: `AORebirth.DungeonGenerator/PlannedLayout.cs` (new, `partial` of `DungeonGenerator` and its `LayoutBuilder`,
so it reuses `TryFit`, `Commit`, the occupancy grid and the open-doorway table unchanged).

1. **First room.** Floor 0: the entrance, exactly as classic places it (west edge, mapped way-in door). Upper floors:
   a lift arrival room from the style's `TransitionArrivals`, tried at every position and rotation and placed where
   its platform stands nearest over the floor below's departure platform (`TryPlaceAligned`, using the same
   `DungeonRoomPlacer` maths the finished endpoints use). The boss floor places the boss room the same way.
2. **Spine.** Main halls (`DungeonEntrances.json` `MainHalls`) running away from the first room, 2-4 of them
   (`ExtendSpine`, scored by distance from the floor's first room, unused hall shapes first). On floor 0 every inward
   entrance doorway opens into a hall, as live layouts do. If no hall fits a doorway, one two-door connector may bridge
   it, and the bridge is rolled back unless a hall fits right behind it (`TryBridge`).
3. **Lift out.** On floors with a floor above, the departure goes in next, on the doorway farthest from the start,
   preferring a real departure room (engine room, hangars, surveillance, alien rooms on the mothership) over a
   one-block dead end.
4. **Real rooms.** `StyleRoomSet.Features` (every growth or one-door room that is not a main hall and not a connector)
   hung off the spine's doorways until the floor has its target: at least `PlannedRoomTarget` (floor size / 3.5,
   2-6) and 30% of the grid's area, up to the floor size in rooms. A real room may take one more one-door room behind
   it; nothing chains further, so there are no mazes. Scoring prefers room types not used yet and bigger rooms.
5. **Close every doorway.** `CloseDoorways`: each doorway still open gets a one-door real room, else a one-block cap
   set right on it. If neither fits, the whole candidate is rejected. The client draws an unpaired doorway as a door
   into nothing, so a planned layout never has one (`Validation.SealedDoors` is always 0).
6. **Score and keep the best.** Six valid candidates per seed (seed + attempt x 7919, as classic) are scored:
   real and varied rooms count for, connector pieces, one-block caps, a room count off target, sprawl and floors not
   sitting over each other count against. The best is kept, so the result is still a pure function of the request.

Piece classes (`StyleRoomSet`): `Connectors` are pieces whose name has `endblock`, `hallway`, `corridor`, `passage`
or `intersection` (or a one-block `_mini`); `Features` are the rest of the growth and one-door rooms. Name-based
because in styles 321/324 real rooms can be one block too. Helpers `OneDoorFeatures`, `Caps` and `Bridges` are the
subsets the builder uses. The planned grid is `14 + floor size` blocks (18-30, under the client's 32), bigger than
classic so 9x9 rooms fit.

Validation is unchanged: every planned layout passes `DungeonLayoutValidator` (connected, every door paired except
the way in) and the server's `DungeonCollisionBuilder` cross-check.

### Results

Planned layout, same 60 Alien Mothership dungeons:

| Per floor | Classic | Planned |
|---|---|---|
| Real rooms | 1.4 | 3.0-3.2 |
| One-block connector pieces | 12.1 | ~2 (mostly single caps closing a doorway) |
| Longest chain of one-block pieces | 8.9 | under 1 (connectors never touch each other) |
| Doorways leading nowhere | 0 | 0 |
| Lift arrival vs. departure (per dungeon) | 75 m | ~2.3 m |

Every style and size a profile uses builds (321, 324, 341, 351 solo; 324/341/386 team, 2-5 floors): 0 failures over
40 seeds each. Small-room styles (offices, clan bases) get 12-14 real rooms per floor; the subway style, whose rooms
have many doors and few one-door rooms, ends up with more single caps (~9 per floor) than the others.

### Versioning and compatibility

- `DungeonLayoutGenerator.CurrentVersion` is now **4**. `FirstPlanVersion = 3` (versions storing a
  `DungeonLayoutPlan`) and `FirstPlannedLayoutVersion = 4`; `IsPlanVersion` covers 3 and 4.
- `TryGenerate(parameters)` builds version 3 as `DungeonLayoutKind.Classic` and version 4 as `Planned`. The request
  carries `Layout`; its default is `Classic`, so every other caller is unchanged.
- `QuestDungeonParameters.TryParse` applies the plan checks to 3 and 4. Unknown future versions still parse and fail
  to generate, as before (no parse-time rejection was added, so a rollback does not drop stored quests).
- Version 1/2 paths are untouched; the V2 golden payload test still passes.
- **A stored v3 dungeon keeps its exact layout**: test `StoredVersion3PlansStillReplayTheClassicLayoutWhileNewDungeonsArePlanned`
  checks the v3 replay is byte-identical to the classic generator and differs from v4.

## Population on planned floors

The spawn rule (`QuestDungeonPlayfield.AddDistrictSpawns`) is unchanged: each room's district points, each taken at
`SpawnPointChancePercent`, at least one per room. On classic floors that "at least one" applies to every one-block
piece, so the real rate was 62-68%, not 35%. Planned floors have few one-block pieces, so at 35% they lose about a
third of their mobs.

`DungeonPopulationPolicy.PlannedLayoutSpawnPointChancePercent` (`{ style: percent }`, optional) replaces the chance for
version 4 dungeons of that style (`SpawnPointChanceFor`). Version 3 dungeons ignore it. Measured with the zone's own
rule over 150 missions per level band (each band's real floor-size range), the Alien Mothership needs **46** to match
classic:

| Mission level | Classic | Planned at 46% |
|---|---|---|
| 15 (2 floors) | 12.9 | 12.9-13.2 |
| 50 (3 floors) | 33.4-34.5 | 34.5-35.7 |
| 100-150 (4 floors) | 54.1 | 55.4-55.8 |
| 220 (5 floors) | 76.1-76.8 | 77.1-78.0 |

(Spawn points per dungeon; a point may spawn a small group.)

## Mission chests: what was there, and what we used

Nothing about chests was rewritten. The system was already complete and switched off:

- `Chest` (open, loot, Break and Enter locks), `MissionChestPopulation` (roll, persist through the mission DAO, place,
  refresh), `MissionChestPlacement` (eligible rooms: not the entrance, start rooms, main halls, lift rooms or
  name-excluded rooms; sites on the style's district points the population did not take; one chest per room before a
  second), `MissionChestPolicy` in `DungeonGeneration.json` (`Enabled`, `RoomDensity`, `SpawnDefinition`,
  `Placement`, terminal `LootTable`).
- Contents: an NPC-style loot table (`MobItemTableEntry` rows) rolled with `LootableDynel.RollItemTable`, the corpse
  roll, at the mission quality.

What we changed:

1. **`MissionChestPolicy.RoomDensity` may go up to 4** (`MaxRoomDensity`). It was capped at 1, and a rejected policy
   turns chests off entirely. Planned floors have fewer, bigger rooms, so we run 2 per eligible room. The spread rule
   and the per-room limit (a room never holds more chests than free points) are unchanged.
2. **`MobItemTableEntry.QualityPercent`** (default 100, so every existing NPC and chest table rolls exactly as before):
   the percent of the level a row's QL centres on (`QualityLevel`). Used for a Controller Recompiler Unit row that
   drops at half the mission level. Carried through both copy sites (`Clone`, `NpcTemplateCatalog`) and validated
   (1-1000) in `IsWellFormed`. Works for NPC tables too.
3. **`QuestDungeonTemplate.MaxMissionQuality = 250`**, applied in `QualityFor`. A dialog asking `level+53%` at level
   220 asked for QL 337; no generation profile covers it, so acceptance failed ("Mission dungeons are unavailable")
   for every player above level 163 on the hardest choice. It now gets a QL 250 mission. `MissionChestPopulation` uses
   the same constant instead of its literal 250.

Live check on a local server: chests spawn in real rooms on every regular floor, open, and loot as the retail capture
shows; the chest appearance (any container item record via `SpawnDefinition` stats 23/702/703) is data only.

## A design question about chest loot

I am not confident in where chest loot lives. Since `b2966d0f` ("Mission loot moves to NPC-style loot tables;
generated-mission content layers retired"), a dungeon's chests take their contents from:

- the quest's own `Dungeon.ChestLoot` for a `Quests.json` dungeon quest, and **only** from there (a quest without it
  gets no chests at all, not the policy table), or
- `DungeonGeneration.json` `Chests.LootTable` for missions rolled at a terminal.

What worries me:

1. **Static dungeons cannot have chests at all.** I checked: the only code that creates a `Chest` is
   `MissionChestPopulation`, and only `QuestDungeonPlayfield` (a generated mission dungeon) owns one. A static dungeon
   (a fixed playfield such as the Subway or the Crypt of Home) has no chest population, no placement and no loot
   source, whichever way the loot table is keyed. If chests in static dungeons are wanted, that is a separate system,
   and it would be good to decide now where its loot lives so it does not end up in a third place.
2. **Loot follows the quest, not the place.** Every quest that sends players into the same style has to repeat the
   same table, and a quest written without one silently produces chestless dungeons. A per-style (or per-playfield)
   table with the quest able to override it would cover both cases and would also be the natural home for static
   dungeon chests.
3. **Tooling.** Because the table sits inside `Quests.json`'s `Dungeon`, content tools that edit quests have to learn
   the dungeon section to edit loot. (We did this on our side; see below.)

I would like your view before we build more on it. Nothing in this branch changes where chest loot comes from.

## Not in this branch, but you should know

- **Lift visibility.** After a same-playfield lift (`TeleportWithinPlayfield` / `FinishIntrazoneLineTeleport`),
  dynels that already stood at the destination (chests, here) were sometimes not shown until a relog, while NPCs
  spawning there were. Either they were made visible before the teleport and the client dropped them on the
  N3Teleport, or they were sent in the same burst and ignored. Needs a capture of a lift ride to pin down; reproduce
  by placing anything static in a boss room and taking the lift up.
- **`ZoneEngine_New.Tests` does not build on master.** It lists two files that are not in git
  (`MissionRollSemanticsTests.cs`, `MissionRollTemplateContractTests.cs`) and many tests no longer match the code
  (`IZoneSession.TransferToPlayfield` now returns `bool`, `IChatEngineLink` grew members, the `Missions` namespace and
  `ZoneHandoff*` types are gone, `DungeonRoomCellTests` calls a changed API). Unrelated to this work. We ran
  `ProceduralDungeonFoundationTests.cs` on its own against the zone: **31 of 31 pass**, including the six new ones:
  - `StoredVersion3PlansStillReplayTheClassicLayoutWhileNewDungeonsArePlanned` (3 cases)
  - `PlannedLayoutsAreConnectedAndArchitecturalForEveryConfiguredProfile` (every profile, min/max size, 12 seeds:
    valid, reachable, deterministic, no sealed doorway, connectors never touching, lifts within 40 m)
  - `MissionQualityStopsAt250SoTheHardestDialogChoiceStillBuildsAtHighLevels`
  - `LootRowQualityPercentCentresTheRollOnAShareOfTheLevel`
  - `ChestRoomDensityMayAskForSeveralChestsPerRoom`

## Seeing it

```
dotnet run --project Tools/DungeonGeneratorViewer -- --gamedata <GameData> --style 386 --seed 1475420236 \
  --floors 3 --size 13 --height 128 --boss --building -1073737460 --out classic.html
# the same with --planned
dotnet run --project Tools/DungeonGeneratorViewer -- --gamedata <GameData> --style 386 --seed 1475420236 \
  --floors 3 --size 13 --height 128 --boss --building -1073737460 --planned --out planned.html
```

`--boss`, `--height`, `--descending` and `--building` match a quest's stored plan; `--sweep N` with `--planned` checks
N seeds. The cross-check's main-hall quota now applies only to classic single-floor layouts (a classic lift arrival is
itself a hall; the planned layout builds a spine).

## Files

| File | Change |
|---|---|
| `Libraries/Source/AORebirth.DungeonGenerator/PlannedLayout.cs` | new: the planned layout |
| `Libraries/Source/AORebirth.DungeonGenerator/DungeonGenerator.cs` | `DungeonLayoutKind`, request `Layout`, dispatch, `partial`, piece classes |
| `Server/ZoneEngine_New/Core/Quests/Dungeons/DungeonLayoutGenerator.cs` | version 4, plan-version checks, planned spawn chance |
| `Server/ZoneEngine_New/Core/Quests/Dungeons/QuestDungeonParameters.cs` | plan checks for 3 and 4 |
| `Server/ZoneEngine_New/Core/Quests/Dungeons/DungeonPopulationPolicy.cs` | `PlannedLayoutSpawnPointChancePercent`, `SpawnPointChanceFor` |
| `Server/ZoneEngine_New/Core/Quests/Dungeons/MissionChestPolicy.cs` | `MaxRoomDensity` (4) |
| `Server/ZoneEngine_New/Core/Quests/Dungeons/MissionChestPopulation.cs` | uses `MaxMissionQuality` |
| `Server/ZoneEngine_New/Core/Quests/QuestTemplate.cs` | `MaxMissionQuality`, capped `QualityFor` |
| `Server/ZoneEngine_New/Core/Mobs/MobTemplates.cs` | `QualityPercent`, `QualityLevel` |
| `Server/ZoneEngine_New/Core/Entities/LootableDynel.cs` | row quality centred on `QualityLevel` |
| `Server/ZoneEngine_New/Core/GameData/NpcTemplateCatalog.cs` | copies `QualityPercent` |
| `Server/ZoneEngine_New.Tests/ProceduralDungeonFoundationTests.cs` | the tests above |
| `Tools/DungeonGeneratorViewer/Program.cs` | `--planned` and full plan options |

## Content that goes with it (GameData, not this branch)

For the Alien Mothership (quest `829N`, style 386), shipped through the GameData repository:

- `DungeonGeneration.json`: `Chests.Enabled` true, `RoomDensity` 2, `SpawnDefinition` the Treasure container
  236570 (flags 536877089, state machine instance 0, lock 50); `Population.PlannedLayoutSpawnPointChancePercent`
  `{ "386": 46 }`.
- Quest `829N` `Dungeon.ChestLoot`: seven rows over new item hashes (alien tradeskill, equipment, base Kyr'Ozch
  weapons, Solid Clumps, Alien Costume, Broken Controller Recompiler Unit at `QualityPercent` 50, a rare tier).
- On our own tools (not AORebirth): the DevelopmentModule's quest overlay and our website's quest editor now accept
  `Dungeon.ChestLoot` / `MobLoot`, so staff can edit chest loot without touching the generated `Quests.json`.
