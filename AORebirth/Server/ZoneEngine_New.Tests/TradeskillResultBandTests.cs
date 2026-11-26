namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

using AORebirth.Enums;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;
using LostEden.Protocol.Messages.N3Messages;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Tradeskills;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// rubi-ka.org team request 22: a Perennium Sniper combine (Result 246408 QL 50 .. 246414 QL 200) minted that one pair at
/// any QL, so from about QL 125 the client named it "Superior Perennium Sniper". The line has a template pair per 50 QL
/// between (ItemGroups.csv group 3405); a combine at a QL is made from the line's templates that hold it. Each case drags
/// the two items together the way the client does and checks the item the player ends up holding.
/// </summary>
[TestClass]
public sealed class TradeskillResultBandTests
{
    const int PlayerId = 52500001;
    const int SourceId = 226020, TargetId = 246402;
    const int Ql50 = 246408, Ql99 = 246409, Ql100 = 246410, Ql149 = 246411, Ql150 = 246412, Ql199 = 246413, Superior = 246414;

    static readonly (int Id, int Ql, string Name)[] Sniper =
    [
        (Ql50, 50, "Perennium Sniper"), (Ql99, 99, "Perennium Sniper"), (Ql100, 100, "Perennium Sniper"),
        (Ql149, 149, "Perennium Sniper"), (Ql150, 150, "Perennium Sniper"), (Ql199, 199, "Perennium Sniper"),
        (Superior, 200, "Superior Perennium Sniper")
    ];

    static readonly (int, int)[] SniperLeaves = [(Ql50, Ql99), (Ql100, Ql149), (Ql150, Ql199), (Superior, Superior)];

    const string SniperLine = "group,item_id,froob_friendly,in_game,rollable\n"
        + "3405,246408,0,1,0\n3405,246409,0,1,0\n3405,246410,0,1,0\n3405,246411,0,1,0\n"
        + "3405,246412,0,1,0\n3405,246413,0,1,0\n3405,246414,0,1,0\n";

    [TestMethod]
    [DataRow(50, Ql50, Ql99, DisplayName = "QL 50: the line's first pair")]
    [DataRow(99, Ql50, Ql99, DisplayName = "QL 99: still the first pair")]
    [DataRow(100, Ql100, Ql149, DisplayName = "QL 100: the second pair")]
    [DataRow(125, Ql100, Ql149, DisplayName = "QL 125: where the reported Superior name began")]
    [DataRow(175, Ql150, Ql199, DisplayName = "QL 175: the third pair")]
    [DataRow(199, Ql150, Ql199, DisplayName = "QL 199: the last QL below Superior")]
    [DataRow(200, Superior, Superior, DisplayName = "QL 200: the Superior, the line's only QL 200 template")]
    public void PerenniumSniperCombineIsMadeFromTheTemplatesThatHoldItsQl(int targetQl, int expectedLow, int expectedHigh)
    {
        using var rig = new Rig(SniperLine, SniperLeaves);

        Item result = rig.Combine(targetQl);

        Assert.AreEqual(expectedLow, result.LowId);
        Assert.AreEqual(expectedHigh, result.HighId);
        Assert.AreEqual(targetQl, result.Quality, "the QL the combine gives is unchanged");
        Assert.AreEqual(targetQl == 200 ? "Superior Perennium Sniper" : "Perennium Sniper", result.Definition!.Name,
            "the name a player sees matches the QL");
        rig.AssertOneResultAndNoFailure(result);
    }

    [TestMethod]
    public void WithoutItemGroupsTheRecipesTwoTemplatesAreKept()
    {
        using var rig = new Rig(itemGroups: null, SniperLeaves);

        Item result = rig.Combine(125);

        Assert.AreEqual((Ql50, Superior), (result.LowId, result.HighId), "no line known: as before");
        rig.AssertOneResultAndNoFailure(result);
    }

    [TestMethod]
    public void APlainPairWithNothingBetweenIsKept()
    {
        // The line holds templates outside the recipe's QL 50..99 range only.
        using var rig = new Rig(SniperLine, SniperLeaves, resultLow: Ql50, resultHigh: Ql99);

        Item result = rig.Combine(75);

        Assert.AreEqual((Ql50, Ql99), (result.LowId, result.HighId));
        rig.AssertOneResultAndNoFailure(result);
    }

