# Current Task

Complete generated Find Item missions and make quest reward delivery durable.

Branch: `codex/find-item-reward-integrity`, based on installed `40caa10b8`.
PROVEN: the base implementation accepted Find Item missions without a bound or
spawned objective; completion and rewards were separate best-effort writes.

The candidate freezes an existing GameData item reference and exact owner/quest/
instance/placement during acceptance, spawns it in a connected room, validates
ClientGetItem against that binding and atomically records collection. Existing
mission DAO transactions and reward ledger deliver configured items/cash/XP
with completion, retain earned pending rewards across expiry/restart and refuse
duplicate payment. Existing snapshots fence stale writes; reconnect reconciles
closed-mission keys. No new catalog, capture loading or schema migration.

Approved engine build, 153 focused mission/dungeon cases and 320 real-MySQL DAO
checks passed. Automated success is separate from original AO client acceptance,
which remains UNVERIFIED and belongs to Mike. Existing
accepted Find Item records are unchanged; test with a newly accepted mission.
The other four objective types and independent dungeon loot remain out of scope.

Implementation, evidence, validation and limitations:
`docs/reports/FIND_ITEM_REWARD_INTEGRITY.md`.
