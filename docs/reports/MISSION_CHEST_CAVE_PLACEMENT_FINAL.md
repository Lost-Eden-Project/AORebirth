# Final cave mission chest placement corrections

Completed 2026-10-25. Scope: Windows/public feature branch only. **READY_FOR_MASTER_INTEGRATION_REVIEW_WITH_DOCUMENTED_BASELINE_EXCEPTIONS**. Both original zero cases now produce validated accessible chests; the final 104-layout campaign has zero empty populations. This is an automated candidate assessment, not original-client acceptance or deployment approval.

## Identity and scope

| Identity | Exact value |
| --- | --- |
| Starting integration | `7d638408a13ab517a2bb497e51e4076630c9161d` |
| Previously validated implementation | `752a0d1bdbb28a590fecca23002befa6080d9618` |
| Final implementation | `152a5d2cac40e955b1d085363677477c8140ad0b` |
| Preserved master | `4f66c4c20d0e6f5f6e300082d81482944b028680` |
| Preserved original chest branch | `72e2ee62a49ff0a72d953aa43fc9c99c376ace8f` |
| Feature branch | `codex/mission-chests-master-integration` |
| Public origin | `https://github.com/subarumike/AORebirth.git` |
| Final runtime source SHA-256 | `688d93b03e2913cd1ed41708a3986c01ad2a5ebe4b0d586bb40ae9a2ba302895` |
| Tested isolated ZoneEngine_New.dll SHA-256 | `5623cca09bcb8cd5c48c040e73393ea1936a90e80866b21296aa14b6da93ad80` |

Implementation commit pushed successfully; `git ls-remote origin refs/heads/codex/mission-chests-master-integration` returned the exact implementation SHA. This report, aggregate CSV and task receipt are delivered in a separate documentation-only child commit. The tested working tree was based on the starting SHA; the fixture prints that parent as SOURCE_SHA. Exact source hashes above identify the changed source actually built, and those bytes were committed unchanged. No claim is made that an unmodified starting binary contains these fixes.

## Proven causes

### Variable-height wall contacts: seed 34928853

**PROVEN:** The two eligible rooms have different geometry. `Mine_Big16_1` room 3 contains 432 flat ground triangles at Y=69 and no wall mesh; its rejection remains correct. `WildCave_Medium16` room 4 has floor heights 66.4–69 and 1,152 wall triangles. Cutting walls only at the room median and then projecting the chest down to the actual floor displaced the horizontal contact: an observed wall gap of 0.814 exceeded the allowed approximately 0.711. This was an incomplete wall proposal, not justification to relax wall adjacency.

Actual median/minimum/maximum support heights now seed a bounded wall pool. Original triangle identity removes duplicate proposals of the same physical triangle at different heights. Each sampled point retains its random fraction, inward direction and offset, then re-slices that same triangle at its actual local floor plus chest-center height, at most twice. Missing local intersections are rejected. The final accepted small-cave pose is on the actual Y=66.4 floor, not an invented high rim.

### Raised supported route omitted: seed 82433732

**PROVEN:** The first meaningful connectivity cut is inside `Mine_Big16_3` room 3. A direct chord from a doorway at Y=261 to the pit floor at Y=256 crosses a genuinely steep bank. At the observed sample, planner projection and runtime snap both return Y=260.55054, producing a 0.44946 step against the unchanged 0.4 limit. The supporting ground triangle has normal Y=0.27216554; reverse sampling also fails. This is not a proven overhead-probe or transform defect.

**PROVEN:** Actual full collision geometry contains a raised, standable bent bridge absent from the coarse ground-only anchor proposal source. The evidence run found 24 standable upper raw-face centroids; ground-only structural points remained below that bridge. Some bridge supports lie approximately 5.51 above the coarse ground. With raw support proposals added, positive entrance-route witnesses use actual raised points. A remaining fixed-seed failure sampled the west and north legs but omitted the turn; its diagonal correctly hit the edge. Successful runs included that supported turn.

The existing walking-anchor search now proposes actual upward faces from the same cell, within its ground-height envelope plus the existing step margin. Actual own-ground comparison identifies distinctly raised faces and gives them priority within existing structural slots. Random ground proposals remain every other attempt. All proposals still pass existing projection, occupancy and route checks. The configured 128-attempt / 40-accepted default anchor budget and maximum four complete searches are unchanged; the chest count is rolled once.

**OBSERVED:** Tested transition landing projections remained within approximately 0.011 and retained own-floor support. No transform or floor-transition change was justified or made. Final raw room-3 route witnesses include `(42.358326,261.505,32.975002)` and `(36.648327,261.5117,32.991673)`; they are actual post-projection points replayed through the existing movement checks, not proof inferred from raw proposal counts.

