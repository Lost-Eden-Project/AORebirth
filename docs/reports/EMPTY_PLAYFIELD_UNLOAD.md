# Ordinary empty-playfield unload

## Problem and policy

PROVEN by source inspection: ordinary playfields stayed in `PlayfieldManager`
and ran their heartbeat until manager shutdown. Cell heat reduced work inside
a field; existing empty-dungeon release applied only to generated quest
dungeons. Neither implemented ordinary-world retirement.

The selected policy fully unloads ordinary worlds after ten minutes empty.
Transient NPC health, corpses, dynamic world state and respawn timers are
discarded. A subsequent entry uses the existing on-demand world build.
Quest-dungeon lifetime and content definitions are unchanged.

## Implementation

- A manager-owned timer checks ordinary worlds every 30 seconds. Player,
  admission or transfer activity resets the empty clock. NPC rebases and
  other maintenance can defer a final check without restarting that clock.
- Under its existing tick lock and a narrow lifecycle lock, a world can mark
  itself retiring only with no players (including link-dead players),
  admission reservations, transfers, queued inbound work or pending rebases.
- Admission reservations cover login/reconnect packet preparation through
  spawn enqueue, destination callbacks, respawn acquisition, and a transfer's
  entire existing completion/return lifecycle. Accepted queue work replaces
  short-lived login reservations as the owner of pending entry.
- Retiring worlds reject new work and stop ticking. The manager keeps the old
  instance published as retiring until its heartbeat/world disposal and cache
  release finish. New acquisition waits outside the manager lock; background
  destination callbacks recheck the instance and rebuild off the tick if needed.
- Disposal and heartbeat joins run outside the manager and tick locks. The
  timer is drained outside the manager lock during shutdown. The existing
  two-second heartbeat stop timeout now fails closed: incomplete cleanup
  leaves the field unavailable and logged, rather than permitting a replacement
  to overlap partially disposed state.
- After successful disposal, the existing data owner removes reloadable
  per-field geometry, metadata, spawns, NPC content, appearance overrides and
  door-list caches. Global item/NPC/XP catalogs and permanent routing indexes
  remain. The exact field's metrics are removed before replacement is allowed.
- Heartbeat exit removes its diagnostic thread slot, including any retained
  dynel references. Snapshot readers exclude retiring fields.

The established last-player inventory hard flush and character snapshot
commit still precede unregistering and releasing online ownership. No schema,
packet format, content source or Linux-specific runtime behavior is added.

## Inspected and changed source

All paths are beneath `AORebirth/Server/ZoneEngine_New/Core/`:

- `Playfield/PlayfieldManager.cs`: timer, guarded acquisitions/callbacks,
  retirement completion and cache/metrics ownership.
- `Playfield/Playfield.cs`: occupancy, admissions, queued work and retirement.
- `Playfield/PlayfieldAdmission.cs`: exactly-once destination reservation.
- `Playfield/PlayfieldInboundQueue.cs`: pending queue detection.
- `Playfield/PlayfieldHeartbeat.cs`: thread cleanup and bounded stop failure.
- `Playfield/PlayfieldTransfer.cs`: destination reservation through completion.
- `MessageHandlers/ZoneLoginHandler.cs`: login/reconnect reservations.
- `Network/ZoneSession.cs`: reserve before scheduling departure.
- `Entities/Player.cs`: respawn destination reservation.
- `Quests/Dungeons/QuestDungeonService.cs`: reserve ordinary return/exterior
  destinations; existing quest cleanup is unchanged.
- `GameData/GameDataStore.cs`: targeted per-playfield cache release.
- `Metrics/TickStallWatch.cs`: release terminated heartbeat references.

Also inspected: `SpawnService`, `ACGPlayfield`, `PlayfieldWorldSimulation`,
`CellHeatScheduler`, `PlayfieldMetricsRegistry`, team/locality ownership and
current development/build authority. No changes were made to those owners.
Task/project status documents and this report accompany the candidate.

## Validation and remaining risks

Approved Windows core-engine build: PASS. Independent source reviews covered
admission ownership, transfer failure/return paths, lock order, shutdown,
cache ownership and retained diagnostic references. Diff checks passed.
Fixture, unit and regression suites were not requested and were not run.
No engines or game client were started, and no production deployment occurred.

UNVERIFIED: timed unloading and re-entry in a running server, live login,
reconnect/zoning/respawn behavior, persistence under failure, and the amount of
CPU/RAM actually recovered. Collection eligibility does not guarantee an
immediate RSS reduction: global catalogs remain and the managed allocator can
retain committed pages. Grace is observed on the 30-second sweep and retirement
waits for transient owner work to drain.

Existing baseline behavior retained: disconnected cross-playfield respawn calls
destination arrival from the source tick even though arrival requires the
destination tick. This candidate reserves that destination but does not repair
the separate respawn owner defect. Existing quest-dungeon admission/sweep races
also remain outside this ordinary-world policy.

An initial nested CMD invocation failed quoting before the build wrapper ran
and changed no repository state. The corrected form runs
`NewZoneEngineBuild\build.cmd > build-verify\empty-playfield-unload-build-final.log 2>&1`
directly in `cmd.exe`; the approved wrapper completed successfully.
