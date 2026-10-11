# Current Task

Repair the alien-only ordinary mission dungeon population.

Branch: `codex/mission-dungeon-npc-population`, based on foundation `df44abd0f`.
OBSERVED: Mike rolled a mission and entered its generated interior in the
original client, but all ordinary enemies were aliens.
PROVEN: QuestDungeonPlayfield selected the same temporary alien family at every
ordinary spawn and did not pass mission QL to the existing NPC resolver.

The repair uses an editable Population section in DungeonGeneration.json,
referencing six existing nonalien NpcTemplates families. Mission QL now reaches
existing interpolation/clamping through HashSpawnSystem. No duplicate catalog,
runtime capture loading, DAO/schema edit, or layout/entrance change is introduced.
The existing objective target remains separate. All ordinary NPCs remain one-shot.

Approved engine build and 121 focused mission/dungeon tests passed, including
actual catalog NPC spawning at QL2/3/25/220, configured room spawn points,
unresolved-hash rejection, existing target separation and preserved layouts.
Builds still report dependency/security and source warnings. The ordinary full
test project has previously recorded unrelated compile failures; only the
existing focused selection was run. DAO tests were not rerun for this repair.

UNVERIFIED: original-client acceptance of the population repair. Mike performs
that check after local installation; the agent does not control the client.
The older catalog has level gaps and upper limits, so requested QL is not a
guarantee of exact resulting NPC level. Human/high-level coverage, guaranteed
team bosses, team acceptance, objectives and independent loot remain unfinished.

Evidence, all 23 captured instances and limitations:
`docs/evidence/MISSION_DUNGEON_NPC_POPULATION_EVIDENCE.md`.
