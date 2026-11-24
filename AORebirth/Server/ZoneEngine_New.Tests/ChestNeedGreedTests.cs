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
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Loot;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Teams;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Need or Greed on chests (rubi-ka.org bug 636). A Need or Greed team member opening a chest puts its stored items up
/// for rolls; while a roll is open no take path hands the item out, and the winner gets the item's own row, moved off the
/// chest through their commit lane exactly as a take moves it. Storage must end equal to memory: each item one row, held
/// by whoever holds it, whatever order the queued writes run in.
/// </summary>
[TestClass]
public sealed class ChestNeedGreedTests
{
    const int LeaderId = 52100001;
    const int MemberId = 52100002;
    const int ChestId = 52190001;

    [TestMethod]
    public void TeamOpeningAChestRollsItsItemsAndTheWinnerGetsTheStoredRow()
    {
        using var rig = new Rig();
        rig.FormTeam(TeamLootMode.NeedOrGreed);
        Item sword = rig.Stock(0, 51500001);
        Item pistol = rig.Stock(1, 51500002);

        Assert.IsTrue(rig.Chest.TryUse(rig.Leader), "the leader opens the chest");
        Assert.IsTrue(rig.Chest.IsHeldForRoll(sword) && rig.Chest.IsHeldForRoll(pistol), "both items are up for rolls");

        // The opener cannot take an item up for a roll.
        Assert.IsFalse(rig.TryTake(rig.Leader, 0), "a held item stays in the chest");
        rig.Flush.HardFlush(rig.Leader);
        rig.Store.AssertRow(sword, (int)IdentityType.Container, ChestId, 0);

        // Sword: the member needs, the leader greeds. The member wins.
        rig.Loot(rig.Leader, "greed");
        rig.Loot(rig.Member, "need");
        Assert.IsFalse(rig.Chest.Loot.Content.Values.Any(held => held.InstanceId == sword.InstanceId), "the sword left the chest");
        Assert.AreSame(sword, rig.Member.Inventory.Inventory.Content.Values.Single(), "the member holds the sword");
        Assert.IsFalse(rig.Chest.IsOpen, "the opener's stale window closed");
        Assert.IsTrue(rig.MemberSession.Bodies.OfType<AddTemplateMessage>().Any(add => add.LowId == sword.LowId),
            "the member's client is shown the sword");

        // The winner moves it again before anything was written: the lane stores the take, then the later move.
        rig.DirtyMove(rig.Member, sword);
        rig.Flush.HardFlush(rig.Member);
        rig.AssertStorageMatchesMemory(sword);

        // Pistol: both pass. It is ordinary loot again, never rolled twice, and the leader takes it.
        rig.Loot(rig.Leader, "pass");
        rig.Loot(rig.Member, "pass");
        Assert.IsFalse(rig.Chest.IsHeldForRoll(pistol));
        Assert.IsTrue(rig.Chest.TryUse(rig.Leader), "the leader reopens the chest");
        Assert.IsFalse(rig.Chest.IsHeldForRoll(pistol), "a reopen does not roll an item again");
        Assert.IsTrue(rig.TryTake(rig.Leader, 1), "the released pistol is taken");
        rig.Flush.HardFlush(rig.Leader);
        rig.AssertStorageMatchesMemory(sword, pistol);
        rig.AssertNoFailure();
    }

    /// <summary>
    /// Bug 651: one member needs, the other passes, and the chest roll ended "Nobody won; it is free to loot" because the
    /// playfield's services had no inventory action service to move the won row with. The needer must get the item.
    /// </summary>
    [TestMethod]
    public void TheOnlyNeedRollerGetsTheChestItemOnTheEnginesOwnPlayfieldServices()
    {
        using var rig = new Rig();
        rig.FormTeam(TeamLootMode.NeedOrGreed);
        Item rapier = rig.Stock(0, 51500051);
        Assert.IsNotNull(rig.Playfield.GetService<InventoryActionService>(), "the playfield exposes the inventory action service");

        Assert.IsTrue(rig.Chest.TryUse(rig.Member), "the member opens the chest");
        rig.Loot(rig.Member, "need");
        rig.Loot(rig.Leader, "pass");

        Assert.AreEqual(0, rig.Chest.Loot.Content.Count, "the rapier left the chest");
        Assert.AreSame(rapier, rig.Member.Inventory.Inventory.Content.Values.Single(), "the needer holds the rapier");
        Assert.AreEqual(0, rig.Leader.Inventory.Inventory.Content.Count);
        rig.Flush.HardFlush(rig.Member);
        rig.AssertStorageMatchesMemory(rapier);
        Assert.IsTrue(rig.Chest.TryUse(rig.Leader), "the leader reopens the chest");
        Assert.AreEqual(0, rig.Chest.Loot.Content.Count, "a reopen does not offer the rapier again");
        rig.AssertNoFailure();
    }

