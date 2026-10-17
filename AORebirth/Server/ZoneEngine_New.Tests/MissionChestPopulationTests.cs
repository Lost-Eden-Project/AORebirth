namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AORebirth.Core.GameData;
using AORebirth.Interfaces.Persistence.Missions;
using AORebirth.World.Collision;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SmokeLounge.AOtomation.Messaging.GameData;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.Quests.Dungeons;
using ZoneEngine_New.Core.WorldSimulation;

[TestClass]
public sealed class MissionChestPopulationTests
{
    const string Quest = "chest.population.test";
    const long Assigned = 12345;

    [TestMethod]
    public void ReconstructionUsesFrozenIdentityAppearanceAndOnlyRemainingDurableItems()
    {
        var dao = new AuthoredMissionTestDao();
        Seed(dao);
        using var first = new World();
        first.Inventory.Rows.Add(ItemRow(7001, 0));
        first.Inventory.Rows.Add(ItemRow(7002, 2));
        Population(dao).Populate(first.Playfield, first.Layout, first.Simulation);
        Chest chest = first.Registry.Dynels().OfType<Chest>().Single();
        Assert.AreEqual(7000, chest.Identity.Instance);
        Assert.AreEqual(17d, chest.Position.x);
        Assert.AreEqual(0.75, chest.GetCollisionRadius());
        CollectionAssert.AreEquivalent(new[] { 7001, 7002 }, chest.Loot.Content.Values.Select(item => item.InstanceId).ToArray());
        Assert.IsTrue(chest.Loot.Content.Values.All(item => item.IsPersisted));

        // A transfer already committed. A new materialization reads row ownership, not the initial snapshot.
        using var restarted = new World();
        restarted.Inventory.Rows.Add(ItemRow(7002, 2));
        Population(dao).Populate(restarted.Playfield, restarted.Layout, restarted.Simulation);
        Chest restored = restarted.Registry.Dynels().OfType<Chest>().Single();
        Assert.AreEqual(chest.Identity, restored.Identity);
        Assert.AreEqual(chest.Position.x, restored.Position.x);
        Assert.AreEqual(chest.Position.z, restored.Position.z);
        Assert.AreEqual(7002, restored.Loot.Content.Single().Value.InstanceId);
        Assert.AreEqual(2, restored.Loot.Content.Single().Key);
        Assert.AreEqual(0, dao.Calls, "A sealed population must never initialize again.");
    }

    [TestMethod]
    public void ExhaustedChestReconstructsEmptyAndNeverRerolls()
    {
        var dao = new AuthoredMissionTestDao();
        Seed(dao);
        using var world = new World();
        Population(dao).Populate(world.Playfield, world.Layout, world.Simulation);
        Assert.AreEqual(0, world.Registry.Dynels().OfType<Chest>().Single().Loot.Content.Count);
        Assert.AreEqual(0, dao.Calls);
    }

    [TestMethod]
    public void LostCommitReplyRecoversFromTheDurablePopulationSealWithoutRetrying()
    {
        var dao = new AuthoredMissionTestDao { UnknownCommit = true };
        using var world = new World();
        Population(dao, EmptyPolicy()).Populate(world.Playfield, world.Layout, world.Simulation);
        Assert.AreEqual(1, dao.Calls);
        Assert.AreEqual("0", dao.ReadCharacter(111).Flags.Single().Value);
        dao.UnknownCommit = false;
        Population(dao, EmptyPolicy()).Populate(world.Playfield, world.Layout, world.Simulation);
        Assert.AreEqual(1, dao.Calls, "A committed seal must survive loss of its acknowledgement.");
        Assert.AreEqual(0, world.Registry.Dynels().Count());
    }

