# Halloween decoration coordinate matching

Date: 2026-10-19. Branch: `codex/halloween-activation`.
Input commit: `e91598e83b99e13c0f8e7c39aa470c9dc70666a9`.

## Findings

The previous 31 selected decorations did not require exact captured coordinates:
their measured 3D discrepancies range from 0.005952 m to 0.026438 m, with median
0.011077 m. Exact float32 comparisons in the previous private update helper
located the same authored record in the current GameData; they did not compare
captured object coordinates with placements. Those identity-preserving authored
record joins remain appropriate and do not alter any coordinates.

Two separate selection problems were found:

- PF655 district 60 records 4 and 11 were excluded solely by the old diagnostic
  `abs(dY) <= 0.05` window. Their compatible, unused captured pumpkin-light
  objects are 0.352910035 m and 0.353379083 m from the authored coordinates.
- PF800 district 2 records 88 and 91 were absent from capture 006's world objects,
  but the previous review omitted the later existing capture 007. That capture
  supplies compatible pumpkin-light/skeleton objects at 0.010194701 m and
  0.010999886 m. They even satisfy the original research windows. Their earlier
  unresolved status was an incomplete evidence search, not floating-point
  equality or an unavailable original hash bridge.

The cause of the approximately 0.35 m vertical discrepancies is UNVERIFIED.
They are not described as floating-point rounding. Their independent captured
template identities, compatible hashes, close locations, lack of competing
placements and one-to-one availability support the selected associations.

## Matching rule and evidence

`Tools/halloween_placement_matching.py` implements the offline matching step.
The runtime continues consuming permanent editable placement bindings; it never
reads captures. This does not add another spawning system or change the older
NPC SCFU exact-evidence classifier.

- Require the same playfield and an explicitly supplied hash/template mapping.
- Measure `sqrt(dX*dX + dY*dY + dZ*dZ)` using all three coordinates.
- Use **0.4 m inclusive** for this bounded evidence set. The tolerance is an
  explicit argument, not a global gameplay setting or universal identity rule.
- Allocate the closest still-eligible pair first across the batch, preserving
  one-to-one ownership of both placements and captured objects. Equal-distance
  competing candidates remain ambiguous rather than depending on input order.
- Deduplicate identical callbacks for one captured identity; conflicting
  observations, invalid numbers and duplicate placement identities fail closed.
- Report nearest compatible and nearest any-template candidates even when they
  are outside the tolerance. Nearness alone cannot override template mismatch.
- Never modify input records, authored coordinates or metadata.

Units follow current AORebirth runtime conventions: `CharacterMotor` names plain
coordinate distances `RemainingPathMeters` and compares coordinate XYZ drift
against `RootDriftMeters`; locality uses `reachMeters` in that same space.
One coordinate unit is treated as one metre, so 0.001 unit is one millimetre.

The 0.4 m threshold covers the measured 0.353379083 m maximum with a small
margin. For the two PF655 additions, the next compatible placement is about
16 m away, with best/second distance gaps greater than 15.65 m. Thresholds
0.4 m and 0.5 m select the same two additional PF655 pairs; 0.05 m and 0.1 m
exclude them. After capture 007 is included, the nearest compatible candidate
for any remaining inactive record is 27.971953 m away. This separation supports
a bounded tolerance without extrapolating
to unrelated objects.

Evidence inspected includes Halloween recoveries 005 and 006 and their retained
authored placements, object ledgers, original template chains and packet
provenance. The later 007 button capture is also relevant to decoration
observations; interaction semantics remain outside this task. Recovery 008's
items are parented inventory objects and provide no parentless decoration
placement evidence. Detailed source identities, measured deltas, input digests
and one-to-one allocation results stay in the private audit.
The indexed 001-008 recovery set was checked: 001/002 describe content packages,
003 is a prospective capture plan, and 004 concerns PF665 attack identifiers.
They supply no additional PF655/PF800 decoration world-item ledger. Across
005/006/007, all 74 retained world callbacks agree with raw CSV hexadecimal bytes
and recomputed packet SHA256. The 16 decoration identities repeated in 006/007
have identical templates and XYZ; identity-based deduplication retains source
provenance without treating callbacks as new objects.

## Selection and preservation