    [TestMethod]
    public void HeldItemsStayHeldWhenAReopenRebuildsThemFromTheirRows()
    {
        using var rig = new Rig();
        rig.FormTeam(TeamLootMode.NeedOrGreed);
        Item sword = rig.Stock(0, 51500011);
        rig.Chest.RefreshLoot = rig.RebuildFromRows;

        Assert.IsTrue(rig.Chest.TryUse(rig.Leader));
        Assert.IsTrue(rig.Chest.TryUse(rig.Leader), "the leader closes the chest");
        Assert.IsTrue(rig.Chest.TryUse(rig.Member), "the member opens it: its items are rebuilt from storage");
        Item rebuilt = rig.Chest.Loot.Content[0];
        Assert.AreNotSame(sword, rebuilt);
        Assert.IsTrue(rig.Chest.IsHeldForRoll(rebuilt), "the rebuilt item is still held");
        Assert.IsFalse(rig.TryTake(rig.Member, 0), "the second opener cannot take the held item");

        rig.Loot(rig.Leader, "need");
        rig.Loot(rig.Member, "pass");
        Assert.AreEqual(0, rig.Chest.Loot.Content.Count, "the rebuilt item left the chest");
        Assert.AreSame(rebuilt, rig.Leader.Inventory.Inventory.Content.Values.Single());
        Assert.AreEqual(0, rig.Member.Inventory.Inventory.Content.Count);

        // A reopen after the win reads storage only once the queued take is written: the item is not offered again.
        Assert.IsTrue(rig.Chest.TryUse(rig.Member));
        Assert.AreEqual(0, rig.Chest.Loot.Content.Count, "the won item is not back in the chest");
        rig.AssertStorageMatchesMemory(rebuilt);
        rig.AssertNoFailure();
    }

    [TestMethod]
    public void AWinnerWhoDisconnectedLosesTheItemToTheNextRollerNotToNobody()
    {
        using var rig = new Rig();
        rig.FormTeam(TeamLootMode.NeedOrGreed);
        Item sword = rig.Stock(0, 51500021);
        Assert.IsTrue(rig.Chest.TryUse(rig.Leader));

        rig.Loot(rig.Member, "need");
        rig.MemberSession.Close();
        rig.Loot(rig.Leader, "greed");

        Assert.AreEqual(0, rig.Member.Inventory.Inventory.Content.Count, "the disconnected member got nothing");
        Assert.AreSame(sword, rig.Leader.Inventory.Inventory.Content.Values.Single(), "the leader got the sword");
        rig.Flush.HardFlush(rig.Leader);
        rig.AssertStorageMatchesMemory(sword);
    }

    [TestMethod]
    public void AWinnerWithAFullInventoryPassesTheItemOnAndNothingIsLost()
    {
        using var rig = new Rig();
        rig.FormTeam(TeamLootMode.NeedOrGreed);
        Item sword = rig.Stock(0, 51500031);
        rig.FillInventory(rig.Member);
        Assert.IsTrue(rig.Chest.TryUse(rig.Leader));

        rig.Loot(rig.Member, "need");
        rig.Loot(rig.Leader, "pass");

        // Nobody who wanted it could take it: it is free loot in the chest, still stored there.
        Assert.IsTrue(rig.Chest.Loot.Content.Values.Any(held => held.InstanceId == sword.InstanceId));
        Assert.IsFalse(rig.Chest.IsHeldForRoll(sword));
        rig.Flush.HardFlush(rig.Member);
        rig.Store.AssertRow(sword, (int)IdentityType.Container, ChestId, 0);
        // Nothing was awarded, so the leader's window is still open and now offers the released item.
        Assert.IsTrue(rig.Chest.IsOpen);
        Assert.IsTrue(rig.TryTake(rig.Leader, 0));
        rig.Flush.HardFlush(rig.Leader);
        rig.AssertStorageMatchesMemory(sword);
    }

