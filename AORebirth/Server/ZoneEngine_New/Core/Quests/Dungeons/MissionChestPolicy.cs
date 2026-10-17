namespace ZoneEngine_New.Core.Quests.Dungeons;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using ZoneEngine_New.Core.Entities;

/// <summary>Editable AORebirth chest policy; rarity labels/weights are user-defined, not retail frequency claims.</summary>
public sealed class MissionChestPolicy
{
    public const string MissingRarity = "Missing";

    public double RoomDensity { get; set; }
    public double CountVariationFraction { get; set; }
    public int MinItems { get; set; }
    public int MaxItems { get; set; }
    public Dictionary<string, double> RarityWeights { get; set; } = new(StringComparer.Ordinal);
    public ChestSpawnDefinition? SpawnDefinition { get; set; }
    public MissionChestPlacementPolicy? Placement { get; set; }

    [JsonIgnore]
    public bool IsValid => double.IsFinite(RoomDensity) && RoomDensity is >= 0 and <= 1
        && double.IsFinite(CountVariationFraction) && CountVariationFraction is >= 0 and <= 1
        && MinItems >= 1 && MaxItems >= MinItems && MaxItems <= 3
        && RarityWeights != null && RarityWeights.Count == 6
        && RarityWeights.ContainsKey(MissingRarity)
        && Enumerable.Range(1, 5).All(value => RarityWeights.ContainsKey(value.ToString(CultureInfo.InvariantCulture)))
        && RarityWeights.Values.All(weight => double.IsFinite(weight) && weight >= 0)
        && RarityWeights.Values.Sum() is > 0 and < double.PositiveInfinity
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
        return checked((int)count);
    }

    internal MissionChestPolicy Clone() => new()
    {
        RoomDensity = RoomDensity,
        CountVariationFraction = CountVariationFraction,
        MinItems = MinItems,
        MaxItems = MaxItems,
        SpawnDefinition = SpawnDefinition == null ? null : new ChestSpawnDefinition
        {
            StateMachineType = SpawnDefinition.StateMachineType,
            StateMachineInstance = SpawnDefinition.StateMachineInstance,
            HeaderStatBytes = SpawnDefinition.HeaderStatBytes,
            LockableValue = SpawnDefinition.LockableValue,
            Stats = SpawnDefinition.Stats.Select(stat => new ChestSpawnStat { StatId = stat.StatId, Value = stat.Value }).ToArray()
        },
        Placement = Placement?.Clone(),
        RarityWeights = new Dictionary<string, double>(RarityWeights, StringComparer.Ordinal)
    };
}

/// <summary>Bounded placement checks over existing collision geometry; these are configurable AORebirth margins.</summary>
public sealed class MissionChestPlacementPolicy
{
    public string[] ExcludedRoomNameParts { get; set; } = [];
    public int CandidateAttemptsPerRoom { get; set; }
    public int CandidatesPerRoom { get; set; }
    public float FloorProbeLift { get; set; }
    public float MaxFloorDrop { get; set; }
    public float WalkSampleSpacing { get; set; }
    public float MaxWalkStep { get; set; }
    public float CollisionPadding { get; set; }
    public float DoorClearance { get; set; }
    public float ObjectiveClearance { get; set; }
    public float ChestClearance { get; set; }
    public float EntryClearance { get; set; }

    [JsonIgnore]
    public bool IsValid => ExcludedRoomNameParts != null && ExcludedRoomNameParts.All(part => !string.IsNullOrWhiteSpace(part))
        && CandidateAttemptsPerRoom is >= 1 and <= 256
        && CandidatesPerRoom is >= 1 and <= 32 && CandidateAttemptsPerRoom >= CandidatesPerRoom
        && Positive(FloorProbeLift) && Positive(MaxFloorDrop) && Positive(WalkSampleSpacing) && Positive(MaxWalkStep)
        && Nonnegative(CollisionPadding) && Positive(DoorClearance) && Positive(ObjectiveClearance)
        && Positive(ChestClearance) && Positive(EntryClearance);

    static bool Positive(float value) => float.IsFinite(value) && value > 0;
    static bool Nonnegative(float value) => float.IsFinite(value) && value >= 0;

    internal MissionChestPlacementPolicy Clone() => new()
    {
        ExcludedRoomNameParts = (string[])ExcludedRoomNameParts.Clone(),
        CandidateAttemptsPerRoom = CandidateAttemptsPerRoom,
        CandidatesPerRoom = CandidatesPerRoom,
        FloorProbeLift = FloorProbeLift,
        MaxFloorDrop = MaxFloorDrop,
        WalkSampleSpacing = WalkSampleSpacing,
        MaxWalkStep = MaxWalkStep,
        CollisionPadding = CollisionPadding,
        DoorClearance = DoorClearance,
        ObjectiveClearance = ObjectiveClearance,
        ChestClearance = ChestClearance,
        EntryClearance = EntryClearance
    };
}
