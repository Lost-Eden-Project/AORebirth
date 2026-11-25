#nullable enable

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
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;

using Vector3 = AORebirth.Core.Vector.Vector3;

namespace ZoneEngine_New.Tests;

/// <summary>
/// Sigma Tuning Unit - Inactive (284434): OnUse SpawnItem SITU at QL 1 into the inventory, then DestroyItem. SITU holds
/// only the QL 200 Sigma Tuning Unit (284300), so the highest-QL-up-to roll found nothing: no item, and the DestroyItem
/// after it still ate the inactive unit. A hash with one template now spawns that template at its own QL; a hash with
/// more (the Clan Merits boards' low/high pair) still makes nothing below its range.
/// </summary>
[TestClass]
public sealed class SpawnItemOnlyTemplateTests
{
    const int Inactive = 284434;
    const int Active = 284300;
    const int CharacterId = 7401;
    const int Placement = 64;

    [TestMethod]
    public void UsingTheInactiveUnitGivesTheActiveUnitAndUsesItUp()
    {
        using var rig = new Rig("""{ "SITU": { "Templates": [284300] } }""", (Active, 200));
        rig.AddUnit("SITU");

        Assert.AreEqual(ItemUseStart.Executed, rig.UseSlot(), rig.Log.Problems);

        Assert.IsFalse(rig.Carried().Any(item => item.LowId == Inactive), "the inactive unit is used up");
        Item granted = rig.Carried().Single(item => item.LowId == Active);
        Assert.AreEqual(Active, granted.HighId);
        Assert.AreEqual(200, granted.Quality);
    }

    [TestMethod]
    public void TheUsedUpUnitCannotBeUsedAgainForASecondActiveUnit()
    {
        using var rig = new Rig("""{ "SITU": { "Templates": [284300] } }""", (Active, 200));
        rig.AddUnit("SITU");
        Assert.AreEqual(ItemUseStart.Executed, rig.UseSlot(), rig.Log.Problems);

        // A second Use on the same slot, the way GenericCmdMessageHandler resolves it: the slot is empty now.
        Assert.IsNull(rig.UseSlot());

        Assert.AreEqual(1, rig.Carried().Count(item => item.LowId == Active), "one inactive unit, one active unit");
    }

    [TestMethod]
    public void AHashWithALowHighPairMintsNothingBelowItsRange()
    {
        // Clan Merits CLMR holds only the QL 7 Sunrise board pair; a QL 2 board must not jump straight to it.
        HashItemMinter minter = Minter("""{ "CLMR": { "Templates": [96356, 96357] } }""", (96356, 7), (96357, 7));
        var minted = new List<Item>();

        minter.MintHighestUpToSpawns("CLMR", 2, ItemSource.Other, minted);

        Assert.AreEqual(0, minted.Count);
    }

    [TestMethod]
    public void ARequestAboveTheOnlyTemplateStillMintsItAtItsOwnQuality()
    {
        HashItemMinter minter = Minter("""{ "SITU": { "Templates": [284300] } }""", (Active, 200));
        var minted = new List<Item>();

        minter.MintHighestUpToSpawns("SITU", 250, ItemSource.Other, minted);

        Assert.AreEqual(200, minted.Single().Quality);
    }

    static Identity Slot => new() { Type = IdentityType.Inventory, Instance = Placement };

    static HashItemMinter Minter(string hashes, params (int Id, int Quality)[] templates)
        => new(HashData(HashItemCatalog.Parse(hashes)), new Templates(templates), new Items());

    /// <summary>The hash part of <see cref="IGameData"/>, served from <paramref name="catalog"/>; anything else fails the test.</summary>
    static IGameData HashData(HashItemCatalog catalog) => Fake<IGameData>((method, args) =>
    {
        switch (method.Name)
        {
            case nameof(IGameData.IsSpawnAllItemHash):
                return false;
            case nameof(IGameData.CanResolveItemHash):
                return catalog.CanResolveItem((string)args![0]!);
            case nameof(IGameData.CollectHashLeafInstances):
                catalog.CollectLeafInstances((string)args![0]!, (List<HashInstance>)args[1]!);
                return null;
            case nameof(IGameData.TryResolveHashInstance):
            {
                bool found = catalog.TryResolveInstance((string)args![0]!, out HashInstance instance);
                args[1] = instance;
                return found;
            }
            default:
                throw new AssertFailedException("Unexpected IGameData call " + method.Name);
        }
    });

    sealed class Rig : IDisposable
    {
        internal readonly Player Player;
        internal readonly Logger Log = new();
        internal readonly ItemUseService Uses;
        readonly Session _session = new();
        readonly Playfield _world = Blank<Playfield>();
        readonly InventoryFlushService _flush;
        readonly ServiceProvider _services;

        internal Rig(string hashes, params (int Id, int Quality)[] templates)
        {
            var items = new Items();
            var catalog = new Templates(templates);
            IGameData data = HashData(HashItemCatalog.Parse(hashes));

            Player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = CharacterId }, Log, items) { FirstName = "Tuner" };
            Player.Inventory.Apply(new CharacterHydrationResult(), CharacterId, items);
            Player.Stats.Set(CharacterStat.Level, 200);
            Player.Stats.Set(CharacterStat.Health, 100);
            Player.Stats.Set(CharacterStat.MaxHealth, 100);
            Player.EnterOnline(_session);

