namespace ZoneEngine_New.Core.DailyLogin;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using ZoneEngine_New.Core.GameData;

/// <summary>Editable reward definitions; browser requests never supply reward identities or quantities.</summary>
public sealed class DailyLoginRewardCatalog
{
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    readonly Dictionary<int, DailyLoginDayReward> _days;
    readonly Dictionary<int, IReadOnlyList<DailyLoginNanoReward>> _professionNanos;

    DailyLoginRewardCatalog(bool freeTestMode, Dictionary<int, DailyLoginDayReward> days,
        Dictionary<int, IReadOnlyList<DailyLoginNanoReward>> professionNanos, IReadOnlyCollection<HashInstance> qualityFamilies)
    {
        FreeTestMode = freeTestMode;
        _days = days;
        _professionNanos = professionNanos;
        QualityFamilies = qualityFamilies;
    }

    public bool FreeTestMode { get; }
    public IReadOnlyDictionary<int, DailyLoginDayReward> Days => _days;
    public IReadOnlyCollection<HashInstance> QualityFamilies { get; }

    public bool TryGet(int day, out DailyLoginDayReward reward) => _days.TryGetValue(day, out reward!);

    public bool TryGetProfessionNanos(int profession, out IReadOnlyList<DailyLoginNanoReward> nanos)
        => _professionNanos.TryGetValue(profession, out nanos!);

    public static DailyLoginRewardCatalog Load(string rewardsPath, string? professionNanoPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rewardsPath);
        return Parse(File.ReadAllText(rewardsPath),
            !string.IsNullOrWhiteSpace(professionNanoPath) && File.Exists(professionNanoPath)
                ? File.ReadAllText(professionNanoPath) : null);
    }

    public static DailyLoginRewardCatalog Parse(string rewardsJson, string? professionNanoJson = null)
    {
        RewardsDocument document = JsonSerializer.Deserialize<RewardsDocument>(rewardsJson, JsonOptions)
            ?? throw new InvalidDataException("Daily Login reward catalog is empty.");
        if (document.Days is null || document.Days.Count == 0)
            throw new InvalidDataException("Daily Login reward catalog has no days.");

        var days = new Dictionary<int, DailyLoginDayReward>();
        foreach ((string key, DailyLoginDayReward reward) in document.Days)
        {
            if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int day)
                || day < 1 || reward is null || reward.Amount <= 0 || reward.ItemId < 0
                || reward.Quality < 0 || reward.QualityDelta < 0 || !IsKnownQualityMode(reward.QualityMode)
                || !days.TryAdd(day, reward))
                throw new InvalidDataException("Invalid Daily Login reward definition for day " + key + ".");
        }

        var professions = new Dictionary<int, IReadOnlyList<DailyLoginNanoReward>>();
        if (!string.IsNullOrWhiteSpace(professionNanoJson))
        {
            NanoDocument nanoDocument = JsonSerializer.Deserialize<NanoDocument>(professionNanoJson, JsonOptions)
                ?? throw new InvalidDataException("Daily Login profession nano catalog is empty.");
            if (nanoDocument.Professions is not null)
            {
                foreach ((string key, DailyLoginNanoReward[] entries) in nanoDocument.Professions)
                {
                    if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int profession)
                        || profession < 1 || entries is null || !professions.TryAdd(profession, entries))
                        throw new InvalidDataException("Invalid Daily Login profession nano catalog key " + key + ".");
                }
            }
        }
        HashItemCatalog families = HashItemCatalog.Parse(document.QualityFamilies.ValueKind == JsonValueKind.Object
            ? document.QualityFamilies.GetRawText() : null);
        return new DailyLoginRewardCatalog(document.FreeTestMode, days, professions, families.Instances);
    }

    static bool IsKnownQualityMode(string? mode)
        => string.IsNullOrEmpty(mode)
            || string.Equals(mode, "fixed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "characterLevel", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "characterLevelPlusMinus", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "professionVendorNano", StringComparison.OrdinalIgnoreCase);

    sealed class RewardsDocument
    {
        public bool FreeTestMode { get; set; }
        public Dictionary<string, DailyLoginDayReward>? Days { get; set; }
        public JsonElement QualityFamilies { get; set; }
    }

    sealed class NanoDocument
    {
        public Dictionary<string, DailyLoginNanoReward[]>? Professions { get; set; }
    }
}

public sealed class DailyLoginDayReward
{
    public int ItemId { get; set; }
    public int Amount { get; set; } = 1;
    public int Quality { get; set; } = 1;
    public string? QualityMode { get; set; }
    public int QualityDelta { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int[]? RandomItemIds { get; set; }
}

public sealed class DailyLoginNanoReward
{
    public int ItemId { get; set; }
    public int Quality { get; set; }
}

public sealed record DailyLoginRewardSelection(
    int LowId, int HighId, int Quality, int Amount, int ItemCount, int StackCount, string ItemName);
