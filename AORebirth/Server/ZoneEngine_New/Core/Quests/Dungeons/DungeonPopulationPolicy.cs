namespace ZoneEngine_New.Core.Quests.Dungeons;

using System;
using System.Linq;
using ZoneEngine_New.Core.GameData;

/// <summary>Mission population references into the existing NPC catalog, not a second source of NPC definitions.</summary>
public sealed class DungeonPopulationPolicy
{
    public int SpawnPointChancePercent { get; set; }

    public string[] RegularNpcHashes { get; set; } = [];

    /// <summary>
    /// A quest's population spread: each spawn's level rolls from this many percent under the mission quality to this
    /// many over. 0: every spawn at the quality.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int LevelSpreadPercent { get; set; }

    /// <summary>One spawn's level for a mission of <paramref name="quality"/>, from the room's seeded <paramref name="roll"/>.</summary>
    public int RollLevel(Random roll, int quality)
    {
        ArgumentNullException.ThrowIfNull(roll);
        if (LevelSpreadPercent <= 0 || quality <= 0)
            return quality;
        int low = Math.Max(1, (int)Math.Round(quality * (100 - LevelSpreadPercent) / 100.0, MidpointRounding.AwayFromZero));
        int high = Math.Max(low, (int)Math.Round(quality * (100 + LevelSpreadPercent) / 100.0, MidpointRounding.AwayFromZero));
        return roll.Next(low, high + 1);
    }

    internal bool IsValid(IGameData gameData)
        => SpawnPointChancePercent is >= 0 and <= 100
            && RegularNpcHashes is { Length: > 0 }
            && RegularNpcHashes.Distinct(StringComparer.Ordinal).Count() == RegularNpcHashes.Length
            && RegularNpcHashes.All(hash => !string.IsNullOrWhiteSpace(hash) && gameData.HasMobTemplate(hash));
}
