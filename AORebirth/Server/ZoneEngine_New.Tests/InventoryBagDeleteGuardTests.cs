namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using AORebirth.Enums;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// LE-01 and LE-34 on the inventory action service, against a store that refuses what MySQL refuses. A bag never
/// opened since login has no loaded page: the old empty check passed it, the delete was queued, the database refused
/// to retire a container with rows and the whole lane was discarded, which a client could arrange on purpose to keep
/// its later changes out of storage. The contents are now loaded on the tick and the delete refused there. What a
/// deleted or consumed item becomes outside the inventory waits for the retire to be stored.
/// </summary>
[TestClass]
public sealed class InventoryBagDeleteGuardTests
{
    static Identity Slot(int placement = 64) => new() { Type = IdentityType.Inventory, Instance = placement };

    [TestMethod]
    public void AnUnopenedBagWithStoredContentsIsRefusedOnTheTickWithoutAQuarantine()
    {
        using var w = new World();
        Item bag = w.Bag(11);
        var child = TestWorld.CreateItem(instanceId: 12);
        ItemInstanceRecord childRow = child.ToRecord(bag.Identity, 0, 1);
        w.Persistence.Rows[12] = childRow;
        w.Repository.Containers[11] = [childRow];

        Assert.IsFalse(w.Actions.TryDelete(w.Player, Slot(), 11), "the stored contents are loaded and found");

        Assert.AreEqual(0, w.Persistence.Calls, "nothing was queued");
        Assert.IsFalse(w.Player.IsPersistenceQuarantined);
        Assert.AreSame(bag, w.Player.Inventory.Inventory.Content[64]);
        Assert.IsTrue(w.Player.Inventory.TryGetBackpackPage(bag.Identity, out Container page) && page.IsHydrated && page.Content.Count == 1,
            "the page is loaded, as opening the bag loads it");
        Assert.AreEqual((int)IdentityType.Inventory, w.Persistence.Rows[11].ContainerType);
        Assert.AreEqual(11, w.Persistence.Rows[12].ContainerInstance);
        Assert.AreEqual(0, w.Session.Messages.Count);
        w.Flush.HardFlush(w.Player);
        Assert.IsFalse(w.Player.IsPersistenceQuarantined);
    }

    [TestMethod]
    public void AnUnopenedEmptyBagIsLoadedAndDeleted()
    {
        using var w = new World();
        w.Bag(11);

        Assert.IsTrue(w.Actions.TryDelete(w.Player, Slot(), 11));
        w.Flush.HardFlush(w.Player);

        Assert.AreEqual((int)IdentityType.None, w.Persistence.Rows[11].ContainerType);
        Assert.IsFalse(w.Player.IsPersistenceQuarantined);
    }

    [TestMethod]
    public void ABagWhoseContentsCannotBeLoadedIsNotDeleted()
    {
        using var w = new World(canLoadBags: false);
        Item bag = w.Bag(11);

        Assert.IsFalse(w.Actions.TryDelete(w.Player, Slot(), 11), "with no way to know the contents the delete fails closed");
        Assert.AreEqual(0, w.Persistence.Calls);
        Assert.AreSame(bag, w.Player.Inventory.Inventory.Content[64]);
    }

    [TestMethod]
    public void AnOpenBagWithContentsInMemoryIsNeverDeletedEvenIfStorageSaysEmpty()
    {
        using var w = new World();
        Item bag = w.Bag(11);
        Container page = w.Player.Inventory.GetOrCreateBackpackPage(bag, bag.Identity, Slot());
        page.Add(0, TestWorld.CreateItem(instanceId: 12));

        Assert.IsFalse(w.Actions.TryDelete(w.Player, Slot(), 11), "what memory holds counts, loaded or not");
        Assert.AreEqual(1, page.Content.Count, "the page's contents are not wiped by a reload");
        Assert.AreEqual(0, w.Persistence.Calls);
    }

