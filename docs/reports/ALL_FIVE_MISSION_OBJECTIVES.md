# Five generated solo mission objectives

Base: installed `9f59727bd`; branch: `codex/all-mission-objectives`.

## Implemented together

| Type | Accepted contract and completion event |
|---|---|
| Kill Person | Frozen catalog leaf, owner, exact NPC identity and reachable placement; that actor's server-side death with existing present kill-credit participants earns completion. |
| Find Person | Frozen passive NPC; visible exact actor inspection (`InfoRequest`) within interaction range and line of sight earns completion. Ordinary targeting does not. |
| Find Item | Existing owner-bound world-item pickup, exact inventory grant and durable completion remain in place. |
| Repair Machine | Tool and key granted in the same acceptance transaction; exact tool applied to the exact registered machine in the owned dungeon is consumed atomically with earning. |
| Return Item | Exact world-item collection grants the item and persists progress 0 to 1 without earning rewards; use against the exact originating terminal consumes it and earns progress 2. |

All use existing `generatedquests`, `characterquests`, inventory mutations,
mission DAO transactions and reward ledger. No new schema or reward framework.
Capacity failures leave objectives or earned rewards recoverable. Repeated
completion/collection cannot grant or consume twice. Earned rewards retain the
existing reconnect/restart recovery. New machine targets never fall through to
ordinary template spells for invalid users or tools.

Abandonment and expiration retire exact Repair/Return artifacts and keys in one
existing cleanup transaction, preserving historical item rows. Online inventory
publication fences stale queued item locations. Find Item's collected item is
unchanged. Failed NPC death commits retry through the existing player retry
tick; no new persistent queue was introduced.

## Content and evidence

The existing `DungeonGeneration.json` selects references in the existing item
and NPC catalogs: Find Item `URSE` unchanged; Return `COSE`; Repair `SPPR`/`CRCH`;
NPC objectives `FECI`, with existing level interpolation. These are editable
AORebirth policies. Cross-QL selection and random NPC target selection are not
claimed as Funcom probability rules or exact retail target-name reproduction.

Inspected the existing acceptance/dungeon/quest/journal/inventory/DAO owners,
the original Find Person accepted action in capture
`mission-20261011-215642-c70bc191`, the completed Repair item pair/action in
`mission-20261012-004902-7174e861`, current roll packet policy and
`docs/evidence/RK_MISSION_ACG_INTERIOR_EVIDENCE_20260728.md`.
Return's action contract uses the established July analysis/current packet
policy; the absent July raw folders were not presented as newly inspected.
Runtime does not read capture folders. Private catalogs are not published.

## Validation and limits

- PROVEN: approved `cmd /d /c tools\build_aorebirth_debug.cmd` succeeded for
  ChatEngine, LoginEngine and ZoneEngine_New, including framework DAO preflight
  compilation. Compiler/package warnings remain; no compile errors.
- PROVEN: `git diff --check` and policy JSON parsing succeeded.
- No unit, fixture, regression, fault-injection or DAO test suites run for this
  task. Previous Find Item test counts are not this candidate's validation.
- Original-client all-five gameplay is UNVERIFIED. Test newly accepted missions;
  older unbound accepted contracts are not rewritten.
- Repair/Return use requires main inventory; move the item out of a backpack.
- An uncommitted kill event lost during a database outage plus disconnect or
  restart can require killing the reconstructed target again. Committed earned
  rewards retain the existing ledger recovery.
- Team acceptance/rosters/bosses, independent dungeon loot and complete modern
  retail NPC appearance/population parity remain separate unfinished work.

Rolling, mission QL, destination pools, room generation and advertised rewards
were not redesigned. No other objective frameworks or content catalogs added.
