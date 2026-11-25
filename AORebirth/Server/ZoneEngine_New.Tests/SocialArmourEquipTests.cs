namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Social armour (battle suits such as 88069, Grid Armor 155173) is worn alone on the armor page, as the client rules it
/// (Gamecode.dll FUN_10081b3c and FUN_100808dc). The server held no such rule, so a move the client never checked
/// (a crafted ClientMoveItemToInventory) stacked a suit's whole-body OnWear stats on a full armor set. Each test drives
/// the engine's move handler the way a client request reaches it.
/// </summary>
[TestClass]
public sealed class SocialArmourEquipTests
{
    const int PlayerId = 52400001;
    const int SuitSlot = 3;
    const int SleeveSlot = 1;

    [TestMethod]
    public void ArmorCannotGoOnOverAWornSocialArmourSuit()
    {
        using var rig = new Rig();
        Item suit = rig.Armor(64, 52410001, SuitSlot, socialArmour: true);
        Item sleeves = rig.Armor(65, 52410002, SleeveSlot);
        rig.Equip(64, SuitSlot);
        Assert.AreSame(suit, rig.WornArmor(SuitSlot));

        rig.Equip(65, SleeveSlot);

        Assert.IsNull(rig.WornArmor(SleeveSlot), "the sleeves are refused while the suit is worn");
        Assert.AreSame(sleeves, rig.Player.Inventory.Inventory.Content[65], "the sleeves stay in the inventory");
        Assert.IsFalse(sleeves.Locked);
        rig.AssertNoFailure();
    }

    [TestMethod]
    public void SocialArmourSuitCannotGoOnOverOtherWornArmor()
    {
        using var rig = new Rig();
        Item sleeves = rig.Armor(65, 52410003, SleeveSlot);
        Item suit = rig.Armor(64, 52410004, SuitSlot, socialArmour: true);
        rig.Equip(65, SleeveSlot);
        Assert.AreSame(sleeves, rig.WornArmor(SleeveSlot));

        rig.Equip(64, SuitSlot);

        Assert.IsNull(rig.WornArmor(SuitSlot), "the suit is refused over other armor");
        Assert.AreSame(suit, rig.Player.Inventory.Inventory.Content[64], "the suit stays in the inventory");
        Assert.IsFalse(suit.Locked);
        rig.AssertNoFailure();
    }

    [TestMethod]
    public void CanBeWornWithSocialArmorItemsGoOnEitherSideOfTheSuit()
    {
        using var rig = new Rig();
        Item ring = rig.Armor(65, 52410005, SleeveSlot, wornWithSocialArmour: true);
        Item suit = rig.Armor(64, 52410006, SuitSlot, socialArmour: true);
        rig.Equip(65, SleeveSlot);
        rig.Equip(64, SuitSlot);
        Assert.AreSame(ring, rig.WornArmor(SleeveSlot));
        Assert.AreSame(suit, rig.WornArmor(SuitSlot), "the suit goes on over an item allowed beside it");

        rig.Unequip(SleeveSlot, 70);
        Assert.AreSame(ring, rig.Player.Inventory.Inventory.Content[70]);
        rig.Equip(70, SleeveSlot);
        Assert.AreSame(ring, rig.WornArmor(SleeveSlot), "and that item goes back on over the suit");
        rig.AssertNoFailure();
    }

    [TestMethod]
    public void SwappingTheSuitForArmorInItsSlotIsAllowed()
    {
        using var rig = new Rig();
        Item suit = rig.Armor(64, 52410007, SuitSlot, socialArmour: true);
        Item back = rig.Armor(65, 52410008, SuitSlot);
        rig.Equip(64, SuitSlot);

        rig.Equip(65, SuitSlot);

        Assert.AreSame(back, rig.WornArmor(SuitSlot), "the suit being taken off does not block what replaces it");
        Assert.IsTrue(rig.Player.Inventory.Inventory.Content.ContainsValue(suit), "the suit came back to the inventory");
        Assert.IsFalse(suit.Locked);
        Assert.IsFalse(back.Locked);
        rig.AssertNoFailure();
    }

