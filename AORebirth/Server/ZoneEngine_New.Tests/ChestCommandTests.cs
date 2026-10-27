namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.Linq;

using AORebirth.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

using ZoneEngine_New.Core.Commands;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Quests.Dungeons;

[TestClass]
public sealed class ChestCommandTests
{
    [TestMethod]
    public void RollParsingUsesNpcStyleInvariantIntegerBoundsAndRejectsExtraArguments()
    {
        Assert.IsTrue(ChestCommand.TryParseRolls(["roll", "1"], out int one));
        Assert.AreEqual(1, one);
        Assert.IsTrue(ChestCommand.TryParseRolls(["ROLL", "10000"], out int maximum));
        Assert.AreEqual(10000, maximum);
        Assert.IsTrue(ChestCommand.TryParseRolls(["roll", "+2"], out int signed));
        Assert.AreEqual(2, signed);
        foreach (string[] args in new[]
        {
            Array.Empty<string>(), new[] { "roll" }, new[] { "other", "1" },
            new[] { "roll", "0" }, new[] { "roll", "-1" }, new[] { "roll", "10001" },
            new[] { "roll", "2147483648" }, new[] { "roll", "1.5" }, new[] { "roll", "1", "extra" }
        })
            Assert.IsFalse(ChestCommand.TryParseRolls(args, out _), string.Join(" ", args));
    }

    [TestMethod]
    public void MissionQualityTakesPrecedenceAndFallbackIsExplicitAndClamped()
    {
        var mission = ChestCommand.ResolveQuality(172, 33);
        Assert.AreEqual(172, mission.Quality);
        Assert.IsTrue(mission.Source.Contains("accepted generated mission", StringComparison.Ordinal));
        var outside = ChestCommand.ResolveQuality(null, 33);
        Assert.AreEqual(33, outside.Quality);
        Assert.IsTrue(outside.Source.Contains("fallback", StringComparison.Ordinal));
        Assert.AreEqual(1, ChestCommand.ResolveQuality(0, 0).Quality);
        Assert.AreEqual(250, ChestCommand.ResolveQuality(null, 999).Quality);
    }

