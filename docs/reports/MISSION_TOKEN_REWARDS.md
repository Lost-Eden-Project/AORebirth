# Generated mission token rewards

## Evidence and policy

Mike authorized direct inspection of the existing MissionCap CSV captures on
2026-10-12. No additional captures or client control were used. All 34 retained
mission folders were considered: 177,818 raw CSV records, of which one trailing
record lacks RawHex in an explicitly unfinished capture. It contributes no
promoted token evidence.

PROVEN: client text category 110 / 79979934 identifies the captured float as
mission token reward chance. Its argument is `f` followed by five base85 digits
encoding the IEEE754 float bits. All 36 captured chance arguments reproduce the
observed `kills * 100 / denominator` values and wire encoding exactly.

| Capture | Actor | Observed kills | Implied denominator | Chance range |
| --- | --- | --- | --- | --- |
| mission-20261011-215642-c70bc191 | Level 220 Omni | 5–24 | 35 | 14.285714–68.571426% |
| mission-20261012-003748-50349c68 | Level 220 Omni | 5–7 | 26 | 19.230770–26.923077% |
| mission-20261012-004902-7174e861 | Level 220 Omni | 5–17 | 21 | 23.809525–80.952377% |

The first capture is truncated; its retained observations remain usable. The
other two are finalized without truncation. All three begin feedback at the
fifth unique NPC death. This proves the observed feedback boundary, not that
the award probability below five kills is zero.

PROVEN: the exact Kill Person objective contributes to progress. In
`50349c68/raw-packets.csv`, row 4590 (ordinal 4589) is target
50000:2060895750's death; row 4592 reports 7/26. A turret also contributes:
`7174e861` row 4560 is its sixth death and row 4567 reports 6/21.

PROVEN: a token award was attempted below 100%. In `7174e861`, the last
chance is 17/21 at row 6415; completion row 6686 contains client text
110 / 128711717 and argument `Omni-Tek Mission Token`. The original client
text identifies this as a Unique-item refusal. The token was already present
before completion. This does **not** prove delivery of another token.

PROVEN content: the authoritative existing item catalog defines 96349 as the
Omni-Tek Mission Token and 96350 as the Clan Mission Token. Both are Unique,
NoDrop, nonstackable, consumable QL1 items. Their existing use effects award
level-dependent token points. One successful mission awards one physical item;
the server must not create one physical item per token point. Existing generic
item requirements, use effects and consumption remain the owner of that behavior.

CONFIGURABLE AORebirth policy: `DungeonGeneration.json` owns the side-to-item
mapping, quality and feedback settings. Chance is the fraction of the frozen
generated eligible NPC population killed, including Kill Person targets but
excluding Find Person targets whose objective requires inspection. The complete
population/death census remains intact for reconstruction. Chance applies from
the first eligible kill, displays captured feedback from kill
five, and is zero for an empty population. Neutral characters have no sided
item. These choices do not claim complete retail denominator eligibility:
memory-visible NPC counts differ from the captured denominators. No invented
gray-level, family, faction-of-enemy or room-activation exclusion is applied.

## Implementation

New accepted missions snapshot the editable token policy in their existing
dungeon parameters. Previously accepted missions without it retain their old
behavior; past kills cannot be reconstructed reliably.

The implementation reuses the existing quest DAO flags and unique observations
to preserve selected NPC leaves, population size and deaths. Token outcome is
stored in the transaction that first earns the objective. Reward retries reuse
that outcome and the existing inventory grant plan/completion ledger, including
full-inventory and Unique-item recovery. Advertised item, credit and XP rewards
remain independent of the token selection; delivery commits through the same
existing atomic completion transaction.

No schema changes, new content catalog, raw-capture runtime dependency, mission
QL/destination changes or replacement reward framework are introduced.

## Validation and limits

Capture reconciliation and authoritative item inspection are complete. The
approved Windows core build (`tools/build_aorebirth_debug.cmd`) and
`git diff --check` succeeded. Focused source review checked frozen outcomes,
unique kill accounting, reconstruction, objective recovery and lock order. It
found and corrected cached-world reuse following failed kill persistence: a
marked instance now requires reconstruction from durable state, after occupants
leave. This is source/build validation, not executed restart or gameplay proof.
Automated fixture, unit and regression suites were not requested and are not run.

