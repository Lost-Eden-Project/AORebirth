# Mission loot tables

Generated mission loot uses the same loot-table shape as an NPC's
`LootTable` in `NpcTemplates.json`: a list of rows, each

```json
{ "Hash": "ARZNQL", "Repeats": 1, "Chance": 26, "LevelMod": 0 }
```

`Hash` names an item hash (`ItemTemplates.json`), `Repeats` is how many times
the row is tried, `Chance` the percent chance per try, and `LevelMod` the
percent window around the roll level the item quality is picked from (0 uses
the roller's default). Rows roll through the existing `LootableDynel.RollItemTable`
and `HashItemMinter`, exactly as an NPC corpse does.

## Where mission loot is defined

- `Quests.json` dungeon quest, under `Dungeon`:
  - `Chests`: the dungeon's chests. `Enabled` (default true) turns them on or
    off for this quest; `RoomDensity` and `CountVariationFraction` are optional
    and override the code defaults (0.3 and 0.2); `LootTable` is what every
    chest holds, rolled at the mission quality when the dungeon is first
    populated. No `Chests`, `Enabled: false` or an empty table: no chests.
  - `MobLoot`: rows added to every NPC that dies in the dungeon, on top of the
    NPC's own table. Null or empty: none.
- Missions rolled at a terminal (no Quests.json template) get no chests and add
  nothing to their NPCs' own tables.
- Rare drops are ordinary rows on the NPC that drops them, in
  `NpcTemplates.json`; the public placeholder template `AAAA` shows the shape.

The chest settings and both tables are frozen into the accepted mission's
parameters, so a dungeon rebuilt later uses what it was accepted with. Chest
contents are rolled once per chest and persisted as ordinary container item rows.

`Chests` in `DungeonGeneration.json` holds only the global policy: `Enabled`
(false turns chest generation off for every dungeon) and `Placement`.
