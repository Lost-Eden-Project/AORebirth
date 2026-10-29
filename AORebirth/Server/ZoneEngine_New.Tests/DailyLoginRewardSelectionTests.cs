namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AORebirth.Enums;
using SmokeLounge.AOtomation.Messaging.GameData;
using ZoneEngine_New.Core.DailyLogin;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.GameData;

[TestClass]
public sealed class DailyLoginRewardSelectionTests
{
    static readonly Lazy<CurrentCatalog> Current = new(LoadCurrentCatalog);
    [TestMethod]
    public void RandomPoolIgnoresMissingTemplatesAndPrefersTheLowEndpoint()
    {
        var templates = new Catalog().Add(1, 1, [1, 2]).Add(2, 300, [1, 2]).Add(3, 1);
        var selector = Selector(templates,
            """{"days":{"1":{"amount":1,"qualityMode":"characterLevel","randomItemIds":[999,2,3,1]}}}""");
        DailyLoginRewardSelection result = Select(selector, 1, 140);
        Assert.AreEqual(1, result.LowId);
        Assert.AreEqual(2, result.HighId);
        Assert.AreEqual(140, result.Quality);
        Assert.AreEqual(1, Select(selector, 1, 0).Quality);
        Assert.AreEqual(300, Select(selector, 1, 500).Quality);
    }

    [TestMethod]
    public void ExplicitFamilyMetadataOrdersEndpointsByTheirActualQualities()
    {
        var templates = new Catalog().Add(10, 200).Add(11, 1);
        var selector = Selector(templates,
            """{"qualityFamilies":{"actual":{"Templates":[10,11]}},"days":{"1":{"amount":1,"qualityMode":"characterLevel","randomItemIds":[10,11]}}}""");
        DailyLoginRewardSelection result = Select(selector, 1, 80, random: new BoundaryRandom(false));
        Assert.AreEqual(11, result.LowId);
        Assert.AreEqual(10, result.HighId);
        Assert.AreEqual(80, result.Quality);
    }

    [TestMethod]
    public void UnpairedTemplatesKeepTheOriginalFixedTemplateFallbackWithoutInventingAnAdjacentPair()
    {
        var templates = new Catalog().Add(10, 1).Add(11, 200);
        var selector = Selector(templates,
            """{"days":{"1":{"amount":1,"qualityMode":"characterLevel","randomItemIds":[10,11]}}}""");
        DailyLoginRewardSelection result = Select(selector, 1, 80, random: new BoundaryRandom(false));
        Assert.AreEqual(10, result.LowId);
        Assert.AreEqual(10, result.HighId);
        Assert.AreEqual(1, result.Quality);
    }

    [TestMethod]
    public void SegmentedExplicitFamiliesFollowSharedIdentitiesAndSelectTheContainingBand()
    {
        var templates = new Catalog().Add(10, 1).Add(20, 50).Add(30, 100);
        var selector = Selector(templates,
            """{"qualityFamilies":{"first":{"Templates":[10,20]},"second":{"Templates":[20,30]}},"days":{"1":{"itemId":10,"amount":1,"qualityMode":"characterLevel"}}}""");
        DailyLoginRewardSelection result = Select(selector, 1, 80);
        Assert.AreEqual(20, result.LowId);
        Assert.AreEqual(30, result.HighId);
        Assert.AreEqual(80, result.Quality);
    }

    [TestMethod]
    public void SingleRelationCanResolveToTheRelatedTemplatesCompleteQualityFamily()
    {
        var templates = new Catalog().Add(10, 200, [20]).Add(20, 1, [20, 30]).Add(30, 200, [20, 30]);
        var selector = Selector(templates,
            """{"days":{"1":{"itemId":10,"amount":1,"qualityMode":"characterLevel"}}}""");
        DailyLoginRewardSelection result = Select(selector, 1, 80);
        Assert.AreEqual(20, result.LowId);
        Assert.AreEqual(30, result.HighId);
        Assert.AreEqual(80, result.Quality);
    }

