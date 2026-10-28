# Offline chest loot recovery

This extends the existing x86 mission analyzer with the recovered common
FullUpdate, ChestFullUpdate, InventoryUpdate and item-transfer byte grammar.
It changes no server runtime. Captures, item data, classification catalogs and
derived evidence remain private and outside canonical runtime GameData.

Use the documented MSBuild command for this analyzer. When analyzing a supplied
recorder package, pass its directory as MSBuild `ReferencePath`; verify that the
AOSharp.Common hash matches the capture metadata. The raw chest reader is
independent of the incomplete recorded Common chest model. Existing ACG fields
that differ across Common versions retain neutral output labels and use their
current or recorded property names; absent fields fail explicitly.

Run the following stages with explicit paths; never write into the original
capture directories:

1. Run `recover_chest_loot.py --mode index --captures <capture-root> --work <private-work>`
   with the approved Python runtime. SQLite retains each original raw/memory row
   and source SHA; unchanged files reuse their indexed rows. All sessions,
   including incomplete finalizations, remain included.
2. Run the mission analyzer with
   `--chests <private-work>/selected-packets.jsonl <private-work>/decoded-packets.jsonl`.
   Preserve its summary and repeat output for validation. The reader requires
   complete body consumption and records captured outgoing zero-length headers
   explicitly; incoming frame-length validation remains strict.
3. Run `recover_chest_loot.py --mode correlate --work <private-work> --decoded <private-work>/decoded-packets.jsonl`.
   Base reports are written under `correlation/`. Inventory owner identities,
   recipient-scoped active handle registrations and exact slots establish the
   joins. Transfer requests must also match inventory epoch and destination.
   Commands are retained as interactions; preceding requests are candidates,
   not an assertion that every Use opens a chest.
4. Supply the existing completed-research known-answer fixture and its decoded
   packets, then run `recover_chest_loot.py --mode known-answer --work <private-work>`.
   This uses the same correlator and keeps historical controls outside corpus
   totals. Research-sourced controls are explicit, never fabricated memory rows.
5. Reconcile recovered rows through
   `Tools/MissionChestRKCatalog/reconcile_captured_loot.py`. Its read-only
   classification cache and additive historical ledger preserve all previous
   assertions and sources. It never changes runtime generation approvals.
6. Run `finalize_chest_loot.py --work <private-work> --validate` after the
   retained decoder, classification and independent audit receipts are ready.
   The finalizer consumes classified rows, endpoint definitions, descriptor
   classifications and exact world-template metadata. It separates positively
   identified rifts, corpse/owned-container exclusions and unknown world roles.
   It verifies originals and protected catalog/policy hashes, repeats cached
   correlation and all six reports, and writes `final-validation.json`.

The finalizer expects the working filenames emitted by these stages and the
retained classification job. It is an audit-completion stage, not an independent
item catalog builder. Its validation deliberately fails if required evidence
receipts are absent. Original byte fixtures and item catalogs are not public
tool dependencies and are never embedded in these sources.

The six outputs preserve all slot appearances, low/high descriptor pairs, QLs,
native counters, chest identities, source CSV hashes and packet ordinals.
Endpoint definitions, repeated observations and distinct physical items are
different counts. Missing native spawn fields stay unknown. Absence of an
expansion requirement is not RK approval. Native counters do not prove physical
transfer quantities. The lockable identity list is authorization metadata, not
a trap-child list.
