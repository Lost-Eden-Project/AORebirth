namespace ZoneEngine_New.Core.DailyLogin;

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

/// <summary>The existing account board shape, preserved when importing file-backed progress.</summary>
public sealed class DailyLoginAccountState
{
    public const int TotalDays = 28;
    public string Month { get; set; } = string.Empty;
    public int ClaimedCount { get; set; }
    public string LastClaimUtc { get; set; } = string.Empty;
    public string LastGrantedUtc { get; set; } = string.Empty;
    public string CycleCompletedOn { get; set; } = string.Empty;
    public int[] Taken { get; set; } = [];
    public int LastCharacterId { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public int NextDay => Enumerable.Range(1, TotalDays).FirstOrDefault(day => !Taken.Contains(day));

    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime? LastGrantedDateUtc => ParseDate(LastGrantedUtc)
        ?? ((Taken ?? []).Length > 0 ? ParseDate(LastClaimUtc) : null);

    public static DailyLoginAccountState Parse(string json)
        => JsonSerializer.Deserialize<DailyLoginAccountState>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Daily Login account state is empty.");

    public string ToJson() => JsonSerializer.Serialize(this);

    public DailyLoginAccountState Normalize(DateTime utcNow)
    {
        string today = Date(utcNow);
        if ((Taken ?? []).Any(day => day is < 1 or > TotalDays))
            throw new InvalidDataException("Daily Login progress contains an invalid taken day; existing claims were retained.");
        int[] taken = (Taken ?? []).Distinct().Order().ToArray();
        string granted = LastGrantedUtc ?? string.Empty;
        string claimed = LastClaimUtc ?? string.Empty;
        string completed = CycleCompletedOn ?? string.Empty;
        ParseDate(granted);
        ParseDate(claimed);
        ParseDate(completed);
        if (taken.Length > 0 && string.IsNullOrEmpty(granted) && string.IsNullOrEmpty(claimed))
            throw new InvalidDataException("Daily Login progress has taken rewards without a grant date; existing claims were retained.");
        // Old files used LastClaimUtc for the daily lock. Retaining it is conservative: importing
        // historical progress must never unlock another grant on the same UTC day.
        if (string.IsNullOrEmpty(granted) && taken.Length > 0)
            granted = claimed;
        if (taken.Length == TotalDays && string.IsNullOrEmpty(completed))
            completed = !string.IsNullOrEmpty(granted) ? granted : claimed;
        if (taken.Length == TotalDays && !string.IsNullOrEmpty(completed)
            && string.CompareOrdinal(today, completed) > 0)
        {
            taken = [];
            claimed = granted = completed = string.Empty;
        }
        return new DailyLoginAccountState
        {
            Month = Utc(utcNow).ToString("yyyy-MM", CultureInfo.InvariantCulture),
            Taken = taken,
            ClaimedCount = taken.Length,
            LastClaimUtc = claimed,
            LastGrantedUtc = granted,
            CycleCompletedOn = completed,
            LastCharacterId = LastCharacterId
        };
    }

    public bool HasClaimedOn(DateTime utcNow)
        => !string.IsNullOrEmpty(LastGrantedUtc) && string.CompareOrdinal(LastGrantedUtc, Date(utcNow)) >= 0;

    public DailyLoginAccountState RecordGrant(int day, int characterId, DateTime utcNow)
    {
        DailyLoginAccountState next = Normalize(utcNow);
        if (next.HasClaimedOn(utcNow) || day != next.NextDay || day < 1)
            throw new InvalidOperationException("Daily Login reward is no longer available.");
        next.Taken = next.Taken.Append(day).Order().ToArray();
        next.ClaimedCount = next.Taken.Length;
        next.LastClaimUtc = next.LastGrantedUtc = Date(utcNow);
        next.CycleCompletedOn = next.Taken.Length == TotalDays ? Date(utcNow) : string.Empty;
        next.LastCharacterId = characterId;
        return next;
    }

    public static string Date(DateTime utcNow) => Utc(utcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static DateTime Utc(DateTime value)
        => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed))
            throw new InvalidDataException("Daily Login progress contains an invalid UTC date.");
        return parsed.Date;
    }
}