    [TestMethod]
    public void FixedRewardKeepsTheSelectedIdentityInsteadOfInterpolatingItsRelations()
    {
        var templates = new Catalog().Add(10, 1, [10, 11]).Add(11, 200, [10, 11]);
        var selector = Selector(templates,
            """{"days":{"1":{"itemId":11,"amount":1,"quality":200,"qualityMode":"fixed"}}}""");
        DailyLoginRewardSelection result = Select(selector, 1, 80);
        Assert.AreEqual(11, result.LowId);
        Assert.AreEqual(11, result.HighId);
        Assert.AreEqual(200, result.Quality);
    }

    [TestMethod]
    public void CharacterLevelVariationIncludesBothBoundsAndClampsToTheTemplateBand()
    {
        var templates = new Catalog().Add(10, 1, [10, 20]).Add(20, 100, [10, 20]);
        var selector = Selector(templates,
            """{"days":{"1":{"itemId":10,"amount":1,"qualityMode":"characterLevelPlusMinus","qualityDelta":10}}}""");
        Assert.AreEqual(40, Select(selector, 1, 50, random: new BoundaryRandom(false)).Quality);
        Assert.AreEqual(60, Select(selector, 1, 50, random: new BoundaryRandom(true)).Quality);
        Assert.AreEqual(1, Select(selector, 1, 1, random: new BoundaryRandom(false)).Quality);
        Assert.AreEqual(100, Select(selector, 1, 100, random: new BoundaryRandom(true)).Quality);
    }

    [TestMethod]
    public void ProfessionNanoUsesOnlyItsOwnPoolAndKeepsTheCrystalFixedQuality()
    {
        var templates = new Catalog().Add(10, 40, [10, 20]).Add(20, 100, [10, 20]).Add(30, 50);
        var selector = Selector(templates,
            """{"days":{"10":{"amount":1,"qualityMode":"professionVendorNano","qualityDelta":10}}}""",
            """{"professions":{"1":[{"itemId":10,"quality":0},{"itemId":20,"quality":100},{"itemId":999,"quality":50}],"2":[{"itemId":30,"quality":50}]}}""");
        DailyLoginRewardSelection result = Select(selector, 10, 50, 1);
        Assert.AreEqual(10, result.LowId);
        Assert.AreEqual(10, result.HighId);
        Assert.AreEqual(40, result.Quality);
        Assert.AreEqual(30, Select(selector, 10, 50, 2).LowId);
        Assert.IsFalse(selector.TrySelect(10, 50, 3, new Random(1), out _, out string error));
        StringAssert.Contains(error, "profession");
    }

    [TestMethod]
    public void ProfessionNanoFallsBackToTheNearestQualityBandWithTiesEligible()
    {
        var templates = new Catalog().Add(10, 20).Add(20, 80).Add(30, 200);
        var selector = Selector(templates,
            """{"days":{"10":{"amount":1,"qualityMode":"professionVendorNano","qualityDelta":10}}}""",
            """{"professions":{"1":[{"itemId":10,"quality":20},{"itemId":20,"quality":80},{"itemId":30,"quality":200}]}}""");
        Assert.AreEqual(10, Select(selector, 10, 50, 1, new BoundaryRandom(false)).LowId);
        Assert.AreEqual(20, Select(selector, 10, 50, 1, new BoundaryRandom(true)).LowId);
    }

    [TestMethod]
    public void CatalogQuantitiesProduceSingleStacksOrSeparateItemsWithoutClientAmounts()
    {
        var templates = new Catalog().Add(1, 1, can: (int)CanFlags.Stackable)
            .Add(2, 1, mintCount: 50000).Add(3, 1);
        var selector = Selector(templates,
            """{"days":{"1":{"itemId":1,"amount":25},"2":{"itemId":2,"amount":50000},"3":{"itemId":3,"amount":2}}}""");
        DailyLoginRewardSelection stack = Select(selector, 1, 1);
        Assert.AreEqual(25, stack.Amount);
        Assert.AreEqual(1, stack.ItemCount);
        Assert.AreEqual(25, stack.StackCount);
        DailyLoginRewardSelection ammo = Select(selector, 2, 1);
        Assert.AreEqual(1, ammo.ItemCount);
        Assert.AreEqual(50000, ammo.StackCount);
        DailyLoginRewardSelection separate = Select(selector, 3, 1);
        Assert.AreEqual(2, separate.ItemCount);
        Assert.AreEqual(1, separate.StackCount);
    }

