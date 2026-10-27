namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

using AORebirth.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SmokeLounge.AOtomation.Messaging.GameData;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Quests.Dungeons;

[TestClass]
public sealed class MissionChestLootTests
{
    [TestMethod]
    public void CountScalesWithFrozenRoomCountAndCanDisableVariation()
    {
        MissionChestPolicy policy = Policy();
        policy.CountVariationFraction = 0;
        foreach ((int rooms, int expected) in new[] { (0, 0), (1, 1), (2, 1), (10, 3), (20, 6), (30, 9), (40, 12), (50, 15) })
            Assert.AreEqual(expected, policy.RollChestCount(rooms, new Random(1)));
    }

    [TestMethod]
    public void ZeroDensityStillDisablesTheMinimumChestTarget()
    {
        MissionChestPolicy policy = Policy();
        policy.RoomDensity = 0;
        foreach (int rooms in new[] { 1, 2, 10 })
            Assert.AreEqual(0, policy.RollChestCount(rooms, new Random(1)));
    }

    [TestMethod]
    public void CountVariationIsSeededAndRemainsWithinConfiguredRange()
    {
        MissionChestPolicy policy = Policy();
        var first = new Random(7919);
        var second = new Random(7919);
        var counts = new HashSet<int>();
        for (int index = 0; index < 1000; index++)
        {
            int count = policy.RollChestCount(10, first);
            Assert.AreEqual(count, policy.RollChestCount(10, second));
            Assert.IsTrue(count is >= 2 and <= 4);
            counts.Add(count);
        }
        CollectionAssert.AreEquivalent(new[] { 2, 3, 4 }, counts.ToArray());
    }

    [TestMethod]
    public void InvalidCountsAndWeightsAreRejected()
    {
        MissionChestPolicy policy = Policy();
        policy.MaxItems = 4;
        Assert.IsFalse(policy.IsValid);
        Assert.ThrowsExactly<ArgumentException>(() => Service(new ChestCatalog().Add(1, 50), policy: policy));
        policy.MaxItems = 3;
        policy.RarityWeights["Missing"] = double.NaN;
        Assert.IsFalse(policy.IsValid);
        policy.RarityWeights["Missing"] = -1;
        Assert.IsFalse(policy.IsValid);
        policy.RarityWeights.Remove("Missing");
        Assert.IsFalse(policy.IsValid);
    }

