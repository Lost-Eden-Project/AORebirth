# Current Task

Repair mission-objective pickup and increase pickup-objective visibility on
`codex/all-mission-objectives`, based on installed `25650e666`.

OBSERVED: Shade's mission D2086788889 received six ClientGetItem requests but
did not collect its world item. PROVEN: the registered pickup codec reads only
the target identity, leaving inherited message.Identity unset. The handler
incorrectly required that absent field to equal the authenticated player.
Remove only that comparison, preserving session, exact owner/binding, dungeon,
range, inventory and transactional validation. No DAO/schema/reward changes.

Add optional WorldScalePercent to the existing item policy and frozen accepted
binding. Find Item and Return Item use 150 through existing static-dynel Scale
stats, omitted after collection into inventory. Existing unscaled accepted
bindings remain valid and are not rewritten; new missions use the size policy.
No new catalog or packet shape. The original client's rendering and complete
pickup/reward sequence require Mike's verification. No suites are requested.

Approved Windows core compilation, policy JSON parsing and git diff --check
passed. Install locally with the existing friendly-item overlay preserved,
then leave the core engines running for Mike's ongoing client testing.

Implementation and validation: `docs/reports/ALL_FIVE_MISSION_OBJECTIVES.md`.
