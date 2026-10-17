# Current Task

CORRECT GENERATED MISSION CHEST PLACEMENT (2026-10-17).

Work only in the attached mission-chests worktree on codex/mission-chests.
Correct existing placement using actual generated wall/floor triangles, existing
room roles, door links, transitions, collision metadata and reachability checks.
Require wall-adjacent inward-facing positions, maximum one chest per eligible
room, passage/door/transition exclusion and separation across rooms and floors.
Preserve density and randomized first initialization; reject unsuitable positions
rather than forcing count. Persisted chest populations must never relocate.

Loot, rarity weights, QL rules, ownership, inventory, multiplayer synchronization,
DAO persistence and original packet behavior are outside this correction.
Mike explicitly requested real-layout, multi-floor, reconstruction and loot/
inventory regression validation. No schema changes or additional migrations.
Codex must never launch, control or test the AO client. Mike owns client testing.

The previous c1b123730 local build and approved single nano-line migration were
installed successfully. Its fresh backup and accepted build remain preserved
under ignored build-verify/database-safety-20261017. Original-client acceptance
is unverified. Existing local servers are being stopped through the approved
wrapper as required for compilation; restore corrected local servers for Mike
when validation completes. No public-master merge or Linux deployment.

Validation: all 65 chest cases passed; inventory is 23 passed / 2 proven
public-baseline failures. Overall: 88 passed / 2 failed / 0 skipped.
Per-real-layout statistics are recorded in
../reports/MISSION_CHEST_PLACEMENT_CORRECTION.md. Prior feature evidence remains
in ../MISSION_CHEST_INTEGRATION.md. Preserve original checkout dirty work.