## Files changed and preserved mechanics

- `AORebirth/Server/ZoneEngine_New/Core/Quests/Dungeons/MissionChestPlacement.cs`: walking discovery at lines 610–726; wall sampling at 764–778; same-triangle refinement at 1032–1058; opt-in detailed rejection and per-search diagnostics. Verbose triangle/probe details remain opt-in.
- `AORebirth/Server/ZoneEngine_New.Tests/MissionChestPlacementTests.cs`: eight fixed placement-seed checks, 104 unique actual cave layouts, independent pose/approach/route validation, bounded diagnostic cases and per-layout output.
- `AORebirth/Server/ZoneEngine_New.Tests/MissionChestObjectiveIntegrationTests.cs`: observed publication diagnostics, captured cell-context regression at line 32, and correct fresh registration for arbitrary test teleports. Production locality behavior is unchanged.
- `docs/ai/CURRENT_TASK.md`, this report, and `docs/reports/MISSION_CHEST_CAVE_PLACEMENT_FINAL_LAYOUTS.csv`: task receipt and aggregate evidence.

Only the placement runtime file changes production behavior. No loot policy, rarity weights, QL clamps, item limit, inventory/ownership/Unique/overflow code, DAO, schema, packet, routing, objective runtime or generated-layout persistence file changed. No typed mission content was integrated. Existing one-per-room selection, nonadjacent-room preference, floor balancing, inward orientation, doorway/objective exclusion, collision tests, support/pocket checks and required-route preservation remain in force. Discovery never creates an unconditional graph edge or directly approves a chest.

Previously sealed populations are read before generation prerequisites in `AORebirth/Server/ZoneEngine_New/Core/Quests/Dungeons/MissionChestPopulation.cs:67–69`; new planning occurs only for an uninitialized population at lines 100–102. Existing identities, positions, contents, exhausted chests and sealed empty populations remain frozen. Population regressions cover these cases; the actual MySQL and TCP evidence is separated below.

## Exact before and after

All rows below use actual style 341 geometry. The two original comparisons use placement seed 7919 and the unchanged policy.

| Layout seed | Floors / size | Eligible | Before requested / placed | Final requested / placed | Final candidates / reachable | Final floors |
| --- | --- | ---: | --- | --- | --- | --- |
| 34928853 | 2 / 10 | 2 | 1 / 0 | 1 / 1 | 1 / 1 | 0:1 |
| 82433732 | 5 / 12 | 24 | 8 / 0, 49 candidates | 8 / 5 | 46 / 26 | 0:2, -1:3 |

The five-floor case is corrected from zero, but does not attain its eight-chest target on this roll. It is not reported as full density or as proof that no other safe location exists. Four independent fixed placement seeds test each of the two layouts, with one Plan call per case:

| Layout seed | Placement seed | Requested | Placed | Wall center min–max |
| --- | --- | ---: | ---: | --- |
| 34928853 | 7919 | 1 | 1 | 0.680108–0.680108 |
| 34928853 | 104729 | 1 | 1 | 0.702593–0.702593 |
| 34928853 | 32452843 | 1 | 1 | 0.693445–0.693445 |
| 34928853 | 49979687 | 1 | 1 | 0.703043–0.703043 |
| 82433732 | 7919 | 8 | 5 | 0.679764–0.710357 |
| 82433732 | 104729 | 8 | 7 | 0.670540–0.701715 |
| 82433732 | 32452843 | 7 | 7 | 0.663406–0.706146 |
| 82433732 | 49979687 | 7 | 7 | 0.661026–0.700996 |

## Final actual-geometry campaign

**PROVEN: 104/104 tests passed, 104 complete artifact rows, 104 distinct input tuples and layout seeds, zero positive-target empty populations, no timeouts.** The cohort contains the four required layouts plus 100 fixed randomized additional layouts, sizes from the actual profile range, and 1–5 floors. Floor-count coverage: 21 single-floor, 21 two-floor, 20 three-floor, 20 four-floor, 22 five-floor layouts. Each case invokes Plan once, with only the existing bounded internal searches. Per-case limit: 60 seconds; outer limit: 40 minutes. Maximum measured case: 19.966 seconds; final test run: 8.0395 minutes.

Across 4,396 cells and 1,467 eligible rooms, 442 chests were requested and 413 placed in 413 distinct per-mission rooms: target density 30.13%, actual density 28.15%. Fourteen layouts have a combined 29-chest shortfall; 90 meet the rolled target. There are no remaining zero cases in this cohort. Fifty-nine layouts place across multiple floors. Wall center distances range 0.66085356–0.7103574; physical sphere-edge gaps range 0.05048555–0.099989414.

