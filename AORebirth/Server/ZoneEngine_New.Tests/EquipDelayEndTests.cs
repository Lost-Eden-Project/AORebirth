namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using AORebirth.Enums;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;
using LostEden.Protocol.Messages.N3Messages;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// The client's equip delay ends only on CharacterAction 0x92 (rubi-ka.org team request 9). The client starts it as it
/// sends any move to or from a worn slot and refuses every item move (trade, combine, bag) while it runs; built from an
/// item without EquipDelay (211) it never ends on its own. Every such request must end with exactly one 0x92 once the
/// server is done with it: after the delayed equip completes, when it is cancelled, and when the server refuses it.
/// </summary>
[TestClass]
public sealed class EquipDelayEndTests
{
    const int PlayerId = 52300001;
    const CharacterActionType EquipDone = (CharacterActionType)0x92;

    [TestMethod]
    public void EquipSendsEquipDoneOnlyAfterTheDelayAndAfterTheMove()
    {
        using var rig = new Rig();
        Item armor = rig.Armor(64, 52310001, equipDelay: 150);
        int slot = rig.ArmorSlot;

        rig.Move(Inventory(64), slot);
        Assert.IsTrue(rig.Moves.HasPending(PlayerId), "the equip waits for its delay");
        rig.Tick(1.0);
        Assert.AreEqual(0, rig.EquipDoneActions().Count, "the client delay keeps running while the server's does");

        rig.Tick(0.6);
        Assert.AreSame(armor, rig.Player.Inventory.Armor.Content[slot], "the armor is worn");
        CharacterActionMessage done = rig.EquipDoneActions().Single();
        Assert.AreEqual(rig.Player.Identity, done.Identity, "the action names the character whose delay ends");
        Assert.AreEqual(new Identity { Type = IdentityType.ArmorPage, Instance = slot }, done.Target);
        Assert.IsTrue(rig.IndexOf(done) > rig.LastIndexOf<ContainerAddItem>(), "the delay ends after the move is acknowledged");
        Assert.IsFalse(armor.Locked);
        rig.AssertNoFailure();
    }

    [TestMethod]
    public void SwapWithAnItemMissingEquipDelayStillEndsTheClientDelay()
    {
        using var rig = new Rig();
        int slot = rig.ArmorSlot;
        Item worn = rig.Armor(64, 52310002, equipDelay: 50);
        rig.Move(Inventory(64), slot);
        rig.Tick(1.0);
        Assert.AreSame(worn, rig.Player.Inventory.Armor.Content[slot]);
        rig.ClearSent();

        // The reported case: one side of the swap has no 211, so the stock client's delay is about 1234567890 cs.
        Item quest = rig.Armor(65, 52310003, equipDelay: null);
        rig.Move(Inventory(65), slot);
        Assert.AreEqual(0, rig.EquipDoneActions().Count);
        rig.Tick(1.0);

        Assert.AreSame(quest, rig.Player.Inventory.Armor.Content[slot], "the new armor is worn");
        Assert.IsTrue(rig.Player.Inventory.Inventory.Content.Values.Contains(worn), "the old armor came back to the inventory");
        Assert.AreEqual(1, rig.EquipDoneActions().Count, "the client's delay is ended once");
        rig.AssertNoFailure();
    }

    [TestMethod]
    public void RefusedEquipEndsTheClientDelayAtOnce()
    {
        using var rig = new Rig();
        Item armor = rig.Armor(64, 52310004, equipDelay: 150, slotMask: 0);

        rig.Move(Inventory(64), rig.ArmorSlot);

        Assert.IsFalse(rig.Moves.HasPending(PlayerId));
        Assert.AreSame(armor, rig.Player.Inventory.Inventory.Content[64], "the armor stays in the inventory");
        Assert.AreEqual(new Identity { Type = IdentityType.ArmorPage, Instance = rig.ArmorSlot },
            rig.EquipDoneActions().Single().Target);
    }

    [TestMethod]
    public void UnequipOfAnEmptyWornSlotEndsTheClientDelay()
    {
        using var rig = new Rig();

        // A stale client view: it shows armor the server does not have in that slot.
        rig.Move(new Identity { Type = IdentityType.ArmorPage, Instance = rig.ArmorSlot }, 0x6F);

        Assert.AreEqual(1, rig.EquipDoneActions().Count);
    }

    [TestMethod]
    public void RefusedUnequipThroughContainerAddItemEndsTheClientDelay()
    {
        using var rig = new Rig();
        int slot = rig.ArmorSlot;
        Item armor = rig.Armor(64, 52310005, equipDelay: 0);
        rig.Move(Inventory(64), slot);
        rig.Tick(1.0);
        Assert.AreSame(armor, rig.Player.Inventory.Armor.Content[slot]);
        rig.ClearSent();

        // The bank is not open, so the drop onto it is refused.
        rig.Moves.Handle(rig.Player, new ClientContainerAddItem
        {
            Identity = rig.Player.Identity,
            Source = new Identity { Type = IdentityType.ArmorPage, Instance = slot },
            Target = new Identity { Type = IdentityType.BankByRef, Instance = PlayerId }
        });

        Assert.AreSame(armor, rig.Player.Inventory.Armor.Content[slot], "the armor stays worn");
        Assert.AreEqual(1, rig.EquipDoneActions().Count);
    }

