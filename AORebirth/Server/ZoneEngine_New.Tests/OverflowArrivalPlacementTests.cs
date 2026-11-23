namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// rubi-ka.org team request 10: the client puts a TemplateAction Overflow item at its first empty overflow placement
/// from 0, and the ContainerAddItem that follows moves whatever it holds at the Source placement into the inventory.
/// From placement 0 with an older item already there, that older item moved and the new one stayed in overflow while
/// the server had the new one in the inventory, until zoning. Each case replays the sent messages through that client
/// rule and requires the client to end up holding what the server holds.
/// </summary>
[TestClass]
public sealed class OverflowArrivalPlacementTests
{
    const int PlayerId = 52400001;
    const int SourceId = 247101, TargetId = 247102, ResultId = 247103, FillerId = 247104;

    [TestMethod]
    [DataRow(new int[0], 0, DisplayName = "empty overflow: placement 0, as captured")]
    [DataRow(new[] { 0 }, 1, DisplayName = "older item at placement 0")]
    [DataRow(new[] { 0, 2 }, 1, DisplayName = "gap at placement 1")]
    [DataRow(new[] { 0, 1, 2 }, 3, DisplayName = "three older items")]
    public void CombineResultMovesFromThePlacementTheClientGaveIt(int[] occupied, int expectedPlacement)
    {
        using var rig = new Rig();
        Container page = rig.Player.Inventory.Inventory;
        Item source = rig.Add(page, page.Offset, SourceId);
        Item target = rig.Add(page, page.Offset + 1, TargetId);
        rig.Add(page, page.Offset + 2, FillerId);
        Item[] older = occupied.Select((slot, i) => rig.Add(rig.Player.Inventory.Overflow, slot, 247200 + i)).ToArray();
        var client = ClientView.Of(rig.Player);

        TradeskillService.CombineOutcome outcome = rig.Tradeskills.TryCombineDrag(rig.Player,
            new Identity { Type = IdentityType.Inventory, Instance = page.Offset },
            new Identity { Type = IdentityType.Inventory, Instance = page.Offset + 1 });

        Assert.AreEqual(TradeskillService.CombineOutcome.Combined, outcome);
        Item result = page.Content[page.Offset];
        Assert.AreEqual(ResultId, result.LowId, "the server puts the result in the slot the used items freed");
        Assert.IsFalse(page.Content.Values.Contains(source) || page.Content.Values.Contains(target));
        CollectionAssert.AreEquivalent(older, rig.Player.Inventory.Overflow.Content.Values.ToArray(), "overflow is untouched");
        ContainerAddItem move = rig.Sent.OfType<ContainerAddItem>().Single();
        Assert.AreEqual(new Identity { Type = IdentityType.OverflowWindow, Instance = expectedPlacement }, move.Source);

        client.Apply(rig.Sent);
        client.AssertMatches(rig.Player);
        rig.AssertNoFailure();
    }

    [TestMethod]
    public void OverflowGrantMovesFromTheSlotTheItemHolds()
    {
        using var rig = new Rig();
        Container page = rig.Player.Inventory.Inventory;
        for (int slot = page.Offset; slot < page.Offset + page.Capacity; slot++)
            rig.Add(page, slot, FillerId);
        rig.Add(rig.Player.Inventory.Overflow, 0, 247200);
        var client = ClientView.Of(rig.Player);
        Item granted = rig.ItemBuilder.CreateWithNewInstance(ResultId, ResultId, 1, ItemSource.Other);

        lock (rig.Player.PersistenceGate)
        {
            Assert.IsTrue(InventoryGrantPlan.TryCreate(rig.Player, [granted], out InventoryGrantPlan plan, allowOverflow: true));
            Assert.IsTrue(plan.PublishAfterCommit());
        }

        Assert.AreSame(granted, rig.Player.Inventory.Overflow.Content[1], "the grant takes the first free overflow slot");
        ContainerAddItem move = rig.Sent.OfType<ContainerAddItem>().Single();
        Assert.AreEqual(new Identity { Type = IdentityType.OverflowWindow, Instance = 1 }, move.Source,
            "the move names the granted item's placement, never the older item at 0");

        client.Apply(rig.Sent);
        client.AssertMatches(rig.Player);
        Assert.IsFalse(rig.Player.IsPersistenceQuarantined);
    }

    /// <summary>
    /// The stock client's handling of the messages these paths send: DeleteItem drops a slot; TemplateAction Overflow
    /// takes the first empty overflow placement from 0 (1004d68c -> 1002a371(0) -> 1002aa8a); ContainerAddItem from an
    /// overflow placement moves that placement's item to the first free inventory slot, if there is one.
    /// </summary>
    sealed class ClientView
    {
        readonly Dictionary<int, int> _inventory;
        readonly Dictionary<int, int> _overflow;
        readonly int _offset, _capacity;

        ClientView(Player player)
        {
            Container page = player.Inventory.Inventory;
            _offset = page.Offset;
            _capacity = page.Capacity;
            _inventory = page.Content.ToDictionary(entry => entry.Key, entry => entry.Value.LowId);
            _overflow = player.Inventory.Overflow.Content.ToDictionary(entry => entry.Key, entry => entry.Value.LowId);
        }

        internal static ClientView Of(Player player) => new(player);