The six previously verified original templates are retained: AUPU/301382,
KELE/301387, BRSK/301385, SLHD/301386, CXNP/301384 and PJUQ/301383.
No unavailable native hash bridge is required to admit a compatible supported
positional association. These associations do not claim to reconstruct the
original native terminal mapping.

Four additional existing records are selected, preserving all previous 31:

| Playfield | Before active | Added | After active | Still inactive |
| --- | ---: | ---: | ---: | ---: |
| PF655 | 13 | 2 | 15 | 15 |
| PF800 | 18 | 2 | 20 | 3 |
| **Total** | **31** | **4** | **35** | **18** |

Only four `staticTemplateId` fields and eight `active:true` fields are inserted
into the existing private PF655/PF800 files. Original coordinates, hashes,
placement identities, overlapping tags and unrelated metadata remain unchanged.
The complete private data is not published in Git.

Global inventories remain 1,186 seasonal placements, 316 `halloween_decor`
placements and 221 six-hash decoration placements. Active event memberships
increase from 228 to 236. Global ON eligibility increases from 114 to 118
(83 PF665 NPC points plus 35 decorations); OFF admits zero seasonal points.
All 95 PPOA decoration rows remain inactive. PF665 remains byte-identical with
73 UNPU and 10 GRUC active and 10 PPOA inactive. Diagnostic `AAAA` and ordinary
content resolution remain unchanged; no placements are added or duplicated.

## Per-record decisions

The complete 22-record review records nearest captured identity/template,
captured-minus-authored XYZ, 3D distance, compatibility, existing ownership,
previous rejection reason and current decision. It is retained privately under
`build-verify/halloween-coordinate-matching-20261019/` with the exact numeric
JSON audit. Deltas are captured minus authored, not proposed corrections. The
table below reports nearest compatible distances; the detailed private review
also gives the nearest any-template identity and its XYZ delta for every row.
For SLHD rows no compatible PF655 template was captured, so their nearest
any-template alternative is shown explicitly.

| PF/district:record | Hash | Nearest compatible distance, m | Previous reason | Current decision |
| --- | --- | ---: | --- | --- |
| 655/35:108 | AUPU | 39.618961 | Outside X/Z research window | Inactive: beyond 0.4 m |
| 655/35:109 | AUPU | 38.902216 | Outside X/Z research window | Inactive: beyond 0.4 m |
| 655/35:110 | AUPU | 38.013120 | Outside X/Z research window | Inactive: beyond 0.4 m |
| 655/35:112 | AUPU | 91.757263 | Outside X/Z research window | Inactive: beyond 0.4 m |
| 655/35:113 | AUPU | 108.884562 | Outside X/Z research window | Inactive: beyond 0.4 m |
| 655/35:114 | AUPU | 104.321417 | Outside X/Z research window | Inactive: beyond 0.4 m |
| 655/35:115 | AUPU | 101.111777 | Outside X/Z research window | Inactive: beyond 0.4 m |
| 655/35:116 | AUPU | 105.946248 | Outside X/Z research window | Inactive: beyond 0.4 m |
| 655/35:117 | AUPU | 83.816086 | Outside X/Z research window | Inactive: beyond 0.4 m |
| 655/35:118 | AUPU | 57.302515 | Outside X/Z research window | Inactive: beyond 0.4 m |
| 655/35:121 | KELE | 65.116949 | Outside X/Z research window | Inactive: beyond 0.4 m |
| 655/35:122 | KELE | 65.114893 | Outside X/Z research window | Inactive: beyond 0.4 m |
| 655/35:125 | SLHD | None; nearest any 301387 at 73.967269 m | Compatible template unobserved | Inactive: incompatible template |
| 655/35:126 | SLHD | None; nearest any 301382 at 72.160323 m | Compatible template unobserved | Inactive: incompatible template |
| 655/35:127 | SLHD | None; nearest any 301382 at 71.125337 m | Compatible template unobserved | Inactive: incompatible template |
| 655/60:4 | AUPU | 0.352910 | X/Z passed; Y exceeded 0.05 m | Activate: unique compatible pair |
| 655/60:11 | AUPU | 0.353379 | X/Z passed; Y exceeded 0.05 m | Activate: unique compatible pair |
| 800/2:87 | AUPU | 27.971953 | Outside X/Z research window | Inactive: beyond 0.4 m |
| 800/2:88 | AUPU | 0.010195 | Capture 007 omitted; no nearby object in 006 | Activate: unique compatible pair |
| 800/2:90 | BRSK | 59.048888 | Outside X/Z research window | Inactive: beyond 0.4 m |
| 800/2:91 | KELE | 0.011000 | Capture 007 omitted; no nearby object in 006 | Activate: unique compatible pair |
| 800/2:96 | AUPU | 66.828990 | Outside X/Z research window | Inactive: beyond 0.4 m |

