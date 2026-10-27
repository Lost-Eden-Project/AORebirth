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
    readonly MissionChestRKAllowlist _allowlist;
    readonly HashInstance[] _families;
    readonly ConcurrentDictionary<int, EligiblePool> _eligible = new();

    public MissionChestLootService(IItemTemplateCatalog catalog, IGameData gameData,
        IItemBuilder items, MissionChestPolicy policy, MissionChestRKAllowlist? allowlist = null)
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
        _allowlist = allowlist ?? MissionChestRKAllowlist.Load(gameData.RootPath, gameData.ItemFamilies);
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
        (QualityChoice choice, string rarity) = Select(missionQuality, random);
        Item minted = _items.CreateWithNewInstance(choice.LowId, choice.HighId, choice.Quality, ItemSource.Loot);
        if (minted.LowId != choice.LowId || minted.HighId != choice.HighId || minted.Quality != choice.Quality
            || !IsApproved(minted, missionQuality, _allowlist) || RarityKey(minted.Definition) != rarity)
            throw new InvalidDataException("Item builder changed an approved chest selection.");
        return minted;
    }

    /// <summary>Same count and selection as Roll, without allocating instance IDs or touching inventory.</summary>
    public IReadOnlyList<MissionChestLootPreview> RollPreview(int missionQuality, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var result = new MissionChestLootPreview[random.Next(_policy.MinItems, _policy.MaxItems + 1)];
        for (int index = 0; index < result.Length; index++)
        {
            (QualityChoice choice, string rarity) = Select(missionQuality, random);
            ItemTemplate definition = _items.CreateTemplate(choice.LowId, choice.HighId, choice.Quality);
            if (!ApprovedDefinition(definition, choice.LowId, choice.HighId, choice.Quality)
                || RarityKey(definition) != rarity)
                throw new InvalidDataException("Item builder changed an approved chest preview.");
            result[index] = new MissionChestLootPreview(choice.LowId, choice.HighId, choice.Quality,
                definition.Name, rarity, _allowlist.EvidenceFor(choice.LowId, choice.HighId, choice.Quality),
                _allowlist.ClassificationFor(choice.LowId, choice.HighId, choice.Quality));
        }
        return result;
    }

    public MissionChestLootDiagnostics GetDiagnostics(int missionQuality)
    {
        EligiblePool pool = GetPool(missionQuality);
        return new MissionChestLootDiagnostics(pool.ApprovedCandidates,
            _families.Length - pool.ApprovedCandidates,
            pool.Groups.Sum(group => group.Items.Sum(item => item.Qualities.Length)));
    }

    (QualityChoice Choice, string Rarity) Select(int missionQuality, Random random)
    {
        EligiblePool pool = GetPool(missionQuality);
        if (pool.Groups.Length == 0)
            throw new InvalidOperationException("RK chest loot coverage gap: missionQL=" + missionQuality
                + " itemQL=" + Math.Max(1, missionQuality - 5) + ".." + Math.Min(250, missionQuality + 5)
                + " approvedEntries=" + _allowlist.Count + " reason="
                + (_allowlist.UnavailableReason ?? "no-approved-identity-and-quality"));

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
        return (item.Qualities[random.Next(item.Qualities.Length)], selected.Rarity);
    }

    EligiblePool GetPool(int missionQuality)
    {
        if (missionQuality is < 1 or > 250)
            throw new ArgumentOutOfRangeException(nameof(missionQuality));
        return _eligible.GetOrAdd(missionQuality, BuildEligible);
    }

    HashInstance[] BuildFamilies(IReadOnlyCollection<HashInstance> source)
    {
        var result = new List<HashInstance>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var covered = new HashSet<int>();
        var knownPairs = new HashSet<(int LowId, int HighId)>();
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
            int[] ordered = ids.OrderBy(id => _catalog.Require(id).Quality).ToArray();
            for (int index = 0; index < ordered.Length - 1; index++)
                knownPairs.Add((ordered[index], ordered[index + 1]));
        }

        // Reviewed existing endpoint pairs can come from other exact GameData metadata (for example mission
        // reward pairs). Missing loot-hash leaves must not force a guessed family or an unrestricted fallback.
        foreach ((int lowId, int highId) in _allowlist.ApprovedPairs.OrderBy(pair => pair.LowId).ThenBy(pair => pair.HighId))
        {
            if (lowId == highId || knownPairs.Contains((lowId, highId))
                || !_catalog.TryGet(lowId, out ItemTemplate low) || !_catalog.TryGet(highId, out ItemTemplate high)
                || low.Quality < 1 || high.Quality <= low.Quality)
                continue;
            int[] ids = new[] { lowId, highId }.Order().ToArray();
            if (!seen.Add(string.Join(",", ids)))
                continue;
            result.Add(new HashInstance("rk-approved:" + lowId + ":" + highId, ids));
            covered.UnionWith(ids);
            knownPairs.Add((lowId, highId));
        }

        foreach (ItemTemplate template in _catalog.Templates.OrderBy(template => template.Id))
        {
            if (template.Id <= 0 || template.Quality < 1 || covered.Contains(template.Id)
                    && !_allowlist.IsAllowed(template.Id, template.Id, template.Quality))
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
        int approvedCandidates = 0;
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
                // A family link, Carry flag or absent expansion requirement is never acquisition evidence.
                // Authorize the exact pair and QL before invoking interpolation, including both endpoints.
                if (!_allowlist.IsAllowed(lowId, highId, quality))
                    continue;
                // Existing builder owns endpoint choice, all interpolation (including Rarity), and QL clamping.
                ItemTemplate definition = _items.CreateTemplate(lowId, highId, quality);
                if (!ApprovedDefinition(definition, lowId, highId, quality))
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

            if (qualities.Count != 0)
                approvedCandidates++;

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
        return new EligiblePool(eligible, eligible.Sum(group => group.Weight), approvedCandidates);
    }

    bool ApprovedDefinition(ItemTemplate definition, int lowId, int highId, int quality)
        => definition.Quality == quality && (definition.Id == lowId || definition.Id == highId)
            && CanCarry(definition) && _allowlist.IsAllowed(lowId, highId, quality);

    public static bool IsApproved(Item item, int missionQuality, MissionChestRKAllowlist allowlist)
        => missionQuality is >= 1 and <= 250
            && item.Quality >= Math.Max(1, missionQuality - 5) && item.Quality <= Math.Min(250, missionQuality + 5)
            && item.Definition.Quality == item.Quality
            && (item.Definition.Id == item.LowId || item.Definition.Id == item.HighId)
            && CanCarry(item.Definition) && allowlist.IsAllowed(item.LowId, item.HighId, item.Quality);

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
    sealed record EligiblePool(EligibleGroup[] Groups, double TotalWeight, int ApprovedCandidates);
}

public sealed record MissionChestLootPreview(int LowId, int HighId, int Quality, string Name, string Rarity,
    string[] Evidence, string Classification = MissionChestRKAllowlist.OrdinaryClassification);
public sealed record MissionChestLootDiagnostics(int ApprovedCandidates, int RejectedCandidates, int EligibleChoices);