        internal void Apply(IEnumerable<MessageBody> sent)
        {
            foreach (MessageBody body in sent)
            {
                switch (body)
                {
                    case CharacterActionMessage { Action: CharacterActionType.DeleteItem } delete:
                        (delete.Target.Type == IdentityType.OverflowWindow ? _overflow : _inventory).Remove(delete.Target.Instance);
                        break;
                    case TemplateActionMessage { Action: TemplateActionType.Overflow } arrived:
                        _overflow[Enumerable.Range(0, int.MaxValue).First(slot => !_overflow.ContainsKey(slot))] = arrived.ItemLowId;
                        break;
                    case ContainerAddItem { Source.Type: IdentityType.OverflowWindow } move:
                        int free = Enumerable.Range(_offset, _capacity).FirstOrDefault(slot => !_inventory.ContainsKey(slot), -1);
                        if (free >= 0 && _overflow.Remove(move.Source.Instance, out int lowId))
                            _inventory[free] = lowId;
                        break;
                }
            }
        }

        internal void AssertMatches(Player player)
        {
            CollectionAssert.AreEquivalent(
                player.Inventory.Inventory.Content.Select(entry => (entry.Key, entry.Value.LowId)).ToArray(),
                _inventory.Select(entry => (entry.Key, entry.Value)).ToArray(), "client inventory matches the server");
            CollectionAssert.AreEquivalent(
                player.Inventory.Overflow.Content.Select(entry => (entry.Key, entry.Value.LowId)).ToArray(),
                _overflow.Select(entry => (entry.Key, entry.Value)).ToArray(), "client overflow matches the server");
        }
    }

    sealed class Rig : IDisposable
    {
        internal readonly Logger Log = new();
        internal readonly Session Session = new();
        internal readonly Player Player;
        internal readonly Items ItemBuilder = new();
        internal readonly TradeskillService Tradeskills;
        readonly InventoryFlushService _flush;
        readonly Store _store = new();
        int _nextInstance = 52410000;

        internal Rig()
        {
            Player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = PlayerId }, Log, ItemBuilder) { FirstName = "Crafter" };
            Player.Inventory.Apply(new CharacterHydrationResult(), PlayerId, ItemBuilder);
            Player.EnterOnline(Session);

            Player crafter = Player;
            _flush = new InventoryFlushService(_ => null, () => [crafter], _store, Log);
            var actions = new InventoryActionService(_store, _flush, new Ids(), Log);
            var recipe = new TradeskillRecipe
            {
                Key = "overflow-arrival", Source = [SourceId], Target = [TargetId],
                Result = new TradeskillResult { LowId = ResultId, HighId = ResultId },
                ResultQl = TradeskillResultQl.Fixed, FixedQl = 1, ConsumeSource = true, ConsumeTarget = true
            };
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "overflow-arrival-" + Guid.NewGuid().ToString("N"));
            Tradeskills = new TradeskillService(new TradeskillCatalog([recipe]), actions, ItemBuilder,
                Fake<IItemTemplateCatalog>(_ => null), Log, Fake<IGameData>(method => method.Name == "get_RootPath" ? root : null));
        }

        internal IEnumerable<MessageBody> Sent => Session.Sent;

        internal Item Add(Container page, int slot, int lowId)
        {
            Item item = ItemBuilder.Create(lowId, lowId, 1, ItemSource.Loot, instanceId: Interlocked.Increment(ref _nextInstance));
            item.IsPersisted = true;
            Assert.IsTrue(page.Add(slot, item));
            _store.Seed(item.InstanceId);
            return item;
        }

        internal void AssertNoFailure()
        {
            Assert.AreEqual(string.Empty, Log.Problems);
            Assert.IsFalse(Player.IsPersistenceQuarantined);
        }

        public void Dispose() => _flush.Dispose();
    }

    static T Fake<T>(Func<MethodInfo, object?> handler) where T : class
    {
        T proxy = DispatchProxy.Create<T, FakeProxy>();
        ((FakeProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    public class FakeProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?>? Handler;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            object? value = Handler!(targetMethod!);
            if (value != null || targetMethod!.ReturnType == typeof(void) || !targetMethod.ReturnType.IsValueType)
                return value;
            return Activator.CreateInstance(targetMethod.ReturnType);
        }
    }

    sealed class Store : ICharacterCoalesceCommit, ITradePersistence, IInventoryMutationPersistence
    {
        readonly ConcurrentDictionary<int, bool> _rows = new();

        internal void Seed(int instanceId) => _rows[instanceId] = true;

        public void Persist(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates, int characterId,
            IReadOnlyList<int> uploadedNanoIds, IReadOnlyList<ActiveNanoRecord>? activeNanos, IReadOnlyList<SkillLockRecord>? skillLocks)
        {
            foreach (ItemInstanceRecord insert in inserts)
                _rows[insert.InstanceId] = true;
        }

        public void Persist(TradePersistenceBatch batch) { }

        public void Persist(InventoryMutationBatch batch) { }
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

    sealed class Items : IItemBuilder
    {
        int _next = 52420000;

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

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality, Name = "item " + lowId };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }

    sealed class Ids : IItemInstanceIdAllocator
    {
        int _next = 52430000;
        public int Allocate() => Interlocked.Increment(ref _next);
    }
}
