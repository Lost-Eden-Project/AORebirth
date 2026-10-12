# Current Task

Increase Find Item objective visibility using Mike's requested 200-percent scale.
He could not locate the objective and deleted that mission. Its missing or
occluded rendering is not proven; this is the requested appearance adjustment.

Change only DungeonGeneration.json FindItem.WorldScalePercent from 150 to 200
in the public policy and selected private runtime policy. Existing acceptance
freezes this value; newly accepted Find Item missions use 200 percent, while
existing bindings remain unchanged. MissionObjectiveItem sends the scale only
for its world representation and omits it after inventory collection.

Inspected DungeonGeneration.json, MissionItemObjectiveBinding.cs,
MissionObjectiveItem.cs, QuestDungeonService.cs, DungeonObjectivePlacement.cs,
QuestDungeonPlayfield.cs, WorldItem.cs, StaticDynel.cs and the installed log.
Runtime code, placement, item identity, pickup, rewards and other objectives
remain unchanged. Preserve local overlays, token data and destination policy.

Validate the scoped JSON change, rebuild/restart through the approved wrapper,
and leave core engines running for Mike's test. No fixture/unit/regression
suites requested or run. Never control the AO client. Client visibility at
200 percent remains pending.
