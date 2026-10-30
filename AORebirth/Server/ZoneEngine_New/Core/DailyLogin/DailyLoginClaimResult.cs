namespace ZoneEngine_New.Core.DailyLogin;

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class DailyLoginClaimResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("committed")] public bool Committed { get; set; }
    [JsonPropertyName("terminal")] public bool Terminal { get; set; }
    [JsonPropertyName("replayed")] public bool Replayed { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("claimToken")] public string ClaimToken { get; set; } = string.Empty;
    [JsonPropertyName("accountKey")] public string AccountKey { get; set; } = string.Empty;
    [JsonPropertyName("characterId")] public int CharacterId { get; set; }
    [JsonPropertyName("hasIdentity")] public bool HasIdentity { get; set; }
    [JsonPropertyName("day")] public int Day { get; set; }
    [JsonPropertyName("itemId")] public int ItemId { get; set; }
    [JsonPropertyName("amount")] public int Amount { get; set; }
    [JsonPropertyName("quality")] public int Quality { get; set; }
    [JsonPropertyName("taken")] public int[] Taken { get; set; } = [];
    [JsonPropertyName("claimedCount")] public int ClaimedCount { get; set; }
    [JsonPropertyName("nextDay")] public int NextDay { get; set; }
    [JsonPropertyName("lastClaimUtc")] public string LastClaimUtc { get; set; } = string.Empty;
    [JsonPropertyName("lastGrantedUtc")] public string LastGrantedUtc { get; set; } = string.Empty;
    [JsonPropertyName("cycleCompletedOn")] public string CycleCompletedOn { get; set; } = string.Empty;
    [JsonPropertyName("claimedToday")] public bool ClaimedToday { get; set; }
    [JsonPropertyName("alreadyClaimedToday")] public bool AlreadyClaimedToday { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static DailyLoginClaimResult Parse(string json)
        => JsonSerializer.Deserialize<DailyLoginClaimResult>(json)
            ?? throw new InvalidDataException("Daily Login claim receipt is empty.");

    public static DailyLoginClaimResult ForState(string account, int characterId, string token, DailyLoginAccountState state,
        DateTime utcNow, string message = "")
        => new()
        {
            AccountKey = account, CharacterId = characterId, ClaimToken = token,
            HasIdentity = characterId > 0 && !string.IsNullOrWhiteSpace(account),
            Taken = state.Taken, ClaimedCount = state.ClaimedCount, NextDay = state.NextDay,
            LastClaimUtc = state.LastClaimUtc, LastGrantedUtc = state.LastGrantedUtc,
            CycleCompletedOn = state.CycleCompletedOn, ClaimedToday = state.HasClaimedOn(utcNow), Message = message
        };
}
