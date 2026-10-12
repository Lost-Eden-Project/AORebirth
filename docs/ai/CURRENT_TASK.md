# Current Task

Finish all five generated solo mission objectives together on
`codex/all-mission-objectives`, based on installed `9f59727bd`.

Kill Person and Find Person freeze an exact catalog NPC and actor identity.
Find Item preserves its working pickup path. Repair Machine supplies its exact
tool at acceptance and consumes it against the bound machine. Return Item
records collection separately and consumes the item at the issuing terminal.
All five reuse the existing quest transaction and recoverable reward ledger.

Approved Windows core compilation passed. No unit/fixture/regression
suites are requested or run. Mike performs one original-client gameplay pass with
newly accepted missions. Team acceptance, bosses, independent loot and retail
random-selection parity remain outside this objective integration.

Repair/Return items must be used from main inventory, matching the existing
atomic inventory-consumption contract. Abandonment/expiry retire those exact
artifacts with the existing key transaction. Failed exact NPC-death commits
retry while the player remains loaded; a wholly uncommitted event lost during
an outage/restart can require killing the reconstructed target again.

Implementation and validation: `docs/reports/ALL_FIVE_MISSION_OBJECTIVES.md`.
