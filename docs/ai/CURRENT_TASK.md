# Current Task

Implement full unloading of empty ordinary playfields after a grace period.

Base: public master `6ec06bbd45e72496923275dcc8fd4e25fecace73`.
Branch: `codex/idle-playfield-unload`.

PROVEN by source inspection: ordinary playfields previously remained loaded
with active heartbeat threads until manager shutdown. Existing cell heat and
quest-dungeon cleanup did not retire ordinary worlds.

Implemented policy: a 30-second manager sweep retires an ordinary playfield
after ten minutes continuously empty of players, admissions and transfers.
Queued work must be drained before retirement. NPC maintenance does not reset
the player-empty clock. Unloading discards transient NPC, corpse and respawn
state; the next entry rebuilds the world on demand.

Admissions protect login, reconnect, respawn, queued destination callbacks and
transfer completion. Existing logout inventory flush/snapshot ordering and
quest-dungeon cleanup remain in place. Retirement stops the heartbeat and
disposes the world before releasing its per-playfield caches and metrics.
A cleanup failure blocks replacement rather than overlapping two worlds.

Validation: approved Windows core-engine build PASS; source/lock review and
diff checks completed. Automated suites were not requested or run. Live client
acceptance, timed idle unloading/re-entry and measured resource recovery remain
UNVERIFIED. No production deployment is included.

See `docs/reports/EMPTY_PLAYFIELD_UNLOAD.md` for scope and remaining risks.