Every returned chest passes independent actual-wall, room eligibility, collision, floor and approach checks; 413 interaction approaches and 413 entrance routes are validated. All 7,735 initially reachable required anchor groups retain an independently replayed route. There are 93 lost peripheral anchor samples: group preservation does not mean every optional sample point remains reachable. The original runtime cardinal support guards remain unchanged; the independent test verifies center support and the full approach pocket rather than claiming a new independent chest-footprint solver.

The campaign records candidate counts and causes from the selected complete search, and retains every internal pass in the ignored raw JSONL. Shortfall equality is required where compatible validated candidate evidence proves the requested capacity; otherwise it remains an explicit shortfall. This bounded search is not a complete proof of physical impossibility.

Floor coverage is measured, not assumed. Layouts 503763222, 122465018 and 389134278 have eligible rooms on multiple floors but only one floor with reachable candidates/placements in the selected search. Their reachability completeness is unproven. The representative five-floor seed 42 places six campaign chests across four floors; a separate runtime population test requires a positive population on multiple floors.

### Every layout

Full precision counts, per-cause rejections, per-room candidate/reachability counts, floor maps, approach/route counts, required-route lengths, preserved groups, lost samples and timing are in [the aggregate CSV](MISSION_CHEST_CAVE_PLACEMENT_FINAL_LAYOUTS.csv). It contains metrics only: no raw mesh, templates, item records, database contents or credentials. The compact table below uses rounded wall distances. For every row, distinct rooms, independently validated approaches and entrance routes equal the placed count.

