# Current Task

Active request (2026-10-28): recover actual mission chest contents from all
supplied October 27–28 recording sessions. Work is isolated on
`codex/mission-chest-loot-recovery` from public master
`c679d683aeff50c3f3c2d737974fe1880feddb7a`.

- Reuse completed original-client protocol research and the original known-answer
  fixture. Correct offline decoding and exact identity/handle/slot joins only.
- Include incomplete finalizations; preserve all original captures.
- Resolve current item definitions and append acquisition evidence through the
  existing offline catalog infrastructure. Keep private output/catalog records
  ignored, outside canonical GameData and public Git.
- Preserve runtime loot policy, rarity, QL, approvals and exclusions. Do not
  include unmerged trap or typed-content development in this feature.
- Validate complete packet accounting, known-answer recovery, deterministic
  reports and source integrity. No master merge, deployment, live DB/server
  changes, new captures or client control is authorized.

Offline workflow and output semantics:
`tools-temp/AOSharpMissionCaptureAnalyzer/CHEST_LOOT_RECOVERY.md`.

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
