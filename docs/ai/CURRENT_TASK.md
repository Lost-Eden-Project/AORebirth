# Current Task

TEAM ROSTER PUBLICATION REPAIR (2026-10-16).

Mike authorized repairing duplicate team-window entries in ZoneEngine_New,
focused packet-publication regression tests, the approved Windows build, and
commit/push reporting. Base: public master
b8cf9323371f67581bef1e4301eab6dd1306bc56. Branch: codex/team-roster-deltas.
Work is isolated from Mike's dirty local installation in an attached worktree.

PROVEN BASE DEFECT: successful invitation acceptance called SendRoster for every
teammate, reannouncing rows already published. Per-packet projection epochs could
discard an initial roster when another join/removal occurred before its owner
queue drained. SpawnService skipped team refresh after zoning, so a retained
client could miss changes made during the transfer.

Repair within the existing TeamService membership authority, owner dispatcher,
session fences, wire models, chat bridge and stats. Track published client rows
separately from authoritative membership. Publish complete initial rosters to
new clients and only missing additions/removals to retained clients. Preserve
initial ordering, invitation validation and raid reassignment ordering. A
published row means handed to the accepted session, not client acknowledgment.

No packet-format, content, database, Linux or deployment changes. Mike explicitly
requested focused automated tests for this task. Run the approved Windows core
build and relevant team/reconnect tests. Actual AO-client rendering after joins
and zoning remains UNVERIFIED until supported by client evidence.

IMPLEMENTED: one fenced owner callback reconciles the latest authoritative team
against rows submitted to that client's accepted session. Initial delivery keeps
the existing self-first handshake; established clients receive additions and
removals only. Raid moves retain the existing remove/re-add fields and ordering.
Zoning keeps published history; a fresh client discards it and receives a full
current roster. Local stats and team/raid chat remain coherent when queued work
is superseded, including leave/rejoin into the same surviving team.

VALIDATION: approved Windows core build PASS; 73 focused TeamService test cases
PASS; scoped independent source review and git diff --check PASS. Tests inspect
the emitted packets, including initial ordering, C/D deltas, concurrent/replayed
acceptance, deferred/reordered callbacks, zoning missed changes, removals,
disband, inactive stats, chat recovery and raid movement/conversion ordering.

Focused command (explicitly requested by Mike):

```cmd
dotnet test AORebirth\Server\ZoneEngine_New.Tests\ZoneEngine_New.Tests.csproj -p:TeamRosterFocusedTests=true --filter FullyQualifiedName~TeamServiceTests
```

The default project compile remains blocked by six missing-type errors in three
unchanged public-master mission-test files. The existing opt-in focused-compile
pattern selects team tests without changing the default/full suite. Two stale
team fixture references were updated to current StuckService/feedback APIs;
runtime behavior was not changed to satisfy those stale references.

UNVERIFIED: actual AO-client team-window rendering and end-to-end production
SpawnService reconnect execution. The production reconnect hook was source
reviewed; owner/session packet reconciliation was tested directly. No client,
server startup, database or deployment actions were performed.
