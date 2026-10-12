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
generated ordinary/objective NPC population killed, including the objective
actor. It applies from the first kill, displays captured feedback from kill
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

UNVERIFIED: original-client token progress/delivery/recovery for this new code;
retail chance below five kills, exact denominator membership, gray-NPC rules,
empty-population behavior and mixed-level team eligibility. Current generated
mission acceptance remains solo; this change does not complete team missions.

Ignored local receipts preserve source rows, ordinals and relevant packet bytes
under `build-verify/mission-token-*`; captures are not copied into runtime data.