| Seed | Floors / size | Cells | Eligible | Requested | Placed / rooms | Candidates / reachable | Wall center min–max | Chests by floor |
| --- | --- | ---: | ---: | ---: | --- | --- | --- | --- |
| 34928853 | 2 / 10 | 17 | 2 | 1 | 1 / 1 | 1 / 1 | 0.680108–0.680108 | {"0":1} |
| 82433732 | 5 / 12 | 65 | 24 | 8 | 5 / 5 | 46 / 26 | 0.679764–0.710357 | {"0":2,"-1":3} |
| 1775745370 | 1 / 12 | 20 | 8 | 3 | 3 / 3 | 20 / 20 | 0.678413–0.708610 | {"0":3} |
| 42 | 5 / 10 | 67 | 17 | 6 | 6 / 6 | 27 / 24 | 0.663284–0.686660 | {"0":2,"-1":1,"-3":2,"-2":1} |
| 166778190 | 1 / 9 | 7 | 2 | 1 | 1 / 1 | 2 / 2 | 0.673340–0.673340 | {"0":1} |
| 1970936330 | 1 / 16 | 28 | 13 | 3 | 3 / 3 | 29 / 29 | 0.676711–0.694551 | {"0":3} |
| 142945996 | 1 / 15 | 19 | 5 | 1 | 1 / 1 | 9 / 9 | 0.698898–0.698898 | {"0":1} |
| 1438214947 | 1 / 10 | 18 | 8 | 2 | 2 / 2 | 9 / 9 | 0.676059–0.706520 | {"0":2} |
| 2011744993 | 1 / 9 | 13 | 3 | 1 | 1 / 1 | 8 / 8 | 0.672632–0.672632 | {"0":1} |
| 1551255127 | 1 / 16 | 23 | 7 | 2 | 2 / 2 | 10 / 10 | 0.681622–0.696548 | {"0":2} |
| 972508374 | 1 / 13 | 15 | 3 | 1 | 1 / 1 | 1 / 1 | 0.673995–0.673995 | {"0":1} |
| 1848503146 | 1 / 12 | 17 | 2 | 1 | 1 / 1 | 4 / 4 | 0.661663–0.661663 | {"0":1} |
| 243930257 | 1 / 9 | 11 | 5 | 1 | 1 / 1 | 5 / 5 | 0.663596–0.663596 | {"0":1} |
| 1571624996 | 1 / 16 | 23 | 7 | 2 | 2 / 2 | 16 / 16 | 0.669578–0.694660 | {"0":2} |
| 1955929327 | 1 / 13 | 16 | 7 | 2 | 2 / 2 | 14 / 14 | 0.690864–0.696240 | {"0":2} |
| 105735566 | 1 / 16 | 36 | 13 | 3 | 3 / 3 | 38 / 38 | 0.672462–0.681760 | {"0":3} |
| 1374084445 | 1 / 9 | 12 | 4 | 1 | 1 / 1 | 2 / 2 | 0.694108–0.694108 | {"0":1} |
| 73751504 | 1 / 16 | 23 | 6 | 1 | 1 / 1 | 6 / 6 | 0.662922–0.662922 | {"0":1} |
| 997138014 | 1 / 12 | 23 | 7 | 3 | 3 / 3 | 13 / 13 | 0.668335–0.689370 | {"0":3} |
| 2080877115 | 1 / 13 | 19 | 9 | 2 | 2 / 2 | 12 / 11 | 0.699264–0.700090 | {"0":2} |
| 538830320 | 1 / 9 | 16 | 5 | 2 | 2 / 2 | 3 / 3 | 0.679306–0.689075 | {"0":2} |
| 1468092887 | 1 / 16 | 24 | 9 | 2 | 2 / 2 | 22 / 22 | 0.700767–0.705228 | {"0":2} |
| 494122740 | 1 / 13 | 15 | 6 | 2 | 2 / 2 | 4 / 4 | 0.678013–0.708408 | {"0":2} |
| 1168994391 | 1 / 15 | 22 | 9 | 2 | 2 / 2 | 5 / 5 | 0.683698–0.699384 | {"0":2} |
| 992687201 | 2 / 8 | 17 | 5 | 1 | 1 / 1 | 5 / 5 | 0.664851–0.664851 | {"0":1} |
| 162582207 | 2 / 12 | 19 | 6 | 2 | 1 / 1 | 1 / 1 | 0.685508–0.685508 | {"0":1} |
| 866067467 | 2 / 9 | 16 | 4 | 1 | 1 / 1 | 4 / 4 | 0.709129–0.709129 | {"0":1} |
| 623816110 | 2 / 9 | 16 | 5 | 1 | 1 / 1 | 1 / 1 | 0.694406–0.694406 | {"0":1} |
| 1091046171 | 2 / 8 | 15 | 1 | 1 | 1 / 1 | 1 / 1 | 0.689831–0.689831 | {"0":1} |
| 1515294717 | 2 / 12 | 14 | 5 | 2 | 2 / 2 | 6 / 6 | 0.672691–0.673054 | {"0":2} |
| 1808080924 | 2 / 9 | 23 | 8 | 2 | 2 / 2 | 17 / 17 | 0.675473–0.693990 | {"0":2} |
| 438489856 | 2 / 8 | 19 | 6 | 2 | 2 / 2 | 9 / 9 | 0.691387–0.699039 | {"0":2} |
| 1056112075 | 2 / 8 | 14 | 6 | 2 | 2 / 2 | 7 / 5 | 0.664566–0.676692 | {"0":2} |
| 663538929 | 2 / 12 | 17 | 7 | 2 | 2 / 2 | 10 / 10 | 0.671456–0.709369 | {"0":2} |
| 1860103187 | 2 / 11 | 17 | 4 | 1 | 1 / 1 | 8 / 8 | 0.698326–0.698326 | {"0":1} |
| 1740476275 | 2 / 9 | 13 | 6 | 2 | 2 / 2 | 15 / 15 | 0.660854–0.666053 | {"0":2} |
| 605065242 | 2 / 8 | 11 | 1 | 1 | 1 / 1 | 1 / 1 | 0.696686–0.696686 | {"0":1} |
| 466497743 | 2 / 12 | 19 | 6 | 2 | 2 / 2 | 23 / 22 | 0.678432–0.701072 | {"0":2} |
| 902325595 | 2 / 11 | 14 | 5 | 2 | 2 / 2 | 8 / 8 | 0.684194–0.708166 | {"0":2} |
| 1049954690 | 2 / 8 | 16 | 4 | 1 | 1 / 1 | 16 / 16 | 0.697152–0.697152 | {"0":1} |
| 793187139 | 2 / 8 | 13 | 3 | 1 | 1 / 1 | 1 / 1 | 0.686669–0.686669 | {"0":1} |
| 1651325420 | 2 / 12 | 18 | 9 | 3 | 3 / 3 | 20 / 20 | 0.664573–0.678878 | {"0":3} |
| 1760714806 | 2 / 10 | 21 | 5 | 1 | 1 / 1 | 7 / 7 | 0.683965–0.683965 | {"0":1} |
| 920221468 | 2 / 8 | 16 | 7 | 2 | 2 / 2 | 22 / 22 | 0.698907–0.709534 | {"0":2} |
| 503763222 | 3 / 9 | 25 | 8 | 2 | 2 / 2 | 6 / 3 | 0.696968–0.702849 | {"-1":2} |
| 1121688784 | 3 / 14 | 39 | 16 | 4 | 4 / 4 | 28 / 28 | 0.681442–0.695823 | {"0":2,"-1":2} |
| 1419359542 | 3 / 14 | 37 | 14 | 5 | 5 / 5 | 33 / 32 | 0.668942–0.708360 | {"0":2,"-1":3} |
| 2044250118 | 3 / 13 | 34 | 9 | 2 | 2 / 2 | 12 / 12 | 0.690341–0.694786 | {"0":1,"-1":1} |
| 301059014 | 3 / 9 | 32 | 8 | 2 | 2 / 2 | 12 / 12 | 0.691917–0.701416 | {"0":1,"-1":1} |
| 1918302277 | 3 / 14 | 50 | 23 | 6 | 6 / 6 | 40 / 38 | 0.669019–0.708748 | {"0":3,"-1":3} |
| 437947795 | 3 / 13 | 36 | 9 | 3 | 3 / 3 | 12 / 10 | 0.665984–0.690208 | {"0":2,"-1":1} |
| 1920362457 | 3 / 14 | 52 | 22 | 6 | 6 / 6 | 31 / 23 | 0.661811–0.703636 | {"0":2,"-1":4} |
| 1481257511 | 3 / 9 | 36 | 14 | 5 | 5 / 5 | 22 / 22 | 0.669403–0.709339 | {"0":2,"-1":3} |
| 191792052 | 3 / 14 | 45 | 14 | 5 | 5 / 5 | 12 / 10 | 0.661497–0.700912 | {"0":3,"-1":2} |
| 1465031234 | 3 / 13 | 39 | 17 | 5 | 5 / 5 | 24 / 23 | 0.666626–0.709808 | {"0":2,"-1":3} |
| 521587749 | 3 / 10 | 29 | 12 | 4 | 4 / 4 | 18 / 17 | 0.665839–0.706948 | {"0":2,"-1":2} |
| 2037323521 | 3 / 9 | 38 | 11 | 4 | 4 / 4 | 21 / 21 | 0.669280–0.710241 | {"0":2,"-1":2} |
| 1569924831 | 3 / 14 | 42 | 11 | 3 | 3 / 3 | 20 / 19 | 0.667194–0.687790 | {"0":2,"-1":1} |
| 251291635 | 3 / 10 | 28 | 8 | 2 | 2 / 2 | 18 / 18 | 0.664512–0.670938 | {"0":1,"-1":1} |
| 937994646 | 3 / 9 | 30 | 9 | 3 | 3 / 3 | 12 / 12 | 0.676598–0.696852 | {"0":1,"-1":2} |
| 371453013 | 3 / 9 | 38 | 13 | 4 | 4 / 4 | 14 / 14 | 0.676807–0.710283 | {"0":3,"-1":1} |
| 1738777416 | 3 / 14 | 42 | 13 | 5 | 5 / 5 | 20 / 20 | 0.661835–0.708238 | {"0":3,"-1":2} |
| 122465018 | 3 / 9 | 32 | 9 | 3 | 2 / 2 | 12 / 12 | 0.661849–0.671480 | {"-1":2} |
| 1976828582 | 3 / 14 | 41 | 10 | 3 | 3 / 3 | 19 / 19 | 0.689733–0.699577 | {"0":1,"-1":2} |
| 1829760195 | 4 / 9 | 50 | 13 | 4 | 4 / 4 | 21 / 19 | 0.673156–0.700006 | {"0":1,"-1":3} |
| 1274675644 | 4 / 14 | 58 | 22 | 8 | 6 / 6 | 19 / 10 | 0.667148–0.709808 | {"0":1,"-1":5} |
| 568880551 | 4 / 11 | 63 | 18 | 4 | 4 / 4 | 25 / 19 | 0.677457–0.708336 | {"0":1,"-2":2,"-1":1} |
| 1497907209 | 4 / 9 | 46 | 21 | 7 | 4 / 4 | 9 / 5 | 0.667884–0.699989 | {"0":1,"-1":3} |
| 399770865 | 4 / 9 | 47 | 12 | 3 | 3 / 3 | 11 / 11 | 0.688957–0.698127 | {"-2":1,"-1":2} |
| 1391665541 | 4 / 14 | 73 | 26 | 9 | 9 / 9 | 24 / 22 | 0.667984–0.706043 | {"0":2,"-1":4,"-2":3} |
| 1666989886 | 4 / 11 | 63 | 23 | 6 | 6 / 6 | 34 / 19 | 0.676218–0.707863 | {"0":4,"-1":2} |
| 967959212 | 4 / 13 | 68 | 22 | 6 | 6 / 6 | 21 / 20 | 0.671882–0.705373 | {"0":2,"-1":1,"-2":3} |
| 859338178 | 4 / 9 | 50 | 15 | 4 | 4 / 4 | 23 / 22 | 0.668096–0.694783 | {"0":2,"-1":1,"-2":1} |
| 1597306092 | 4 / 14 | 63 | 19 | 6 | 6 / 6 | 26 / 17 | 0.669949–0.707603 | {"0":4,"-1":2} |
| 1969370470 | 4 / 9 | 52 | 18 | 6 | 6 / 6 | 33 / 17 | 0.662777–0.691116 | {"0":4,"-1":2} |
| 1501924008 | 4 / 13 | 60 | 19 | 6 | 6 / 6 | 24 / 17 | 0.669143–0.696521 | {"0":4,"-1":2} |
| 1939697545 | 4 / 9 | 42 | 14 | 4 | 3 / 3 | 12 / 7 | 0.674186–0.698615 | {"0":1,"-1":2} |
| 817584364 | 4 / 14 | 73 | 27 | 9 | 9 / 9 | 42 / 30 | 0.661766–0.703315 | {"0":3,"-1":5,"-2":1} |
| 1226780253 | 4 / 9 | 56 | 20 | 5 | 4 / 4 | 32 / 17 | 0.685874–0.702031 | {"0":3,"-1":1} |
| 1592713223 | 4 / 10 | 58 | 20 | 6 | 6 / 6 | 46 / 40 | 0.663119–0.703094 | {"0":2,"-1":2,"-2":2} |
| 389134278 | 4 / 9 | 59 | 17 | 6 | 2 / 2 | 10 / 2 | 0.672024–0.701736 | {"0":2} |
| 727039136 | 4 / 14 | 52 | 20 | 6 | 6 / 6 | 37 / 33 | 0.671280–0.708374 | {"0":3,"-2":1,"-1":2} |
| 631988407 | 4 / 14 | 69 | 28 | 10 | 10 / 10 | 40 / 31 | 0.668189–0.703400 | {"0":5,"-1":5} |
| 1189173296 | 4 / 12 | 72 | 18 | 5 | 5 / 5 | 25 / 24 | 0.674682–0.704498 | {"0":2,"-1":2,"-2":1} |
| 1411924208 | 5 / 10 | 67 | 22 | 7 | 7 / 7 | 35 / 30 | 0.669678–0.702703 | {"0":3,"-3":2,"-1":2} |
| 406139732 | 5 / 14 | 77 | 24 | 7 | 5 / 5 | 55 / 18 | 0.667480–0.700157 | {"0":4,"-1":1} |
| 1560770669 | 5 / 13 | 96 | 35 | 10 | 8 / 8 | 45 / 21 | 0.662460–0.706718 | {"0":2,"-1":3,"-2":3} |
| 513405445 | 5 / 10 | 69 | 22 | 7 | 7 / 7 | 37 / 22 | 0.671481–0.702102 | {"0":1,"-2":3,"-1":3} |
| 800743875 | 5 / 10 | 77 | 19 | 5 | 5 / 5 | 28 / 18 | 0.663687–0.698310 | {"0":1,"-1":4} |
| 1996590652 | 5 / 14 | 90 | 27 | 7 | 7 / 7 | 49 / 25 | 0.666634–0.705420 | {"0":2,"-1":3,"-2":2} |
| 14650149 | 5 / 11 | 76 | 20 | 5 | 5 / 5 | 66 / 43 | 0.664397–0.693487 | {"0":2,"-1":2,"-2":1} |
| 1011706164 | 5 / 13 | 78 | 26 | 8 | 6 / 6 | 32 / 8 | 0.661858–0.696171 | {"0":2,"-1":3,"-2":1} |
| 723584011 | 5 / 10 | 82 | 24 | 7 | 7 / 7 | 44 / 34 | 0.662254–0.699348 | {"0":2,"-2":3,"-1":2} |
| 586496698 | 5 / 14 | 84 | 26 | 9 | 9 / 9 | 54 / 21 | 0.666492–0.709388 | {"0":5,"-1":4} |
| 1795093430 | 5 / 14 | 90 | 35 | 10 | 10 / 10 | 49 / 35 | 0.661301–0.706648 | {"0":3,"-2":4,"-1":3} |
| 1081113774 | 5 / 10 | 66 | 22 | 7 | 7 / 7 | 45 / 43 | 0.669502–0.709015 | {"0":2,"-1":3,"-2":2} |
| 151067072 | 5 / 10 | 74 | 28 | 7 | 5 / 5 | 35 / 12 | 0.685133–0.706493 | {"0":3,"-1":2} |
| 184078556 | 5 / 14 | 83 | 33 | 10 | 10 / 10 | 59 / 55 | 0.674896–0.710058 | {"0":2,"-2":3,"-1":3,"-3":2} |
| 1882997415 | 5 / 12 | 88 | 37 | 11 | 11 / 11 | 43 / 34 | 0.666687–0.708928 | {"0":4,"-2":3,"-1":4} |
| 1150325040 | 5 / 12 | 75 | 23 | 7 | 7 / 7 | 60 / 51 | 0.687362–0.702698 | {"0":1,"-1":3,"-2":3} |
| 46624273 | 5 / 10 | 73 | 24 | 7 | 5 / 5 | 50 / 17 | 0.662474–0.700829 | {"0":4,"-1":1} |
| 1437388438 | 5 / 14 | 87 | 39 | 13 | 13 / 13 | 61 / 38 | 0.667037–0.707416 | {"0":3,"-1":5,"-2":5} |
| 741424743 | 5 / 14 | 92 | 27 | 7 | 7 / 7 | 31 / 24 | 0.661849–0.696571 | {"0":2,"-1":5} |
| 1663521602 | 5 / 11 | 76 | 28 | 10 | 7 / 7 | 64 / 20 | 0.662327–0.707755 | {"0":2,"-1":4,"-2":1} |