    [TestMethod]
    public void ALineWithoutExactPairsUsesTheTwoTemplatesNextToTheQl()
    {
        // Health and Nano Kit 291004 (QL 1) .. 291006 (QL 400): no ItemTemplates leaf pairs them.
        string line = "group,item_id,froob_friendly,in_game,rollable\n5093,291004,1,1,1\n5093,291005,1,1,1\n5093,291006,1,1,1\n";
        (int, int, string)[] kit = [(291004, 1, "Health and Nano Kit"), (291005, 200, "Health and Nano Kit"), (291006, 400, "Health and Nano Kit")];
        using var rig = new Rig(line, [], kit, resultLow: 291004, resultHigh: 291006);

        Assert.AreEqual((291004, 291005), Ids(rig.Combine(150)));
        Assert.AreEqual((291005, 291006), Ids(rig.Combine(300)));
        rig.AssertNoFailure();
    }

    [TestMethod]
    public void AQlSharedByTwoBandsStaysOnTheBandItsPairNames()
    {
        // Lower band 300001 QL 1 / 300004 QL 100, upper band 300002 QL 100 / 300003 QL 200: QL 100 ends one band and
        // starts the other. By QL then id the line reads 300001, 300002, 300004, 300003, so the two templates next to
        // QL 150 would be 300004/300003 (one of each band) and next to QL 50 300001/300002.
        string line = "group,item_id,in_game\n9,300001,1\n9,300003,1\n9,300002,1\n9,300004,1\n";
        (int, int, string)[] items = [(300001, 1, "Lower"), (300002, 100, "Upper"), (300003, 200, "Upper"), (300004, 100, "Lower")];
        using var rig = new Rig(line, [(300001, 300004), (300002, 300003)], items, resultLow: 300001, resultHigh: 300003);

        Assert.AreEqual((300002, 300003), Ids(rig.Combine(150)));
        Assert.AreEqual((300001, 300004), Ids(rig.Combine(50)));
        rig.AssertNoFailure();
    }

    [TestMethod]
    public void ATemplateOutOfTheGameIsNeverMade()
    {
        string line = SniperLine.Replace("3405,246410,0,1,0", "3405,246410,0,0,0").Replace("3405,246411,0,1,0", "3405,246411,0,0,0");
        using var rig = new Rig(line, SniperLeaves);

        Item result = rig.Combine(125);

        Assert.AreNotEqual(Ql100, result.LowId);
        Assert.AreNotEqual(Ql149, result.HighId);
        Assert.AreEqual((Ql99, Ql150), Ids(result), "the in-game templates next to QL 125");
        rig.AssertOneResultAndNoFailure(result);
    }

    [TestMethod]
    [DataRow(Superior, 200, 125, DisplayName = "holds the Superior, combines QL 125")]
    [DataRow(Ql100, 120, 175, DisplayName = "holds a QL 100-149 one, combines QL 175")]
    [DataRow(Ql150, 160, 160, DisplayName = "holds a QL 150-199 one, combines the same band")]
    public void AUniqueLineRefusesASecondCopyFromAnyBand(int heldId, int heldQl, int combineQl)
    {
        // The Unique flag sits on the recipe's high end only; the held copy and the new result may be other bands.
        using var rig = new Rig(SniperLine, SniperLeaves, uniqueId: Superior);
        rig.Give(heldId, heldQl);
        int before = rig.Player.Inventory.Inventory.Content.Count;

        TradeskillService.CombineOutcome outcome = rig.TryCombine(combineQl);

        Assert.AreEqual(TradeskillService.CombineOutcome.Refused, outcome);
        Assert.AreEqual(before + 2, rig.Player.Inventory.Inventory.Content.Count, "both ingredients kept, nothing made");
        Assert.AreEqual(1, rig.Player.Inventory.Inventory.Content.Values.Count(item => item.LowId == heldId), "still one copy");
        rig.AssertNoFailure();
    }

    [TestMethod]
    public void AUniqueLineCombinesWhenNoCopyIsHeld()
    {
        using var rig = new Rig(SniperLine, SniperLeaves, uniqueId: Superior);

        Item result = rig.Combine(125);

        Assert.AreEqual((Ql100, Ql149), Ids(result));
        rig.AssertOneResultAndNoFailure(result);
    }

    [TestMethod]
    public void PrivateItemGroupsHoldThePerenniumSniperLine()
    {
        string? root = Environment.GetEnvironmentVariable("AO_REBIRTH_GAMEDATA_PATH");
        if (string.IsNullOrEmpty(root) || !File.Exists(Path.Combine(root, ItemLineCatalog.FileName)))
            Assert.Inconclusive("private GameData ItemGroups.csv not available");

        ItemLineCatalog lines = ItemLineCatalog.Load(root!);

        Assert.IsTrue(lines.TryGetLine(Ql50, out IReadOnlyList<int> line));
        CollectionAssert.IsSubsetOf(new[] { Ql50, Ql99, Ql100, Ql149, Ql150, Ql199, Superior }, line.ToArray());
    }

