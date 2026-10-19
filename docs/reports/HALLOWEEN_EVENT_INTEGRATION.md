# Halloween activation: phase 1

Date: 2026-10-18. Branch: `codex/halloween-activation`.
Base public source: `b7faf6b57753a36d9c9fd7d63017db64d6a95bf0`.

## Result

PROVEN by compilation and focused automated validation: existing `ZoneEngine_New`
hash populations now use one startup-loaded Halloween switch in addition to
their existing placement/event conditions. The selected private PF665 data
contains 73 enabled UNPU and 10 enabled GRUC placements. Its 10 PPOA placements
remain inactive. The shipped setting is OFF.

Live AO client appearance, combat, loot and complete Halloween gameplay remain
UNVERIFIED. No client, backend engine, production server or database was started
or modified for gameplay verification. The normal build wrapper performed its
existing local engine cleanup. No Linux reconciliation, packaging or deployment
was performed for this unmerged feature branch.

## Configuration and semantics

The existing `AORebirth/Config/Gameplay.json` now contains:

```json
"events": {
  "halloween": {
    "enabled": false
  }
}
```

`GameplaySettings` supplies the same OFF default if the section is omitted and
rejects explicit null event objects. `HashSpawnSystem` captures the setting at
construction through the existing per-playfield dependency injection. The
ordinary startup configuration log now includes `halloweenEnabled` and the
resolved configuration path.

- OFF vetoes every placement carrying any of the nine exact Halloween tags,
  even when every placement membership is active.
- ON removes that global veto only. Every named event must still have
  `Active=true`; existing placement/template/cell validation still applies.
- Unrelated events, empty event names and overlapping memberships retain their
  existing semantics. Tag overlap creates no additional placement.
- The classifier uses exact event identities, including the separate 2019 tag.
  A future or merely similar event name is not silently classified.
- The `AAAA` fallback, authored-template lookup and NPC/static routing are
  unchanged. No template-existence gate was added to hide diagnostic placements.

Configuration remains startup-only. To activate this candidate later, set
`events.halloween.enabled=true` in the configuration selected by the normal
`GameplaySettings.ResolvePath` mechanism and restart `ZoneEngine_New` normally.
That mechanism prefers `Config/Gameplay.json` beside the selected GameData tree,
then the process's `Config/Gameplay.json`. Use the startup-reported path rather
than assuming the source-tree copy is selected. OFF followed by a normal restart
removes the old process's population and admits no Halloween hash points in the
new process. No hot switching, player-specific event state, database schema or
new event/spawn framework was introduced.

## Actual tags and unchanged inventory

These identities and membership counts were read from the private GameData,
not inferred from an event-name prefix:

| Tag | Memberships before and after |
| --- | ---: |
| `halloween_all` | 1,185 |
| `halloween2019_all` | 1 |
| `halloween_uph` | 747 |
| `halloween_decor` | 316 |
| `halloween_pvp_uph` | 190 |
| `halloween_pvp_uph_boss` | 7 |
| `halloween_quests` | 8 |
| `halloween_fc_shop` | 8 |
| `halloween_buttons` | 3 |
| **Total memberships** | **2,465** |

Memberships overlap; they are not unique placement counts.

| Inventory / eligibility | Before | After |
| --- | ---: | ---: |
| Global unique Halloween placements | 1,186 | 1,186 |
| Halloween hashes / playfield files | 25 / 25 | 25 / 25 |
| PF665 total placement rows | 612 | 612 |
| PF665 seasonal rows | 93 | 93 |
| PF665 UNPU rows, levels 125-150 | 73 | 73 |
| PF665 GRUC rows, levels 200-250 | 10 | 10 |
| PF665 PPOA rows, level 1 | 10 | 10 |
| Explicitly active Halloween memberships globally | 0 | 166 |
| PF665 eligible seasonal points with global OFF | 0 | 0 |
| PF665 eligible seasonal points with global ON | 0 | 83 |
| PF665 inactive PPOA placements | 10 | 10 |
| PF665 unchanged non-Halloween rows | 519 | 519 |
| PF665 unrelated DSNA birthday placements within those rows | 201 | 201 |

## Private data edit and publication boundary

Only existing records in the authoritative private
`D:/AORebirth-fresh/GameData/Playfields/665/Spawns.json` were edited. Exact
existing hash/numeric hash, level bounds, district, ordered event membership and
preimage checks selected the 73 UNPU and 10 GRUC records. Each has two named
event memberships; exactly 166 `active:true` fields were inserted. All ten PPOA
records and their 30 memberships retain omitted/default-false activation.

PROVEN by offline comparison: removing precisely those inserted fields restores
the complete original JSON semantics and original bytes. Coordinates, hashes,
manifest identities, rotations, respawn values and unrelated metadata are
unchanged. The other 24 seasonal files are byte-identical. No placements were
imported, regenerated, appended, removed or duplicated.

The private data file is ignored and is **not part of the public commit**. The
local backup and detailed audit are retained under ignored
`build-verify/halloween-activation-20261018/`:

- `PF665.Spawns.before.json`: untouched preimage.
- `PF665.private-data-audit.json`: identities, exact selectors, hashes and
  preservation/count results.
- `enable_private_pf665.py`: scoped, fail-closed one-time edit helper.

These private artifacts were not copied into public GameData, a fixture, Linux
runtime data or a public Git repository. The source branch and the local private
data are separate delivery outputs; another checkout needs the separately
maintained private data selection before expecting the same 83-point candidate.

## Other activation paths inspected