## Runtime and protected regressions

| Validation | Actual result | Local evidence under `build-verify/mission-chests-cave-final-20261024` |
| --- | --- | --- |
| Isolated Windows ZoneEngine_New build | PASS, exit 0; existing warnings retained | `raised-final-windows-zone.log`, `windows/zone/ZoneEngine_New.dll` |
| Final 104 actual caves | 104/104 PASS | `final-cave-campaign-104-results/final-cave-campaign-104.trx`, `final-cave-campaign-104.jsonl` |
| Targeted/critical/detailed cohort | 16/16 PASS: eight positive fixed rolls, two critical diagnostic cases, two critical population cases, four detailed diagnostic cases | `raised-layer-targeted-results/raised-layer-targeted.trx` |
| Core protocol/loot/population/persistence/inventory/overflow/reward/reconnect/visibility | 115/117 PASS; exactly two proven inventory baseline failures | `raised-final-core-results/raised-final-core.trx`; `before-core-results/before-core.trx` |
| Final safety, all five objectives and captured teleport regression | 23/23 PASS | `fixed-final-safety-objectives-results/fixed-final-safety-objectives.trx` |
| Separate final all-five-objective repeat | 8/8 PASS | `fixed-final-objective-repeat-results/fixed-final-objectives-repeat.trx` |
| Isolated real-MySQL production-source chest validation | PASS, 50 checks, exit 0 | `final-mysql-chests.log` |
| Owned connected TCP acceptance | PASS_WITH_PROVEN_BASELINE_EXCEPTIONS, exit 0; cleanup PASS | `final-connected-tcp.log` |

