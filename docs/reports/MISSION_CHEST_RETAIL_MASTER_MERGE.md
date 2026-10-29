# Retail mission chest loot — final master integration

## Source and content identities

| Identity | Revision |
| --- | --- |
| Starting fetched public master | `c679d683aeff50c3f3c2d737974fe1880feddb7a` |
| Final fast-forward merged source / freshly validated candidate | `d76a2fdeecc78afd921fec23263c61ddb2b7eb36` |
| Approved immutable private GameData revision | `2b5431fd382bd5fa00a7b232efb85dc556ad7cc2` |

The final publication adds this report and current-task documentation to the
validated candidate. Its exact report-bearing master SHA, push result and remote
readback are recorded in the private post-publication receipt and completion
response. This separates the measured source candidate from its documentation
commit without embedding a commit's own hash inside its content.

All three integration commits form a direct linear fast-forward from the
starting master:

- `90b4de0ea5dfc490caf69008a42761b23cc4f4ff`: offline capture recovery.
- `5c6b3103a804bd05c535fd70a0fcb9293acc82cf`: additive reviewed catalog integration.
- `d76a2fdeecc78afd921fec23263c61ddb2b7eb36`: captured quest-use item correction.

No merge conflict or replacement runtime implementation was required. Public
master already contains the production RK allowlist, mandatory exclusions,
two-stage rarity selector and `.chest roll` command. The integration's 16 reviewed
paths are tooling, tests and documentation. The entire ZoneEngine_New runtime
tree is unchanged at `995049290a56a5e7f5334076f720b5c2323eaa8a`.

## Approved private catalogs

The exact private commit and its raw policy blobs were verified, rather than
substituting a mutable branch tip. Canonical policy bytes match that commit.
No catalogs were regenerated or copied into public Git.

| Catalog | SHA-256 |
| --- | --- |
| `Loot/MissionChestRKAllowlist.json` | `1fd1b3d57b46ca9e1e1f08837304f5209d0b82b5fdc4600005a188af042b23f3` |
| `Loot/MissionChestSpecialExclusions.json` | `20eba79b7aa504fe50fdd333778f69b6faaee401fcba6e92c3c58bfb62f69085` |
| `Loot/MissionChestSeasonalExclusions.json` | `d041620afcde32e6723d8a959626c0eef3512d701f1c274e2240225db30ee84d` |

Verified totals: 25,118 approved definitions, 13,269 identity/QL bands, all 186
captured pairs and all 291 recorded observations covered. Gift wrapped basket
203261 is approved at exactly QL 1. Its quest-use metadata is independent of
acquisition eligibility. All 23,052 existing implant definitions remain approved;
no category balancing was introduced.

Expansion, Halloween, seasonal and other explicit negative policies remain
effective, with zero overlap between approved definitions and mandatory denied
identities. All approved definitions resolve in the current item database.
One historical denial-only missing definition remains documented and unchanged;
it is not approved or generatable.

The private commit has seven inherited item-family maps narrower than the
canonical Windows snapshot. All ten exclusion hashes resolve, explicit identity
denials preserve the same 11,152 effective exclusions, and no approved identity
is denied. Neither family catalog was replaced during this task.

## Fresh integration validation

These results were executed for this merged candidate, not relabeled from
historical feature runs. The existing governed feature-validation helper bound
the exact candidate, its source descriptor and the selected GameData manifest.
All stage source identities stayed unchanged during their execution.

| Fresh check | Result | Enforced bound |
| --- | --- | --- |
| Catalog preparation/review tests | 14 passed | 60 seconds |
| Captured inventory-evidence reconciliation tests | 5 passed | 60 seconds |
| Chest, inventory, policy, protocol and command tests | 99 passed | Shared 900-second process bound |
| Existing mission-objective integration cases | 8 passed | Same process; 480 seconds per test |
| Existing Find Item / reward checks | 20 passed | Same process; 480 seconds per test |
| Disposable actual-source MySQL persistence | 50 checks passed | 300 seconds |
| Actual 186-pair / 291-observation catalog reconciliation | Passed | 60 seconds |

The expanded C# run passed 127/127 tests. It freshly executed the combined
production selector for 10,000 chest populations across all 250 mission QLs,
producing 19,948 items with zero unauthorized or excluded selections and zero
durable IDs allocated by preview. The existing 1–3 item count, mission QL ±5
window, item-quality ceiling, interpolation and configured rarity weights passed
unchanged. Existing chest inventory, persistence and mission-objective behavior
passed within the selected scope.

The separate full-installed-pool QL 1 probe freshly selected the basket after
2,229 bounded populations, matching seed 10147. Replaying that seed through the
same production service and chest-command preview reported it correctly. The
fixed broad campaign did not happen to draw the basket; that sampling result is
retained separately without changing probabilities.

