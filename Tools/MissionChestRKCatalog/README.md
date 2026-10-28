# Offline mission chest catalog tools

These tools prepare private review inputs and validate an explicitly authored
ordinary Rubi-Ka approval catalog. They do not install GameData, start an engine,
award items, operate the AO client, or establish build or gameplay acceptance.
Keep all exported definitions, reviews, cached source material and generated
policy JSON outside public Git.

`Tools/export-mission-chest-rk-metadata.cmd` runs the standalone source-linked
metadata exporter. Supply `--gamedata-root` and `--output-root`. It reads
`items.dat` using the existing `ItemsDatReader` and exports identities, stored
quality, current flags/stats, ordered criteria, function argument evidence and
decoded shop stock lines. The receipt binds the export to its source SHA-256.
Nullable integer function arguments mean that the argument was not an integer;
the export does not claim to decode arbitrary expressions or other value types.

`cache_review_source.py` accepts `--url` and `--output` to cache one bounded
public source with its URL, HTTP result and content hash. It reuses a matching
cache and never executes downloaded material. Cached material is evidence for a
separate review, not an automatic eligibility decision.

`prepare_approved_catalog.py` accepts `--input-root`, `--gamedata-root` and
`--output-root`. It requires the metadata export/receipt, enum map, both negative
policy catalogs and an `ordinary-family-reviews.json` authored review input.
Each review supplies exact template identities, an ordinary category, positive
RK evidence, ordinary source/category evidence and either a complete canonical
leaf hash, one fixed-quality identity, or an exact existing typed pair source.
Typed pair sources are current checkout-relative JSON paths and SHA-256 values;
each review identifies its exact source row. The tool rejects changed source
files, different endpoint identities/qualities and ambiguous equal-quality
family variants. It does not pair records by names or extend their QL bounds.

Optional `ApprovedQualityRanges` restrict a review to its supported acquisition
intervals. Output intervals intersect those ranges with actual endpoint bounds.
The validator gives seasonal/special exclusions and conservative current
metadata vetoes priority. Missing expansion metadata is never RK proof. Current
restriction markers are conservative approval vetoes, not declarations of
historical exclusivity.

Outputs are the runtime approval JSON plus separate offline review and QL
coverage results. Their status explicitly distinguishes artifact validation from
the production selector campaign. The existing mission selector's item QL cap
is preserved in offline coverage; source-supported family maxima are retained.
Use the actual production selector for runtime simulations and retain every
normal build, Windows acceptance and release requirement.

For an additive private review, `--reviews` selects an explicitly authored review
file while reusing the existing metadata cache under `--input-root`. An exact
captured LowID/HighID relationship can use the existing hash-bound `PairSources`
format in an ignored checkout-relative private file. Its `Key` supplies `LowId`,
`HighId`, `LowQl` and `HighQl`; the endpoint qualities must agree with current
metadata. Names never establish a pair or quality interval.

Use `--existing-policy` together with `--existing-review-results` to retain a
source-bound approval catalog. Existing entries and approved definition records
remain unchanged and in their original order. The tool appends only uncovered
pair/QL intervals and new definitions, rejects category conflicts, and records
retained/added counts in `ordinary-rk-merge-manifest.json`. It does not rebuild
historical reviews or reinterpret their older typed pair sources.

A review may explicitly explain a current-metadata false positive through
`MetadataExceptions`. `AllowedFlags` accepts only `Special` and `NoDrop`.
`TargetCriteria` accepts exact `NPCFamily` terms with `TermIndex` and
`Target: "OnTarget"`; ordered requirement selectors must prove that subject in
the current metadata. `Evidence` must contain exact source locators and `Reason`
must explain the authored exception. Raw numeric target fields alone do not
prove an OnTarget requirement. These exceptions never waive Unique, Disabled,
Stationary, exclusion catalogs, expansion, quest or GM restrictions, or direct
NPCFamily stats. Positive RK and ordinary evidence remain mandatory. Without
explicit exceptions and additive options, existing validation behavior remains
unchanged.

`reconcile_captured_loot.py` joins captured low/high template descriptors and
their recorded quality to the existing metadata, requirement classification,
historical reconstruction, authored approvals and negative policy catalogs.
`build-index` takes `--metadata-root`, `--archive-root`, `--gamedata-root` and
`--output`. It verifies the existing export and classification source hashes,
then creates a private SQLite lookup with a source manifest; it does not rebuild
the item census. `classify` takes `--index`, `--input` and `--output` for a JSON
array of descriptors. Requirements, historical associations and generation
approval remain separate fields. A descriptor's two template endpoints do not
represent two physical drops.

`merge-evidence` takes `--existing`, `--input` and `--output` to produce an
additive offline HistoricalEvidence copy. Only explicitly verified inventory
records with exact session, chest, packet and slot locators are admitted.
Existing assertions and sources retain their order and values; repeated
observations are deduplicated by their complete evidence. Capture observations
append acquisition evidence without changing origin, exclusions, approvals,
rarity or QL policy. Keep the lookup, input records, source manifest and merged
evidence outside canonical GameData and public Git.