Builds used the existing native Windows MSBuild command with isolated OutDir, preserving running servers. The final runtime source hash above remained frozen through the campaign, final build, TCP run and implementation commit. Later changes were test-only publication diagnostics/helper correction. The MySQL harness tests unchanged final DAO source; it is not a live player database or an in-memory substitute.

The original frozen player layout (1775745370, QL125) separately generated/persisted/instantiated/serialized/published **2/2/2/2/2** on its runtime roll and reconstructed the same population. Five-floor seed 42 separately requested 6 and generated/persisted/instantiated/serialized/published **5/5/5/5/5** across three floors. These are real geometry and locality with the existing in-memory transactional test DAO, not real server restart evidence. Their different placement seeds explain differences from the campaign table.

The 50 real-MySQL checks cover durable initialization, rollback/no seal, concurrent initialization, fresh-DAO frozen layout/key reload, commit-outcome recovery, expected-source transfer, ownership/occupied-target rollback, one-winner concurrent take, lost commit acknowledgement and retained looted rows. The full-catalog loot regression independently rolls 500 chests across five mission QL bands, including 1–3 item limits, QL ±5 clamps and fresh identities. Existing rarity grouping, Carry, NoDrop, Unique and overflow tests remain intact.

TCP used owned disposable MySQL, WorldEngine, LoginEngine and ZoneEngine processes on fixture-local endpoints; imported an empty schema snapshot with `MIGRATIONS_EXECUTED=0`; and performed post-start writes through TCP only. It found a current generated cave within the existing 12-attempt cap (attempt 5, style341/seed1824653611, 36 cells, 13 eligible, requested4). Generated/persisted/instantiated/published counts stayed **4/4/4/4** at entry, reconnect and restart. Opening, partial take/reopen, authentication reconnect, distinct Zone process restart, frozen population/empty reopen/player retention, two players, nonopener rejection, opener handoff, duplicate take rejection and peer loot passed. Restarted Zone processes used the identical final binary SHA-256. `DISPOSABLE_CONTAINER_RESIDUE=NONE DISPOSABLE_NETWORK_RESIDUE=NONE DISPOSABLE_CLEANUP=PASS`.

