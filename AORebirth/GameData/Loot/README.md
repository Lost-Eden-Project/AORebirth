# Generated mission chest policies

The three JSON files in this public folder are empty schema placeholders. The
reviewed content belongs in private canonical GameData. Empty placeholders are
not a release loot pool.

- `MissionChestRKAllowlist.json`: Version 1 with Entries containing LowId,
  HighId, MinQuality, MaxQuality, Classification (`OrdinaryRubiKa`) and nonempty
  Evidence references. Approve only positively reviewed ordinary RK identities
  and existing endpoint relationships. Exact historical chest-drop proof is
  not required. Missing expansion requirements or a matching name alone do not
  establish approval.
- `MissionChestSeasonalExclusions.json` and `MissionChestSpecialExclusions.json`:
  Version 1 with Entries containing TemplateIds, optional canonical leaf
  FamilyHash, Category and nonempty Evidence. A family veto covers all its
  members. Parent loot roots must be expanded offline into exact TemplateIds;
  the runtime family collection contains leaves only.

Either endpoint matching an exclusion vetoes the pair, even when approved.
Exclusions do not read event enablement and affect generated mission chests
only. Designated event/NPC sources and player-owned items retain their existing
rules. Missing or invalid required policy files and empty QL pools fail closed.

Source-supported quality ranges may exceed mission QL 250. The selector retains
the existing mission QL 1–250 admission and item window from max(1, mission-5)
through min(250, mission+5), intersected with actual endpoint and approval bounds.
The existing builder owns interpolation. Explicit reviewed pairs may reuse
other exact existing GameData pair metadata when a loot hash leaf is missing;
do not fabricate relationships or widen ranges.

Restricted persisted chest rows are withheld from display and transfer while
retaining their database records and original slots. Policy changes take effect
through the existing restart/reconstruction boundary, not a file watcher.

The complete reviewed policy bundle belongs in the accepted Windows GameData
manifest and must reach Linux/live unchanged through the established workflow.
The GM `.chest roll <1-10000>` command previews the production selector without
awarding items or allocating item instances; it does not replace required
source/content validation or Mike's Windows acceptance.