    [TestMethod]
    public void TakingTheSuitOffLetsArmorGoOnAgain()
    {
        using var rig = new Rig();
        rig.Armor(64, 52410009, SuitSlot, socialArmour: true);
        Item sleeves = rig.Armor(65, 52410010, SleeveSlot);
        rig.Equip(64, SuitSlot);
        rig.Unequip(SuitSlot, 70);

        rig.Equip(65, SleeveSlot);

        Assert.AreSame(sleeves, rig.WornArmor(SleeveSlot));
        rig.AssertNoFailure();
    }

    [TestMethod]
    public void WeaponsAndSocialClothingStillGoOnWithTheSuit()
    {
        using var rig = new Rig();
        Item suit = rig.Armor(64, 52410011, SuitSlot, socialArmour: true);
        rig.Equip(64, SuitSlot);

        // The client checks weapons and utilities through the wield path, which has no social armour rule.
        Item hud = rig.Add(65, 52410012, ItemClass.Weapon, 1 << (int)WeaponSlots.Hud1, CanFlags.Wear);
        rig.Move(Inventory(65), rig.Player.Inventory.Equipment.Offset + (int)WeaponSlots.Hud1 - 1);
        rig.Tick(1.0);
        Assert.AreSame(hud, rig.Player.Inventory.Equipment.Content[rig.Player.Inventory.Equipment.Offset + (int)WeaponSlots.Hud1 - 1]);

        // The social page passes the rule by (the client's social flag).
        Item shirt = rig.Add(66, 52410013, ItemClass.Armor, 1 << SuitSlot, CanFlags.Wear);
        int socialSlot = rig.Player.Inventory.Social.Offset + SuitSlot - 1;
        rig.Move(Inventory(66), socialSlot);
        rig.Tick(1.0);
        Assert.AreSame(shirt, rig.Player.Inventory.Social.Content[socialSlot]);
        Assert.AreSame(suit, rig.WornArmor(SuitSlot));
        rig.AssertNoFailure();
    }