### Baseline failures and the newly observed fixture failure

**PROVEN baseline inventory failures:** `DeleteRetiresCorrectInstanceBeforeAcknowledgingAndDuplicateIsNoOp` and `UnsupportedOnUseAloneDoesNotCountAsExecuted`. The initial exact integration core run and final frozen core run both report 115/117 with these same two failures. No inventory behavior or expectation was altered. Overall regression status is qualified, not plain PASS.

**PROVEN baseline shutdown exception:** the owned Zone shutdown returns exit3 with ordered `NotSupportedException,ObjectDisposedException`. Exact master 4f66c4c2 and starting integration reproductions were already retained, and the starting-integration comparison remained applicable because shutdown source is unchanged. The existing explicit chest-only opt-in accepts only that exact known shutdown signature; other failures remain fatal. Final TCP therefore reports `PASS_WITH_PROVEN_BASELINE_EXCEPTIONS`. Login and World stop with exit0. This task does not claim graceful Zone shutdown is repaired.

**PROVEN test-harness cell-context cause, corrected without production change:** one combined run and diagnostic run reported three persisted/instantiated objective chests but only two session publications. At `(53.915504,5,142.87596)` in actual style324/seed818302858, the chest was cell20 while the arbitrarily teleported player retained linked-room context and resolved to cell1. Both pre-clear sent and visible sets were empty, excluding a cleared-message/dedup explanation. The helper jumped through cells15 and3 before this target; a fresh registration resolves cell20.