    [TestMethod]
    public void KnownRollbackExposesNoChestAndAllowsAnExplicitLaterInitialization()
    {
        var dao = new AuthoredMissionTestDao { Failure = new InvalidOperationException("rollback") };
        using var world = new World();
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            Population(dao, EmptyPolicy()).Populate(world.Playfield, world.Layout, world.Simulation));
        Assert.AreEqual(0, dao.Flags.Count);
        Assert.AreEqual(0, world.Registry.Dynels().Count());
        dao.Failure = null;
        Population(dao, EmptyPolicy()).Populate(world.Playfield, world.Layout, world.Simulation);
        Assert.AreEqual(2, dao.Calls);
        Assert.AreEqual(1, dao.Flags.Count);
    }

    [TestMethod]
    [DataRow("surface")]
    [DataRow("player-sphere")]
    [DataRow("missing-placement")]
    [DataRow("invalid-placement")]
    public void MissingPlacementPrerequisitesDoNotSealAndCanBeRetried(string missing)
    {
        var dao = new AuthoredMissionTestDao();
        MissionChestPolicy policy = PositivePolicy();
        if (missing == "missing-placement")
            policy.Placement = null;
        if (missing == "invalid-placement")
            policy.Placement!.WalkSampleSpacing = 0;
        using (var unavailable = new World(withSurface: missing != "surface", withPlayerSphere: missing != "player-sphere"))
        {
            Population(dao, policy).Populate(unavailable.Playfield, unavailable.Layout, unavailable.Simulation);
            Assert.AreEqual(0, dao.Calls, "Unavailable placement inputs must not start population persistence.");
            Assert.AreEqual(0, dao.Flags.Count, "A missing prerequisite must not permanently seal an empty population.");
            Assert.AreEqual(0, unavailable.Registry.Dynels().Count());
        }

        using var repaired = new World(withSurface: true, withPlayerSphere: true);
        // This valid one-room layout rounds the configured 30% target to zero. It still proves that
        // repairing the prerequisites permits initialization of the same previously unsealed run.
        Population(dao, PositivePolicy()).Populate(repaired.Playfield, repaired.Layout, repaired.Simulation);
        Assert.AreEqual(1, dao.Calls);
        Assert.AreEqual("0", dao.ReadCharacter(111).Flags.Single().Value);
    }

    [TestMethod]
    public void ExplicitZeroDensityCanSealWithoutPlacementPrerequisites()
    {
        var dao = new AuthoredMissionTestDao();
        using var world = new World();
        Assert.IsNull(world.Simulation.Surface);
        Population(dao, EmptyPolicy()).Populate(world.Playfield, world.Layout, world.Simulation);
        Assert.AreEqual(1, dao.Calls);
        Assert.AreEqual("0", dao.ReadCharacter(111).Flags.Single().Value);
    }

    [TestMethod]
    public void MalformedUnsealedAndIncompletePopulationsFailBeforeWorldPublication()
    {
        foreach (bool includeSeal in new[] { false, true })
        {
            var dao = new AuthoredMissionTestDao();
            if (includeSeal)
                AddFlag(dao, MissionChestPopulationFlags.Seal(Assigned), "2");
            AddFlag(dao, MissionChestPopulationFlags.Chest(Assigned, 0), JsonSerializer.Serialize(Snapshot()));
            using var world = new World();
            Assert.ThrowsExactly<InvalidDataException>(() => Population(dao).Populate(world.Playfield, world.Layout, world.Simulation));
            Assert.AreEqual(0, world.Registry.Dynels().Count());
            Assert.AreEqual(0, dao.Calls);
        }
    }

    [TestMethod]
    public void ChangedStoredQualityAndCollidingWorldIdentityFailClosed()
    {
        var dao = new AuthoredMissionTestDao();
        Seed(dao);
        using var invalid = new World();
        invalid.Inventory.Rows.Add(ItemRow(7001, 0, quality: 200));
        Assert.ThrowsExactly<InvalidDataException>(() => Population(dao).Populate(invalid.Playfield, invalid.Layout, invalid.Simulation));
        Assert.AreEqual(0, invalid.Registry.Dynels().Count());

        using var collision = new World();
        var existing = new Dynel(new Identity { Type = IdentityType.Container, Instance = 7000 });
        collision.Registry.Register(existing);
        Assert.ThrowsExactly<InvalidDataException>(() => Population(dao).Populate(collision.Playfield, collision.Layout, collision.Simulation));
        Assert.AreSame(existing, collision.Registry.Dynels().Single());
    }

    static MissionChestPopulation Population(AuthoredMissionTestDao dao, MissionChestPolicy? policy = null)
        => new(dao, 111, Quest, Assigned, "frozen-layout", new QuestDungeonParameters { Quality = 50 },
            policy, new StubItemBuilder(), new StubLogger());

    static MissionChestPolicy EmptyPolicy() => new()
    {
        RoomDensity = 0, CountVariationFraction = 0, MinItems = 1, MaxItems = 3,
        SpawnDefinition = MissionChestProtocolTests.CreateCapturedChest().SpawnDefinition,
        RarityWeights = new Dictionary<string, double>
        {
            ["Missing"] = 70, ["1"] = 20, ["2"] = 6, ["3"] = 2.5, ["4"] = 1, ["5"] = 0.5
        }
    };

    static MissionChestPolicy PositivePolicy()
    {
        MissionChestPolicy policy = EmptyPolicy();
        policy.RoomDensity = 0.3;
        policy.Placement = new MissionChestPlacementPolicy
        {
            CandidateAttemptsPerRoom = 8, CandidatesPerRoom = 4, FloorProbeLift = 2, MaxFloorDrop = 4,
            WalkSampleSpacing = 0.4f, MaxWalkStep = 0.4f, CollisionPadding = 0.05f,
            DoorClearance = 2, ObjectiveClearance = 2, ChestClearance = 1.5f, EntryClearance = 2
        };
        return policy;
    }

    static void Seed(AuthoredMissionTestDao dao)
    {
        AddFlag(dao, MissionChestPopulationFlags.Chest(Assigned, 0), JsonSerializer.Serialize(Snapshot()));
        AddFlag(dao, MissionChestPopulationFlags.Seal(Assigned), "1");
    }

    static MissionChestSnapshot Snapshot() => new()
    {
        ContainerInstance = 7000, RoomIndex = 0, X = 17, Y = 4, Z = 29, YawDegrees = 90,
        CollisionRadius = 0.75f, SpawnDefinition = MissionChestProtocolTests.CreateCapturedChest().SpawnDefinition
    };

    static void AddFlag(AuthoredMissionTestDao dao, string key, string value)
        => dao.Flags[new MissionKeyData(111, Quest) + "|" + key] = new MissionFlagData
        {
            CharacterId = 111, QuestId = Quest, FlagKey = key, Value = value, Version = 1
        };

    static ItemInstanceRecord ItemRow(int instance, int slot, int quality = 50) => new()
    {
        InstanceId = instance, ContainerType = (int)IdentityType.Container, ContainerInstance = 7000,
        ContainerPlacement = slot, ItemType = 51053, LowId = 1000, HighId = 1000,
        Quality = quality, StackCount = 1, Source = AORebirth.Enums.ItemSource.Loot
    };

    sealed class World : IDisposable
    {
        readonly ServiceProvider _services;
        readonly bool _ownsSimulation;
        internal readonly QuestDungeonPlayfield Playfield;
        internal readonly DynelRegistry Registry = new();
        internal readonly Inventory Inventory = new();
        internal readonly DungeonWorldLayout Layout;
        internal readonly PlayfieldWorldSimulation Simulation =
            (PlayfieldWorldSimulation)RuntimeHelpers.GetUninitializedObject(typeof(PlayfieldWorldSimulation));

        internal World(bool withSurface = false, bool withPlayerSphere = false)
        {
            Playfield = (QuestDungeonPlayfield)RuntimeHelpers.GetUninitializedObject(typeof(QuestDungeonPlayfield));
            var catalog = new Catalog();
            IGameData data = new StubGameData(HashItemCatalog.Parse("{}"))
                { DynelCollisionSphere = new CollisionSphereData { Radius = 0.75f, CenterY = 0.75f } };
            if (withSurface || withPlayerSphere)
            {
                IGameData overridden = DispatchProxy.Create<IGameData, PopulationGameData>();
                var view = (PopulationGameData)overridden;
                view.Inner = data;
                view.PlayerSphere = withPlayerSphere ? new CollisionSphereData { Radius = 0.25f, CenterY = 0.25f } : null;
                data = overridden;
            }
            _services = new ServiceCollection().AddSingleton(Registry).AddSingleton<IInventoryRepository>(Inventory)
                .AddSingleton<IItemTemplateCatalog>(catalog).AddSingleton<IItemInstanceIdAllocator>(new StubInstanceIdAllocator())
                .AddSingleton(data)
                .AddSingleton(new PlayfieldLocality(4582, null)).BuildServiceProvider();
            typeof(Playfield).GetField("_serviceProvider", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Playfield, _services);
            typeof(Playfield).GetField("<Identity>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Playfield, new Identity { Type = IdentityType.Playfield, Instance = 4582 });
            typeof(QuestDungeonPlayfield).GetField("<Layout>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Playfield, new DungeonLayout(null!, [], new AORebirth.Core.Vector.Vector3(), 1, null));
            var room = new StyleRoomTemplate(1, 0, 0, 5, 5, System.Numerics.Vector3.Zero);
            var mesh = new CollisionTriangleMesh(
                [new(0, 0, 0), new(32, 0, 0), new(0, 0, 32), new(32, 0, 32)],
                [new CollisionTriangle(0, 2, 1), new CollisionTriangle(1, 2, 3)]);
            Layout = new DungeonWorldLayout(new PlayfieldCollisionSet(4582, withSurface ? [mesh] : [], null),
                [new DungeonRoomBounds(0, System.Numerics.Vector3.Zero, new System.Numerics.Vector3(32, 8, 32))],
                placements: [new DungeonRoomPlacement(0, room, System.Numerics.Vector3.Zero, 0)]);
            if (withSurface)
            {
                Simulation = PlayfieldWorldSimulation.Create(4582, new PlayfieldGeometryData { Collision = Layout.Collision },
                    null, new DestinationsCatalog(), data, new StubLogger());
                _ownsSimulation = true;
                Assert.IsNotNull(Simulation.Surface, "The prerequisite test must isolate a present collision surface.");
            }
        }

        public void Dispose()
        {
            if (_ownsSimulation)
                Simulation.Dispose();
            _services.Dispose();
        }
    }

    /// <summary>Test-only view over the existing stub, varying just the player sphere and empty exit configuration.</summary>
    public class PopulationGameData : DispatchProxy
    {
        internal IGameData Inner = null!;
        internal CollisionSphereData? PlayerSphere;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "get_PlayerCollisionSphere")
                return PlayerSphere;
            if (targetMethod.Name == "GetConfiguredExitProxyDoorInstances")
                return Array.Empty<int>();
            return targetMethod.Invoke(Inner, args);
        }
    }

    sealed class Catalog : IItemTemplateCatalog
    {
        readonly ItemTemplate _item = new() { Id = 1000, Quality = 50 };
        readonly ItemTemplate _chest = new() { Id = 162447, Quality = 1 };
        public IReadOnlyCollection<ItemTemplate> Templates => new[] { _item, _chest };
        public bool TryGet(int id, out ItemTemplate template)
        {
            template = id == _item.Id ? _item : _chest;
            return id == _item.Id || id == _chest.Id;
        }
        public ItemTemplate Require(int id) => TryGet(id, out ItemTemplate item) ? item : throw new KeyNotFoundException();
    }

    sealed class Inventory : IInventoryRepository
    {
        internal readonly List<ItemInstanceRecord> Rows = [];
        public IReadOnlyList<ItemInstanceRecord> GetContainerItems(int id) => Rows.Where(row => row.ContainerInstance == id).ToArray();
        public IReadOnlyList<ItemInstanceRecord> GetCarriedItems(int id) => throw new NotSupportedException();
        public IReadOnlyList<ItemInstanceRecord> GetBankItems(int id) => throw new NotSupportedException();
        public int LeaseInstanceIdBlock(int count) => throw new NotSupportedException();
        public ItemInstanceRecord Insert(ItemInstanceRecord item) => throw new NotSupportedException();
        public void UpdateLocation(int id, int type, int owner, int slot) => throw new NotSupportedException();
        public void UpdateLocations(IReadOnlyList<ItemLocationUpdate> locations) => throw new NotSupportedException();
        public void PersistNewAndUpdateLocations(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates)
            => throw new NotSupportedException();
    }
}

internal sealed partial class AuthoredMissionTestDao : IMissionChestPopulationTransaction
{
    public bool TryInitializeChestPopulation(MissionChestPopulationRequest request)
    {
        var key = new MissionKeyData(CharacterId, request.QuestId);
        string seal = MissionChestPopulationFlags.Seal(request.AssignedAtUtcTicks);
        if (GetFlag(key, seal) != null)
            return false;
        for (int index = 0; index < request.Chests.Count; index++)
        {
            MissionChestInitialData chest = request.Chests[index];
            foreach (MissionItemInstanceData item in chest.Items)
                Items.Add(item.InstanceId, Copy(item));
            SaveFlag(key, new MissionFlagData
            {
                CharacterId = CharacterId, QuestId = request.QuestId,
                FlagKey = MissionChestPopulationFlags.Chest(request.AssignedAtUtcTicks, index), Value = chest.SnapshotJson
            });
        }
        SaveFlag(key, new MissionFlagData
        {
            CharacterId = CharacterId, QuestId = request.QuestId, FlagKey = seal,
            Value = request.Chests.Count.ToString(CultureInfo.InvariantCulture)
        });
        return true;
    }
}