The actual catalog verifier reused existing immutable item metadata and checked
its source hashes. It performed no packet decoding, item-database regeneration
or new loot research.

## Baselines and historical evidence

Two unchanged unrelated inventory tests were excluded using retained failures
and measured relevant source fingerprints:

- `DeleteRetiresCorrectInstanceBeforeAcknowledgingAndDuplicateIsNoOp`
- `UnsupportedOnUseAloneDoesNotCountAsExecuted`

They are not counted as passes. No new regression occurred in the executed
scope. Broad geometry/native stress and the default whole-solution fixture cohort
were not run for this non-runtime source integration.

Earlier connected TCP acceptance remains **historical evidence only**. It covered
real wire chest rolls, two-player ownership, transfers, reconnect/reconstruction,
restart and persisted restricted-item withholding, with proven pre-existing Zone
shutdown exceptions. This task did not rerun TCP or start/restart AO backend
engines. Fresh MySQL checks used only the existing owned disposable fixture;
player databases were not accessed. Owned fixture resources were cleaned up.

Feature receipts remain `DEPLOYABLE=NO` and `WINDOWS_USER_ACCEPTED=NO`. Fresh
integration tests are not Windows release readiness, original-client acceptance
or Linux deployment acceptance.

## Public/private separation and preservation

Source review found no private GameData, raw captures, databases, credentials or
ignored research payload in the public integration. Only the bounded private
revision and policy hashes are recorded here; private role bindings, content and
full execution evidence remain private.

The primary Windows checkout had two local configuration modifications and five
untracked files. Their paths and hashes were recorded privately. No private
configuration, player data or rollback material was discarded or overwritten.
The isolated worktree was used for review and validation. Any safe primary
checkout fast-forward and its preservation checks are recorded after publication.

The public push uses the configured public origin and a normal non-forced
fast-forward. Remote master readback must match the published source SHA and
contain all three integration commits. The final documentation-only delta must
leave every freshly tested runtime, test and tool byte unchanged. The private
post-push receipt records those actual checks and the final authoritative pair.

## Windows release prerequisites

1. Synchronize the owning Windows source to the freshly verified public master,
   preserving unrelated local configuration and work. Source parity is required
   before the normal Windows release build.
2. Use the existing ignored profile and `tools/windows_release_contract.cmd`.
   `prepare-build` creates the complete current content plan;
   `reconcile-content --approved-plan-sha256` re-intakes and rejects a changed
   plan before canonical writes. Existing reviewed resolutions and qualified
   generated-items provenance must be retained.
3. Require `CONTENT_DUMP_SHA` in the prepared and applied plan to equal approved
   `2b5431fd382bd5fa00a7b232efb85dc556ad7cc2`. The current helper has no documented
   immutable-revision CLI flag and re-resolves the configured content branch.
   Reject a newer/older resolution; do not repurpose configuration or invent a
   flag. If the branch has advanced, resolve that narrow intake requirement
   before applying anything.
4. Review the primary profile's stale allowlist `PRIVATE_CATALOG_ADOPTIONS`
   binding, which still targets the prior 24,925-definition policy. Use the
   established exact staged-hash and actual instruction-evidence mechanism to
   reconcile it with the approved revision. Do not silently clear settings,
   reuse the older policy or manufacture approval. Existing seasonal/special
   bindings already match the approved bytes. No profile was changed here.
5. Reconcile all approved content additions, modifications and removals with the
   existing ownership rules. Preserve legitimate canonical/private/generated
   repairs and existing item data. Do not replace the whole tree or reset its
   provenance to bypass conflicts. Produce the complete deterministic Windows
   GameData manifest.
6. Under a later authorized build/startup task, use the established Windows
   wrappers and measured release receipt. Stop for Mike's explicit acceptance of
   the exact source/content/manifest/build-startup tuple. This task supplies no
   acceptance and authorizes no startup.

## Linux prerequisites and remaining acceptance

Linux must consume the exact Windows-accepted public source, private content
revision above and accepted canonical Windows GameData manifest. Reconcile only
allowlisted Linux platform/build/package/service adaptations, with no runtime
drift. Use the existing private GitHub Actions online builder and PATCH MODE
dependency scope; retain verified unchanged outputs. Include every accepted
content addition, modification and removal and require the complete Windows /
Linux package manifest equality.

The private Linux source/package workflow and production deployment require
their appropriate later authorization. Production deployment is separate and
must preserve the accepted source/content/manifest chain, current configuration,
player data and established backup/rollback procedures. No private deployment
tooling, production connection, Linux build, server restart, migration or binary
deployment was used during this integration.
