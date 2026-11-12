namespace ZoneEngine_New.Core.Quests.Dungeons;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

/// <summary>
/// DungeonGeneration.json chest policy: the global switch and which rooms chests avoid. Chests are defined per quest
/// (a Quests.json dungeon quest's Dungeon.Chests); a dungeon whose frozen chest loot table is empty gets none. The
/// count settings default in code and a quest may override them for its own dungeons.
/// </summary>
public sealed class MissionChestPolicy
{
    /// <summary>Chests per eligible room when the quest names none.</summary>
    public const double DefaultRoomDensity = 0.3;

    /// <summary>Count variation either way when the quest names none.</summary>
    public const double DefaultCountVariationFraction = 0.2;

    /// <summary>Most chests per eligible room a quest may ask for.</summary>
    public const double MaxRoomDensity = 4;

    /// <summary>False: generated dungeons get no chest population at all (no placement, loot or stored-chest work).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Chests per eligible room: 0.3 puts one in about a third of the rooms, 2 about two in each. Every room gets one
    /// before any gets a second, and a room never holds more than its free spawn points. Set per mission, not from JSON.
    /// </summary>
    [JsonIgnore]
    public double RoomDensity { get; set; } = DefaultRoomDensity;

    /// <summary>How far the chest count varies either way, as a fraction. Set per mission, not from JSON.</summary>
    [JsonIgnore]
    public double CountVariationFraction { get; set; } = DefaultCountVariationFraction;

    public MissionChestPlacementPolicy? Placement { get; set; }

    public static bool IsValidRoomDensity(double density) => double.IsFinite(density) && density is >= 0 and <= MaxRoomDensity;

    public static bool IsValidCountVariation(double variation) => double.IsFinite(variation) && variation is >= 0 and <= 1;

    [JsonIgnore]
    public bool IsValid => IsValidRoomDensity(RoomDensity) && IsValidCountVariation(CountVariationFraction)
        && (Placement == null || Placement.IsValid);

    /// <summary>This policy with a mission's frozen count overrides (null keeps the code defaults).</summary>
    internal MissionChestPolicy ForMission(double? roomDensity, double? countVariationFraction)
    {
        MissionChestPolicy policy = Clone();
        policy.RoomDensity = roomDensity ?? DefaultRoomDensity;
        policy.CountVariationFraction = countVariationFraction ?? DefaultCountVariationFraction;
        return policy;
    }

    /// <summary>One count roll over eligible actual rooms, persisted with the generated placement results.</summary>
    public int RollChestCount(int roomCount, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (roomCount < 0)
            throw new ArgumentOutOfRangeException(nameof(roomCount));
        if (!IsValid)
            throw new InvalidOperationException("Invalid mission chest policy.");
        double factor = 1 + ((2 * random.NextDouble() - 1) * CountVariationFraction);
        double count = Math.Round(roomCount * RoomDensity * factor, MidpointRounding.AwayFromZero);
        if (roomCount > 0 && RoomDensity > 0)
            count = Math.Max(1, count);
        return checked((int)count);
    }

    internal MissionChestPolicy Clone() => new()
    {
        Enabled = Enabled,
        RoomDensity = RoomDensity,
        CountVariationFraction = CountVariationFraction,
        Placement = Placement?.Clone()
    };
}

/// <summary>Which rooms chests avoid, by a part of the room template's name (hallways, stairs, vents and the like).</summary>
public sealed class MissionChestPlacementPolicy
{
    public string[] ExcludedRoomNameParts { get; set; } = [];

    [JsonIgnore]
    public bool IsValid => ExcludedRoomNameParts != null && ExcludedRoomNameParts.All(part => !string.IsNullOrWhiteSpace(part));

    internal MissionChestPlacementPolicy Clone() => new() { ExcludedRoomNameParts = (string[])ExcludedRoomNameParts.Clone() };
}
