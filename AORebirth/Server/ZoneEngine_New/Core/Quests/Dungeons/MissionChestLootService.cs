namespace ZoneEngine_New.Core.Quests.Dungeons;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using AORebirth.Enums;
using SmokeLounge.AOtomation.Messaging.GameData;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;

/// <summary>
/// Independent chest selection over the item database and existing family metadata. Allocation is not persistence;
/// the dungeon DAO must save the resulting instances and placement atomically before exposing a chest.
/// </summary>
public sealed class MissionChestLootService
{
    readonly IItemTemplateCatalog _catalog;
    readonly IItemBuilder _items;
    readonly MissionChestPolicy _policy;
    readonly HashInstance[] _families;
    readonly ConcurrentDictionary<int, EligiblePool> _eligible = new();

    public MissionChestLootService(IItemTemplateCatalog catalog, IGameData gameData,
        IItemBuilder items, MissionChestPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.IsValid)
            throw new ArgumentException("Invalid mission chest policy.", nameof(policy));
        _catalog = catalog;
        _items = items;
        _policy = policy.Clone();
        _families = BuildFamilies(gameData.ItemFamilies);
    }

    public IReadOnlyList<Item> Roll(int missionQuality, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var result = new Item[random.Next(_policy.MinItems, _policy.MaxItems + 1)];
        for (int index = 0; index < result.Length; index++)
            result[index] = RollItem(missionQuality, random);
        return result;
    }

    public Item RollItem(int missionQuality, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (missionQuality is < 1 or > 250)
            throw new ArgumentOutOfRangeException(nameof(missionQuality));
        EligiblePool pool = _eligible.GetOrAdd(missionQuality, BuildEligible);
        if (pool.Groups.Length == 0)
            throw new InvalidOperationException("No eligible mission chest items for mission QL " + missionQuality);

        // Eligibility comes first. Empty groups have no weight, so this sum renormalizes the configured groups.
        double roll = random.NextDouble() * pool.TotalWeight;
        EligibleGroup selected = pool.Groups[^1];
        foreach (EligibleGroup group in pool.Groups)
        {
            roll -= group.Weight;
            if (roll >= 0)
                continue;
            selected = group;
            break;
        }

        EligibleItem item = selected.Items[random.Next(selected.Items.Length)];
        QualityChoice choice = item.Qualities[random.Next(item.Qualities.Length)];
        Item minted = _items.CreateWithNewInstance(choice.LowId, choice.HighId, choice.Quality, ItemSource.Loot);
        if (minted.Quality != choice.Quality || !CanCarry(minted.Definition)
            || RarityKey(minted.Definition) != selected.Rarity)
            throw new InvalidDataException("Item builder changed an eligible chest selection.");
        return minted;
    }

    HashInstance[] BuildFamilies(IReadOnlyCollection<HashInstance> source)
    {
        var result = new List<HashInstance>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var covered = new HashSet<int>();
        foreach (HashInstance family in source)
        {
            int[] ids = family.TemplateIds.Distinct().Order().ToArray();
            if (ids.Length < 2 || ids.Any(id => id <= 0 || !_catalog.TryGet(id, out ItemTemplate template) || template.Quality < 1))
                continue;
            // Same-QL variants do not define interpolation bands. Preserve each original record as a fixed-Ql
            // choice below rather than letting TrySelectIds silently choose one variant for all the others.
            if (ids.Select(id => _catalog.Require(id).Quality).Distinct().Count() != ids.Length)
                continue;
            string key = string.Join(",", ids);
            if (!seen.Add(key))
                continue;
            result.Add(new HashInstance(family.Hash, ids));
            covered.UnionWith(ids);
        }

        foreach (ItemTemplate template in _catalog.Templates.OrderBy(template => template.Id))
        {
            if (template.Id <= 0 || template.Quality < 1 || covered.Contains(template.Id))
                continue;
            // No name-based pairing or fabricated quality range for records absent from family metadata.
            result.Add(new HashInstance(template.Id.ToString(CultureInfo.InvariantCulture), [template.Id]));
        }
        return result.ToArray();
    }

    EligiblePool BuildEligible(int missionQuality)
    {
        int minimum = Math.Max(1, missionQuality - 5);
        int maximum = Math.Min(250, missionQuality + 5);
        var groups = new Dictionary<string, List<EligibleItem>>(StringComparer.Ordinal);
        var uniqueChoices = new Dictionary<string, HashSet<QualityChoice[]>>(StringComparer.Ordinal);
        foreach (HashInstance family in _families)
        {
            int lowQuality = family.TemplateIds.Min(id => _catalog.Require(id).Quality);
            int highQuality = family.TemplateIds.Max(id => _catalog.Require(id).Quality);
            int from = Math.Max(minimum, lowQuality);
            int to = Math.Min(maximum, highQuality);
            if (from > to)
                continue;
            var qualities = new Dictionary<string, List<QualityChoice>>(StringComparer.Ordinal);
            for (int quality = from; quality <= to; quality++)
            {
                if (!HashItemCatalog.TrySelectIds(family, quality, id => _catalog.Require(id).Quality,
                        out int lowId, out int highId))
                    continue;
                // Existing builder owns endpoint choice, all interpolation (including Rarity), and QL clamping.
                ItemTemplate definition = _items.CreateTemplate(lowId, highId, quality);
                if (definition.Quality != quality || !CanCarry(definition))
                    continue;
                string rarity = RarityKey(definition);
                if (!_policy.RarityWeights.ContainsKey(rarity))
                    throw new InvalidDataException("Mission chest rarity has no configured group: " + rarity);
                if (_policy.RarityWeights[rarity] == 0)
                    continue;
                if (!qualities.TryGetValue(rarity, out List<QualityChoice>? choices))
                {
                    choices = [];
                    qualities.Add(rarity, choices);
                }
                choices.Add(new QualityChoice(lowId, highId, quality));
            }

            foreach ((string rarity, List<QualityChoice> choices) in qualities)
            {
                QualityChoice[] options = choices.ToArray();
                if (!groups.TryGetValue(rarity, out List<EligibleItem>? candidates))
                {
                    candidates = [];
                    groups.Add(rarity, candidates);
                    uniqueChoices.Add(rarity, new HashSet<QualityChoice[]>(QualityChoiceComparer.Instance));
                }
                // Different full family definitions may become the exact same mintable item in this QL window.
                // They receive one ticket, while genuinely different eligible identities/ranges remain distinct.
                if (uniqueChoices[rarity].Add(options))
                    candidates.Add(new EligibleItem(options));
            }
        }

        EligibleGroup[] eligible = groups.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new EligibleGroup(pair.Key, _policy.RarityWeights[pair.Key], pair.Value.ToArray())).ToArray();
        return new EligiblePool(eligible, eligible.Sum(group => group.Weight));
    }

    static bool CanCarry(ItemTemplate template)
        => template.Stats.TryGetValue(CharacterStat.Can, out int can) && ((uint)can & (uint)CanFlags.Carry) != 0;

    static string RarityKey(ItemTemplate template)
        => template.Stats.TryGetValue(CharacterStat.Rarity, out int rarity)
            ? rarity.ToString(CultureInfo.InvariantCulture) : MissionChestPolicy.MissingRarity;

    readonly record struct QualityChoice(int LowId, int HighId, int Quality);
    sealed class QualityChoiceComparer : IEqualityComparer<QualityChoice[]>
    {
        internal static readonly QualityChoiceComparer Instance = new();

        public bool Equals(QualityChoice[]? first, QualityChoice[]? second)
            => ReferenceEquals(first, second) || first != null && second != null && first.AsSpan().SequenceEqual(second);

        public int GetHashCode(QualityChoice[] choices)
        {
            var hash = new HashCode();
            foreach (QualityChoice choice in choices)
                hash.Add(choice);
            return hash.ToHashCode();
        }
    }

    sealed record EligibleItem(QualityChoice[] Qualities);
    sealed record EligibleGroup(string Rarity, double Weight, EligibleItem[] Items);
    sealed record EligiblePool(EligibleGroup[] Groups, double TotalWeight);
}
