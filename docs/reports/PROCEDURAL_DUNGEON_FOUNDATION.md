# Procedural mission dungeon foundation

Date: 2026-10-11. Branch: `codex/procedural-dungeon-foundation`.
Base: public master `3805aa851fcbe9ba81a9147954a9f1bf26e4fa8f`.

This increment extends the existing dungeon assembler and accepted-mission
pipeline. It does not complete NPC, objective, team ownership or loot work.

## Implemented

- New solo acceptance selects original PF321, PF324, PF341 or PF351 room
  resources using `GameData/DungeonGeneration.json`. Profiles cover mission
  QL1–250 without changing the established character-level/slider calculation.
- Version 3 stores the selected style, dimensions, floor direction and lock
  policy in the existing accepted-quest JSON before the acceptance transaction.
  Re-entry uses these frozen choices; editing selection profiles does not
  reselect an accepted dungeon. Future algorithm changes require a new version.
- Version 1/2 seed-only records retain their old generation path. PF324 remains
  only in that compatibility contract, not as a dependency for new selection.
- Team layout generation supports two-floor PF324 and two-to-five-floor PF341
  plans, with original room geometry, explicit floor connections and a separate
  doorless boss chamber. These layouts are constructed as real test worlds.
- Existing `DungeonEntrances.json` owns room-role/anchor metadata. There is no
  parallel room catalog, NPC catalog or WorldContent system. Platform/button
  item templates and anchors are data; C# supplies placement and use mechanics.
- Floor platforms/buttons use existing static-dynel registration, world-use
  dispatch and intrazone teleport. They retain the same instance and require a
  living, non-quarantined player in that exact world, within interaction range.
- Door and floor connections form one validated reachable room graph. Room
  overlap, grid bounds and transition anchors are checked. Target-distance
  traversal now includes floor connections.
- PF351 exposed a pre-existing assembler contradiction: its entrance has two
  inward connections, both requiring halls, while a small layout allowed only
  one hall. New layouts permit at least those required adjacent halls; old
  requests retain their original quota and byte output.

**IMPLEMENTED_POLICY:** interior choices, QL floor bands, room growth targets,
and reuse of captured role anchors are configurable AORebirth generation rules.
They do not claim Funcom's random algorithm, probabilities or eligibility rules.
Room growth targets are not exact room counts: capping can add rooms. The
47-room retail example is evidence, not an imposed output size.

## Preservation and scope

Mission rolling, QL calculation, five-offer cohorts, costs, destinations,
acceptance transaction, key linking, DAO code/schema and advertised rewards are
unchanged. The two destination GameData files are unchanged. No historical
captures or research datasets are loaded at runtime.

Normal acceptance remains solo. Team mode is not inferred from party membership.
The capture selector, roster-aware acceptance and guaranteed boss population
must be integrated together next. A generated boss chamber alone is not a
playable team mission. Existing temporary NPC/kill-target content remains
unfinished and is not expanded here. No loot or mission-objective changes were
made, and terminal rewards remain independent of future boss/ordinary loot.

## Evidence and validation

**OBSERVED:** original corpus audit found 23 independent layouts, 349 room
records, 184 matching source-file hashes, and 36 matching platform/button
identity/position records. PF324 team floors ascend; PF341 team floors descend;
both use 64-unit separation. Details and exact references are in
[the evidence report](../evidence/PROCEDURAL_DUNGEON_FOUNDATION_EVIDENCE.md).

**PROVEN by automated validation:**

- Approved Windows core-engine build completed successfully.
- 111 focused mission/foundation cases passed, zero failed or skipped.
- Configured-boundary coverage generated 288 layouts across all configured
  styles/floor counts and both room-size boundaries, seeds 0–15. It checks
  occupied geometry and independently reconstructed graph connectivity.
- Additional three-seed style/mode cases construct real collision layouts;
  real quest-world tests materialize platforms/buttons and exercise travel in
  both directions, including denial for wrong instance, distance, death and
  persistence quarantine.
- Three pre-change v2 payload/spawn fixtures match byte-for-byte. New frozen
  plans survive JSON round-trip and deterministic reconstruction. Invalid plans
  and disconnected floors are rejected.
- Existing offer/fee, exact destination, acceptance, relog, key-copy,
  abandonment/deletion and key-removal regression cases remain included.
- Disposable MySQL: 74 identity checks and 275 persistence/rollback/concurrency
  checks passed. Initial Docker-unavailable attempt was resolved by starting
  the existing Docker dependency; no DAO or schema repair was made.

The first foundation run was 102/103: PF351 failed the hall-quota contradiction
described above. The expanded suite passed after that targeted fix. A separate
review caught character-level 220 being mistaken for the mission-QL ceiling;
policy now supports the existing QL250 range, with explicit QL250 tests.

The full unfiltered test project was not claimed as passing. The existing
focused selection is used because unrelated typed-content fixtures reference
absent types; those fixtures were not suppressed or repaired. The approved
build still reports dependency/nullability warnings outside this change.

**UNVERIFIED:** original-client acceptance of these new layouts/platforms,
complete NPC encounters, team sharing/presence-sensitive completion, and loot.
The agent did not launch the AO client or perform additional captures. The
approved capture analyzer could not be built because dependencies were absent;
the independent original-byte audit and its limitations are recorded explicitly.

## Files and local state

Inspected: existing lower generator/geometry, room/door GameData, dungeon
parameters/service/playfield, mission roll/QL owners, generic world use,
static dynels, intrazone teleport and existing mission/DAO test workflows.

Changed: three lower-generator source files; `DungeonEntrances.json`; new
`DungeonGeneration.json`; runtime layout generator/plan/parameters/service;
quest playfield and new floor-transition dynel; focused tests and legacy
fixture; this report, evidence report, current-task and project-state records.

Only the two editable policy/role JSON files were staged into the existing
private GameData root for validation, after backing up the previous role file.
No private content catalog was copied into Git. Primary-checkout local edits
were preserved; implementation and compiled engines are in the isolated
foundation worktree. Backend services are stopped; no deployment was performed.

## Next milestone

Use the existing NPC hash/catalog and level-resolution systems to populate
solo objectives and guaranteed team bosses, carry the observed team selector
through acceptance with roster/instance membership, and test completion for
players actually present. Add regular/chest/boss loot through existing item and
corpse systems, independently of terminal-advertised rewards. No further retail
captures are required to begin that work.