    /// <summary>What a deleted item becomes outside the inventory (a world item a drop leaves) waits for the retire to be stored.</summary>
    [TestMethod]
    public void TheDeleteStoredCallbackRunsOnlyOnceTheRetireIsStored()
    {
        using var w = new World();
        w.Add(21, 1);
        bool stored = false;

        Assert.IsTrue(w.Actions.TryDelete(w.Player, Slot(), 21, stored: () => stored = true));
        Assert.IsFalse(stored, "the retire is only queued");
        w.Flush.HardFlush(w.Player);

        Assert.IsTrue(stored);
        Assert.AreEqual((int)IdentityType.None, w.Persistence.Rows[21].ContainerType);
    }

    [TestMethod]
    public void TheDeleteStoredCallbackNeverRunsWhenTheRetireFails()
    {
        using var w = new World();
        w.Add(21, 1);
        bool stored = false;
        w.Persistence.Failure = new InvalidOperationException("refused");

        Assert.IsTrue(w.Actions.TryDelete(w.Player, Slot(), 21, stored: () => stored = true));
        Assert.ThrowsExactly<InvalidOperationException>(() => w.Flush.HardFlush(w.Player));

        Assert.IsFalse(stored, "nothing outside the inventory follows an unstored retire");
        Assert.IsTrue(w.Player.IsPersistenceQuarantined);
        Assert.AreEqual((int)IdentityType.Inventory, w.Persistence.Rows[21].ContainerType, "storage still holds the item");
    }

    /// <summary>LE-34: using one of a stack takes one; the last one is retired; the effect waits for the store.</summary>
    [TestMethod]
    public void ConsumingOneTakesOneFromAStackAndRetiresTheLast()
    {
        using var w = new World();
        Item stack = w.Add(31, 3);
        int stored = 0;

        Assert.IsTrue(w.Actions.TryConsumeOne(w.Player, Slot(), 31, () => stored++));
        Assert.AreEqual(2, stack.StackCount);
        Assert.AreEqual(0, stored, "the effect waits for the store");
        w.Flush.HardFlush(w.Player);
        Assert.AreEqual(1, stored);
        Assert.AreEqual(2, w.Persistence.Rows[31].StackCount);

        Assert.IsTrue(w.Actions.TryConsumeOne(w.Player, Slot(), 31, () => stored++));
        Assert.AreEqual(1, stack.StackCount);
        Assert.IsTrue(w.Actions.TryConsumeOne(w.Player, Slot(), 31, () => stored++));
        Assert.IsFalse(w.Player.Inventory.Inventory.Content.ContainsKey(64), "the last one leaves the slot");
        w.Flush.HardFlush(w.Player);

        Assert.AreEqual(3, stored);
        Assert.AreEqual((int)IdentityType.None, w.Persistence.Rows[31].ContainerType, "the last one is retired");
        Assert.IsFalse(w.Player.IsPersistenceQuarantined);
    }

    [TestMethod]
    public void AConsumedUnitWhoseStoreFailsRunsNoEffect()
    {
        using var w = new World();
        Item stack = w.Add(31, 2);
        int stored = 0;
        w.Persistence.Failure = new InvalidOperationException("refused");

        Assert.IsTrue(w.Actions.TryConsumeOne(w.Player, Slot(), 31, () => stored++));
        Assert.AreEqual(1, stack.StackCount);
        Assert.ThrowsExactly<InvalidOperationException>(() => w.Flush.HardFlush(w.Player));

        Assert.AreEqual(0, stored);
        Assert.IsTrue(w.Player.IsPersistenceQuarantined);
        Assert.AreEqual(2, w.Persistence.Rows[31].StackCount, "storage still holds both");
    }

    internal sealed class World : IDisposable
    {
        public readonly Player Player = TestWorld.CreatePlayer(111);
        public readonly Session Session = new();
        public readonly Persistence Persistence = new();
        public readonly Ids Ids = new();
        public readonly InventoryFlushService Flush;
        public readonly InventoryActionService Actions;
        /// <summary>Stored bag contents, by container instance: what a bag never opened since login would load.</summary>
        public readonly ContainerRows Repository = new();
        readonly ServiceProvider _services;