            var registry = new DynelRegistry();
            Set(_world, "<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = 4582 });
            Set(_world, "_tickSync", new Lock());
            Set(_world, "_lifecycleSync", new Lock());
            Set(_world, "_dynelRegistry", registry);
            Set(_world, "_logger", Log);
            Player.Playfield = _world;
            Player.Position = new Vector3(100f, 10f, 100f);
            registry.Register(Player);

            var persistence = new Persistence();
            _flush = new InventoryFlushService(id => id == CharacterId ? Player : null, () => [Player], persistence, Log);
            var moves = new InventoryMoveService(Log, _flush, new InventoryActionService(persistence, _flush, new Ids(), Log));
            Uses = new ItemUseService(_world, Log, moves, Fake<IInventoryRepository>(), items);
            _services = new ServiceCollection()
                .AddSingleton(registry)
                .AddSingleton(new ZoneEngine_New.Core.Playfield.Locality.PlayfieldLocality(4582, null))
                .AddSingleton(_flush)
                .AddSingleton(moves)
                .AddSingleton(Uses)
                .AddSingleton(data)
                .AddSingleton<IItemTemplateCatalog>(catalog)
                .AddSingleton(new HashItemMinter(data, catalog, items))
                .BuildServiceProvider();
            Set(_world, "_serviceProvider", _services);
        }

        /// <summary>The inactive unit as items.dat has it: SpawnItem hash, QL 1, min 0, TimeExist 0, Flags 3, then DestroyItem.</summary>
        internal Item AddUnit(string hash)
        {
            var template = new ItemTemplate
            {
                Id = Inactive,
                Name = "Sigma Tuning Unit - Inactive",
                Quality = 200,
                Stats = new Dictionary<CharacterStat, int> { [CharacterStat.Can] = (int)CanFlags.Use },
                SpellList = new Dictionary<EventType, List<ItemSpell>>
                {
                    [EventType.OnUse] =
                    [
                        new ItemSpell
                        {
                            FunctionType = (int)FunctionType.SpawnItem, Target = (int)ItemTarget.User,
                            Arguments = [hash, 1, 0, 0, 3, 0, 0, 0]
                        },
                        new ItemSpell { FunctionType = (int)FunctionType.DestroyItem, Target = (int)ItemTarget.User, Arguments = [] }
                    ]
                }
            };
            var unit = new Item { LowId = Inactive, HighId = Inactive, Quality = 200, StackCount = 1, Definition = template };
            unit.AssignInstanceId(650001);
            Assert.IsTrue(Player.Inventory.Inventory.Add(Placement, unit));
            return unit;
        }

        /// <summary>A client Use on the unit's slot: the item is looked up there first, as GenericCmdMessageHandler does; null when the slot is empty.</summary>
        internal ItemUseStart? UseSlot()
            => Player.Inventory.TryGetUseItem(Slot, out Item item) ? Uses.TryBegin(Player, Slot, item) : null;

        internal IEnumerable<Item> Carried()
            => Player.Inventory.Inventory.Content.Values.Concat(Player.Inventory.Overflow.Content.Values);

        public void Dispose()
        {
            _flush.Dispose();
            _services.Dispose();
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
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public bool TransferToPlayfield(Playfield destination, Vector3 landing) => false;
        public void Send(byte[] packet) { }
        public void Send(MessageBody body) { }
        public void Send(MessageBody body, int sender, int receiver) { }
        public void SendInitiateCompression() { }
        public void Close() => State = SessionState.Closed;
    }

    sealed class Persistence : ICharacterCoalesceCommit, IInventoryMutationPersistence
    {
        public void Persist(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates, int characterId,
            IReadOnlyList<int> uploadedNanoIds, IReadOnlyList<ActiveNanoRecord>? activeNanos, IReadOnlyList<SkillLockRecord>? skillLocks) { }
        public void Persist(InventoryMutationBatch batch) { }
    }

    sealed class Items : IItemBuilder
    {
        int _next = 700000;

        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null) => new()
            {
                LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount,
                InstanceId = instanceId, Identity = identity ?? Identity.None, Definition = CreateTemplate(lowId, highId, quality)
            };

        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
        {
            Item item = Create(lowId, highId, quality, source, stackCount);
            item.AssignInstanceId(++_next);
            return item;
        }

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }

    sealed class Templates((int Id, int Quality)[] templates) : IItemTemplateCatalog
    {
        readonly Dictionary<int, ItemTemplate> _byId = templates.ToDictionary(t => t.Id, t => new ItemTemplate { Id = t.Id, Quality = t.Quality });

        public bool TryGet(int aoid, out ItemTemplate template) => _byId.TryGetValue(aoid, out template!);

        public ItemTemplate Require(int aoid) => _byId[aoid];
    }

    sealed class Ids : IItemInstanceIdAllocator
    {
        int _next = 800000;
        public int Allocate() => Interlocked.Increment(ref _next);
    }

    /// <summary>An interface stub: every call goes to <see cref="Handler"/>, or fails the test when there is none.</summary>
    public class FakeProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?>? Handler;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Handler != null ? Handler(targetMethod!, args) : throw new AssertFailedException("Unexpected call " + targetMethod!.Name);
    }

    static T Fake<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        T proxy = DispatchProxy.Create<T, FakeProxy>();
        ((FakeProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    static T Blank<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    static void Set(object target, string name, object value) => Field(target.GetType(), name).SetValue(target, value);

    static FieldInfo Field(Type type, string name)
    {
        for (Type? at = type; at != null; at = at.BaseType)
            if (at.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) is FieldInfo field)
                return field;
        throw new MissingFieldException(type.Name, name);
    }
}
