# Halloween decoration integration

Date: 2026-10-19. Branch: `codex/halloween-activation`.
Input: phase 1 commit `193cb6daa3cc422b3be3efda8046b46e7d968a92`.

## Cause and existing mechanism

OBSERVED: every private `halloween_decor` membership was inactive. The six
decoration hashes also had no item or NPC catalog binding. The global ON switch
correctly did not override those inactive flags. Merely enabling the rows would
not resolve their original item templates through `GameDataStore`.

The existing item catalog maps a whole hash. Adding six global mappings would
change classification for 221 existing placements, including unobserved records
whose original resolution remains unknown. That mechanism cannot express the
evidence-supported subset. The smallest targeted extension is an optional
`StaticTemplateId` on the existing `PlayfieldSpawnEntry`. No second population,
spawn framework, event system or placement file was introduced.

`HashSpawnSystem.LoadSpawns` first applies the existing structural and event
conditions. An explicit template must be positive and exist in the actual item
catalog; otherwise the placement is skipped, without falling back to a different
item or diagnostic NPC. Valid explicit bindings use the existing
`SpawnStaticBranches` -> `SpawnService.SpawnStaticInstance` -> `WorldItem` path.
Unbound records retain their existing hash resolution, including `AAAA`.

## Evidence and bounded selection

The existing recovery reports and their retained packet/definition artifacts
were inspected read-only: `AO_HALLOWEEN_BOREALIS_PLACEMENT_CAPTURE_006.md` and
`AO_HALLOWEEN_DECORATION_ZONE_CAPTURE_005.md`, their placement comparisons,
observation correlations, world-item ledgers, original resource chains and
provenance. The 19 Borealis callbacks represent 18 objects; one object was
delivered twice. The retained raw packet bytes and recomputed SHA256 agree for
all 19 Borealis callbacks and all 15 ICC world callbacks.

PROVEN: received SimpleItem object -> original item template -> visual resource.
No proposed template mapping was rejected:

| Hash | Original template | Object | PF800 selected | PF655 selected |
| --- | ---: | --- | ---: | ---: |
| AUPU | 301382 | Street Party Pumpkin Lights | 8 | 5 |
| KELE | 301387 | Skeleton | 4 | 6 |
| BRSK | 301385 | Bronto Skeleton Head | 2 | 2 |
| SLHD | 301386 | Skeleton Head | 2 | 0 |
| CXNP | 301384 | Tombstone | 1 | 0 |
| PJUQ | 301383 | Coffin | 1 | 0 |
| **Total** | | | **18** | **13** |

SUPPORTED, not a proven native ACG terminal bridge: the selected authored
placement -> captured object associations. Each selected observation has one
eligible authored placement within the report's diagnostic coordinate window.
No captured object transmits the original placement ID/hash, and none has exact
XYZ equality with its authored location. These are explicit curated bindings
on the selected existing rows; they are not promoted to global hash mappings.
Original authored coordinates, rotations and radius remain unchanged; no
grounding correction or captured-transform substitution was invented.

PF800 district 2 selected source records: 75-86, 89, 92-95 and 97. Records 87,
88, 90, 91 and 96 remain inactive and unbound. The 23 authored decorations were
not equated to 18 captured objects.

PF655 district 35 selected source records: 104-107, 111, 119, 120, 123 and 124.
District 60 selected source records: 0, 1, 6 and 8. Its two vertical-mismatch
candidates, records 4 and 11, remain inactive and unbound. No evidence supports
extending these bindings to the remaining playfields.

## Private GameData accounting

Only the existing private `Playfields/800/Spawns.json` and
`Playfields/655/Spawns.json` are changed. Each selected record gains one explicit
template binding and `active:true` on its two existing event memberships.
Private data and detailed backups/audits remain outside the public commit.

| Inventory | Before | After |
| --- | ---: | ---: |
| Unique Halloween placements | 1,186 | 1,186 |
| `halloween_decor` placements | 316 | 316 |
| Six-hash decoration placements | 221 | 221 |
| Selected explicit decoration bindings | 0 | 31 |
| Active Halloween event memberships | 166 | 228 |
| PF800 six-hash rows / eligible ON / inactive | 23 / 0 / 23 | 23 / 18 / 5 |
| PF655 six-hash rows / eligible ON / inactive | 30 / 0 / 30 | 30 / 13 / 17 |
| Six-hash rows remaining inactive globally | 221 | 190 |
| PPOA decoration rows remaining inactive globally | 95 | 95 |
| PF665 UNPU / GRUC eligible ON | 73 / 10 | 73 / 10 |
| PF665 PPOA inactive | 10 | 10 |
| New placements / removed placements | 0 / 0 | 0 / 0 |

The 31 selected points are eligible with global ON and all their memberships
active, subject to normal cell, chance, timer and spawn validation. Global OFF
admits zero selected decoration points. Inactive unrelated overlapping events
remain vetoes. The setting is still restart-controlled and shipped OFF.