    static (int, int) Ids(Item item) => (item.LowId, item.HighId);

    sealed class Rig : IDisposable
    {
        internal readonly Logger Log = new();
        internal readonly Session Session = new();
        internal readonly Player Player;
        internal readonly Items ItemBuilder;
        internal readonly TradeskillService Tradeskills;
        readonly InventoryFlushService _flush;
        readonly Store _store = new();
        readonly string _root;
        int _nextInstance = 52510000;

        internal Rig(string? itemGroups, (int, int)[] leaves, (int Id, int Ql, string Name)[]? templates = null,
            int resultLow = Ql50, int resultHigh = Superior, int uniqueId = 0)
        {
            var catalog = new Dictionary<int, ItemTemplate>();
            foreach ((int id, int ql, string name) in (templates ?? Sniper)
                .Concat([(SourceId, 200, "Perennium Combine Source"), (TargetId, 200, "Perennium Sniper Target")]))
                catalog[id] = new ItemTemplate { Id = id, Quality = ql, Name = name, Flags = id == uniqueId ? (int)ItemFlags.Unique : 0 };
            var catalogFake = new TemplateCatalog(catalog);
            ItemBuilder = new Items(catalogFake);

            _root = Path.Combine(Path.GetTempPath(), "tradeskill-bands-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            if (itemGroups != null)
                File.WriteAllText(Path.Combine(_root, ItemLineCatalog.FileName), itemGroups);

            Player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = PlayerId }, Log, ItemBuilder) { FirstName = "Crafter" };
            Player.Inventory.Apply(new CharacterHydrationResult(), PlayerId, ItemBuilder);
            Player.EnterOnline(Session);

            Player crafter = Player;
            _flush = new InventoryFlushService(_ => null, () => [crafter], _store, Log);
            var actions = new InventoryActionService(_store, _flush, new InstanceIds(), Log);
            var recipe = new TradeskillRecipe
            {
                Key = "226020>246402", Source = [SourceId], Target = [TargetId],
                Result = new TradeskillResult { LowId = resultLow, HighId = resultHigh },
                ResultQl = TradeskillResultQl.Target, ConsumeSource = true, ConsumeTarget = true
            };
            var pairs = new HashSet<(int, int)>(leaves);
            string root = _root;
            Tradeskills = new TradeskillService(new TradeskillCatalog([recipe]), actions, ItemBuilder, catalogFake, Log,
                Fake<IGameData>((method, args) => method.Name switch
                {
                    "get_RootPath" => root,
                    "TryGetAssignedItemHash" => Assigned(pairs, args),
                    _ => null
                }));
        }

        static object Assigned(HashSet<(int, int)> pairs, object?[] args)
        {
            bool found = pairs.Contains(((int)args[0]!, (int)args[1]!));
            args[2] = found ? "HASH" : string.Empty;
            return found;
        }

        /// <summary>Drags a fresh source onto a fresh target of <paramref name="targetQl"/>; returns the result.</summary>
        internal Item Combine(int targetQl)
        {
            var before = new HashSet<Item>(Player.Inventory.Inventory.Content.Values, ReferenceEqualityComparer.Instance);
            Assert.AreEqual(TradeskillService.CombineOutcome.Combined, TryCombine(targetQl));
            return Player.Inventory.Inventory.Content.Values.Single(item => !before.Contains(item));
        }

        internal TradeskillService.CombineOutcome TryCombine(int targetQl)
        {
            Container page = Player.Inventory.Inventory;
            int sourceSlot = FreeSlot(page);
            Add(page, sourceSlot, SourceId, 200);
            int targetSlot = FreeSlot(page);
            Add(page, targetSlot, TargetId, targetQl);
            return Tradeskills.TryCombineDrag(Player,
                new Identity { Type = IdentityType.Inventory, Instance = sourceSlot },
                new Identity { Type = IdentityType.Inventory, Instance = targetSlot });
        }

        internal void Give(int lowId, int quality) => Add(Player.Inventory.Inventory, FreeSlot(Player.Inventory.Inventory), lowId, quality);

        static int FreeSlot(Container page)
            => Enumerable.Range(page.Offset, page.Capacity).First(slot => !page.Content.ContainsKey(slot));

        void Add(Container page, int slot, int lowId, int quality)
        {
            Item item = ItemBuilder.Create(lowId, lowId, quality, ItemSource.Loot, instanceId: Interlocked.Increment(ref _nextInstance));
            item.IsPersisted = true;
            Assert.IsTrue(page.Add(slot, item));
            _store.Seed(item.InstanceId);
        }

