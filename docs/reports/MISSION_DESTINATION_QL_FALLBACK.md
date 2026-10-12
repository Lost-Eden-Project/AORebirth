# Configurable destination fallback for unobserved QLs

## Failure and authority

PROVEN: installed c3ddc1e5e rejected Shade's centered Borealis rolls on
2026-10-12 at level 34, expected QL34, Omni, difficulty detent 6 and all six
secondary sliders zero. The terminal is 0000DAC1:C0020320 in playfield 800 at
(629.798, 66.8001, 713.0741). All 2242 placements and 812 supported destinations
loaded; exact-origin QL34 and QL-wide QL34 both had zero candidates.

The complete 93185-record per-offer ledger contains 92830 resolved offers and
355 unresolved offers, with no current or original expected-QL34 record.
Runtime data has 47 terminal pools and 45 observed QLs. Private/public pools
matched before the change. This is a genuine unobserved combination, not an
eligibility exclusion or a QL-calculation defect. Failure occurs before offer
publication and its credit transaction.

Mike explicitly approved configurable fallback after being told that this
replaces his earlier prohibition on neighboring-QL reuse. The policy is an
AORebirth gameplay choice, not reconstructed Funcom eligibility or probabilities.

## Selection policy

`Missions/Destinations/MissionDestinations.json` now enables the optional
`AllowNearestTerminalQlFallback` flag. Omitted or false retains exact-only
behavior. `MissionDestinationCatalog` remains the single selection owner:

1. Use the exact originating-terminal-playfield and expected-QL pool.
2. Otherwise use the union of observed destinations at that exact QL.
3. Only if both fail and the flag is true, use the nearest captured QL pool
   for that same originating terminal playfield. Equal distances choose the
   lower QL deterministically.
4. If that origin has no pool, the request remains unsupported and uncharged.

For Borealis QL34, step 3 selects all 184 existing identities from Borealis's
QL35 pool. Andromeda QL34 uses its QL33 pool of 192 identities. The requested
mission remains QL34, including objective/reward calculations. Existing uniform
identity selection with replacement and five-offer generation are unchanged.
No artificial observed pool, weighting, coordinate, entrance or capture record
is added. Future missing QLs use the same configurable rule; it also applies
outside the captured QL range, without claiming observed high-QL coverage.

Logging distinguishes NEAREST_ORIGIN_QL_FALLBACK, requested expectedQl and
selected poolQl, and explicitly marks cross-QL eligibility UNPROVEN. Unknown
origins do not gain a nearest-QL cross-origin fallback. Actual generation and
diagnostics use the same catalog method.

## Scope and validation

Changed runtime owners: MissionDestinationCatalog.cs,
MissionDestinationCatalogData.cs, QuestAlternativeMessageHandler.cs and
GeneratedMissionRollService.cs. The latter only changes the selection comment
and unsupported message; mission generation mechanics remain unchanged.
The only GameData change is the optional flag in the existing destination file.
MissionEntrancePlacements.json, all destination pool contents, mission levels,
DAO/schema, acceptance, dungeon generation, tokens and rewards are unchanged.

Inspected the above owners, MissionLevelRuntime, MissionLevels.csv, the installed
log and the complete per-offer distribution ledger. Approved Windows core build,
git diff --check and data comparison passed: only the policy flag changed;
2242 placements, 812 supported identities, 45 observed QLs and 47 pools remain
intact. Independent source review found no concrete defect in the lookup,
metadata or unchanged reward-quality path. No fixture/unit/regression suites
were requested or run. Existing
fixtures that insist QL34 is always unsupported describe the superseded policy
when the new flag is enabled; they were not run, changed or bypassed.
Original-client QL34 five-offer, acceptance and dungeon verification is pending.
