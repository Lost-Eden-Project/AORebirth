# Current Task

Use the existing boss pool for Kill Person on `codex/all-mission-objectives`,
based on source 989c0ccb2 / installed runtime 615696581.

OBSERVED: Mike confirms Kill Person works, but its target is an ordinary enemy
and should use the boss pool with a modest size increase. PROVEN: NpcHashes
selects FECI, while the existing Find Person boss pool selects MFPB. The private
MFPB catalog profile resolves as spawnable and attackable at scale 100.

Change the existing Kill Person NpcHashes policy to MFPB and configure a
KillPersonScalePercent of 120. Freeze the optional positive scale in the
existing accepted NPC binding and apply it through SpawnMob's existing
pre-registration configure callback. Find Person retains its current size.
Older accepted targets are not rewritten. Keep exact kill completion, mission
identity, destinations, room generation, rewards and working Find Item unchanged.
No new NPC catalog/leaf, schema change, boss loot or separate mission framework.

Compile with the approved wrapper, preserve local overlays/private catalogs,
and restart for Mike's standing local-testing request. No suites are requested.
Kill Person completion is client-accepted; boss selection/appearance in a new
mission remains UNVERIFIED until Mike tests the update.

Approved Windows core compilation, policy JSON validation and git diff --check
passed. Existing Find/Return item scale remains 150. No suites run.

Implementation and validation: `docs/reports/ALL_FIVE_MISSION_OBJECTIVES.md`.