        public World(bool canLoadBags = true)
        {
            Player.Session = Session;
            Session.BindPlayer(Player);
            Player.Playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
            var manager = (PlayfieldManager)RuntimeHelpers.GetUninitializedObject(typeof(PlayfieldManager));
            typeof(PlayfieldManager).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, new Lock());
            typeof(PlayfieldManager).GetField("_playersByCharacterId", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(manager, new Dictionary<int, Player>());
            Flush = new InventoryFlushService(new Lazy<PlayfieldManager>(() => manager), new Coalesce(Persistence), new StubLogger());
            var services = new ServiceCollection()
                .AddSingleton(new PlayfieldLocality(4582, null))
                .AddSingleton(Flush);
            if (canLoadBags)
                services.AddSingleton<IInventoryRepository>(Repository).AddSingleton<IItemBuilder>(new RecordItems());
            _services = services.BuildServiceProvider();
            typeof(Playfield).GetField("_serviceProvider", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Player.Playfield, _services);
            Actions = new InventoryActionService(Persistence, Flush, Ids, new StubLogger());
        }

        /// <summary>A stored stackable item in the inventory slot.</summary>
        public Item Add(int id, int count, int slot = 64, int lowId = 1000)
        {
            Item item = TestWorld.CreateItem(instanceId: id, lowId: lowId, highId: lowId);
            item.StackCount = count;
            item.Definition.Stats[CharacterStat.Can] = (int)CanFlags.Stackable;
            Player.Inventory.Inventory.Add(slot, item);
            Persistence.Rows[id] = item.ToRecord(Player.Inventory.Inventory.Identity, slot, count);
            return item;
        }

        /// <summary>A stored bag in the inventory slot, never opened since login (no page).</summary>
        public Item Bag(int id, int slot = 64)
        {
            Item bag = Add(id, 1, slot, lowId: 99228);
            bag.Identity = new Identity { Type = IdentityType.Container, Instance = id };
            return bag;
        }

        public void Dispose()
        {
            Flush.Dispose();
            _services.Dispose();
        }
    }

    internal sealed class Ids : IItemInstanceIdAllocator
    {
        int _next = 100;
        public int Allocate() => _next++;
    }

    /// <summary>The coalesced flush writes into the same rows the queued mutation path does.</summary>
    sealed class Coalesce(Persistence rows) : ICharacterCoalesceCommit
    {
        public void Persist(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates, int characterId,
            IReadOnlyList<int> uploadedNanoIds, IReadOnlyList<ActiveNanoRecord>? activeNanos, IReadOnlyList<SkillLockRecord>? skillLocks)
            => rows.ApplyFlush(inserts, updates);
    }

    /// <summary>item_instances with the MySQL transaction's rules: a batch applies whole or not at all, and a container is retired only empty.</summary>
    internal sealed class Persistence : IInventoryMutationPersistence
    {
        public Dictionary<int, ItemInstanceRecord> Rows = new();
        public Dictionary<int, int> Stats = new();
        public int Calls;
        public Exception? Failure;

        public void ApplyFlush(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates)
        {
            foreach (ItemInstanceRecord row in inserts)
                Rows.Add(row.InstanceId, row);
            foreach (ItemLocationUpdate update in updates)
            {
                ItemInstanceRecord row = Rows[update.InstanceId];
                Rows[update.InstanceId] = Copy(row, update.ContainerType, update.ContainerInstance, update.ContainerPlacement,
                    update.StackCount ?? row.StackCount);
            }
        }