Detailed private preservation results and preimages are retained under ignored
`build-verify/halloween-decorations-20261019/`. No other seasonal files, global
hash catalogs or the PF665 file are changed. No placements are duplicated.

## Static objects and visibility

The recovered objects are server-delivered SimpleItems, so they belong to the
existing runtime `WorldItem` owner. That owner allocates/registers each dynel in
the existing registry/locality and constructs `SimpleItemFullUpdateMessage`
using its original low/high template IDs, quality, position, heading and name.
The existing codec supplies the client template identity used to load its
authored appearance. No replacement model, mesh override or packet format was
introduced.

`SpawnService.LoadStaticDynels`, ordinary authored objects, vendors and native
client-resident statels remain unchanged. `SpawnSource.StaticDynel` objects have
existing client-resident visibility behavior; they are distinct from runtime
hash `WorldItem` objects. This work does not claim that the Halloween switch
hides client-loaded scenery.

## Files and validation

Public files changed:

- `AORebirth/Libraries/Source/AORebirth.Core/GameData/PlayfieldSpawnsData.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Playfield/HashSpawnSystem.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Playfield/SpawnContentValidation.cs`
- `AORebirth/Server/ZoneEngine_New/Core/Playfield/SpawnService.cs`
- `AORebirth/Server/ZoneEngine_New.Tests/HalloweenActivationTests.cs`
- `AORebirth/Server/ZoneEngine_New.Tests/HalloweenDecorationTests.cs`
- `AORebirth/Server/ZoneEngine_New.Tests/ZoneEngine_New.Tests.csproj`
- This report.

Additional inspected owners: `GameDataStore`, `HashItemCatalog`, `HashInstance`,
`HashItemMinter`, `ItemTemplateCatalog`, `ItemBuilder`, `StaticDynel`, `WorldItem`,
playfield service registration, locality visibility and the existing SimpleItem
codec/name tests. Current startup, authority, standards, workflow and testing
documents were read. Existing unrelated work was preserved.

PROVEN by automated validation:

- Approved Windows core build PASS (`cmd /d /c tools\build_aorebirth_debug.cmd`).
  Existing compiler/dependency warnings remain; no build wrapper was changed.
- Focused regression run PASS: **45 passed, 0 failed, 0 skipped**, including the
  actual private GameData and item definitions. The full unrelated suite was
  not run. The first new-test compilation required a signed-bitmask cast fix;
  the first execution exposed an incorrect new assertion about decoded string
  terminators. Both test defects were corrected against existing codec behavior;
  no production packet code was changed to satisfy them.
- All six actual private templates resolve with their original mesh IDs and
  quality 1. Their existing WorldItem packets serialize/deserialize with correct
  identities, templates, quality, transforms, names and visibility flags.
- Bound player sessions receive nearby runtime objects once, then despawn and
  replacement publication after cell sleep/wake. Client-resident statics retain
  existing behavior. These are automated session tests, not an AO client test.
- Actual private PF800/PF655 loader registration admits 18/13 explicit decoration
  points with ON and zero with OFF. Each selected record constructs/registers
  one WorldItem with its exact selected template, without a global hash alias.
  These private-data spawn checks bypass only probabilistic chance/timer;
  separate lifecycle tests exercise the ordinary scheduler.
- Missing/nonpositive bindings fail closed. A binding does not propagate to an
  unbound row with the same hash; its diagnostic fallback remains functional.
  Inactive placement and unrelated overlapping-event conditions still veto.
- Phase 1 regressions pass, including all nine tags, ordinary NPC populations,
  PF665 73 UNPU/10 GRUC/zero PPOA, death delay, despawn and respawn.
- Private offline byte/semantic preservation, inventory and duplicate checks
  pass. Removing exactly the 31 template and 62 activation insertions restores
  both complete original files byte-for-byte. PF665 remains byte-identical.
- Scoped diff review and `git diff --check` pass.

Focused command (with the private-data environment variable explicitly set):

```cmd
cmd /d /c dotnet test AORebirth\Server\ZoneEngine_New.Tests\ZoneEngine_New.Tests.csproj --configuration Debug --nologo -p:HalloweenActivationFocusedTests=true --logger trx --results-directory build-verify\halloween-decorations-20261019\test-results
```

Build/test logs, TRX, private preimages and the exact edit audit remain in the
ignored validation directory. The source/report/test commit is public; these
private files and the two private GameData edits are not included in Git.

## Remaining limits

Live original-client rendering and OFF/ON across real server restarts remain
UNVERIFIED. Automated server/packet validation cannot prove client rendering.
No AO client, capture tool, database or production/Linux deployment was used.
The approved Windows build stops local backend engines; they were not restarted
for this task.

The 190 unselected six-hash records, 95 PPOA decoration rows, original unresolved
ACG terminal bindings and client-resident scenery remain outside this repair.
THII, TEIA, THIL, quests, rewards, dialogue and button behavior are unchanged.
