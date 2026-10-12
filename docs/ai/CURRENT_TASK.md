# Current Task

Implement kill-progress-based sided tokens for new generated missions, using
the existing dungeon population, quest persistence and atomic reward delivery.
Preserve the client-accepted mission objectives, rolling, destinations and keys.

Mike explicitly approved direct inspection of retained MissionCap CSV files.
PROVEN: 36 chance updates match killed/total percentages, objective kills count,
and a completion at about 81 percent attempted a token award but encountered
the existing Unique token. Authoritative item data owns side requirements and
level-dependent points when one physical token is used.

Persist population selections and unique deaths, freeze the award outcome with
objective earning, and use the existing recoverable inventory/quest completion
transaction. Editable policy remains in DungeonGeneration.json. Old accepted
missions without the policy keep their existing behavior.

Use the approved Windows build, install locally and leave the core engines
running for Mike's testing. Do not run fixture/unit/regression suites or control
the AO client. Original-client token verification remains pending.

Implementation and focused source review are complete. Approved Windows core
build and git diff validation succeeded. Install this feature result with the
matching private DungeonGeneration token policy and preserve local overlays.

Evidence, policy boundaries and validation: `docs/reports/MISSION_TOKEN_REWARDS.md`.