        public void Persist(InventoryMutationBatch batch)
        {
            Calls++;
            var next = new Dictionary<int, ItemInstanceRecord>(Rows);
            var nextStats = new Dictionary<int, int>(Stats);
            foreach (ItemInstanceRecord expected in batch.ExpectedSources)
            {
                if (!next.TryGetValue(expected.InstanceId, out ItemInstanceRecord? actual)
                    || actual.ContainerType != expected.ContainerType || actual.ContainerInstance != expected.ContainerInstance
                    || actual.ContainerPlacement != expected.ContainerPlacement || actual.StackCount != expected.StackCount)
                    throw new InvalidOperationException("stale shared container source");
            }
            foreach (ItemInstanceRecord row in batch.Inserts)
                next.Add(row.InstanceId, row);
            foreach (ItemLocationUpdate location in batch.Locations)
            {
                ItemInstanceRecord row = next[location.InstanceId];
                next[location.InstanceId] = Copy(row, location.ContainerType, location.ContainerInstance, location.ContainerPlacement, row.StackCount);
            }
            foreach (var stack in batch.Stacks)
            {
                ItemInstanceRecord row = next[stack.InstanceId];
                if (row.StackCount != stack.ExpectedCount)
                    throw new InvalidOperationException("stale stack");
                next[stack.InstanceId] = Copy(row, row.ContainerType, row.ContainerInstance, row.ContainerPlacement, stack.FinalCount);
            }
            foreach (int container in batch.EmptyContainersBeforeRetire)
            {
                if (next.Values.Any(row => row.ContainerType == (int)IdentityType.Container && row.ContainerInstance == container))
                    throw new InvalidOperationException("Cannot retire a nonempty item container.");
            }
            foreach (StatRecord stat in batch.FinalStats)
                nextStats[stat.StatId] = stat.StatValue;
            if (Failure != null)
                throw Failure;
            Rows = next;
            Stats = nextStats;
        }

        static ItemInstanceRecord Copy(ItemInstanceRecord r, int type, int owner, int placement, int count) => new()
        {
            InstanceId = r.InstanceId, ContainerType = type, ContainerInstance = owner, ContainerPlacement = placement,
            ItemType = r.ItemType, LowId = r.LowId, HighId = r.HighId, Quality = r.Quality, StackCount = count, Source = r.Source
        };
    }

    internal sealed class Session : IZoneSession
    {
        public readonly List<MessageBody> Messages = new();
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public bool TransferToPlayfield(Playfield destination, Vector3 landing) => throw new NotSupportedException();
        public void Send(byte[] packet) => throw new NotSupportedException();
        public void Send(MessageBody message) => Messages.Add(message);
        public void Send(MessageBody message, int sender, int receiver) => Messages.Add(message);
        public void SendInitiateCompression() => throw new NotSupportedException();
        public void Close() => State = SessionState.Closed;
    }

    /// <summary>Bag contents as stored; every other read or write fails (nothing else reaches the database here).</summary>
    internal sealed class ContainerRows : IInventoryRepository
    {
        public readonly Dictionary<int, List<ItemInstanceRecord>> Containers = new();
        public IReadOnlyList<ItemInstanceRecord> GetCarriedItems(int id) => throw new NotSupportedException();
        public IReadOnlyList<ItemInstanceRecord> GetBankItems(int id) => throw new NotSupportedException();
        public IReadOnlyList<ItemInstanceRecord> GetContainerItems(int id)
            => Containers.TryGetValue(id, out List<ItemInstanceRecord>? rows) ? rows : [];
        public int LeaseInstanceIdBlock(int count) => throw new NotSupportedException();
        public ItemInstanceRecord Insert(ItemInstanceRecord item) => throw new NotSupportedException();
        public void UpdateLocation(int id, int type, int owner, int placement) => throw new NotSupportedException();
        public void UpdateLocations(IReadOnlyList<ItemLocationUpdate> updates) => throw new NotSupportedException();
        public void PersistNewAndUpdateLocations(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates)
            => throw new NotSupportedException();
    }

    /// <summary>The stub builder, plus items read back from their stored rows (what loading a bag needs).</summary>
    sealed class RecordItems : IItemBuilder
    {
        readonly StubItemBuilder _stub = new();
        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null)
            => _stub.Create(lowId, highId, quality, source, stackCount, instanceId, identity, statsBlob);
        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
            => _stub.CreateWithNewInstance(lowId, highId, quality, source, stackCount);
        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => _stub.CreateTemplate(lowId, highId, quality);
        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item)
        {
            item = _stub.Create(row.LowId, row.HighId, row.Quality, row.Source, Math.Max(1, row.StackCount), row.InstanceId);
            item.IsPersisted = true;
            return true;
        }
    }
}
