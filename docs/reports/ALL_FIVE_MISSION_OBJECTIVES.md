# Five generated solo mission objectives

Base: installed `9f59727bd`; branch: `codex/all-mission-objectives`.

## Implemented together

| Type | Accepted contract and completion event |
|---|---|
| Kill Person | Frozen catalog leaf, owner, exact NPC identity and reachable placement; that actor's server-side death with existing present kill-credit participants earns completion. |
| Find Person | Frozen boss NPC with normal combat behavior; visible exact actor inspection (`InfoRequest`) within interaction range and line of sight earns completion. Ordinary targeting does not. |
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
Kill Person `FECI`; Find Person uses its separate `MFPB` human boss pool in the
existing full NPC catalog, with existing level interpolation. These are editable
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

## Find Person boss correction

OBSERVED: Mike's Find Person client test spawned a regular enemy profile. The
shared NPC objective pool supplied the same unmodified `FECI` to both NPC types.
Find Person now selects a separate boss pool and freezes its resolved leaf using
the unchanged accepted NPC binding. The authored `MFPB`/Mission Contact variant
in the existing full NPC catalog uses original `BEMC` boss bands/stats at levels
1/200/400, existing `FECI` human appearance/equipment and base scale 125 observed
on the retail Find Person target. No invented HP multiplier or stat-growth rule.
Existing catalog interpolation supports the mission QL; completion still
requires exact actor inspection. The initial candidate made the target passive.

No replacement NPC catalog, binding format, schema change or raid-boss substitution. The source
Find Person capture's Monte Laurence is not claimed as an identified catalog
hash. Boss modifier nanos alone were rejected as the permanent solution because
their existing size/glow effects expire after 100 seconds. Persistent appearance
comes from the boss variant's base data. Generic direct OnWear combat-stat
modifiers remain outside this repair. Existing accepted actors are
preserved; test a newly accepted Find Person mission.

Inspected existing `NpcTemplateCatalog`, `NpcCharacter`, `SpawnService`,
`WearCastNano`, `NanoRuntime`, `StatModifierSpells`, objective binding/spawning
and completion owners, private NPC/item catalogs and the original Find Person
capture. Final runtime changes are `DungeonFindItemPolicy`, `QuestDungeonService`
and existing DungeonGeneration policy. Added one private NPC catalog leaf;
private catalogs are not published. Task/status documentation records the change.
Approved core compilation, policy/catalog JSON parsing and `git diff --check`
passed. Existing private NPC records remain byte-identical before the appended
leaf; a local backup preserves the complete original catalog. No suites or AO
client control performed. Actual boss appearance awaits Mike's client pass.

OBSERVED follow-up: Mike reports the boss target is better, but too large and
must be attackable. Its base scale is now 100 and the optional KZ15 growth item
is removed from the existing private leaf. The spawn code no longer overrides
attackability or suppresses the normal NPC brain. Find Person inspection
completion, exact actor binding and other mission objectives remain unchanged;
killing the target is not substituted for the Find Person completion event.
Changed `QuestDungeonPlayfield`, the existing public/private policy documentation
and private `NpcTemplates` data, plus task/status notes. Approved follow-up core
compilation and policy/catalog validation passed; no suites requested or AO client
actions performed. The copied human equipment has an existing SIW1 monster-weapon
bridge covering QL1–400. Inspect before killing: killing alone does not earn
Find Person, and a target killed beforehand returns on dungeon reconstruction.

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
