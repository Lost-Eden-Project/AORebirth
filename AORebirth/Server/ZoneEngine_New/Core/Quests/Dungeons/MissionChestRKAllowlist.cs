namespace ZoneEngine_New.Core.Quests.Dungeons;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ZoneEngine_New.Core.GameData;

/// <summary>Reviewed ordinary RK pairs and QL ranges, with unconditional special/seasonal vetoes.</summary>
public sealed class MissionChestRKAllowlist
{
    public const string RelativePath = "Loot/MissionChestRKAllowlist.json";
    public const string OrdinaryClassification = "OrdinaryRubiKa";

    readonly Dictionary<(int LowId, int HighId), Approval[]> _pairs;
    readonly MissionChestLootExclusions _exclusions;

    MissionChestRKAllowlist(Approval[] approvals, string? unavailableReason = null,
        MissionChestLootExclusions? exclusions = null)
    {
        _pairs = approvals.GroupBy(entry => (entry.LowId, entry.HighId))
            .ToDictionary(group => group.Key, group => group.ToArray());
        UnavailableReason = unavailableReason;
        Count = approvals.Length;
        _exclusions = exclusions ?? MissionChestLootExclusions.Empty;
    }

    public int Count { get; }
    public string? UnavailableReason { get; }
    public IEnumerable<(int LowId, int HighId)> ApprovedPairs => _pairs.Keys;

    public static MissionChestRKAllowlist Load(string gameDataRoot, IReadOnlyCollection<HashInstance>? families = null)
    {
        string path = Path.Combine(gameDataRoot, "Loot", "MissionChestRKAllowlist.json");
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path),
                    MissionChestLootExclusions.Load(gameDataRoot, families ?? []))
                : new MissionChestRKAllowlist([], "allowlist-missing");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException)
        {
            // Missing or invalid policy withholds loot, including previously persisted rows.
            return new MissionChestRKAllowlist([], "allowlist-invalid: " + exception.Message);
        }
    }

    public static MissionChestRKAllowlist Parse(string json, MissionChestLootExclusions? exclusions = null)
    {
        Document document = JsonSerializer.Deserialize<Document>(json)
            ?? throw new InvalidDataException("RK chest allowlist is null.");
        if (document.Version != 1 || document.Entries == null)
            throw new InvalidDataException("RK chest allowlist requires Version 1 and Entries.");
        var approvals = new List<Approval>();
        foreach (Entry entry in document.Entries)
        {
            if (entry == null || entry.LowId <= 0 || entry.HighId <= 0
                || entry.MinQuality < 1 || entry.MaxQuality < entry.MinQuality
                || entry.Evidence == null || entry.Evidence.Length == 0
                || entry.Classification != OrdinaryClassification
                || entry.Evidence.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Invalid RK chest approval identity, QL range, classification or evidence.");
            approvals.Add(new Approval(entry.LowId, entry.HighId, entry.MinQuality, entry.MaxQuality,
                entry.Evidence.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
        }
        return new MissionChestRKAllowlist(approvals.ToArray(), approvals.Count == 0 ? "allowlist-empty" : null, exclusions);
    }

    public bool IsAllowed(int lowId, int highId, int quality)
        => !_exclusions.IsExcluded(lowId, highId) && _pairs.TryGetValue((lowId, highId), out Approval[]? entries)
            && entries.Any(entry => quality >= entry.MinQuality && quality <= entry.MaxQuality);

    public string[] EvidenceFor(int lowId, int highId, int quality)
        => IsAllowed(lowId, highId, quality) && _pairs.TryGetValue((lowId, highId), out Approval[]? entries)
            ? entries.Where(entry => quality >= entry.MinQuality && quality <= entry.MaxQuality)
                .SelectMany(entry => entry.Evidence).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            : [];

    public string ClassificationFor(int lowId, int highId, int quality)
        => IsAllowed(lowId, highId, quality) ? OrdinaryClassification : "Unapproved";

    public string[] ExclusionReasons(int lowId, int highId) => _exclusions.ReasonsFor(lowId, highId);

    sealed record Approval(int LowId, int HighId, int MinQuality, int MaxQuality, string[] Evidence);

    public sealed class Document
    {
        public int Version { get; set; }
        public Entry[]? Entries { get; set; }
    }

    public sealed class Entry
    {
        public int LowId { get; set; }
        public int HighId { get; set; }
        public int MinQuality { get; set; }
        public int MaxQuality { get; set; }
        public string[]? Evidence { get; set; }
        public string? Classification { get; set; }
    }
}
