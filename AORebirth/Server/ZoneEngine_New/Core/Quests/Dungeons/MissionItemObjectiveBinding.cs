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

    [JsonIgnore]
    public bool IsValid => !string.IsNullOrWhiteSpace(QuestId) && OwnerId > 0
        && IdentityType == (int)SmokeLounge.AOtomation.Messaging.GameData.IdentityType.Terminal
        && Instance > 0 && LowId > 0 && HighId > 0 && Quality > 0 && RoomIndex > 0
        && float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z) && QuestInstanceStat >= 0;

    [JsonIgnore]
    public Identity Identity => new() { Type = (SmokeLounge.AOtomation.Messaging.GameData.IdentityType)IdentityType, Instance = Instance };

    public bool SameAs(MissionItemObjectiveBinding? other)
        => other != null && string.Equals(QuestId, other.QuestId, StringComparison.Ordinal)
            && OwnerId == other.OwnerId && IdentityType == other.IdentityType && Instance == other.Instance
            && LowId == other.LowId && HighId == other.HighId && Quality == other.Quality && RoomIndex == other.RoomIndex
            && BitConverter.SingleToUInt32Bits(X) == BitConverter.SingleToUInt32Bits(other.X)
            && BitConverter.SingleToUInt32Bits(Y) == BitConverter.SingleToUInt32Bits(other.Y)
            && BitConverter.SingleToUInt32Bits(Z) == BitConverter.SingleToUInt32Bits(other.Z)
            && QuestInstanceStat == other.QuestInstanceStat && WorldFlags == other.WorldFlags;
}