    [TestMethod]
    public void RetailSuitsCarryTheSocialArmourFlagAndBlockOtherArmor()
    {
        var runtime = new GameDataStore(new Logger());
        if (!File.Exists(Path.Combine(runtime.RootPath, "items.dat")))
            Assert.Inconclusive("Needs the runtime GameData items.dat (AO_REBIRTH_GAMEDATA_PATH).");
        var catalog = new ItemTemplateCatalog(runtime, new Logger());
        const int BattleSuit = 88069, GridArmor = 155173, NanoSleeves = 21793, NcuHacker = 158416, PressCard = 159892;
        foreach (int id in new[] { BattleSuit, GridArmor, NanoSleeves, NcuHacker, PressCard })
        {
            if (!catalog.TryGet(id, out _))
                Assert.Inconclusive("The runtime items.dat lacks " + id + ".");
        }

        Item Real(int id)
        {
            Assert.IsTrue(catalog.TryGet(id, out ItemTemplate template));
            return new Item { LowId = id, HighId = id, Quality = template.Quality, Source = ItemSource.Loot, Identity = Identity.None, Definition = template };
        }

        Assert.IsTrue(SocialArmour.IsSocialArmour(Real(BattleSuit)), "88069 is social armour");
        Assert.IsTrue(SocialArmour.IsSocialArmour(Real(GridArmor)), "155173 is social armour");
        Assert.IsFalse(SocialArmour.IsSocialArmour(Real(NanoSleeves)));
        Assert.IsTrue(Real(PressCard).Can(CanFlags.CanBeWornWithSocialArmor), "159892 is allowed beside social armour");

        using var rig = new Rig();
        PlayerInventory inventory = rig.Player.Inventory;
        Item sleeves = Real(NanoSleeves);
        Assert.IsTrue(inventory.Armor.Add(inventory.Armor.Offset + SleeveSlot - 1, sleeves));
        Assert.IsFalse(SocialArmour.Allows(inventory, Real(GridArmor), inventory.Armor, null), "Grid Armor over sleeves");
        Assert.AreSame(sleeves, inventory.Armor.Remove(inventory.Armor.Offset + SleeveSlot - 1));

        Assert.IsTrue(inventory.Armor.Add(inventory.Armor.Offset + SuitSlot - 1, Real(BattleSuit)));
        Assert.IsFalse(SocialArmour.Allows(inventory, sleeves, inventory.Armor, null), "sleeves over a battle suit");
        Assert.IsTrue(SocialArmour.Allows(inventory, Real(PressCard), inventory.Armor, null), "a press card over a battle suit");
        Assert.IsTrue(SocialArmour.Allows(inventory, Real(NcuHacker), inventory.Equipment, null), "the weapon page is not ruled");
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
            Player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = PlayerId }, Log, _items) { FirstName = "Suited" };
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

        void SetField(string name, object value)
            => typeof(Playfield).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Playfield, value);

        int ArmorSlot(int relative) => Player.Inventory.Armor.Offset + relative - 1;

        internal Item? WornArmor(int relative) => Player.Inventory.Armor.Content.GetValueOrDefault(ArmorSlot(relative));

        /// <summary>Armor for armor-page slot <paramref name="relative"/>, in inventory slot <paramref name="slot"/>.</summary>
        internal Item Armor(int slot, int instanceId, int relative, bool socialArmour = false, bool wornWithSocialArmour = false)
        {
            CanFlags can = CanFlags.Wear | (wornWithSocialArmour ? CanFlags.CanBeWornWithSocialArmor : 0);
            return Add(slot, instanceId, ItemClass.Armor, 1 << relative, can,
                socialArmour ? (int)ItemFlags.ItemSocialArmour : 0);
        }

        internal Item Add(int slot, int instanceId, ItemClass itemClass, int slotMask, CanFlags can, int flags = 0)
        {
            int lowId = 5400 + slot;
            _items.Flags[lowId] = flags;
            Item item = _items.Create(lowId, lowId, 50, ItemSource.Loot, instanceId: instanceId);
            item.Definition.Stats[CharacterStat.ItemClass] = (int)itemClass;
            item.Definition.Stats[CharacterStat.Slot] = slotMask;
            item.Definition.Stats[CharacterStat.Can] = unchecked((int)can);
            item.Definition.Stats[CharacterStat.EquipDelay] = 50;
            item.IsPersisted = true;
            Assert.IsTrue(Player.Inventory.Inventory.Add(slot, item));
            _store.Seed(item.InstanceId);
            return item;
        }

        internal void Equip(int inventorySlot, int relative)
        {
            Move(Inventory(inventorySlot), ArmorSlot(relative));
            Tick(1.0);
        }

        internal void Unequip(int relative, int inventorySlot)
        {
            Move(new Identity { Type = IdentityType.ArmorPage, Instance = ArmorSlot(relative) }, inventorySlot);
            Tick(1.0);
        }

        internal void Move(Identity source, int targetPlacement)
            => Moves.Handle(Player, new ClientMoveItemToInventory
            {
                Identity = Player.Identity, SourceContainer = source, Slot = targetPlacement
            });

        internal void Tick(double seconds) => Moves.Tick(Playfield, seconds);

        internal void AssertNoFailure()
        {
            Assert.AreEqual(string.Empty, Log.Problems);
            Assert.IsFalse(Player.IsPersistenceQuarantined);
            Assert.IsFalse(Moves.HasPending(PlayerId), "no equip is left pending");
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
        internal readonly Dictionary<int, int> Flags = new();
        int _next = 51422700;

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
            => new() { Id = lowId, Quality = quality, Flags = Flags.GetValueOrDefault(lowId) };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }

    sealed class Ids : IItemInstanceIdAllocator
    {
        int _next = 51430000;
        public int Allocate() => Interlocked.Increment(ref _next);
    }
}