        internal void AssertOneResultAndNoFailure(Item result)
        {
            Container page = Player.Inventory.Inventory;
            Assert.IsFalse(page.Content.Values.Any(item => item.LowId is SourceId or TargetId), "both used items are gone");
            Assert.AreEqual(1, page.Content.Values.Count(item => ReferenceEquals(item, result)));
            _flush.HardFlush(Player);
            Assert.IsTrue(_store.Inserted(result.InstanceId), "the result's row is written");
            AssertNoFailure();
        }

        internal void AssertNoFailure()
        {
            Assert.AreEqual(string.Empty, Log.Problems);
            Assert.IsFalse(Player.IsPersistenceQuarantined);
        }

        public void Dispose()
        {
            _flush.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }

    static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        T proxy = DispatchProxy.Create<T, FakeProxy>();
        ((FakeProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class FakeProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[], object?>? Handler;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            object? value = Handler!(targetMethod!, args ?? []);
            if (value != null || targetMethod!.ReturnType == typeof(void) || !targetMethod.ReturnType.IsValueType)
                return value;
            return Activator.CreateInstance(targetMethod.ReturnType);
        }
    }

    sealed class TemplateCatalog(Dictionary<int, ItemTemplate> templates) : IItemTemplateCatalog
    {
        public IReadOnlyCollection<ItemTemplate> Templates => templates.Values;
        public bool TryGet(int aoid, out ItemTemplate template) => templates.TryGetValue(aoid, out template!);
        public ItemTemplate Require(int aoid) => templates[aoid];
    }

    sealed class Store : ICharacterCoalesceCommit, ITradePersistence, IInventoryMutationPersistence
    {
        readonly ConcurrentDictionary<int, bool> _rows = new();
        readonly ConcurrentDictionary<int, bool> _inserted = new();

        internal void Seed(int instanceId) => _rows[instanceId] = true;

        internal bool Inserted(int instanceId) => _inserted.ContainsKey(instanceId);

        public void Persist(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates, int characterId,
            IReadOnlyList<int> uploadedNanoIds, IReadOnlyList<ActiveNanoRecord>? activeNanos, IReadOnlyList<SkillLockRecord>? skillLocks)
        {
            foreach (ItemInstanceRecord insert in inserts)
                _rows[insert.InstanceId] = _inserted[insert.InstanceId] = true;
        }

        public void Persist(TradePersistenceBatch batch) { }

        public void Persist(InventoryMutationBatch batch)
        {
            foreach (ItemInstanceRecord insert in batch.Inserts)
                _rows[insert.InstanceId] = _inserted[insert.InstanceId] = true;
        }
    }

    sealed class Logger : IZoneLogger
    {
        readonly object _sync = new();
        string _problems = string.Empty;

        internal string Problems { get { lock (_sync) return _problems; } }

        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { lock (_sync) _problems += "WARN " + message + Environment.NewLine; }
        public void Error(string message) { lock (_sync) _problems += "ERROR " + message + Environment.NewLine; }
        public void Error(Exception exception, string message) { lock (_sync) _problems += "ERROR " + message + " " + exception + Environment.NewLine; }
        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }

    sealed class Session : IZoneSession
    {
        internal readonly ConcurrentQueue<MessageBody> Sent = new();
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public bool TransferToPlayfield(Playfield destination, Vector3 landing) => false;
        public void Send(byte[] packet) { }
        public void Send(MessageBody body) => Sent.Enqueue(body);
        public void Send(MessageBody body, int sender, int receiver) => Sent.Enqueue(body);
        public void SendInitiateCompression() { }
        public void Close() => State = SessionState.Closed;
    }

    /// <summary>Builds the definition the way the engine does: the template nearer the QL names the item.</summary>
    sealed class Items(IItemTemplateCatalog catalog) : IItemBuilder
    {
        int _next = 52520000;

        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null) => new()
            {
                LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount,
                InstanceId = instanceId, Identity = identity ?? Identity.None, Definition = CreateTemplate(lowId, highId, quality)
            };

        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
        {
            Item item = Create(lowId, highId, quality, source, stackCount);
            item.AssignInstanceId(Interlocked.Increment(ref _next));
            return item;
        }

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality)
        {
            catalog.TryGet(lowId, out ItemTemplate low);
            catalog.TryGet(highId, out ItemTemplate high);
            ItemTemplate? nearer = low == null ? high : high == null ? low
                : Math.Abs(quality - low.Quality) <= Math.Abs(high.Quality - quality) ? low : high;
            return new ItemTemplate { Id = lowId, Quality = quality, Name = nearer?.Name ?? "item " + lowId, Flags = nearer?.Flags ?? 0 };
        }

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }

    sealed class InstanceIds : IItemInstanceIdAllocator
    {
        int _next = 52530000;
        public int Allocate() => Interlocked.Increment(ref _next);
    }
}