    [TestMethod]
    public void ReservedDaysStayUnavailableAndCannotSynthesizeAnItem()
    {
        var selector = Selector(new Catalog(),
            """{"days":{"19":{"itemId":0,"amount":1},"21":{"itemId":0,"amount":1}}}""");
        foreach (int day in new[] { 19, 21 })
        {
            Assert.IsFalse(selector.TrySelect(day, 50, 1, new Random(1), out var selection, out string error));
            Assert.IsNull(selection);
            StringAssert.Contains(error, "no reward configured");
        }
    }

    [TestMethod]
    public void MissingRandomPoolFallsBackOnlyToTheServerConfiguredSeed()
    {
        var selector = Selector(new Catalog().Add(1, 1),
            """{"days":{"1":{"itemId":1,"amount":1,"randomItemIds":[999]},"2":{"itemId":998,"amount":1}}}""");
        Assert.AreEqual(1, Select(selector, 1, 1).LowId);
        Assert.IsFalse(selector.TrySelect(2, 1, 1, new Random(1), out _, out string error));
        StringAssert.Contains(error, "missing");
    }

    [TestMethod]
    public void MalformedRewardDefinitionsAreRejected()
    {
        foreach (string json in new[]
        {
            """{"days":{}}""",
            """{"days":{"1":{"itemId":1,"amount":0}}}""",
            """{"days":{"1":{"itemId":1,"qualityMode":"unknown"}}}""",
            """{"days":{"1":{"itemId":-1}}}"""
        })
            Assert.ThrowsExactly<InvalidDataException>(() => DailyLoginRewardCatalog.Parse(json));
    }

    [TestMethod]
    public void RecoveredPublicFamiliesScaleActualCurrentRechargersAndStimsInsteadOfClampingToQualityOne()
    {
        CurrentCatalog current = Current.Value;
        var selector = new DailyLoginRewardSelector(current.Templates, current.Rewards, current.Data);
        var builder = new ItemBuilder(current.Templates, new StubLogger());
        foreach (int day in new[] { 3, 4, 17, 26 })
        {
            foreach (int level in new[] { 1, 100, 123, 220 })
            {
                DailyLoginRewardSelection selected = Select(selector, day, level);
                Assert.AreEqual(level, selected.Quality, "day=" + day + " level=" + level);
                Assert.AreEqual(level, builder.CreateTemplate(selected.LowId, selected.HighId, selected.Quality).Quality);
                Assert.IsTrue(current.Templates.TryGet(selected.LowId, out _));
                Assert.IsTrue(current.Templates.TryGet(selected.HighId, out _));
            }
        }
    }

    [TestMethod]
    public void ActualCurrentCatalogSupportsEveryConfiguredCalendarRewardAndItsExactQuantity()
    {
        CurrentCatalog current = Current.Value;
        var selector = new DailyLoginRewardSelector(current.Templates, current.Rewards, current.Data);
        foreach ((int day, DailyLoginDayReward reward) in current.Rewards.Days.OrderBy(pair => pair.Key))
        {
            bool selected = selector.TrySelect(day, 123, 1, new Random(17), out var result, out string error);
            if (day is 19 or 21)
            {
                Assert.IsFalse(selected, "Reserved day " + day + " must remain empty.");
                continue;
            }
            Assert.IsTrue(selected, "day=" + day + " " + error);
            Assert.IsNotNull(result);
            Assert.AreEqual(reward.Amount, result.Amount, "day=" + day);
            Assert.AreEqual(1, result.ItemCount, "The configured calendar grants one item or charged stack per day; day=" + day);
            Assert.AreEqual(reward.Amount, result.StackCount, "Configured stack/charge quantity; day=" + day);
        }
        foreach ((int day, int count) in new[] { (2, 50000), (3, 50), (17, 50), (4, 25), (26, 25), (9, 25), (23, 25) })
        {
            DailyLoginRewardSelection selected = Select(selector, day, 123);
            Assert.AreEqual(1, selected.ItemCount);
            Assert.AreEqual(count, selected.StackCount, "day=" + day);
        }
    }

