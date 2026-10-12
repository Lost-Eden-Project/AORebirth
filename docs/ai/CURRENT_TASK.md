# Current Task

Repair delayed mission reward delivery on `codex/all-mission-objectives`, based
on installed runtime e65160c86. Preserve exact Kill Person target binding and
the existing objective, inventory, reward ledger and persistence owners.

OBSERVED: Shade's exact MFPB target death starts reward delivery at uptime
11:55.011. Delivery retries every five seconds and commits at 12:10.112,
after a second mob causes a level-up at 12:07.589. The second mob is not the
accepted objective. The rejected attempts did not log their DAO result reason.

PROVEN: a quest XP level-up adds current/max health and nano to its projection.
Completion sends these runtime-only values to durable stat comparison, although
the existing checkpoint intentionally excludes them. Shade's stored vital rows
remain 34/34/28/28. This is a persistence-contract defect; its explanation of
this particular delayed delivery is INFERRED from the observed event sequence.

Keep the full XP projection for post-commit runtime publication, but exclude
runtime-only stats from completion's durable expectations, mutations and save
baseline using the existing StatCollection policy. Persist vital percentages as
before. Log rejected completion status/reason so future deferrals are visible.
Do not change DAO/schema, XP amounts, target identity or other mission mechanics.

Use the approved Windows build, preserve local overlays/private catalogs, and
restart for Mike's standing local-testing request. No suites are requested.
Original-client verification of a reward that levels the character is pending.

Approved Windows core compilation and git diff --check succeeded. No fixture,
unit, regression or DAO suites were run.

Evidence and validation: `docs/reports/ALL_FIVE_MISSION_OBJECTIVES.md`.
