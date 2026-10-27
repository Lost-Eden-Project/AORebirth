namespace ZoneEngine_New.Core.Quests.Dungeons;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ZoneEngine_New.Core.GameData;

/// <summary>Chest-only exclusions. Event enablement and the event's designated loot sources are not inputs.</summary>
public sealed class MissionChestLootExclusions
{
    public const string SeasonalPath = "Loot/MissionChestSeasonalExclusions.json";
    public const string SpecialPath = "Loot/MissionChestSpecialExclusions.json";
    readonly Dictionary<int, string[]> _categories;

    MissionChestLootExclusions(Dictionary<int, string[]> categories) => _categories = categories;

    public static MissionChestLootExclusions Empty { get; } = new(new());
    public int Count => _categories.Count;

    public static MissionChestLootExclusions Load(string root, IReadOnlyCollection<HashInstance> families)
    {
        var categories = new Dictionary<int, HashSet<string>>();
        foreach (string relativePath in new[] { SeasonalPath, SpecialPath })
        {
            MissionChestLootExclusions file = Parse(File.ReadAllText(Path.Combine(root, relativePath)), families);
            foreach ((int id, string[] reasons) in file._categories)
            {
                if (!categories.TryGetValue(id, out HashSet<string>? current))
                    categories.Add(id, current = new(StringComparer.Ordinal));
                current.UnionWith(reasons);
            }
        }
        return new MissionChestLootExclusions(categories.ToDictionary(pair => pair.Key,
            pair => pair.Value.Order(StringComparer.Ordinal).ToArray()));
    }

    public static MissionChestLootExclusions Parse(string json, IReadOnlyCollection<HashInstance>? families = null)
    {
        Document document = JsonSerializer.Deserialize<Document>(json)
            ?? throw new InvalidDataException("Mission chest exclusions are null.");
        if (document.Version != 1 || document.Entries == null)
            throw new InvalidDataException("Mission chest exclusions require Version 1 and Entries.");
        var categories = new Dictionary<int, HashSet<string>>();
        foreach (Entry entry in document.Entries)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Category) || entry.Evidence == null
                || entry.Evidence.Length == 0 || entry.Evidence.Any(string.IsNullOrWhiteSpace)
                || entry.TemplateIds == null || entry.TemplateIds.Any(id => id <= 0))
                throw new InvalidDataException("Invalid mission chest exclusion identity, category or evidence.");
            var ids = new HashSet<int>(entry.TemplateIds);
            if (!string.IsNullOrWhiteSpace(entry.FamilyHash))
            {
                HashInstance[] matches = families?.Where(family => string.Equals(family.Hash, entry.FamilyHash,
                    StringComparison.Ordinal)).ToArray() ?? [];
                if (matches.Length == 0)
                    throw new InvalidDataException("Unknown excluded mission chest family: " + entry.FamilyHash);
                foreach (HashInstance family in matches)
                    ids.UnionWith(family.TemplateIds);
            }
            if (ids.Count == 0 || ids.Any(id => id <= 0))
                throw new InvalidDataException("Mission chest exclusion has no valid identity.");
            foreach (int id in ids)
            {
                if (!categories.TryGetValue(id, out HashSet<string>? reasons))
                    categories.Add(id, reasons = new(StringComparer.Ordinal));
                reasons.Add(entry.Category);
            }
        }
        return new MissionChestLootExclusions(categories.ToDictionary(pair => pair.Key,
            pair => pair.Value.Order(StringComparer.Ordinal).ToArray()));
    }

    public bool IsExcluded(int lowId, int highId) => _categories.ContainsKey(lowId) || _categories.ContainsKey(highId);

    public string[] ReasonsFor(int lowId, int highId)
        => (_categories.GetValueOrDefault(lowId) ?? []).Concat(_categories.GetValueOrDefault(highId) ?? [])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public sealed class Document
    {
        public int Version { get; set; }
        public Entry[]? Entries { get; set; }
    }

    public sealed class Entry
    {
        public int[]? TemplateIds { get; set; } = [];
        public string? FamilyHash { get; set; }
        public string? Category { get; set; }
        public string[]? Evidence { get; set; }
    }
}
