# Current Task

Implement the shared procedural mission dungeon layout foundation.

Base: public master `3805aa851` (includes the accepted mission destinations and ordinary playfield unloading).
Branch: `codex/procedural-dungeon-foundation`.

Scope: extend the existing connector-based room assembler, use editable layout
policies, add connected multi-floor layouts with a separate boss chamber and
existing world-use/intrazone travel, and preserve accepted version-1/2 layouts.

New solo acceptance uses the same transaction/key/entrance flow. Team layout
generation is exercised independently; carrying team mode through offer/roster
acceptance and populating a guaranteed boss are the next encounter milestone.
Do not enable a team mission without that boss contract. NPC/content/loot,
terminal rewards, QL, destinations, DAO and schema are outside this increment.

Evidence inputs are the completed retail capture corpus only. Runtime reads
GameData, never captures. Generated policy is explicitly AORebirth policy;
observations do not establish Funcom's random selection probabilities.

Validation: approved build and 111 focused mission/foundation cases passed,
including 288 configured-boundary layouts, real collision/floor-travel worlds,
and legacy byte-for-byte fixtures. Disposable MySQL passed 74 identity and
275 persistence/rollback/concurrency checks. Mike performs original-client
acceptance; the agent does not control the client. Services are stopped.

See `docs/evidence/PROCEDURAL_DUNGEON_FOUNDATION_EVIDENCE.md` for capture provenance.
Implementation and remaining encounter work: `docs/reports/PROCEDURAL_DUNGEON_FOUNDATION.md`.
