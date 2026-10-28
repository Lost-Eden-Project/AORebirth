# Current Task

Active request (2026-10-28): integrate eligible recovered retail mission chest
items into the actual private development RK chest policy. Work is isolated on
`codex/mission-chest-retail-integration`, following recovery commit
`90b4de0ea5dfc490caf69008a42761b23cc4f4ff` and public master
`c679d683aeff50c3f3c2d737974fe1880feddb7a`.

- Reuse completed recovery outputs; do not repeat decoding or collect captures.
- Account for all 186 exact pairs and current supported QL spans. Retail chest
  contents are positive acquisition evidence; preserve expansion, seasonal,
  progression and special-purpose vetoes and retain unresolved eligibility.
- Use the existing approval generator and runtime policy loader. Preserve every
  existing valid approval and the intentional implant majority. Do not change
  rarity, mission QL windows, counts, placement, inventory or persistence.
- Install reviewed private policy bytes in development GameData. Keep catalogs,
  reports, credentials and capture records outside public Git.
- Run the requested bounded production-selector campaign and focused chest,
  inventory, disposable MySQL and connected TCP checks through feature validation.
  Verify the real ZoneEngine policy load and the existing chest-roll command.
- Commit and push public-safe tooling/test changes only. No master merge,
  deployment, live database/server changes or AO client control is authorized.

Offline workflow and output semantics:
`tools-temp/AOSharpMissionCaptureAnalyzer/CHEST_LOOT_RECOVERY.md`.

Completed development integration: 185 of 186 recovered pairs are eligible;
129 are new pair identities and eight receive supported QL extensions. One
quest component is excluded. The installed private policy has 25,117 approved
definitions and 13,268 bands, retaining every previous approval and all 23,052
implant definitions. Requested selector, focused, disposable MySQL and connected
TCP validation completed; TCP retains a proven pre-existing shutdown exception.
See `docs/reports/MISSION_CHEST_RETAIL_LOOT_INTEGRATION.md`. No release acceptance,
master merge or deployment is implied.

## Previous task context (not current release authorization)

Complete the existing ordinary RK mission chest restriction and its governed
Windows-first release. Starting freshly fetched public master:
`7213f2ab63aca7333b7d7be19a0d360c7af5c558`.

- Preserve the draft on `codex/mission-chest-rk-restriction`, initially based on
  `47dae275238c83972779d62d351c718554d7b7b3`; preserve newer master changes at integration.
- Retain the reviewed private 24,925-definition / 13,131-band policy and its
  independent seasonal/special vetoes. Do not rebalance implant eligibility,
  rarity, counts, quality limits or dedicated event loot.
- Use the supported validation-only feature mode for compilation and requested
  selector, persistence, inventory, MySQL and connected TCP checks. Feature
  observations never authorize production publishing or Windows acceptance.
- After successful feature validation, integrate and push public master, then
  build and validate the exact freshly synchronized Windows candidate with the
  verified private policies and complete canonical content manifest.
- Mike's explicit acceptance of the new exact Windows tuple is still required.
  Historical acceptance does not authorize changed source or data.
- Linux online build and normal production deployment are authorized only after
  that mandatory gate. Do not claim either before actual execution.
- Keep full execution evidence and release details private; maintain the required
  `docs/reports/MISSION_CHEST_RK_LIVE_DEPLOYMENT_FINAL.md` report without publishing
  protected catalogs, connections or private infrastructure.

Development observations: four engines compile; 10,000 production-selector
populations cover every mission QL with no unauthorized selections. Core results
are 293 passes and seven exact public-source baseline failures, not an all-green
suite. Disposable MySQL has 50 passing checks. The broad geometry run retains
unresolved long-run null references and a host abort; isolated controls do not
erase those outcomes. Unmodified public source separately reproduces the native
geometry crash and the exact Zone shutdown exception sequence. Connected TCP
results and any later Windows readiness remain measured private evidence.