    [TestMethod]
    public void FreeForAllTeamsAndSoloOpenersLootChestsAsBefore()
    {
        using var rig = new Rig();
        rig.FormTeam(TeamLootMode.FreeForAll);
        Item sword = rig.Stock(0, 51500041);
        Assert.IsTrue(rig.Chest.TryUse(rig.Leader));
        Assert.IsFalse(rig.Chest.IsHeldForRoll(sword));
        Assert.IsTrue(rig.TryTake(rig.Leader, 0));
        rig.Flush.HardFlush(rig.Leader);
        rig.AssertStorageMatchesMemory(sword);
        rig.AssertNoFailure();
    }

    sealed class Rig : IDisposable
    {
        internal readonly Store Store = new();
        internal readonly Logger Log = new();
        internal readonly Session LeaderSession = new();
        internal readonly Session MemberSession = new();
        internal readonly Player Leader;
        internal readonly Player Member;
        internal readonly DynelRegistry Registry;
        internal readonly InventoryFlushService Flush;
        internal readonly InventoryActionService Actions;
        internal readonly TeamService Teams;
        internal readonly Playfield Playfield;
        internal readonly Chest Chest;
        readonly ServiceProvider _services;
        readonly Items _items = new();
        DateTime _now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

        internal Rig()
        {
            Leader = Online(LeaderId, "Leader", LeaderSession);
            Member = Online(MemberId, "Member", MemberSession);
            Flush = new InventoryFlushService(_ => null, () => [Leader, Member], Store, Log);
            Actions = new InventoryActionService(Store, Flush, new Ids(), Log);
            Teams = new TeamService(null, () => _now, (_, action) => action());
            // Attached before they enter the bare playfield, which has no metadata for the LFT location.
            Teams.AttachPlayer(Leader);
            Teams.AttachPlayer(Member);

            // The playfield's own service graph, as the engine builds it (bug 651: a hand-built one registered the
            // inventory action service the real one lacked, so every chest win fell through to "Nobody won").
            Playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
            var manager = (PlayfieldManager)RuntimeHelpers.GetUninitializedObject(typeof(PlayfieldManager));
            SetOn(manager, "<Teams>k__BackingField", Teams);
            SetOn(manager, "<ItemTemplates>k__BackingField", RuntimeHelpers.GetUninitializedObject(typeof(ItemTemplateCatalog)));
            SetField("<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield, Instance = 4582 });
            SetField("<ResourceId>k__BackingField", 4582);
            SetField("<Geometry>k__BackingField", new PlayfieldGeometryData());
            SetField("_playfieldManager", manager);
            SetField("_logger", Log);
            SetField("_playerHydrator", RuntimeHelpers.GetUninitializedObject(typeof(PlayerHydrator)));
            SetField("_gameData", RuntimeHelpers.GetUninitializedObject(typeof(GameDataStore)));
            SetField("_items", _items);
            SetField("_hashItems", RuntimeHelpers.GetUninitializedObject(typeof(HashItemMinter)));
            SetField("_inventoryRepository", RuntimeHelpers.GetUninitializedObject(typeof(MySqlInventoryRepository)));
            SetField("_instanceIds", new Ids());
            SetField("_inventoryMoves", new InventoryMoveService(Log, Flush, Actions));
            SetField("_inventoryFlush", Flush);
            SetField("_trades", RuntimeHelpers.GetUninitializedObject(typeof(Core.Trade.TradeService)));
            SetField("_characterSnapshot", RuntimeHelpers.GetUninitializedObject(typeof(CharacterSnapshotService)));
            SetField("_shopDao", new Shops());
            var build = (IServiceCollection)typeof(Playfield).GetMethod("BuildServices", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Playfield, null)!;
            _services = build.BuildServiceProvider();
            SetField("_serviceProvider", _services);
            Registry = _services.GetRequiredService<DynelRegistry>();
            SetField("_dynelRegistry", Registry);

            foreach (Player player in new[] { Leader, Member })
            {
                player.Playfield = Playfield;
                player.Inventory.SeedClientContainerHandle(117);
                Registry.Register(player);
            }
            Chest = new Chest(new Identity { Type = IdentityType.Container, Instance = ChestId })
            {
                SpawnDefinition = new ChestSpawnDefinition
                {
                    StateMachineType = 1000015, StateMachineInstance = 7, HeaderStatBytes = 111, LockableValue = 15,
                    Stats = [new() { StatId = 23, Value = 162447 }]
                },
                Playfield = Playfield,
                Position = Leader.Position
            };
            Registry.Register(Chest);
        }

        void SetField(string name, object value) => SetOn(Playfield, name, value);

        static void SetOn<T>(T target, string name, object value)
            => (typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(T).Name, name)).SetValue(target, value);