Implementation files: `DungeonGeneration.json`, `ClientFeedback.cs`, `Player.cs`,
`HashSpawnSystem.cs`, `QuestDungeonPlayfield.cs`, `PlayfieldManager.cs`,
`DungeonLayoutGenerator.cs`, `QuestDungeonParameters.cs`, `QuestDungeonService.cs`,
`MissionTokenProgress.cs`, `QuestService.cs`, `QuestService.Completion.cs` and
`QuestService.Tokens.cs`. Existing mission DAO interfaces/implementations,
inventory grants/Unique checks, NPC catalog resolution and item use effects
were inspected and reused. DAO/schema, NPC catalogs and item definitions are
unchanged. The player tick now includes pending exact-target earning retries;
pending reward feedback uses the existing plain-text protocol correctly.

OBSERVED: Mike saw token-chance feedback up to 95 percent after Shade became
Omni. Token delivery/recovery and the corrected full-clear path remain CLIENT
UNVERIFIED;
retail chance below five kills, exact denominator membership, gray-NPC rules,
empty-population behavior and mixed-level team eligibility. Current generated
mission acceptance remains solo; this change does not complete team missions.

Ignored local receipts preserve source rows, ordinals and relevant packet bytes
under `build-verify/mission-token-*`; captures are not copied into runtime data.

## Find Person full-clear correction

PROVEN local defect on 34c8d30f9: Shade's Find Person mission D2086840384
persisted 21 ordinary NPC deaths and a census of 22. The sole remaining entry
was the exact Find Person target, MFPB 50000:2086840385. At 02:10:31 local time
the inspection completed the mission while that target was alive. The frozen
Omni outcome used 21/22 and drew 21, so it awarded no token. Killing the target
after completion could not change the already-frozen result.

The correction excludes the non-kill objective from token chance, including
both the denominator and any recorded death of that actor. Chat feedback and
reward selection use the same eligible counts. Clearing these 21 enemies now
means 21/21 and a guaranteed winning roll upon objective completion. Kill
Person targets still contribute, preserving the original capture evidence.
This is an AORebirth objective-eligibility rule satisfying the requested
full-clear behavior, not newly proven retail Find Person denominator logic.

Raw population flags, unique deaths and fingerprints remain unchanged. Existing
uncompleted token-enabled missions can use the correction without a census
migration. Previously frozen completion outcomes remain immutable. No DAO,
schema, content, loot, packet or quest-objective mechanics change. The scoped
runtime files are MissionTokenProgress.cs and QuestService.Tokens.cs.

Validation: approved Windows core build and `git diff --check` succeeded.
No fixture/unit/regression suites were requested or run. Original-client
verification of the corrected full-clear percentage and token delivery remains
pending; the observed 95-percent feedback only verifies the earlier message path.

The earlier Neutral Find Item mission's separate missing ordinary spawn is not
the cause of this Omni Find Person failure and is not silently recategorized.

## Immediate token use after completion

OBSERVED on 73c32cdc2: Mike received and used a mission token after zoning.
The local log records quest D2086848942 awarding an implant and Omni token at
02:30:42 on 2026-10-12. At 02:30:50 and 02:30:51, the attempted token uses
address inventory slot 67, which the server resolves to the reward implant.
After zoning, FullCharacter records the token at slot 69, and its use succeeds
at 02:31:47. Token delivery and use are now observed; immediate use failed.

PROVEN source defect: InventoryGrantPlan freezes free slots before the mission
key is removed. QuestService.Completion invoked QuestEnded first, causing
QuestDungeonService to send the key's DeleteItem before reward AddTemplate
messages. AddTemplate uses the client's first free slot, so this ordering makes
the client and committed server placements disagree. Zoning rebuilds the client
inventory from the authoritative placements and masks the defect.

The scoped repair changes only QuestService.Completion.cs: publish the committed
grant before invoking QuestEnded. The latter runs in finally, retaining durable
key cleanup even when publication fails. Existing exception quarantine and
reconnect recovery remain intact. No packet definitions, inventory planning,
DAO/schema, token probabilities or GameData are changed.

Inspected: QuestService.Completion.cs, InventoryGrantPlan.cs,
QuestDungeonService.cs, Playfield.cs, InventoryStacking.cs, TradeService.cs and
the installed ZoneEngine_New log. Approved Windows core build and
git diff --check passed. No fixture/unit/regression suites were requested or run.
Immediate-use original-client verification remains pending.

PROVEN from the existing private items.dat: both sided tokens give 1 point at
levels 1-14, 2 at 15-49, 3 at 50-74, 4 at 75-99, 5 at 100-124, 6 at 125-149,
7 at 150-174, 8 at 175-189 and 9 at 190-220. Existing generic item-use functions
evaluate the character's level when the token is used. Shade receiving 2 points
at level 32 matches that data. These are level bands, not a strict title-level
mapping. The existing catalog is preserved; no replacement values were approved.
ItemTemplateCatalog, ItemsDatReader, DatItemTemplate and Character.TitleLevelFor
were inspected for this distinction. Private item data is not published.
