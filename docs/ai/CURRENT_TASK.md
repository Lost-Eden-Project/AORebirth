# Current Task

Integrate mission chests into current public master in this isolated worktree.

- Branch: codex/mission-chests-master-integration.
- Starting master: 4f66c4c20d0e6f5f6e300082d81482944b028680.
- Chest source: 72e2ee62a49ff0a72d953aa43fc9c99c376ace8f (all five unique commits).
- Preserve current Unique/overflow/inventory, WorldEngine routing, doors/NPC visibility and lifecycle behavior.
- Exclude typed mission content. Preserve saved chest populations and existing loot policy.
- Run requested focused tests, isolated MySQL and connected synthetic-client acceptance with bounded timeouts/owned cleanup.
- Do not alter running Windows/Linux servers, actual player databases, private GameData or schema; never control the AO client.
- User corrected publication destination to existing PUBLIC origin/subarumike/AORebirth. Commit/push only this feature branch after validation; no master merge or deployment.
- Report: docs/reports/MISSION_CHEST_MASTER_INTEGRATION.md.