All nearest compatible alternatives for the 15 distance-rejected rows belong to
other selected placements after allocation; none is reused. The three remaining
SLHD rows cannot borrow an incompatible skeleton or pumpkin template merely
because one is the nearest object. No remaining record is rejected for a small
coordinate discrepancy or an unavailable original hash bridge.

## Validation and limits

PROVEN by automated/offline validation:

- Approved Windows core build PASS using
  `cmd /d /c tools\build_aorebirth_debug.cmd`. Existing dependency/compiler
  warnings remain; no build/lifecycle workflow was changed.
- **22 matching tests PASS** using
  `cmd /d /c python -m unittest discover -s Tools\tests -p test_halloween_placement_matching.py -v`.
  Covers exact/zero-distance, millimeter and rounded coordinates, true XYZ
  distance, inclusive tolerance, closest candidate, order invariance, one-to-one
  ownership, repeated/conflicting callbacks, ambiguous competitors, unrelated
  templates/playfields, invalid numeric input and input preservation.
- The actual private normalized input (53 authored rows, 55 distinct world
  identities from the scoped captures) produces **35 matches**, preserves all
  31 previous object associations, adds exactly the four listed pairs, and leaves
  18 placements unmatched. Objects from another playfield cannot bind here.
- **45 focused runtime tests PASS, zero failed/skipped**, with private GameData
  explicitly selected. PF800 admits 20 selected decorations with ON and zero
  with OFF; PF655 admits 15 with ON and zero with OFF. Their actual original
  templates construct/register through the existing WorldItem path. The six
  template/packet/nearby-publication and cell-lifecycle checks still pass.
- Phase 1 conditions, ordinary content, `AAAA` diagnostics, NPC lifecycle and
  PF665 73 UNPU/10 GRUC/zero PPOA regressions pass.
- Exact byte and full semantic restoration pass: removing only the four binding
  and eight activation insertions restores both previous private files. All
  previous 31 rows, other 23 seasonal files, PF665 and complete item/NPC/monster
  catalogs retain their prior bytes. Placement counts and duplicate-key checks
  pass with zero additions/removals/duplicates.
- Independent scoped matcher review and `git diff --check` pass.

Runtime test command:

```cmd
cmd /d /c dotnet test AORebirth\Server\ZoneEngine_New.Tests\ZoneEngine_New.Tests.csproj --configuration Debug --nologo -p:HalloweenActivationFocusedTests=true --logger trx --results-directory build-verify\halloween-coordinate-matching-20261019\test-results
```

`AO_REBIRTH_HALLOWEEN_GAMEDATA_PATH` was explicitly set to the authoritative
private dataset for that run. No full unrelated suites were run. Detailed
runtime test limitations remain as documented in the preceding integration
report: automated sessions and server construction do not prove client rendering.
No gameplay or packet implementation is modified for this matching correction.

Public files changed: the offline matcher, its synthetic test file,
`AORebirth/Server/ZoneEngine_New.Tests/HalloweenDecorationTests.cs` (updated
private-data counts), and this report. Private files changed:
`GameData/Playfields/655/Spawns.json` and `GameData/Playfields/800/Spawns.json`.
Private data, normalized capture inputs, record decisions, original preimages,
applied-update audit, logs and TRX remain in ignored
`build-verify/halloween-coordinate-matching-20261019/`, outside the public commit.

Files inspected include the startup/authority/standards/workflow/testing rules,
the preceding integration report, current decoration tests, `CharacterMotor`,
locality unit conventions, existing exact/nearest offline correlation owners,
the prior private selection helper/audit, the private spawn files/catalog hashes
and the indexed Halloween reports and associated captured-object artifacts.

No AO client, capture tool or production/Linux deployment was used. Live-client
rendering and restart acceptance remain UNVERIFIED. The global Halloween switch
still loads at startup, defaults OFF, and respects each placement's conditions.
No quest, reward, dialogue, button behavior or client-loaded scenery activation
is added. Existing unrelated configuration and documentation work is preserved.
