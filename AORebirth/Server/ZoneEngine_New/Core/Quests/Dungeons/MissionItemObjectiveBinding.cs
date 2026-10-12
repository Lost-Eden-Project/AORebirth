namespace ZoneEngine_New.Core.Quests.Dungeons;

using System;
using System.Text.Json.Serialization;
using SmokeLounge.AOtomation.Messaging.GameData;

/// <summary>The accepted mission's exact world item, independent of its advertised reward.</summary>
public sealed class MissionItemObjectiveBinding
{
    public string QuestId { get; set; } = string.Empty;
    public int OwnerId { get; set; }
    public int IdentityType { get; set; }
    public int Instance { get; set; }
    public int LowId { get; set; }
    public int HighId { get; set; }
    public int Quality { get; set; }
    public int RoomIndex { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public int QuestInstanceStat { get; set; }
    public uint? WorldFlags { get; set; }
    public int? WorldScalePercent { get; set; }
    public int MissionType { get; set; } = 2;
    public bool IsSourceItem { get; set; }

    [JsonIgnore]
    public bool IsValid => !string.IsNullOrWhiteSpace(QuestId) && OwnerId > 0
        && MissionType is 2 or 3 or 4 && (!IsSourceItem || MissionType == 3)
        && IdentityType == (int)SmokeLounge.AOtomation.Messaging.GameData.IdentityType.Terminal
        && Instance > 0 && LowId > 0 && HighId > 0 && Quality > 0 && (IsSourceItem ? RoomIndex == 0 : RoomIndex > 0)
        && float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z) && QuestInstanceStat >= 0
        && (WorldScalePercent == null || WorldScalePercent > 0);

    [JsonIgnore]
    public Identity Identity => new() { Type = (SmokeLounge.AOtomation.Messaging.GameData.IdentityType)IdentityType, Instance = Instance };

    public bool SameAs(MissionItemObjectiveBinding? other)
        => other != null && string.Equals(QuestId, other.QuestId, StringComparison.Ordinal)
            && OwnerId == other.OwnerId && IdentityType == other.IdentityType && Instance == other.Instance
            && LowId == other.LowId && HighId == other.HighId && Quality == other.Quality && RoomIndex == other.RoomIndex
            && BitConverter.SingleToUInt32Bits(X) == BitConverter.SingleToUInt32Bits(other.X)
            && BitConverter.SingleToUInt32Bits(Y) == BitConverter.SingleToUInt32Bits(other.Y)
            && BitConverter.SingleToUInt32Bits(Z) == BitConverter.SingleToUInt32Bits(other.Z)
            && QuestInstanceStat == other.QuestInstanceStat && WorldFlags == other.WorldFlags
            && WorldScalePercent == other.WorldScalePercent
            && MissionType == other.MissionType && IsSourceItem == other.IsSourceItem;
}

/// <summary>The catalog NPC and exact actor identity frozen with an accepted mission.</summary>
public sealed class MissionNpcObjectiveBinding
{
    public string QuestId { get; set; } = string.Empty;
    public int OwnerId { get; set; }
    public int IdentityType { get; set; } = (int)SmokeLounge.AOtomation.Messaging.GameData.IdentityType.CanbeAffected;
    public int Instance { get; set; }
    public string NpcHash { get; set; } = string.Empty;
    public int Quality { get; set; }
    public int? ScalePercent { get; set; }
    public int RoomIndex { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public int MissionType { get; set; }

    [JsonIgnore]
    public Identity Identity => new() { Type = (SmokeLounge.AOtomation.Messaging.GameData.IdentityType)IdentityType, Instance = Instance };

    [JsonIgnore]
    public bool IsValid => !string.IsNullOrWhiteSpace(QuestId) && OwnerId > 0
        && IdentityType == (int)SmokeLounge.AOtomation.Messaging.GameData.IdentityType.CanbeAffected
        && Instance > 0 && !string.IsNullOrWhiteSpace(NpcHash) && Quality > 0 && RoomIndex > 0
        && MissionType is 0 or 1 && float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z)
        && (ScalePercent == null || ScalePercent > 0);

    public bool SameAs(MissionNpcObjectiveBinding? other) => other != null
        && string.Equals(QuestId, other.QuestId, StringComparison.Ordinal) && OwnerId == other.OwnerId
        && IdentityType == other.IdentityType && Instance == other.Instance && NpcHash == other.NpcHash
        && Quality == other.Quality && ScalePercent == other.ScalePercent
        && RoomIndex == other.RoomIndex && MissionType == other.MissionType
        && BitConverter.SingleToUInt32Bits(X) == BitConverter.SingleToUInt32Bits(other.X)
        && BitConverter.SingleToUInt32Bits(Y) == BitConverter.SingleToUInt32Bits(other.Y)
        && BitConverter.SingleToUInt32Bits(Z) == BitConverter.SingleToUInt32Bits(other.Z);
}