        Player Online(int id, string name, Session session)
        {
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, Log, _items) { FirstName = name };
            player.Inventory.Apply(new CharacterHydrationResult(), id, _items);
            player.Stats.Set(CharacterStat.Level, 60);
            player.Stats.Set(CharacterStat.Profession, 3);
            player.EnterOnline(session);
            return player;
        }

        internal void FormTeam(TeamLootMode mode)
        {
            Teams.TryHandle(Leader, new CharacterActionMessage
            {
                Identity = Leader.Identity, Action = CharacterActionType.TeamRequestInvite, Target = Member.Identity
            });
            Teams.TryHandle(Member, new CharacterActionMessage
            {
                Identity = Member.Identity, Action = CharacterActionType.TeamRequestReply, Target = Leader.Identity, Parameter2 = 1
            });
            Assert.IsTrue(Teams.AreTeammates(Leader, Member), "the two are a team");
            Assert.IsTrue(Teams.SetLootMode(Leader, mode));
            Assert.AreEqual(mode, Teams.GetTeam(Leader)!.LootMode);
        }

        /// <summary>A stored chest item, as a mission chest holds it: a row on the chest container at its slot.</summary>
        internal Item Stock(int slot, int instanceId)
        {
            Item item = _items.Create(5100 + slot, 5100 + slot, 50, ItemSource.Loot, instanceId: instanceId);
            item.IsPersisted = true;
            Assert.IsTrue(Chest.Loot.Add(slot, item));
            Store.Seed(item, (int)IdentityType.Container, ChestId, slot);
            return item;
        }

        /// <summary>A mission chest's refresh: its contents rebuilt as new items from the rows stored on it.</summary>
        internal bool RebuildFromRows(Chest chest)
        {
            chest.Loot.Content.Clear();
            foreach ((int id, (int type, int owner, int slot)) in Store.Rows())
            {
                if (type != (int)IdentityType.Container || owner != ChestId)
                    continue;
                Item item = _items.Create(5100 + slot, 5100 + slot, 50, ItemSource.Loot, instanceId: id);
                item.IsPersisted = true;
                Assert.IsTrue(chest.Loot.Add(slot, item));
            }
            return true;
        }

        internal void FillInventory(Player player)
        {
            Container page = player.Inventory.Inventory;
            int id = 51600000 + player.Identity.Instance % 1000 * 100;
            for (int slot = page.FindFreeSlot(); slot >= 0; slot = page.FindFreeSlot())
            {
                Item filler = _items.Create(7000, 7000, 1, ItemSource.Loot, instanceId: ++id);
                filler.IsPersisted = true;
                Assert.IsTrue(page.Add(slot, filler));
                Store.Seed(filler, (int)page.Identity.Type, player.Identity.Instance, slot);
            }
        }

        /// <summary>/loot need|greed|pass as the stock client sends it: answers the player's oldest open roll.</summary>
        internal void Loot(Player player, string choice)
            => _services.GetRequiredService<LootRollService>().HandleCommand(player, ["loot", choice]);

        /// <summary>The player drags the chest slot's item to their inventory, through the engine's move handler.</summary>
        internal bool TryTake(Player player, int slot)
        {
            Assert.AreEqual(player.Identity, Chest.OpenerIdentity, "the taker has the chest open");
            new InventoryMoveService(Log, Flush, Actions).Handle(player, new ClientMoveItemToInventory
            {
                Identity = player.Identity,
                SourceContainer = new Identity { Type = IdentityType.Backpack, Instance = (Chest.InventoryHandle << 16) | slot },
                Slot = 0x6F
            });
            return !Chest.Loot.Content.ContainsKey(slot);
        }

        internal void DirtyMove(Player player, Item item)
        {
            Container page = player.Inventory.Inventory;
            int from = page.Content.Single(pair => ReferenceEquals(pair.Value, item)).Key;
            page.Content.Remove(from);
            int to = page.FindFreeSlot();
            if (to == from)
            {
                Assert.IsTrue(page.Add(from + 1, item));
                to = from + 1;
            }
            else
                Assert.IsTrue(page.Add(to, item));
            player.Inventory.MarkDirty(item, page, to);
            Flush.NotifyDirty(player);
        }

