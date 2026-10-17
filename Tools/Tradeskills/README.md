# Tradeskills

Live keeps tradeskill recipes on the server only: nothing in the RDB says what two items make. ZoneEngine_New
(`Core/Tradeskills`) combines items from one GameData file, `GameData/Tradeskills.json`. It holds the CellAO
tradeskill table, converted once and unverified; captured and hand-checked recipes, each with `Provenance` and
`Evidence`, replace its entries. When two recipes match the same items, the earlier one in
the file wins.

Recipes carry no XP. A combine gives the result's QL x `xp.tradeskillXpPerQl` (5) XP, and only while
`xp.tradeskillGrantsXp` is on in `Config/Gameplay.json` (off by default).

## Getting a recipe in

1. Play the combine live with SessionDump injected (archive on). It records every attempt, from the tradeskill
   window or by dragging one item onto another, in `capture\tradeskills\<source>_<target>_*.json`: the items,
   what was made and at what QL, which inputs were used up, skill requirements, XP, feedback, and the player's
   tradeskill skills per attempt.
2. Run `Tools/CaptureToGameData/capture_to_gamedata.py --playfield <pf> --write`. Recipes that made something at
   least once become `Tradeskills.json` entries. A key that already has a hand-checked recipe is left alone and
   reported, so compare the two and fix the hand-checked one if the capture disagrees.

A combine also completes active `Assemble` quests whose `Objective.SpecItem` is an item hash covering the item it
made (`QuestService.OnItemAssembled`).

## Recipe fields

```json
{ "Key": "150922>42620", "Source": [150922], "Target": [42620, 42619], "AnyOrder": true,
  "Result": { "LowId": 150923, "HighId": 150924 }, "ResultQl": "Target",
  "ConsumeSource": false, "ConsumeTarget": true,
  "Skills": [{ "Stat": 125, "Percent": 400, "PerBump": 0 }],
  "Provenance": "cellao-arete-captures", "Evidence": "capture 20260721-001538: Screwdriver kept, Robot Junk used" }
```

- `Source` / `Target`: template ids; an item matches by its low or high id. `AnyOrder` accepts the items either way.
- `ResultQl`: `Target` (target QL plus skill bumps, capped by `MaxBump`; implants also by QL tier), `Source`, or
  `Fixed` with `FixedQl`.
- `Skills`: needed skill is `Percent`/100 x target QL; every `PerBump` points above it adds a result QL.
- `MinTargetQl`, `QlRangePercent` (source QL may be this % below the target's; 1 = at least the target's QL),
  `Implant` as in CellAO.

## Sources so far

- 40 Arete/Thrak recipes from the old CellAO engine's capture-backed rules (git `a1497a26`, captures 2026-07-18..26):
  Antonio Stacklund's Adaptation Factory, the Personalized Basic Robot Brain chain, Vernon Godfray's library hack,
  Dr. Mason's leg implant, Lorelei's pet cage, the Thrak garden key. Antonio's Adaptation Factory is kept as a
  tool, unlike the old rules: live hands out one, and a captured Range Meter run used it for two combines.
- The CellAO tradeskill table (101,095 recipes), converted once into `Tradeskills.json`; rows naming items the
  client data does not have were dropped. The SQL table and its converter are gone: edit `Tradeskills.json`.

## Not verified yet

The window protocol and the result packets follow CellAO's handler and the July captures of Arete combines. The next
capture with SessionDump's tradeskill recorder shows the live order and fields of the window replies (`Actions` in
each sample); check `TradeskillService` against it.