    [TestMethod]
    public void ActualProfessionNanoCatalogUsesPresentFixedCrystalsWithinWindowOrNearestQuality()
    {
        CurrentCatalog current = Current.Value;
        var selector = new DailyLoginRewardSelector(current.Templates, current.Rewards, current.Data);
        Assert.IsTrue(current.Rewards.TryGet(10, out DailyLoginDayReward reward));
        int delta = reward.QualityDelta > 0 ? reward.QualityDelta : 10;
        foreach (int profession in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 14, 15 })
        {
            Assert.IsTrue(current.Rewards.TryGetProfessionNanos(profession, out var pool));
            var usable = pool.Where(entry => current.Templates.TryGet(entry.ItemId, out _))
                .Select(entry => (entry.ItemId, Quality: entry.Quality > 0 ? entry.Quality : current.Templates.Require(entry.ItemId).Quality)).ToArray();
            Assert.IsTrue(usable.Length > 0, "profession=" + profession);
            foreach (int level in new[] { 1, 123, 220 })
            {
                DailyLoginRewardSelection selected = Select(selector, 10, level, profession);
                Assert.AreEqual(selected.LowId, selected.HighId, "Profession crystals remain fixed templates.");
                Assert.AreEqual(current.Templates.Require(selected.LowId).Quality, selected.Quality);
                int Distance(int quality) => quality < Math.Max(1, level - delta) ? Math.Max(1, level - delta) - quality
                    : quality > level + delta ? quality - level - delta : 0;
                int nearest = usable.Min(entry => Distance(entry.Quality));
                Assert.IsTrue(usable.Any(entry => entry.ItemId == selected.LowId && Distance(entry.Quality) == nearest),
                    "profession=" + profession + " level=" + level);
            }
        }
    }

    static CurrentCatalog LoadCurrentCatalog()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "AI_START_HERE.md")))
            root = Directory.GetParent(root)?.FullName;
        Assert.IsNotNull(root);
        string dataRoot = Path.Combine(root, "GameData");
        if (!File.Exists(Path.Combine(dataRoot, "items.dat")))
            Assert.Inconclusive("This current-catalog check requires the selected private items.dat.");
        var data = new StubGameData(HashItemCatalog.Parse(File.ReadAllText(Path.Combine(dataRoot, "ItemTemplates.json"))), rootPath: dataRoot);
        var templates = new ItemTemplateCatalog(data, new StubLogger());
        DailyLoginRewardCatalog rewards = DailyLoginRewardCatalog.Load(
            Path.Combine(root, "tools-temp", "icc-rk-local-web", "daily", "rewards.json"),
            Path.Combine(root, "AORebirth", "Server", "ZoneEngine_New", "Content", "Daily", "day10-profession-nanos.json"));
        return new CurrentCatalog(templates, rewards, data);
    }

    sealed record CurrentCatalog(ItemTemplateCatalog Templates, DailyLoginRewardCatalog Rewards, StubGameData Data);

    static DailyLoginRewardSelector Selector(Catalog templates, string rewards, string? nanos = null)
        => new(templates, DailyLoginRewardCatalog.Parse(rewards, nanos));

    static DailyLoginRewardSelection Select(DailyLoginRewardSelector selector, int day, int level,
        int profession = 1, Random? random = null)
    {
        Assert.IsTrue(selector.TrySelect(day, level, profession, random ?? new Random(1), out var selection, out string error), error);
        Assert.IsNotNull(selection);
        return selection;
    }

    sealed class BoundaryRandom(bool upper) : Random
    {
        public override int Next(int maxValue) => upper ? maxValue - 1 : 0;
        public override int Next(int minValue, int maxValue) => upper ? maxValue - 1 : minValue;
    }

    sealed class Catalog : IItemTemplateCatalog
    {
        readonly Dictionary<int, ItemTemplate> _items = new();

        public Catalog Add(int id, int quality, int[]? relations = null, int can = 0, int mintCount = 0)
        {
            _items[id] = new ItemTemplate
            {
                Id = id,
                Quality = quality,
                Name = "Item " + id,
                Relations = relations is null ? new() : new(relations),
                Stats = new() { [CharacterStat.Can] = can, [CharacterStat.MaxEnergy] = mintCount }
            };
            return this;
        }

        public bool TryGet(int aoid, out ItemTemplate template) => _items.TryGetValue(aoid, out template!);
        public ItemTemplate Require(int aoid) => _items[aoid];
    }
}
