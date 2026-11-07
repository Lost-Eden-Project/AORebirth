namespace ZoneEngine_New.Core.Quests.Dungeons;

using System;
using System.Collections.Generic;

/// <summary>A dungeon quest's objective kind, as stored in its frozen parameters (MissionType).</summary>
internal enum MissionRollType { Unknown = -1, KillPerson = 0, FindPerson = 1, FindItem = 2, RepairMachine = 3, FindItemReturn = 4 }

/// <summary>One DungeonGeneration.json MissionTypes row: the journal action and icon for an objective kind.</summary>
public sealed class DungeonMissionTypeRow
{
    public string Type { get; set; } = string.Empty;
    public int Icon { get; set; }
    public int Action { get; set; }
}

/// <summary>
/// The journal action and icon per dungeon objective kind, from DungeonGeneration.json MissionTypes. Set once by
/// <see cref="DungeonLayoutGenerator"/> when the policy loads; the journal is static.
/// </summary>
internal static class DungeonMissionTypes
{
    static readonly Dictionary<MissionRollType, DungeonMissionTypeRow> Rows = new();

    internal static void Configure(IEnumerable<DungeonMissionTypeRow> rows)
    {
        Rows.Clear();
        foreach (DungeonMissionTypeRow row in rows)
            if (Enum.TryParse(row.Type, ignoreCase: true, out MissionRollType type) && type != MissionRollType.Unknown)
                Rows[type] = row;
    }

    internal static bool IsComplete => Rows.Count == 5;

    internal static int Icon(MissionRollType type) => Rows.TryGetValue(type, out DungeonMissionTypeRow? row) ? row.Icon : 0;

    internal static int Action(MissionRollType type) => Rows.TryGetValue(type, out DungeonMissionTypeRow? row) ? row.Action : 0;
}
