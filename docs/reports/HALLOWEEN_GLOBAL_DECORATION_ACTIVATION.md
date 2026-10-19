# Global Halloween decoration activation

Date: 2026-10-19. Branch: `codex/halloween-activation`.
Input commit: `c47c365d9dfe0f980f28cbc5c854643de0c3f371`.

## Scope and result

The current private GameData contains **316 `halloween_decor` placements in 18
playfields**. Exactly **221 use the six supplied decoration hashes in 12
playfields**; the remaining **95 are unresolved PPOA**. These totals were
recalculated from current files, including overlapping event memberships.

Mike explicitly authorized applying the six mappings to all existing authored
decoration records in this task. That expands the preceding capture-bounded
selection: **35 existing bindings are preserved and 186 are added**, giving
221 individually active decoration placements. This does not claim every
location has independent retail-client capture or rendering acceptance.

The existing authored hash identifies the selected template; proximity does not
assign it. Every selected row independently joins the recovered authored
placement manifest by playfield, district, hash and original float32 position.
All 221 have valid structure, matching numeric/manifest hashes and unique
placement keys. No position, radius, heading, placement identity or unrelated
metadata is changed, and no layout is copied between playfields.

## Complete current inventory

Names below are the actual private `metadata.json` names. PF655's metadata is
Andromeda; it includes the previously tested ICC decoration area. Hash counts
are placements, not event memberships. All values in the last column remain
inactive and unbound.

| PF | Metadata location name | Placement counts by hash | Total decor | Previously active | Newly active | Active after | Unresolved PPOA |
| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: |
| 540 | Old Athen | AUPU 13; KELE 10; BRSK 3; SLHD 3; CXNP 1; PJUQ 1 | 31 | 0 | 31 | 31 | 0 |
| 566 | Newland City | AUPU 11; KELE 6; BRSK 1; SLHD 7; CXNP 2; PJUQ 1 | 28 | 0 | 28 | 28 | 0 |
| 635 | Stret East Bank | PPOA 10 | 10 | 0 | 0 | 0 | 10 |
| 650 | Upper Stret East Bank | PPOA 10 | 10 | 0 | 0 | 0 | 10 |
| 655 | Andromeda | AUPU 17; KELE 8; BRSK 2; SLHD 3 | 30 | 15 | 15 | 30 | 0 |
| 665 | Broken Shores | PPOA 10 | 10 | 0 | 0 | 0 | 10 |
| 710 | Omni-1 Trade | AUPU 25; KELE 5; BRSK 4; SLHD 3; CXNP 1; PJUQ 1 | 39 | 0 | 39 | 39 | 0 |
| 760 | 4 Holes | PPOA 10 | 10 | 0 | 0 | 0 | 10 |
| 790 | Stret West Bank | PPOA 10 | 10 | 0 | 0 | 0 | 10 |
| 791 | Holes in the Wall | PPOA 10 | 10 | 0 | 0 | 0 | 10 |
| 800 | Borealis | AUPU 11; KELE 5; BRSK 3; SLHD 2; CXNP 1; PJUQ 1 | 23 | 20 | 3 | 23 | 0 |
| 6300 | SBC-Xpm Site Alpha-Romeo 21 | AUPU 3; KELE 2; BRSK 1; SLHD 4; PPOA 5 | 15 | 0 | 10 | 10 | 5 |
| 6301 | SBC-Xpm Site Alpha-Romeo 22 | AUPU 3; KELE 2; BRSK 1; SLHD 4; PPOA 5 | 15 | 0 | 10 | 10 | 5 |
| 6302 | SBC-Xpm Site Alpha Romeo 23 | AUPU 3; KELE 2; BRSK 1; SLHD 4; PPOA 5 | 15 | 0 | 10 | 10 | 5 |
| 6303 | SBC-Xpm Site Alpha Romeo 24 | AUPU 3; KELE 2; BRSK 1; SLHD 4; PPOA 5 | 15 | 0 | 10 | 10 | 5 |
| 6304 | SBC-Xpm Site Alpha Romeo 25 | AUPU 3; KELE 2; BRSK 1; SLHD 4; PPOA 5 | 15 | 0 | 10 | 10 | 5 |
| 6305 | SBC-Xpm Site Alpha Romeo 26 | AUPU 3; KELE 2; BRSK 1; SLHD 4; PPOA 5 | 15 | 0 | 10 | 10 | 5 |
| 6306 | SBC-Xpm Site Alpha Romeo 27 | AUPU 3; KELE 2; BRSK 1; SLHD 4; PPOA 5 | 15 | 0 | 10 | 10 | 5 |
| **Total** | **18 playfields** | | **316** | **35** | **186** | **221** | **95** |

| Hash | Original item template | Object | Selected placements |
| --- | ---: | --- | ---: |
| AUPU | 301382 | Street Party Pumpkin Lights | 98 |
| KELE | 301387 | Skeleton | 48 |
| BRSK | 301385 | Bronto Skeleton Head | 20 |
| SLHD | 301386 | Skeleton Head | 46 |
| CXNP | 301384 | Tombstone | 5 |
| PJUQ | 301383 | Coffin | 4 |
| **Total** | | | **221** |

## Existing activation and preservation

Each selected row uses its existing optional `staticTemplateId` field and the
existing `HashSpawnSystem` -> `SpawnService.SpawnStaticInstance` -> `WorldItem`
path. No runtime C# changes, global hash catalog changes, new content owner or
separate Halloween spawner are required.

All 221 selected records have exactly `halloween_all` and `halloween_decor`.
Only those two existing memberships are activated. All 95 PPOA records retain
their inactive `halloween_all`, `halloween_decor`, `halloween_pvp_uph` overlap.
There are no unrelated memberships on the selected rows; the runtime's existing
unrelated-event veto remains covered by regression tests.

