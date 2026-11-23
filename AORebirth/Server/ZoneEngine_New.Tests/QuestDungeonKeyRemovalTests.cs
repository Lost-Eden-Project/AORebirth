namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

using AORebirth.Core.GameData;
using AORebirth.Database.Domain.Quests;
using AORebirth.Enums;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;
using LostEden.Protocol.Messages.N3Messages;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Utility.GameData.Missions;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Quests;
using ZoneEngine_New.Core.Quests.Dungeons;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Bug #664: mission keys stayed in the player's inventory after the mission was completed, until relog.
/// <list type="bullet">
/// <item>A key in an opened bag: the in-memory removal only looked at the main pages.</item>
/// <item>A team mission (live, 2026-11-22): the finisher's own row, still active until their reward commits, counted as
/// another member holding it, so the dungeon stayed open. It ended on the zone-out reconnect (world entry replays ended
/// quests), where the inventory delete is refused outside InPlay, so the key already sent to the client stayed.</item>
/// </list>
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class QuestDungeonKeyRemovalTests
{
    const int CharacterId = 51664001;
    const int TeammateId = 51664002;
    const int BagId = 51664100;
    const int KeyId = 51664200;
    const int BagSlot = 3;
    const int KeySlot = 68;

    /// <summary>The live case: the mission ends while the player's session is reconnecting into the zone they walked to.</summary>
    [TestMethod]
    public void AKeyInTheInventoryLeavesItWhenTheMissionEndsDuringTheZoneOutReconnect()
    {
        using var rig = new Rig();
        Item key = rig.Stored(QuestDungeonService.KeyLowId, KeyId, rig.Player.Inventory.Inventory, KeySlot,
            (int)IdentityType.Inventory, CharacterId);
        rig.Store.Retire(key);
        rig.Session.State = SessionState.Loading;

        rig.Service.RemoveKeyInMemory(rig.Player, KeyId);

        Assert.IsFalse(rig.Player.Inventory.Inventory.Content.ContainsKey(KeySlot), "the key left the inventory");
        CharacterActionMessage delete = rig.Session.Sent.OfType<CharacterActionMessage>().Single();
        Assert.AreEqual(CharacterActionType.DeleteItem, delete.Action);
        Assert.AreEqual(new Identity { Type = IdentityType.Inventory, Instance = KeySlot }, delete.Target,
            "the client deletes the key from the slot it showed it in");
        rig.Session.State = SessionState.InPlay;
        rig.Flush.HardFlush(rig.Player);
        rig.Store.AssertRetired(key);
        rig.AssertNoFailure();
    }

    /// <summary>A queued move of the inventory key, written before or after the removal, never brings it back.</summary>
    [TestMethod]
    [DataRow(true, DisplayName = "queued move flushed before the removal")]
    [DataRow(false, DisplayName = "queued move still pending at the removal")]
    public void AQueuedMoveOfAnInventoryKeyNeverWritesItBack(bool flushFirst)
    {
        using var rig = new Rig();
        Container inventory = rig.Player.Inventory.Inventory;
        Item key = rig.Stored(QuestDungeonService.KeyLowId, KeyId, inventory, KeySlot, (int)IdentityType.Inventory, CharacterId);
        lock (rig.Player.PersistenceGate)
        {
            inventory.Content.Remove(KeySlot);
            Assert.IsTrue(inventory.Add(KeySlot + 1, key));
            rig.Player.Inventory.MarkDirty(key, inventory, KeySlot + 1);
        }
        rig.Flush.NotifyDirty(rig.Player);
        rig.Store.Retire(key);
        if (flushFirst)
        {
            rig.Flush.HardFlush(rig.Player);
            rig.Store.AssertRow(key, (int)IdentityType.Inventory, CharacterId, KeySlot + 1);
        }

        rig.Service.RemoveKeyInMemory(rig.Player, KeyId);
        rig.Flush.HardFlush(rig.Player);

        Assert.IsFalse(rig.Player.Inventory.EnumerateHeldItems().Any(item => item.InstanceId == KeyId), "the key is held nowhere");
        rig.Store.AssertRetired(key);
        rig.AssertNoFailure();
    }

    /// <summary>
    /// The live case: the only holder finishes. Their row reads active (earned, reward commit queued), but nobody else
    /// still holds the mission, so it ends now and takes the key with it.
    /// </summary>
    [TestMethod]
    public void TheLastFinisherEndsATeamMissionThoughTheirOwnRowStillReadsActive()
    {
        using var rig = new Rig();
        rig.Quests.Active.Add(CharacterId);

        Assert.IsFalse(rig.Service.OtherHoldersKeepItOpen(rig.Player, Quest(QuestState.Completed)));
    }

    /// <summary>Two members inside finish moments apart: each one's earned row reads active while their reward commits.</summary>
    [TestMethod]
    public void TwoFinishersDoNotWaitForEachOther()
    {
        using var rig = new Rig();
        rig.Quests.Active.AddRange([CharacterId, TeammateId]);

        Assert.IsFalse(rig.Service.OtherHoldersKeepItOpen(rig.Player, Quest(QuestState.Completed)),
            "a teammate who has earned it is finishing too; waiting for them would leave both keys behind");
    }

    [TestMethod]
    public void ATeammateStillPlayingKeepsTheMissionOpen()
    {
        using var rig = new Rig();
        rig.Quests.Active.AddRange([CharacterId, TeammateId]);
        rig.Quests.Unearned.Add(TeammateId);

        Assert.IsTrue(rig.Service.OtherHoldersKeepItOpen(rig.Player, Quest(QuestState.Completed)),
            "the teammate's dungeon and key stay until they finish or leave it");
    }

    /// <summary>Dropping out evicts whoever is inside, so a teammate who has earned it and is still collecting keeps it open.</summary>
    [TestMethod]
    public void AbandoningNeverEvictsATeammateWhoIsFinishing()
    {
        using var rig = new Rig();
        rig.Quests.Active.AddRange([CharacterId, TeammateId]);

        Assert.IsTrue(rig.Service.OtherHoldersKeepItOpen(rig.Player, Quest(QuestState.Abandoned)));
    }

    [TestMethod]
    public void TheLastHolderAbandoningEndsIt()
    {
        using var rig = new Rig();
        rig.Quests.Active.Add(CharacterId);

        Assert.IsFalse(rig.Service.OtherHoldersKeepItOpen(rig.Player, Quest(QuestState.Abandoned)));
    }

    static PlayerQuest Quest(QuestState state) => new()
    {
        QuestId = "D51664999", Source = QuestSource.Generated, Template = new QuestTemplate { Scope = "Team" }, State = state
    };

    [TestMethod]
    public void AKeyInAnOpenBagLeavesTheBagWhenTheMissionEnds()
    {
        using var rig = new Rig();
        (Container bag, Item key) = rig.KeyInBag(open: true);
        rig.Store.Retire(key); // EndQuestKeys has committed

        rig.Service.RemoveKeyInMemory(rig.Player, KeyId, BagId);

        Assert.IsFalse(bag.Content.Values.Any(item => item.InstanceId == KeyId), "the key left the bag");
        Assert.IsTrue(rig.Session.Sent.OfType<InventoryUpdateMessage>().Any(), "the open bag window was refreshed");
        rig.Flush.HardFlush(rig.Player);
        rig.Store.AssertRetired(key);
        rig.AssertNoFailure();
    }

    [TestMethod]
    public void AKeyInAClosedLoadedBagLeavesItWithoutAWindowUpdate()
    {
        using var rig = new Rig();
        (Container bag, Item key) = rig.KeyInBag(open: false);
        rig.Store.Retire(key);

        rig.Service.RemoveKeyInMemory(rig.Player, KeyId, BagId);

        Assert.AreEqual(0, bag.Content.Count, "the key left the bag; it is gone when the bag is next opened");
        Assert.IsFalse(rig.Session.Sent.OfType<InventoryUpdateMessage>().Any(), "a closed bag gets no window update");
        rig.Flush.HardFlush(rig.Player);
        rig.Store.AssertRetired(key);
        rig.AssertNoFailure();
    }

    /// <summary>The dead-key cleanup at the entrance names no bag: every loaded bag the player holds is searched.</summary>
    [TestMethod]
    public void AKeyInABagIsFoundWithoutTheBagBeingNamed()
    {
        using var rig = new Rig();
        (Container bag, Item key) = rig.KeyInBag(open: true);
        rig.Store.Retire(key);

        rig.Service.RemoveKeyInMemory(rig.Player, KeyId);

        Assert.AreEqual(0, bag.Content.Count);
        rig.Flush.HardFlush(rig.Player);
        rig.Store.AssertRetired(key);
        rig.AssertNoFailure();
    }

    /// <summary>
    /// The key moved inside the bag just before the mission ended. Whether that queued save is written before the
    /// removal (it lands on the retired row and moves it back into the bag) or is still pending (the removal replaces
    /// it), the row ends retired and the key is nowhere in memory: no resurrected key on relog.
    /// </summary>
    [TestMethod]
    [DataRow(true, DisplayName = "queued move flushed before the removal")]
    [DataRow(false, DisplayName = "queued move still pending at the removal")]
    public void AQueuedMoveOfTheKeyNeverWritesItBackIntoTheBag(bool flushFirst)
    {
        using var rig = new Rig();
        (Container bag, Item key) = rig.KeyInBag(open: true);
        lock (rig.Player.PersistenceGate)
        {
            bag.Content.Remove(BagSlot);
            Assert.IsTrue(bag.Add(BagSlot + 2, key));
            rig.Player.Inventory.MarkDirty(key, bag, BagSlot + 2);
        }
        rig.Flush.NotifyDirty(rig.Player);
        rig.Store.Retire(key); // EndQuestKeys commits while the move is queued

        if (flushFirst)
        {
            rig.Flush.HardFlush(rig.Player);
            rig.Store.AssertRow(key, (int)IdentityType.Container, BagId, BagSlot + 2); // the stale write the fix must undo
        }

        rig.Service.RemoveKeyInMemory(rig.Player, KeyId, BagId);
        rig.Flush.HardFlush(rig.Player);

        Assert.AreEqual(0, bag.Content.Count);
        Assert.IsFalse(rig.Player.Inventory.EnumerateHeldItems().Any(item => item.InstanceId == KeyId), "the key is held nowhere");
        rig.Store.AssertRetired(key);
        rig.AssertNoFailure();
    }

    /// <summary>Only the mission key goes: a non-key item in the same bag, even when named by its instance, stays put.</summary>
    [TestMethod]
    public void OtherItemsInTheBagAreLeftAlone()
    {
        using var rig = new Rig();
        (Container bag, Item key) = rig.KeyInBag(open: true);
        Item other = rig.Stored(7000, KeyId + 1, bag, BagSlot + 1, (int)IdentityType.Container, BagId);
        rig.Store.Retire(key);

        rig.Service.RemoveKeyInMemory(rig.Player, KeyId + 1, BagId);
        rig.Service.RemoveKeyInMemory(rig.Player, KeyId, BagId);
        rig.Flush.HardFlush(rig.Player);

        Assert.AreSame(other, bag.Content.Values.Single(), "a non-key item is never removed as a key");
        rig.Store.AssertRow(other, (int)IdentityType.Container, BagId, BagSlot + 1);
        rig.Store.AssertRetired(key);
        rig.AssertNoFailure();
    }

    sealed class Rig : IDisposable
    {
        internal readonly Rows Store = new();
        internal readonly QuestRows Quests = new();
        internal readonly Logger Log = new();
        internal readonly Session Session = new();
        internal readonly Player Player;
        internal readonly InventoryFlushService Flush;
        internal readonly QuestDungeonService Service;
        readonly Items _items = new();

        internal Rig()
        {
            string root = GameDataPaths.ResolveRuntimeRoot();
            Player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = CharacterId }, Log, _items) { FirstName = "Keyholder" };
            Player.Inventory.Apply(new CharacterHydrationResult(), CharacterId, _items);
            Player.EnterOnline(Session);
            Player.Playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));

            Flush = new InventoryFlushService(_ => null, () => [Player], Store, Log);
            var ids = new Ids();
            var actions = new InventoryActionService(Store, Flush, ids, Log);
            var data = new GameDataStore(Log);
            var manager = (PlayfieldManager)RuntimeHelpers.GetUninitializedObject(typeof(PlayfieldManager));
            var quests = new QuestService(new QuestCatalog(data, Log), Quests, _items, Flush, Log);
            Service = new QuestDungeonService(Quests, quests, MissionDestinationCatalog.Load(root),
                new DungeonLayoutGenerator(data, Log), _items, ids, Flush, actions,
                new Lazy<PlayfieldManager>(() => manager), data, Log);
            Log.Reset();
        }

        /// <summary>A bag in the player's inventory, loaded this session, holding one stored mission key.</summary>
        internal (Container Bag, Item Key) KeyInBag(bool open)
        {
            Item bagItem = Stored(7100, BagId, Player.Inventory.Inventory, 64, (int)IdentityType.Inventory, CharacterId);
            bagItem.Identity = new Identity { Type = IdentityType.Container, Instance = BagId };
            Container bag = Player.Inventory.GetOrCreateBackpackPage(bagItem, bagItem.Identity,
                new Identity { Type = IdentityType.Inventory, Instance = 64 });
            bag.IsHydrated = true;
            if (open)
            {
                bag.IsOpen = true;
                bag.InventoryHandle = Player.Inventory.AllocateClientContainerHandle();
                Player.Inventory.RegisterBackpackHandle(bag.InventoryHandle, bag.Identity);
            }

            Item key = Stored(QuestDungeonService.KeyLowId, KeyId, bag, BagSlot, (int)IdentityType.Container, BagId);
            return (bag, key);
        }

        internal Item Stored(int lowId, int instanceId, Container page, int slot, int rowType, int rowOwner)
        {
            Item item = _items.Create(lowId, lowId, 1, ItemSource.Quest, instanceId: instanceId);
            item.IsPersisted = true;
            Assert.IsTrue(page.Add(slot, item));
            Store.Seed(item, rowType, rowOwner, slot);
            return item;
        }

        internal void AssertNoFailure()
        {
            Assert.AreEqual(string.Empty, Log.Problems);
            Assert.IsFalse(Player.IsPersistenceQuarantined);
        }

        public void Dispose()
        {
            Service.Dispose();
            Flush.Dispose();
        }
    }

    /// <summary>item_instances as the MySQL DAO writes it; a retired row is re-homed to (0, 0, its instance id).</summary>
    sealed class Rows : ICharacterCoalesceCommit, IInventoryMutationPersistence
    {
        readonly object _sync = new();
        readonly Dictionary<int, (int Type, int Owner, int Slot)> _rows = new();

        internal void Seed(Item item, int type, int owner, int slot)
        {
            lock (_sync)
                _rows.Add(item.InstanceId, (type, owner, slot));
        }

        /// <summary>What EndQuestKeys and RetireUnlinkedKeys write.</summary>
        internal void Retire(Item item)
        {
            lock (_sync)
                _rows[item.InstanceId] = (0, 0, item.InstanceId);
        }

        internal void AssertRow(Item item, int type, int owner, int slot)
        {
            lock (_sync)
            {
                Assert.IsTrue(_rows.TryGetValue(item.InstanceId, out var row), "No row for item " + item.InstanceId + ".");
                Assert.AreEqual((type, owner, slot), row, "Row of item " + item.InstanceId);
            }
        }

        internal void AssertRetired(Item item) => AssertRow(item, 0, 0, item.InstanceId);

        public void Persist(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates, int characterId,
            IReadOnlyList<int> uploadedNanoIds, IReadOnlyList<ActiveNanoRecord>? activeNanos, IReadOnlyList<SkillLockRecord>? skillLocks)
            => Write(inserts, updates);

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

    /// <summary>
    /// characterquests holders of the one team mission: <see cref="Active"/> rows with State=1, of which
    /// <see cref="Unearned"/> have not earned the objective. Key removal reaches no other part of the store: it follows
    /// a retirement already committed.
    /// </summary>
    sealed class QuestRows : ICharacterQuestStore
    {
        internal readonly List<int> Active = [];
        internal readonly List<int> Unearned = [];

        public IList<int> LoadActiveQuestHolders(string questId) => Active.ToArray();
        public IList<int> LoadUnearnedQuestHolders(string questId)
        {
            CollectionAssert.IsSubsetOf(Unearned, Active, "an unearned holder is an active one");
            return Unearned.ToArray();
        }
        public IList<string> LoadExpiredQuestsWithKeys(long nowUtcTicks, int limit) => [];
        public GeneratedQuestRow? LoadGenerated(string questId) => null;
        public IList<RetiredDungeonKey> EndQuestKeys(string questId) => [];
        public void CloseCharacterQuests(string questId, int state, long nowUtcTicks) { }
        public int PurgeEndedGeneratedQuests(long cutoffUtcTicks, int limit) => 0;
        public bool TryAssignDungeonQuest(int ownerId, GeneratedQuestRow quest, CharacterQuestRow characterRow,
            QuestDungeonKeyItemRow key, QuestDungeonKeyItemRow? sourceItem) => throw new AssertFailedException();
        public IList<CharacterQuestRow> Load(int characterId) => [];
        public IList<GeneratedQuestRow> LoadGeneratedFor(int characterId) => [];
        public void Save(CharacterQuestRow row) { }
        public void SaveGenerated(GeneratedQuestRow row) { }
        public bool TryAcceptOfferQuest(int ownerId, int offerType, int offerInstance, GeneratedQuestRow quest,
            CharacterQuestRow characterRow, QuestDungeonKeyItemRow key, long nowUtcTicks) => throw new AssertFailedException();
        public string? TryAddDuplicateKey(int sourceKeyInstanceId, QuestDungeonKeyItemRow copy, int maxKeys, long nowUtcTicks) => null;
        public IDictionary<int, string> LoadKeyQuests(IReadOnlyCollection<int> keyInstanceIds) => new Dictionary<int, string>();
        public IList<int> RetireUnlinkedKeys(IReadOnlyCollection<int> keyInstanceIds, int keyLowId) => [];
        public int RetireDeadKeysForCharacter(int characterId, int keyLowId, IReadOnlyCollection<int> ownedContainerTypes, int bagContainerType) => 0;
    }

    sealed class Logger : IZoneLogger
    {
        readonly object _sync = new();
        string _problems = string.Empty;

        internal string Problems { get { lock (_sync) return _problems; } }
        internal void Reset() { lock (_sync) _problems = string.Empty; }

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
        int _next = 51664700;

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
        int _next = 51664800;
        public int Allocate() => Interlocked.Increment(ref _next);
    }
}
