namespace ZoneEngine_New.Core.Quests.Dungeons;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Mobs;

/// <summary>
/// Editable AORebirth chest placement policy. What a chest holds is an NPC-style item table: a Quests.json dungeon
/// quest's Dungeon.ChestLoot, or for terminal-rolled missions this policy's LootTable. A dungeon whose effective chest
/// item table is empty gets no chests.
/// </summary>
public sealed class MissionChestPolicy
{
    /// <summary>False: generated dungeons get no chest population at all (no placement, loot or stored-chest work).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Chests per eligible room: 0.3 puts one in about a third of the rooms, 2 about two in each. Every room gets one
    /// before any gets a second, and a room never holds more than its free spawn points.
    /// </summary>
    public double RoomDensity { get; set; }

    /// <summary>Most chests per eligible room a policy may ask for.</summary>
    public const double MaxRoomDensity = 4;
    public double CountVariationFraction { get; set; }

    /// <summary>Chest item table for missions rolled at a terminal (a Quests.json quest brings its own ChestLoot).</summary>
    public List<MobItemTableEntry> LootTable { get; set; } = [];

    public ChestSpawnDefinition? SpawnDefinition { get; set; }
    public MissionChestPlacementPolicy? Placement { get; set; }

    [JsonIgnore]
    public bool IsValid => double.IsFinite(RoomDensity) && RoomDensity is >= 0 and <= MaxRoomDensity
        && double.IsFinite(CountVariationFraction) && CountVariationFraction is >= 0 and <= 1
        && LootTable != null && LootTable.All(MobItemTableEntry.IsWellFormed)
        && (Placement == null || Placement.IsValid) && AppearanceValid();

    bool AppearanceValid()
    {
        try
        {
            SpawnDefinition?.Validate();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
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
        LootTable = MobItemTableEntry.Clone(LootTable),
        SpawnDefinition = SpawnDefinition == null ? null : new ChestSpawnDefinition
        {
            StateMachineType = SpawnDefinition.StateMachineType,
            StateMachineInstance = SpawnDefinition.StateMachineInstance,
            HeaderStatBytes = SpawnDefinition.HeaderStatBytes,
            LockableValue = SpawnDefinition.LockableValue,
            Stats = SpawnDefinition.Stats.Select(stat => new ChestSpawnStat { StatId = stat.StatId, Value = stat.Value }).ToArray()
        },
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
