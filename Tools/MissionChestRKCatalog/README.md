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