Three bare exact-starting objective cohorts passed 24/24, so they were not used to dismiss the new failure as baseline. Instead, the new deterministic captured-geometry test replayed the exact three positions against unchanged exact-7d runtime and the candidate: both proved contextual cells15→3→1 versus fresh cells15→3→20. Only the identical test file was overlaid on the owned comparison checkout; runtime files remained byte-identical and the test overlay was restored afterwards. Evidence: `captured-cell-exact-start-results/captured-cell-exact-start.trx` and `captured-cell-candidate-results/captured-cell-candidate.trx`, both 1/1 PASS.

Production `CellGrid.ResolveRoom` intentionally preserves movement room context; `UnregisterDynel` clears Cell. The helper now unregisters before each independent teleport, then performs the same real registration and visibility activation, asserts player/chest cell equality, and retains the full generated=persisted=instantiated=published assertions. Final 23/23 plus separate 8/8 runs passed. This corrects the test setup; it makes no claim about arbitrary client teleport handling or newly fixes production locality.

Earlier evidence is retained: the first full campaign was 103/104 with zero at605065242/2floors/size8, and the first expanded fixed-seed cohort was11/12 with the sparse bridge turn omitted. Final raised-surface priority produces1/1 at605065242 with the same placement seed1750440212, and all eight fixed rolls are positive. Diagnostic-only zero-safe assertions were never counted as population acceptance. An early invocation missing the required frozen-layout environment input was corrected before final validation. Read-only command/read mistakes were corrected without source, service or database mutation; failed logs remain in the ignored evidence directory.

## Preservation, publication and remaining limitations

Final checks retain all 12 protected preexisting file hashes, all 15 stash SHAs, the untracked local audit report, private frozen-layout/schema input hashes, master and the original chest branch. Original dirty checkout work was not staged or overwritten. The owned exact-start comparison worktree is restored clean at7d638408; earlier worktrees remain intact. No player database, configuration, protected GameData, capture store, accepted build, running Windows/Linux server, master branch or deployment was changed. No AO client or capture tool was launched or controlled.

No migration or schema change was introduced. No protected GameData, raw capture bytes, schema dump, private infrastructure, credentials, database rows, build outputs or ignored raw evidence is included in the public commit. Only the intended runtime/test sources, task receipt, report and aggregate metrics are published.

Remaining limits: bounded sampling can return fewer chests than requested; 14 measured cases do. There is no proof that every theoretical floor/route or safe location is sampled. Raw raised-surface classification adds geometry-bounded preprocessing (raw upward faces against own-ground triangles); proposal budgets are unchanged, but runtime cost is not claimed identical. Current sphere casts are the existing approximation, not original-client movement acceptance. The two inventory failures and graceful Zone shutdown defect remain baseline issues. Full unfiltered historical test suites, Linux builds, gameplay/client acceptance, master integration and deployment were not performed.

The placement candidate is ready for master integration review with these explicit baseline exceptions and density limits. Keep the feature branch for review; do not infer merge, deployment or client acceptance from this report.