`events.halloween.enabled=false` suppresses all 221 selected points regardless
of their active flags. ON allows them subject to their flags and all existing
template, placement, cell, chance and scheduler validation. The setting remains
restart-controlled; no placement is admitted twice for overlapping tags.

The previously implemented 0.4 m three-dimensional, closest-compatible,
one-to-one capture matcher remains unchanged. Its exact/millimeter/rounding and
ambiguity regressions are retained. Existing authored records do not need to
move to agree with captured coordinates; a missing capture at another playfield
does not invalidate the explicit six-hash mapping authorized in this task.

Exactly **186 template properties and 372 `active:true` properties** are added
across the existing private Spawns files for PF540, PF566, PF655, PF710, PF800
and PF6300-6306. Removing only those insertions restores every complete original
file byte-for-byte and by full JSON semantic comparison. Existing 35 records,
all PPOA records, all metadata, all catalogs and the other 13 seasonal files are
unchanged. Original template definitions supply appearances; no model or mesh
substitution is introduced.

| Global accounting | Before | After |
| --- | ---: | ---: |
| Unique seasonal placements | 1,186 | 1,186 |
| Seasonal Spawns files | 25 | 25 |
| `halloween_decor` placements | 316 | 316 |
| Selected static bindings | 35 | 221 |
| Active seasonal memberships | 236 | 608 |
| Seasonal points eligible with global ON | 118 | 304 |
| Seasonal points eligible with global OFF | 0 | 0 |
| PF665 UNPU / GRUC eligible with ON | 73 / 10 | 73 / 10 |
| PF665 PPOA inactive | 10 | 10 |
| Added / removed / duplicate placements | 0 / 0 / 0 | 0 / 0 / 0 |

The 304 eligible seasonal points comprise 221 decorations and the preserved
83 PF665 NPC placements. PF665 is byte-identical. No authored AAAA placement
rows were found; the existing AAAA diagnostic catalog/fallback and its fixture
behavior remain unchanged. Quests, combat, shops, rewards, dialogue, button
behavior, native client-resident statics and ordinary content are unchanged.

## Validation and delivery

PROVEN by automated/offline validation:

- Approved Windows core build PASS using
  `cmd /d /c tools\build_aorebirth_debug.cmd`.
- **56 focused runtime tests PASS, zero failed/skipped**, with the actual
  private GameData explicitly selected. All 221 decoration points load with ON,
  construct/register their original item templates and produce valid spawn
  message bodies; all 12 playfields admit zero selected points with OFF.
- One actual selected placement in each of the 12 playfields passes nearby
  player publication, actual packet codec round-trip, duplicate-publication
  prevention, cell-sleep despawn and wake/republication. These are automated
  bound-session checks, not live client-rendering verification.
- Global inventory test passes: 221 mapped/bound/active rows with the stated
  per-hash/per-playfield counts and 95 inactive, unbound PPOA rows. Existing
  conditions, AAAA fallback, ordinary content, NPC lifecycle and PF665 tests
  still pass. Private-data construction tests bypass chance/timer only;
  separate existing lifecycle tests exercise the ordinary scheduler.
- **22 coordinate-matching tests PASS**, preserving exact, millimeter, rounding,
  3D-distance, closest-pair, one-to-one, duplicate-callback, incompatible-template
  and ambiguity behavior.
- Scoped byte/semantic preservation, complete inventory, zero-duplicate checks,
  independent selector/manifest review and `git diff --check` pass.

Focused commands:

```cmd
cmd /d /c dotnet test AORebirth\Server\ZoneEngine_New.Tests\ZoneEngine_New.Tests.csproj --configuration Debug --nologo -p:HalloweenActivationFocusedTests=true --logger trx --results-directory build-verify\halloween-global-decorations-20261019\test-results
cmd /d /c python -m unittest discover -s Tools\tests -p test_halloween_placement_matching.py -v
```

The private-data opt-in variable was explicitly set to the authoritative private
GameData for the runtime run. No full unrelated suites were run. Build/test
warnings remain diagnostic; no runtime code was changed to satisfy tests.
Logs and TRX remain private in the ignored validation directory.

The approved `restart-engines.cmd` workflow completed successfully. ChatEngine,
LoginEngine and ZoneEngine_New pass the normal core process/listener ownership
checks. Fresh startup confirms `halloweenEnabled=True` from the existing local
configuration. The core servers are **left running for Mike's testing**, as
requested. Optional WebEngine remains inactive. Shipped Gameplay defaults are
unchanged; the local testing override remains ON.

Public files changed: `HalloweenDecorationTests.cs` and this report. The tests
now cover actual private bindings in every one of the 12 selected playfields,
including locations beyond ICC and Borealis, without copying private placements
or assets into fixtures.

Private files changed: the 12 existing `GameData/Playfields/<PF>/Spawns.json`
files listed above. Private data is excluded from the public commit. Exact
selectors, per-record identity checks, metadata names, before/after fingerprints,
preimages and the applied audit remain under ignored
`build-verify/halloween-global-decorations-20261019/`.

Inspected files include current startup/authority/workflow/testing rules,
private Spawns and metadata, item/NPC/monster catalog fingerprints, recovered
authored manifest/template definitions, preceding integration and coordinate
reports, the existing offline matcher, decoration tests and private scoped
update helpers/audits. Current unrelated configuration/documentation changes
are preserved and excluded from the commit.

Live original-client rendering remains UNVERIFIED. Automated server/session
tests cannot prove client rendering at every location. No AO client, capture
tool, database schema change, Linux port or production deployment is performed.
