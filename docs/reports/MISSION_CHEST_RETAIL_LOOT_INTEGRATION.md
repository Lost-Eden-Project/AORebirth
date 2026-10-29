# Recovered retail mission chest loot integration

## Current correction: captured quest-use item restored

The initial basket exclusion below was incorrect: additional quest use does not
establish acquisition exclusivity. The verified captured fixed QL 1 item is now
approved, and only its erroneous special exclusion was removed. Current totals
are **186/186 eligible captured pairs, 25,118 approved definitions and 13,269
approval bands**. All prior 25,117 definitions and 13,268 bands are preserved.

The catalog tool binds the reviewed Unique exception to exact hash-verified
world-chest slot evidence and captured qualities. Quest-use metadata remains
independent. Other explicit exclusions, seasonal bytes and gameplay mechanics
are unchanged. The existing automatic quest-role audit flagged one separate
association/exclusivity ambiguity for review; it was left unchanged.

Fourteen catalog tests, 99 focused chest/inventory tests and 50 disposable MySQL
checks passed. A bounded check of the full installed production pool selected
the restored QL 1 item after 2,229 populations (matching seed 10147); the same
service/seed reproduced it through the chest command. The broad 10,000-population
campaign produced 19,948 authorized items with no exclusion leaks. Its fixed seed
did not happen to draw this item; the targeted full-pool proof is separate.
Connected TCP passed with the retained proven shutdown baseline exception.

All 39 related item/quest/NPC/script files remain byte-identical. The existing
Smug/Eel quest records reference the item, but canonical content lacks the
completed basket-to-Tuner exchange binding. Preservation is verified; a working
end-to-end exchange is not claimed or introduced by this correction.

The following initial-integration record is retained as history. Its basket
rejection and blanket Unique-veto description are superseded by this correction.
No master merge or deployment has occurred.

## Initial integration history

Status: development policy installed and validated, 2026-10-28. No master merge,
production deployment, AO client operation or live database change.

Product base: `c679d683aeff50c3f3c2d737974fe1880feddb7a`. Recovery tooling:
`90b4de0ea5dfc490caf69008a42761b23cc4f4ff`. Integration branch:
`codex/mission-chest-retail-integration`.

## Result

All 186 captured low/high descriptor pairs were reviewed against current item
definitions, supported endpoint qualities, existing approvals, historical
classifications and negative policies. Exact retail chest acquisition supplies
positive evidence; absent expansion requirements alone do not grant approval.

| Measure | Result |
| --- | ---: |
| Eligible recovered pairs now covered | 185 |
| Entirely new eligible pair identities | 129 |
| Existing pairs receiving supported QL extensions | 8 |
| Existing pairs already covering their full supported span | 48 |
| Rejected quest/progression pair | 1 |
| Unresolved pairs | 0 |
| Recovered observations approved, before / after | 78 / 289 |
| Total approved endpoint definitions, before / after | 24,925 / 25,117 |
| Total approval bands, before / after | 13,131 / 13,268 |
| Existing implant definitions retained | 23,052 |

There were 56 captured pairs with some earlier approval; 55 covered every
captured observed quality. These counts differ from the 48 pairs whose full
supported span was already approved. The rejected fixed quest item accounts
for the other two captured observations.

Every prior approval entry and prior definition/category review remains
unchanged. Only uncovered pair/quality intervals were appended. Captures span
QL 1–74; current verified endpoint metadata supports spans through QL 400.
The existing production mission range and item-quality ceiling remain 1–250.
The seasonal policy is unchanged; one exact quest-component veto was added to
the special exclusions. Private identities, policy records and capture evidence
remain outside public Git.

## Implementation

`Tools/MissionChestRKCatalog/prepare_approved_catalog.py` now accepts an explicit
private review file and an existing source-bound policy/review baseline. It
preserves old entries and categories and appends only uncovered intervals.

Four exact authored reviews address metadata false positives. The tool permits
reviewed Special/NoDrop flags and an exact NPCFamily requirement proven to read
OnTarget by the ordered selector context. Evidence and an authored reason are
mandatory. Unique, Disabled, Stationary, negative catalogs, expansion, quest,
GM and direct NPCFamily restrictions cannot be waived.

The existing runtime policy loader and production selector consume the installed
development policies. Runtime gameplay, interpolation, rarity weights, item
counts, mission-quality windows, chest placement/density, ownership, inventory
and persistence code are unchanged. No replacement loot generator or item
database was introduced.

## Validation

- Nine focused offline catalog tests passed under an enforced 60-second bound.
- All 99 selected chest/inventory tests passed. Two exact unrelated inventory
  baseline failures were excluded with retained failing evidence and unchanged
  relevant source fingerprints; they are not counted as passes.
- The actual combined production selector generated 19,948 items from 10,000
  populations across all 250 mission QLs. Unauthorized selections: zero;
  excluded selections: zero; allocated durable IDs: zero.
- All 137 added bands passed 311 supported minimum/midpoint/maximum probes with
  the real item catalog, interpolation, selector and exclusion policies. The
  existing chest command also reported each probed pair correctly.
- In normal random sampling, 72 new bands appeared and supplied 2,998 items.
  This observation is separate from deterministic reachability of all new bands.
- Disposable MySQL passed all 50 checks. Owning Login and World compilation
  passed; focused/connected builds compiled the owning ZoneEngine.
- Real connected TCP passed GM permission checks, `.chest roll 20` and
  `.chest roll 10000`, accepted-mission chests, transfers, two-player ownership,
  reconnect/reconstruction and persisted restricted-item withholding.

TCP is `PASS_WITH_PROVEN_PUBLIC_BASELINE_EXCEPTIONS`: both owned Zone shutdowns
matched the retained pre-existing shutdown signature. Login/World shutdowns
completed normally. Relevant shutdown source fingerprints were checked; no
whole-file identity claim or unrelated runtime repair was made.

The initial launcher syntax error occurred before tests ran. The first TCP
attempt subsequently exposed a disposable schema snapshot missing the three
canonical mission-allocator seed rows. Its failure/logs were retained. A derived
schema-only input appended precisely those current migration rows, without
player data or live migrations; the bounded rerun passed.

Runtime loading is supported by the real Zone child receiving the canonical
development GameData root, matching installed/candidate policy hashes, startup
root evidence and successful wire-level commands through the existing selector.
The policy entry count is derived from those bound files, not asserted to be a
literal startup-log field.

All build/test receipts are feature validation, nondeployable and not Windows
user acceptance. Exact commands, hashes, private policy backups, per-pair
decisions, source observations and failed-attempt evidence are retained in the
owning checkout's ignored task evidence directory.

## Files and remaining work

Public changes comprise the existing catalog tool and README, focused catalog
tests, `MissionChestRKPolicyTests.cs`, current-task notes and this report. The
private development allowlist and special exclusions are installed separately;
no protected catalogs or credentials are published.

Review/merge the feature branch only when authorized. Carry the reviewed private
policies through existing content adoption/reconciliation, then build and obtain
Mike's explicit Windows acceptance of the changed exact inputs before Linux
packaging. Production deployment remains separately authorized. No previous
acceptance is reused for the expanded policy.