| Existing owner | Evidence / action in this phase |
| --- | --- |
| `HashSpawnSystem.LoadSpawns` | Global condition applied to all nine exact tags, alongside current `Active` conditions. Covers both NPC and item/static hash points. |
| District populations | Parsed 630 district files, 4,204 districts and 7,322 population entries. No exact binding to the 25 Halloween-tagged hashes was found. No speculative district classification or behavior change. |
| Authored NPCs/vendors | Five catalogs contain 22 NPCs and seven standalone shops; no exact Halloween binding was identified. Ordinary activation unchanged. |
| Authored quest props | Current interaction data has seven manifests, 43 missions and two ordinary PF6553 props; no exact Halloween binding was identified. Ordinary activation unchanged. |
| `SpawnService.LoadStaticDynels` | Inspected exact `Dynels.dat` identity/template/transform construction. No seasonal record bridge was established. Ordinary map objects, terminals and vendors remain unchanged. |
| Client-loaded scenery | No actual client visibility/state-control mechanism was validated. The switch does not claim to hide client-resident statics or `1000003` scenery. |

Future exact seasonal definitions using another existing owner will need the
same global condition in that owner. This phase does not identify ordinary
objects as seasonal from a name, nearby coordinate or shared asset.

Source owners inspected include `HashSpawnSystem`, `SpawnService`,
`Playfield`, `PlayfieldDistrictsData`, `NpcContentActivationService`,
`NpcContentDefinitions`, `QuestPropService` and `InteractionContent`.

## Files changed

Public source/test/report changes:

- `AORebirth/Config/Gameplay.json`: default-OFF setting.
- `AORebirth/Server/ZoneEngine_New/Core/Configuration/GameplaySettings.cs`:
  typed settings, OFF defaults and null validation.
- `AORebirth/Server/ZoneEngine_New/Core/Playfield/HashSpawnSystem.cs`:
  startup setting injection, exact nine-tag classification and global veto.
- `AORebirth/Server/ZoneEngine_New/Program.cs`: startup setting visibility.
- `AORebirth/Server/ZoneEngine_New.Tests/HalloweenActivationTests.cs`:
  focused configuration, activation, private-data and lifecycle tests.
- `AORebirth/Server/ZoneEngine_New.Tests/ZoneEngine_New.Tests.csproj`:
  existing opt-in focused-test pattern, including spawn-heading regressions.
- This report.

Private local content change: `GameData/Playfields/665/Spawns.json` as described
above. Existing dirty engine configuration, project/task notes and unrelated
untracked files were preserved and excluded from the feature commit.

## Validation performed

**Approved Windows core build: PASS.** Command:

```cmd
cmd /d /c tools\build_aorebirth_debug.cmd
```

This builds the existing ChatEngine/LoginEngine/ZoneEngine_New graph and
DatabasePreflight. It does not run the AO client. Initial attempts stopped before
compilation because CMD could not use the oversized inherited PATH; the same
approved wrapper succeeded with a short process-local replacement containing
Windows system directories, Windows PowerShell and the installed .NET SDK.
No repository build/lifecycle wrapper was changed.

**Focused tests: PASS, 29 passed / 0 failed / 0 skipped.** The private-data
environment variable was explicitly set to the authoritative local GameData for
this run; the PF665 test was executed, not skipped:

```cmd
set AO_REBIRTH_HALLOWEEN_GAMEDATA_PATH=D:\AORebirth-fresh\GameData
cmd /d /c dotnet test AORebirth\Server\ZoneEngine_New.Tests\ZoneEngine_New.Tests.csproj --configuration Debug --nologo -p:HalloweenActivationFocusedTests=true
```

The focused selection follows the test project's existing opt-in pattern; it
does not suppress or change the default suite. Full unrelated suites were not
run. Tests use the actual runtime assembly and GameData loader/hash registration:

- Missing-section defaults, explicit ON, invalid/null settings and settings
  reload/recreated spawn system.
- Every exact tag with OFF/ON, and inactive memberships remaining blocked.
- Overlap without duplicate points, repeated initialization, unrelated inactive
  events, ordinary events, empty names and nonmatching prefixes/case.
- Existing structural validation and `AAAA` diagnostics.
- Actual NPC construction/registration, occupied-point duplication prevention,
  cell sleep/despawn and wake, `DespawnAsDeath`, respawn delay and subsequent
  respawn through the existing scheduler. This is not client combat/corpse proof.
- Actual private PF665 loader registration: OFF zero, ON exactly 73 UNPU and
  10 GRUC, zero PPOA, unique point keys and unchanged source bytes.
- Existing spawn-heading regressions.

Private offline edit validation separately covers the complete 1,186-placement
inventory, absence of new duplicates and preservation of unrelated bytes/data.
`git diff --check` and scoped review passed. Build/test logs, TRX and the private
audit remain under ignored `build-verify/halloween-activation-20261018/`.

## Remaining limits

- No live AO client test was performed. Mike's original-client verification is
  still required for models, movement/combat/nanos, death, corpse loot, respawn
  presentation and OFF/ON across a real restart.
- Default configuration remains OFF; this task did not launch an event or deploy
  the feature. The 83 slots become eligible only when the selected configuration
  is ON and the server restarts, subject to ordinary cell activation.
- Other seasonal placements remain inactive. Unresolved PPOA and other original
  identities were not assigned replacement templates, and `AAAA` was preserved.
- The earlier content inventory found 331 placements without authored NPC/item
  bindings, plus specific incomplete UNPU loot descendants. This phase does not
  claim to resolve those original-content gaps.
- No speculative Halloween quest, reward, vendor stock, button result, dialogue
  or client scenery mechanism was implemented.