    [TestMethod]
    public void EveryChestHasOneToThreeItemsWithFreshOriginalIdentities()
    {
        MissionChestLootService service = Service(new ChestCatalog().Add(12, 50));
        var random = new Random(314159);
        var sizes = new HashSet<int>();
        var instances = new HashSet<int>();
        for (int index = 0; index < 200; index++)
        {
            IReadOnlyList<Item> items = service.Roll(50, random);
            Assert.IsTrue(items.Count is >= 1 and <= 3);
            sizes.Add(items.Count);
            foreach (Item item in items)
            {
                Assert.IsTrue(instances.Add(item.InstanceId));
                Assert.AreEqual(12, item.LowId);
                Assert.AreEqual(12, item.HighId);
                Assert.AreEqual(50, item.Quality);
                Assert.AreEqual(ItemSource.Loot, item.Source);
                Assert.IsFalse(item.IsPersisted);
            }
        }
        CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, sizes.ToArray());
    }

    [TestMethod]
    public void VariableQualityUsesKnownFamilyAndRespectsBothWindowClamps()
    {
        var catalog = new ChestCatalog().Add(1, 1).Add(2, 300);
        MissionChestLootService service = Service(catalog, """{"FAMILY":{"Templates":[1,2]}}""");
        var random = new Random(17);
        foreach ((int mission, int min, int max) in new[] { (1, 1, 6), (10, 5, 15), (50, 45, 55), (200, 195, 205), (250, 245, 250) })
        {
            var qualities = new HashSet<int>();
            for (int index = 0; index < 300; index++)
            {
                Item item = service.RollItem(mission, random);
                Assert.AreEqual(1, item.LowId);
                Assert.AreEqual(2, item.HighId);
                Assert.IsTrue(item.Quality >= min && item.Quality <= max);
                qualities.Add(item.Quality);
            }
            Assert.AreEqual(min, qualities.Min());
            Assert.AreEqual(max, qualities.Max());
        }
    }

    [TestMethod]
    public void UnknownFamilyLinksUseOnlyStoredQualityWithoutNamePairing()
    {
        // Equal names cannot establish a low/high relationship absent from ItemTemplates.json.
        var catalog = new ChestCatalog().Add(1, 1, name: "Same name").Add(2, 200, name: "Same name");
        MissionChestLootService service = Service(catalog);
        Assert.AreEqual(1, service.RollItem(1, new Random(1)).Quality);
        Assert.AreEqual(200, service.RollItem(200, new Random(1)).Quality);
        Assert.ThrowsExactly<InvalidOperationException>(() => service.RollItem(50, new Random(1)));
    }

    [TestMethod]
    public void FixedQualityMustIntersectMissionWindowBeforeRaritySelection()
    {
        var catalog = new ChestCatalog().Add(1, 1).Add(2, 250, rarity: 5).Add(3, 50, rarity: 3);
        MissionChestLootService service = Service(catalog);
        for (int index = 0; index < 100; index++)
            Assert.AreEqual(3, service.RollItem(50, new Random(index)).LowId);
        Assert.AreEqual(2, service.RollItem(250, new Random(2)).LowId);
    }

    [TestMethod]
    public void DatabaseCarryRuleRejectsNoncarryButDoesNotExcludeNoDropOrUnique()
    {
        var catalog = new ChestCatalog().Add(1, 50, carry: false).Add(2, 50,
            flags: (int)(ItemFlags.NoDrop | ItemFlags.Unique));
        MissionChestLootService service = Service(catalog);
        for (int index = 0; index < 100; index++)
        {
            Item item = service.RollItem(50, new Random(index));
            Assert.AreEqual(2, item.LowId);
            Assert.AreEqual((int)(ItemFlags.NoDrop | ItemFlags.Unique), item.Flags);
        }
    }

    [TestMethod]
    public void MissingRarityRemainsEligibleAndEmptyGroupsRenormalize()
    {
        MissionChestPolicy policy = Policy();
        var service = Service(new ChestCatalog().Add(1, 50), policy: policy);
        for (int index = 0; index < 100; index++)
        {
            Item item = service.RollItem(50, new Random(index));
            Assert.AreEqual(1, item.LowId);
            Assert.IsFalse(item.Definition.Stats.ContainsKey(CharacterStat.Rarity));
        }
    }

    [TestMethod]
    public void RarityGroupFrequencyMatchesConfiguredWeightIndependentOfPopulation()
    {
        var catalog = new ChestCatalog();
        for (int index = 1; index <= 100; index++)
            catalog.Add(index, 50);
        for (int rarity = 1; rarity <= 5; rarity++)
            catalog.Add(100 + rarity, 50, rarity);
        MissionChestLootService service = Service(catalog);
        const int samples = 40000;
        var observed = Policy().RarityWeights.Keys.ToDictionary(key => key, _ => 0);
        var random = new Random(15485863);
        for (int index = 0; index < samples; index++)
        {
            Item item = service.RollItem(50, random);
            string key = item.Definition.Stats.TryGetValue(CharacterStat.Rarity, out int value)
                ? value.ToString(CultureInfo.InvariantCulture) : "Missing";
            observed[key]++;
        }
        foreach ((string rarity, double weight) in Policy().RarityWeights)
        {
            double probability = weight / 100;
            double tolerance = 6 * Math.Sqrt(samples * probability * (1 - probability)) + 1;
            Assert.IsTrue(Math.Abs(observed[rarity] - samples * probability) <= tolerance,
                "Group " + rarity + " observed=" + observed[rarity] + " expected=" + samples * probability);
        }
    }

    [TestMethod]
    public void EligibleItemsAreUniformWithinSelectedGroup()
    {
        MissionChestLootService service = Service(new ChestCatalog().Add(1, 50).Add(2, 50).Add(3, 200));
        var random = new Random(104729);
        int first = 0;
        for (int index = 0; index < 10000; index++)
        {
            Item item = service.RollItem(50, random);
            Assert.IsTrue(item.LowId is 1 or 2);
            if (item.LowId == 1)
                first++;
        }
        Assert.IsTrue(Math.Abs(first - 5000) < 300);
    }

    [TestMethod]
    public void ExactFamilyAliasesDoNotIncreaseItemSelectionProbability()
    {
        var catalog = new ChestCatalog().Add(1, 1).Add(2, 100).Add(3, 50);
        MissionChestLootService service = Service(catalog,
            """{"A":{"Templates":[1,2]},"ALIAS":{"Templates":[2,1]},"FIXED":{"Templates":[3]}}""");
        var random = new Random(2003);
        int variable = 0;
        for (int index = 0; index < 10000; index++)
        {
            if (service.RollItem(50, random).LowId == 1)
                variable++;
        }
        Assert.IsTrue(Math.Abs(variable - 5000) < 300);
    }

    [TestMethod]
    public void OverlappingFamiliesWithIdenticalEligibleChoicesReceiveOneTicket()
    {
        var catalog = new ChestCatalog().Add(1, 1).Add(2, 100).Add(3, 200).Add(4, 50);
        MissionChestLootService service = Service(catalog,
            """{"SHORT":{"Templates":[1,2]},"LONG":{"Templates":[1,2,3]}}""");
        var random = new Random(3001);
        int variable = 0;
        for (int index = 0; index < 10000; index++)
            if (service.RollItem(50, random).LowId == 1)
                variable++;
        // Both families produce exactly the same pair/qualities at 45..55; the unlinked QL50 item is the other ticket.
        Assert.IsTrue(Math.Abs(variable - 5000) < 300, "Identical eligible items were counted more than once.");
    }

    [TestMethod]
    public void OverlappingFamiliesKeepDifferentEligibleIdentityAndQualityChoices()
    {
        var catalog = new ChestCatalog().Add(1, 1).Add(2, 50).Add(3, 100).Add(4, 50);
        MissionChestLootService service = Service(catalog,
            """{"SHORT":{"Templates":[1,2]},"LONG":{"Templates":[1,2,3]}}""");
        var random = new Random(4001);
        int fixedItems = 0;
        bool sawUpperBand = false;
        for (int index = 0; index < 12000; index++)
        {
            Item item = service.RollItem(50, random);
            if (item.LowId == 4)
                fixedItems++;
            if (item.LowId == 2 && item.HighId == 3 && item.Quality > 50)
                sawUpperBand = true;
        }
        // Short is 45..50, Long also supports 51..55 via another original pair, and fixed is the third ticket.
        Assert.IsTrue(Math.Abs(fixedItems - 4000) < 330, "Different eligible choice arrays were collapsed.");
        Assert.IsTrue(sawUpperBand);
    }

    [TestMethod]
    public void SameQualityVariantsRemainDistinctOriginalTemplates()
    {
        var catalog = new ChestCatalog().Add(1, 50).Add(2, 50);
        MissionChestLootService service = Service(catalog, """{"VARIANTS":{"Templates":[1,2]}}""");
        var ids = new HashSet<int>();
        var random = new Random(1063);
        for (int index = 0; index < 100; index++)
        {
            Item item = service.RollItem(50, random);
            Assert.AreEqual(item.LowId, item.HighId);
            ids.Add(item.LowId);
        }
        CollectionAssert.AreEquivalent(new[] { 1, 2 }, ids.ToArray());
    }

    [TestMethod]
    public void MissingEndpointRarityUsesExistingBuilderDefinition()
    {
        var catalog = new ChestCatalog().Add(1, 1).Add(2, 10, rarity: 5);
        MissionChestPolicy policy = Policy();
        foreach (string key in policy.RarityWeights.Keys.ToArray())
            policy.RarityWeights[key] = key == "Missing" ? 1 : 0;
        MissionChestLootService service = Service(catalog, """{"A":{"Templates":[1,2]}}""", policy);
        for (int index = 0; index < 50; index++)
        {
            Item item = service.RollItem(1, new Random(index));
            Assert.AreEqual(1, item.Quality);
            Assert.IsFalse(item.Definition.Stats.ContainsKey(CharacterStat.Rarity));
        }
    }

    [TestMethod]
    public void UnknownRarityAndMissingFamilyTemplatesDoNotUseFallbackDefinitions()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => Service(new ChestCatalog().Add(1, 50, rarity: 6))
            .RollItem(50, new Random(1)));
        var service = Service(new ChestCatalog().Add(1, 1), """{"INCOMPLETE":{"Templates":[1,999]}}""");
        Assert.ThrowsExactly<InvalidOperationException>(() => service.RollItem(50, new Random(1)));
        Assert.AreEqual(1, service.RollItem(1, new Random(1)).LowId);
    }

    [TestMethod]
    public void MissingRarityAtEitherEndpointNeverInventsZero()
    {
        foreach (bool lowMissing in new[] { true, false })
        {
            var catalog = new ChestCatalog().Add(1, 1, lowMissing ? null : 5).Add(2, 10, lowMissing ? 5 : null);
            MissionChestLootService service = Service(catalog, """{"A":{"Templates":[1,2]}}""");
            var random = new Random(123);
            for (int index = 0; index < 100; index++)
            {
                Item item = service.RollItem(5, random);
                if (item.Definition.Stats.TryGetValue(CharacterStat.Rarity, out int rarity))
                    Assert.AreEqual(5, rarity);
                else
                    Assert.AreEqual(lowMissing ? 1 : 10, item.Quality);
            }
        }
    }

    [TestMethod]
    public void FrozenPolicyOwnsIndependentWeightsAppearanceStatsAndPlacement()
    {
        MissionChestPolicy policy = Policy();
        policy.SpawnDefinition = new ChestSpawnDefinition
        {
            StateMachineType = 1,
            Stats = new[] { CharacterStat.Flags, CharacterStat.StaticInstance, CharacterStat.ACGItemLevel,
                CharacterStat.ACGItemTemplateID, CharacterStat.ACGItemTemplateID2, CharacterStat.MultipleCount }
                .Select(stat => new ChestSpawnStat { StatId = (int)stat, Value = 1 }).ToArray()
        };
        policy.Placement = new MissionChestPlacementPolicy { CandidateAttemptsPerRoom = 10 };
        MissionChestPolicy frozen = policy.Clone();
        policy.RarityWeights["Missing"] = 1;
        policy.SpawnDefinition.Stats[0].Value = 99;
        policy.Placement.CandidateAttemptsPerRoom = 20;
        Assert.AreEqual(70d, frozen.RarityWeights["Missing"]);
        Assert.AreEqual(1u, frozen.SpawnDefinition!.Stats[0].Value);
        Assert.AreEqual(10, frozen.Placement!.CandidateAttemptsPerRoom);
        Assert.AreNotSame(policy.SpawnDefinition, frozen.SpawnDefinition);
    }

    static MissionChestPolicy Policy() => new()
    {
        RoomDensity = 0.3, CountVariationFraction = 0.2, MinItems = 1, MaxItems = 3,
        RarityWeights = new Dictionary<string, double>
        {
            ["Missing"] = 70, ["1"] = 20, ["2"] = 6, ["3"] = 2.5, ["4"] = 1, ["5"] = 0.5
        }
    };

    static MissionChestLootService Service(ChestCatalog catalog, string json = "{}", MissionChestPolicy? policy = null)
    {
        // These are synthetic item mechanics fixtures, not a review of production loot eligibility.
        // Explicitly approve their existing identities so the prior rarity/interpolation checks stay meaningful.
        var data = new StubGameData(HashItemCatalog.Parse(json));
        var entries = new List<MissionChestRKAllowlist.Entry>();
        var covered = new HashSet<int>();
        foreach (HashInstance family in data.ItemFamilies)
        {
            int[] ids = family.TemplateIds.Distinct().ToArray();
            if (ids.Length < 2 || ids.Any(id => !catalog.TryGet(id, out _))
                || ids.Select(id => catalog.Require(id).Quality).Distinct().Count() != ids.Length)
                continue;
            foreach (int quality in Enumerable.Range(1, 250))
            {
                if (quality < ids.Min(id => catalog.Require(id).Quality) || quality > ids.Max(id => catalog.Require(id).Quality))
                    continue;
                HashItemCatalog.TrySelectIds(family, quality, id => catalog.Require(id).Quality, out int low, out int high);
                entries.Add(Entry(low, high, quality));
            }
            covered.UnionWith(ids);
        }
        entries.AddRange(catalog.Templates.Where(template => !covered.Contains(template.Id) && template.Quality is >= 1 and <= 250)
            .Select(template => Entry(template.Id, template.Id, template.Quality)));
        MissionChestRKAllowlist allowlist = MissionChestRKAllowlist.Parse(JsonSerializer.Serialize(
            new MissionChestRKAllowlist.Document { Version = 1, Entries = entries.ToArray() }));
        return new MissionChestLootService(catalog, data, new ItemBuilder(catalog, new ChestIds(), new StubLogger()),
            policy ?? Policy(), allowlist);

        static MissionChestRKAllowlist.Entry Entry(int low, int high, int quality) => new()
        {
            LowId = low, HighId = high, MinQuality = quality, MaxQuality = quality,
            Evidence = ["https://example.test/synthetic-chest-fixture"],
            Classification = MissionChestRKAllowlist.OrdinaryClassification
        };
    }

    sealed class ChestIds : IItemInstanceIdAllocator
    {
        int _next;
        public int Allocate() => ++_next;
    }

    sealed class ChestCatalog : IItemTemplateCatalog
    {
        readonly Dictionary<int, ItemTemplate> _templates = new();
        public IReadOnlyCollection<ItemTemplate> Templates => _templates.Values;

        public ChestCatalog Add(int id, int quality, int? rarity = null, bool carry = true, int flags = 0, string? name = null)
        {
            var stats = new Dictionary<CharacterStat, int> { [CharacterStat.Can] = carry ? (int)CanFlags.Carry : 0 };
            if (rarity is int value)
                stats[CharacterStat.Rarity] = value;
            _templates.Add(id, new ItemTemplate
            {
                Id = id, Quality = quality, Name = name ?? "Item " + id, DynelType = 51005, Flags = flags, Stats = stats
            });
            return this;
        }

        public bool TryGet(int aoid, out ItemTemplate template) => _templates.TryGetValue(aoid, out template!);
        public ItemTemplate Require(int aoid) => _templates[aoid];
    }
}