    [TestMethod]
    public void CancelledEquipEndsTheClientDelayOnceAndNeverCompletes()
    {
        using var rig = new Rig();
        Item armor = rig.Armor(64, 52310006, equipDelay: 150);
        rig.Move(Inventory(64), rig.ArmorSlot);

        rig.Player.InterruptTimedActions(TimedActionInterrupt.Jump);
        Assert.IsFalse(rig.Moves.HasPending(PlayerId));
        Assert.AreEqual(1, rig.EquipDoneActions().Count, "the cancelled equip ends the client's delay");

        rig.Tick(2.0);
        Assert.AreSame(armor, rig.Player.Inventory.Inventory.Content[64], "the cancelled equip never lands");
        Assert.IsFalse(armor.Locked);
        Assert.AreEqual(1, rig.EquipDoneActions().Count, "no second end");
    }

    [TestMethod]
    public void WornRequestWhileAnEquipIsPendingDoesNotEndTheRunningDelay()
    {
        using var rig = new Rig();
        Item first = rig.Armor(64, 52310007, equipDelay: 150);
        Item second = rig.Armor(65, 52310008, equipDelay: 150);
        rig.Move(Inventory(64), rig.ArmorSlot);

        rig.Move(Inventory(65), rig.ArmorSlot);
        Assert.AreEqual(0, rig.EquipDoneActions().Count, "the first equip's delay is still running");

        rig.Tick(2.0);
        Assert.AreSame(first, rig.Player.Inventory.Armor.Content[rig.ArmorSlot]);
        Assert.AreSame(second, rig.Player.Inventory.Inventory.Content[65], "the second request was refused");
        Assert.IsFalse(second.Locked);
        Assert.AreEqual(1, rig.EquipDoneActions().Count);
        Assert.IsFalse(rig.Player.IsPersistenceQuarantined);
    }

    [TestMethod]
    public void InventoryMoveSendsNoEquipDone()
    {
        using var rig = new Rig();
        Item armor = rig.Armor(64, 52310009, equipDelay: 150);

        rig.Move(Inventory(64), 70);

        Assert.AreSame(armor, rig.Player.Inventory.Inventory.Content[70]);
        Assert.AreEqual(0, rig.EquipDoneActions().Count);
    }

    static Identity Inventory(int slot) => new() { Type = IdentityType.Inventory, Instance = slot };

    sealed class Rig : IDisposable
    {
        internal readonly Logger Log = new();
        internal readonly Session Session = new();
        internal readonly Player Player;
        internal readonly InventoryFlushService Flush;
        internal readonly InventoryMoveService Moves;
        internal readonly Playfield Playfield;
        readonly ServiceProvider _services;
        readonly Items _items = new();
        readonly Store _store = new();

        internal Rig()
        {
            Player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = PlayerId }, Log, _items) { FirstName = "Wearer" };
            Player.Inventory.Apply(new CharacterHydrationResult(), PlayerId, _items);
            Player.Stats.Set(CharacterStat.Level, 60);
            Player.Stats.Set(CharacterStat.Profession, 3);
            Player.EnterOnline(Session);

            Player wearer = Player;
            Flush = new InventoryFlushService(_ => null, () => [wearer], _store, Log);
            var actions = new InventoryActionService(_store, Flush, new Ids(), Log);
            Moves = new InventoryMoveService(Log, Flush, actions);

            Playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
            _services = new ServiceCollection().AddSingleton(new DynelRegistry()).AddSingleton(Flush)
                .AddSingleton(actions).AddSingleton(Moves).BuildServiceProvider();
            SetField("<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield, Instance = 4582 });
            SetField("<ResourceId>k__BackingField", 4582);
            SetField("_serviceProvider", _services);
            Player.Playfield = Playfield;
        }

        internal int ArmorSlot => Player.Inventory.Armor.Offset;

        void SetField(string name, object value)
            => typeof(Playfield).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Playfield, value);

        /// <summary>Armor for the first armor slot; <paramref name="equipDelay"/> null leaves EquipDelay (211) unset.</summary>
        internal Item Armor(int slot, int instanceId, int? equipDelay, int slotMask = 1 << 1)
        {
            Item item = _items.Create(5200 + slot, 5200 + slot, 50, ItemSource.Loot, instanceId: instanceId);
            item.Definition.Stats[CharacterStat.ItemClass] = (int)ItemClass.Armor;
            item.Definition.Stats[CharacterStat.Slot] = slotMask;
            if (equipDelay is int delay)
                item.Definition.Stats[CharacterStat.EquipDelay] = delay;
            item.IsPersisted = true;
            Assert.IsTrue(Player.Inventory.Inventory.Add(slot, item));
            _store.Seed(item.InstanceId);
            return item;
        }

        /// <summary>The client drags an item, through the engine's move handler.</summary>
        internal void Move(Identity source, int targetPlacement)
            => Moves.Handle(Player, new ClientMoveItemToInventory
            {
                Identity = Player.Identity, SourceContainer = source, Slot = targetPlacement
            });

        internal void Tick(double seconds) => Moves.Tick(Playfield, seconds);

        internal List<CharacterActionMessage> EquipDoneActions()
            => Session.Sent.OfType<CharacterActionMessage>().Where(action => action.Action == EquipDone).ToList();

        internal int IndexOf(MessageBody body) => Session.Sent.ToList().IndexOf(body);

        internal int LastIndexOf<T>() where T : MessageBody
            => Session.Sent.ToList().FindLastIndex(body => body is T);

        internal void ClearSent() => Session.Sent.Clear();

        internal void AssertNoFailure()
        {
            Assert.AreEqual(string.Empty, Log.Problems);
            Assert.IsFalse(Player.IsPersistenceQuarantined);
        }

        public void Dispose()
        {
            Flush.Dispose();
            _services.Dispose();
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
        int _next = 51322700;

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

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }

    sealed class Ids : IItemInstanceIdAllocator
    {
        int _next = 51330000;
        public int Allocate() => Interlocked.Increment(ref _next);
    }
}