        /// <summary>Each item has exactly one row, where exactly one player or the chest holds it now.</summary>
        internal void AssertStorageMatchesMemory(params Item[] items)
        {
            foreach (Item item in items)
            {
                var holders = new List<(int Type, int Owner, int Slot)>();
                foreach (Player player in new[] { Leader, Member })
                    foreach (Container page in new[] { player.Inventory.Inventory, player.Inventory.Overflow })
                        foreach (KeyValuePair<int, Item> held in page.Content)
                            if (held.Value.InstanceId == item.InstanceId)
                                holders.Add(((int)page.Identity.Type, page.Identity.Instance, held.Key));
                foreach (KeyValuePair<int, Item> held in Chest.Loot.Content)
                    if (held.Value.InstanceId == item.InstanceId)
                        holders.Add(((int)IdentityType.Container, ChestId, held.Key));
                Assert.AreEqual(1, holders.Count, "item " + item.InstanceId + " is held exactly once in memory");
                Store.AssertRow(item, holders[0].Type, holders[0].Owner, holders[0].Slot);
            }
        }

        internal void AssertNoFailure()
        {
            Assert.AreEqual(string.Empty, Log.Problems);
            Assert.IsFalse(Leader.IsPersistenceQuarantined);
            Assert.IsFalse(Member.IsPersistenceQuarantined);
        }

        public void Dispose()
        {
            Flush.Dispose();
            _services.Dispose();
        }
    }

    /// <summary>item_instances as the MySQL DAO writes it: a shared source row must be exactly where it was last seen.</summary>
    sealed class Store : ICharacterCoalesceCommit, ITradePersistence, IInventoryMutationPersistence
    {
        readonly object _sync = new();
        readonly Dictionary<int, (int Type, int Owner, int Slot)> _rows = new();

        internal KeyValuePair<int, (int Type, int Owner, int Slot)>[] Rows()
        {
            lock (_sync)
                return [.. _rows];
        }

        internal void Seed(Item item, int type, int owner, int slot)
        {
            lock (_sync)
                _rows.Add(item.InstanceId, (type, owner, slot));
        }

        internal void AssertRow(Item item, int type, int owner, int slot)
        {
            lock (_sync)
            {
                Assert.IsTrue(_rows.TryGetValue(item.InstanceId, out var row), "No row for item " + item.InstanceId + ".");
                Assert.AreEqual((type, owner, slot), row, "Row of item " + item.InstanceId);
            }
        }

        public void Persist(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates, int characterId,
            IReadOnlyList<int> uploadedNanoIds, IReadOnlyList<ActiveNanoRecord>? activeNanos, IReadOnlyList<SkillLockRecord>? skillLocks)
            => Write(inserts, updates);

        public void Persist(TradePersistenceBatch batch) => Write(batch.Inserts, batch.Updates);

        public void Persist(InventoryMutationBatch batch)
        {
            lock (_sync)
            {
                foreach (ItemInstanceRecord expected in batch.ExpectedSources)
                    if (!_rows.TryGetValue(expected.InstanceId, out var row)
                        || row != (expected.ContainerType, expected.ContainerInstance, expected.ContainerPlacement))
                        throw new InvalidOperationException("Shared source row changed for InstanceId=" + expected.InstanceId);
                Write(batch.Inserts, batch.Locations);
            }
        }

        void Write(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates)
        {
            lock (_sync)
            {
                foreach (ItemLocationUpdate update in updates)
                    if (!_rows.ContainsKey(update.InstanceId))
                        throw new InvalidOperationException("UpdateLocations found no row for InstanceId=" + update.InstanceId);
                foreach (ItemInstanceRecord insert in inserts)
                    if (!_rows.TryAdd(insert.InstanceId, (insert.ContainerType, insert.ContainerInstance, insert.ContainerPlacement)))
                        throw new InvalidOperationException("Duplicate insert for InstanceId=" + insert.InstanceId);
                foreach (ItemLocationUpdate update in updates)
                    _rows[update.InstanceId] = (update.ContainerType, update.ContainerInstance, update.ContainerPlacement);
            }
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
        internal IEnumerable<MessageBody> Bodies => Sent;
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
        int _next = 51222700;

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

    sealed class Shops : AORebirth.Interfaces.Persistence.Shops.IShopDao
    {
        public IList<AORebirth.Interfaces.Persistence.Shops.ShopVendorData> ListForPlayfield(int playfieldId) => [];
    }

    sealed class Ids : IItemInstanceIdAllocator
    {
        int _next = 51300000;
        public int Allocate() => Interlocked.Increment(ref _next);
    }
}