    [TestMethod]
    public void NonGmCannotRunCommandOrConstructTheLootSelector()
    {
        int constructed = 0;
        var command = new ChestCommand(() =>
        {
            constructed++;
            throw new InvalidOperationException("A denied command must not construct a selector.");
        });
        Player player = TestWorld.CreatePlayer(1);
        player.Stats.Set(CharacterStat.GmLevel, 0, StatDetail.Base);
        var session = new RecordingZoneSession();
        new GmCommandDispatcher([command]).TryExecute(session, player, "chest", ["roll", "1"]);
        Assert.AreEqual(1, command.RequiredGmLevel);
        Assert.AreEqual(0, constructed);
        Assert.IsTrue(session.Sent.OfType<ChatTextMessage>().Any(message => message.Text.Contains("Insufficient GM level", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void InvalidArgumentsDoNotConstructSelectorOrAlterInventory()
    {
        int constructed = 0;
        var command = new ChestCommand(() =>
        {
            constructed++;
            throw new InvalidOperationException("Invalid arguments must not construct a selector.");
        });
        Player player = TestWorld.CreatePlayer(1);
        int before = player.Inventory.Inventory.Content.Count;
        var session = new RecordingZoneSession();
        command.Execute(new GmCommandContext(session, player, ["roll", "10001"], command));
        Assert.AreEqual(0, constructed);
        Assert.AreEqual(before, player.Inventory.Inventory.Content.Count);
        Assert.IsTrue(session.Sent.OfType<ChatTextMessage>().Any(message => message.Text.Contains("Usage:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ProductionPreviewReportsApprovedLootEvidenceWithoutAllocatingOrAwardingItems()
    {
        var ids = new CountingIds();
        MissionChestLootService selector = Selector(ids);
        var command = new ChestCommand(() => selector);
        Player player = TestWorld.CreatePlayer(1);
        player.Stats.Set(CharacterStat.GmLevel, 1, StatDetail.Base);
        player.Stats.Set(CharacterStat.Level, 50, StatDetail.Base);
        int before = player.Inventory.Inventory.Content.Count;
        var session = new RecordingZoneSession();
        new GmCommandDispatcher([command]).TryExecute(session, player, "chest", ["roll", "20"]);
        string text = string.Join("\n", session.Sent.OfType<ChatTextMessage>().Select(message => message.Text));
        Assert.IsTrue(text.Contains("Chest populations: 20", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("total generated items:", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("ID 701/701; QL 50; rarity Missing", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("https://example.test/chest-source", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("classification OrdinaryRubiKa", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("rejected candidates 1", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("player-level fallback", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("Unapproved synthetic item", StringComparison.Ordinal));
        Assert.AreEqual(0, ids.Allocations);
        Assert.AreEqual(before, player.Inventory.Inventory.Content.Count);
        Assert.IsTrue(session.Sent.All(message => message is ChatTextMessage));
    }

    [TestMethod]
    public void MissingApprovedPoolReportsCoverageGapAndNeverFallsBackToGeneralItems()
    {
        var ids = new CountingIds();
        MissionChestLootService selector = Selector(ids, empty: true);
        var command = new ChestCommand(() => selector);
        Player player = TestWorld.CreatePlayer(1);
        player.Stats.Set(CharacterStat.GmLevel, 1, StatDetail.Base);
        player.Stats.Set(CharacterStat.Level, 50, StatDetail.Base);
        var session = new RecordingZoneSession();
        new GmCommandDispatcher([command]).TryExecute(session, player, "chest", ["roll", "1"]);
        string text = string.Join("\n", session.Sent.OfType<ChatTextMessage>().Select(message => message.Text));
        Assert.IsTrue(text.Contains("coverage gap", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("missionQL=50", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("ID 701/701", StringComparison.Ordinal));
        Assert.AreEqual(0, ids.Allocations);
        Assert.AreEqual(0, player.Inventory.Inventory.Content.Count);
    }

    [TestMethod]
    public void ResultDetailsAreBoundedAndSanitizedWhileTotalsRemainComplete()
    {
        string hostile = "<a href=\"unsafe\">" + new string('x', 5000);
        var results = Enumerable.Range(1, 130).Select(id =>
            (new MissionChestLootPreview(id, id, 50, hostile, "Missing", [hostile, hostile, hostile, hostile]), 1)).ToArray();
        IReadOnlyList<string> lines = ChestCommand.FormatResults(50, "accepted generated mission QL", 130, 130,
            new MissionChestLootDiagnostics(130, 77, 130), results);
        string text = string.Join("\n", lines);
        Assert.IsTrue(text.Contains("total generated items: 130", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("Showing 100 of 130", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("additional references", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("<a href=\"unsafe\">", StringComparison.Ordinal));
        Assert.IsTrue(lines.Count <= 32);
        Assert.IsTrue(lines.All(line => line.Length <= GetStatsAomlBuilder.DefaultMaxBodyLength + 128));
    }

    static MissionChestLootService Selector(CountingIds ids, bool empty = false)
    {
        var catalog = new Catalog();
        var policy = new MissionChestPolicy
        {
            RoomDensity = 0.3, CountVariationFraction = 0.2, MinItems = 1, MaxItems = 3,
            RarityWeights = new Dictionary<string, double>
            {
                ["Missing"] = 70, ["1"] = 20, ["2"] = 6, ["3"] = 2.5, ["4"] = 1, ["5"] = 0.5
            }
        };
        string json = empty ? "{\"Version\":1,\"Entries\":[]}" : """
            {"Version":1,"Entries":[{"LowId":701,"HighId":701,"MinQuality":50,"MaxQuality":50,
              "Evidence":["https://example.test/chest-source"],"Classification":"OrdinaryRubiKa"}]}
            """;
        return new MissionChestLootService(catalog, new StubGameData(HashItemCatalog.Parse("{}")),
            new ItemBuilder(catalog, ids, new StubLogger()), policy, MissionChestRKAllowlist.Parse(json));
    }

    sealed class CountingIds : IItemInstanceIdAllocator
    {
        public int Allocations { get; private set; }
        public int Allocate() => ++Allocations;
    }

    sealed class Catalog : IItemTemplateCatalog
    {
        readonly Dictionary<int, ItemTemplate> _items = new()
        {
            [701] = Template(701, "Approved synthetic item"),
            [702] = Template(702, "Unapproved synthetic item")
        };
        public IReadOnlyCollection<ItemTemplate> Templates => _items.Values;
        public bool TryGet(int aoid, out ItemTemplate template) => _items.TryGetValue(aoid, out template!);
        public ItemTemplate Require(int aoid) => _items[aoid];
        static ItemTemplate Template(int id, string name) => new()
        {
            Id = id, Name = name, Quality = 50, DynelType = 51005,
            Stats = new Dictionary<CharacterStat, int> { [CharacterStat.Can] = (int)CanFlags.Carry }
        };
    }
}
