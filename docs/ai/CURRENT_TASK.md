# Current Task

MISSION CHEST IMPLEMENTATION 001 (2026-10-17).

Feature branch: codex/mission-chests, derived from public master
749f819fdf764ce0cc9debd17304607e71bbf363. Development is isolated in the
attached mission-chests worktree. Preserve the original checkout's dirty work,
private full GameData and accepted local engines. No engine replacement,
public-master merge or Linux deployment is authorized by this task.

Implemented: captured original chest spawn/contents protocol, independent
JSON-configured rarity-group loot selection, frozen-layout collision-checked
placement, atomic mission-DAO population seal, persisted inventory source guards,
reconstruction and existing-lifecycle cleanup. Ordinary chests only; traps and
locked generation remain separate work.

Mike explicitly approved AORebirth rarity group weights Missing=70, 1=20, 2=6,
3=2.5, 4=1, 5=0.5, with empty-group renormalization before uniform item selection.
These are gameplay policy, not original numeric rarity definitions.

PROVEN evidence: original mission-20261011-004128-b8e7bf2e ordinal 480,
155-byte ChestFullUpdate, SHA256
E86620C76173EE0D943B94C144C40E4EDBA87B5F27B406CD93E62884EB42FD31.
External research/capture artifacts were read only under Mike's explicit scope.

Validation: final focused run compiled and all 56 chest cases passed;
34 disposable real-MySQL checks passed. Inventory regression is 23 passed/two
failed on BOTH this candidate and an untouched exact-public-SHA checkout.
Combined result: 79 passed/two proven public-baseline failures, zero skipped.
Existing objective/reward coverage is blocked by its unchanged stale handler
fixture constructor; no pass is claimed. Original-client acceptance is
UNVERIFIED. Full integration details and limitations are in
../MISSION_CHEST_INTEGRATION.md.

The selected private DungeonGeneration.json received only the validated Chests
section, with rollback copy under ignored build-verify and all other properties
verified unchanged. No player DB/schema mutation, capture startup, AO client
control, accepted-engine lifecycle operation or deployment was performed.
