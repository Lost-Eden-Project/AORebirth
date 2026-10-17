namespace ZoneEngine_New.Tests;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

using AORebirth.Core.GameData;
using AORebirth.DungeonGenerator;
using AORebirth.Enums;
using AORebirth.World.Collision;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Quests.Dungeons;
using ZoneEngine_New.Core.WorldSimulation;
using AoVector3 = AORebirth.Core.Vector.Vector3;
using Vector3 = System.Numerics.Vector3;

[TestClass]
[DoNotParallelize]
public sealed class MissionChestPlacementTests
{
    static readonly Lazy<GameDataStore> Data = new(() => new GameDataStore(new StubLogger(), null, RuntimeRoot()));
    static readonly Lazy<ItemTemplateCatalog> Templates = new(() => new ItemTemplateCatalog(Data.Value, new StubLogger()));

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(321, 1)]
    [DataRow(324, 1)]
    [DataRow(341, 1)]
    [DataRow(351, 1)]
    [DataRow(341, 2)]
    [DataRow(341, 5)]
    public void RealFrozenGeometryProducesStableFloorSupportedClearPositions(int style, int floors)
    {
        MissionChestPolicy policy = ReadPolicy();
        var (layout, dungeon) = Build(style, floors);
        using PlayfieldWorldSimulation world = World(dungeon);
        Assert.IsTrue(MissionChestPlacement.TryGetChestSphere(Data.Value, Templates.Value, policy, out var sphere));
        var positions = MissionChestPlacement.Plan(dungeon, layout, world, Data.Value, Templates.Value, [], policy, new Random(7919));
        var restored = MissionChestPlacement.Plan(dungeon, layout, world, Data.Value, Templates.Value, [], policy, new Random(7919));
        CollectionAssert.AreEqual(positions.ToArray(), restored.ToArray());
        int requested = policy.RollChestCount(dungeon.Rooms.Count, new Random(7919));
        TestContext.WriteLine("style={0} floors={1} rooms={2} requested={3} verified={4} roomsWithChests={5}",
            style, floors, dungeon.Rooms.Count, requested, positions.Count, positions.Select(position => position.RoomIndex).Distinct().Count());
        Assert.IsTrue(positions.Count > 0, "No verified positions in style " + style + " floors=" + floors);
        Assert.AreEqual(requested, positions.Count, "The representative frozen layout must meet its configured chest target.");
        Assert.AreEqual(positions.Count, positions.Select(position => (position.X, position.Y, position.Z)).Distinct().Count());
        foreach (MissionChestPosition position in positions)
        {
            var point = new Vector3(position.X, position.Y, position.Z);
            Assert.IsTrue(dungeon.Rooms[position.RoomIndex].Contains(point));
            Assert.IsTrue(world.TryRaycastDown(new AoVector3(position.X, position.Y + 1, position.Z), 2, out AoVector3 floor));
            Assert.AreEqual(position.Y, floor.yf, 0.001f);
            Assert.IsTrue(position.YawDegrees is >= 0 and < 360);
            foreach (DungeonDoorPlacement door in dungeon.Doors)
                AssertClear(point, door.Position, policy.Placement!.DoorClearance, sphere.Radius, policy.Placement.MaxWalkStep);
            foreach (DungeonFloorTransition transition in layout.FloorTransitions)
            {
                AssertClear(point, transition.From.Position, policy.Placement!.DoorClearance, sphere.Radius, policy.Placement.MaxWalkStep);
                AssertClear(point, transition.To.Position, policy.Placement!.DoorClearance, sphere.Radius, policy.Placement.MaxWalkStep);
            }
        }
    }

    [TestMethod]
    public void RealObjectiveExclusionNeverGetsReplacedByUncheckedPosition()
    {
        MissionChestPolicy policy = ReadPolicy();
        var (layout, dungeon) = Build(324, 1);
        using PlayfieldWorldSimulation world = World(dungeon);
        var original = MissionChestPlacement.Plan(dungeon, layout, world, Data.Value, Templates.Value, [], policy, new Random(7919));
        Assert.IsTrue(original.Count > 0);
        var objective = new Vector3(original[0].X, original[0].Y, original[0].Z);
        var positions = MissionChestPlacement.Plan(dungeon, layout, world, Data.Value, Templates.Value, [objective], policy, new Random(7919));
        Assert.IsTrue(MissionChestPlacement.TryGetChestSphere(Data.Value, Templates.Value, policy, out var sphere));
        foreach (MissionChestPosition position in positions)
            AssertClear(new Vector3(position.X, position.Y, position.Z), objective,
                policy.Placement!.ObjectiveClearance, sphere.Radius, policy.Placement.MaxWalkStep);
    }

    [TestMethod]
    public void MissingCollisionFailsClosedWithoutInventingChestExtents()
    {
        MissionChestPolicy policy = ReadPolicy();
        var (layout, dungeon) = Build(324, 1);
        using PlayfieldWorldSimulation empty = PlayfieldWorldSimulation.Create(1, new PlayfieldGeometryData(), null,
            new DestinationsCatalog(), Data.Value, new StubLogger());
        Assert.AreEqual(0, MissionChestPlacement.Plan(dungeon, layout, empty, Data.Value, Templates.Value,
            [], policy, new Random(7919)).Count);
        var missing = new StubGameData(HashItemCatalog.Parse("{}"));
        Assert.IsFalse(MissionChestPlacement.TryGetChestSphere(missing, Templates.Value, policy, out _));
    }

    [TestMethod]
    public void WalkSamplingRejectsExcessiveOrUnrepresentableCountsWithoutDownsampling()
    {
        Assert.IsTrue(MissionChestPlacement.TryGetWalkSampleCount(1, 0.25f, out int ordinary));
        Assert.AreEqual(4, ordinary);
        Assert.IsTrue(MissionChestPlacement.TryGetWalkSampleCount(
            MissionChestPlacement.MaximumWalkSamplesPerLink, 1, out int maximum));
        Assert.AreEqual(MissionChestPlacement.MaximumWalkSamplesPerLink, maximum);
        foreach ((float distance, float spacing) in new[]
        {
            (1f, 1e-8f), (1f, float.Epsilon), (float.MaxValue, float.Epsilon),
            (float.PositiveInfinity, 1f), (1f, float.NaN), (1f, 0f),
            ((float)MissionChestPlacement.MaximumWalkSamplesPerLink + 1, 1f)
        })
        {
            Assert.IsFalse(MissionChestPlacement.TryGetWalkSampleCount(distance, spacing, out int rejected));
            Assert.AreEqual(0, rejected);
        }
    }

    [TestMethod]
    public void TinySpacingCannotApproveUncheckedRealGeometryPaths()
    {
        MissionChestPolicy policy = ReadPolicy();
        policy.Placement!.WalkSampleSpacing = float.Epsilon;
        var (layout, dungeon) = Build(324, 1);
        using PlayfieldWorldSimulation world = World(dungeon);
        Assert.AreEqual(0, MissionChestPlacement.Plan(dungeon, layout, world, Data.Value, Templates.Value,
            [], policy, new Random(7919)).Count);
    }

    [TestMethod]
    public void RealFullItemDatabaseProducesValidIndependentLootAcrossMissionQualityBands()
    {
        MissionChestPolicy policy = ReadPolicy();
        var ids = new SmokeItemIds();
        var builder = new ItemBuilder(Templates.Value, ids, new StubLogger());
        var loot = new MissionChestLootService(Templates.Value, Data.Value, builder, policy);
        var random = new Random(32452843);
        var instances = new System.Collections.Generic.HashSet<int>();
        foreach (int missionQuality in new[] { 10, 50, 100, 200, 250 })
        {
            int minimum = Math.Max(1, missionQuality - 5);
            int maximum = Math.Min(250, missionQuality + 5);
            int count = 0;
            var elapsed = Stopwatch.StartNew();
            for (int chest = 0; chest < 100; chest++)
            {
                var items = loot.Roll(missionQuality, random);
                Assert.IsTrue(items.Count is >= 1 and <= 3);
                foreach (Item item in items)
                {
                    Assert.IsTrue(item.LowId > 0 && item.HighId > 0 && item.InstanceId > 0);
                    Assert.IsTrue(item.ResolvedItemType > 0, "Selected item has no valid identity type: " + item.LowId);
                    Assert.AreEqual(item.InstanceId, item.Identity.Instance);
                    Assert.IsTrue(instances.Add(item.InstanceId), "An allocation was reused by another chest.");
                    Assert.AreEqual(ItemSource.Loot, item.Source);
                    Assert.IsFalse(item.IsPersisted);
                    Assert.IsTrue(item.Quality >= minimum && item.Quality <= maximum);
                    Assert.AreEqual(item.Quality, item.Definition.Quality);
                    Assert.IsTrue(item.Definition.Stats.TryGetValue(
                        SmokeLounge.AOtomation.Messaging.GameData.CharacterStat.Can, out int can)
                        && ((uint)can & (uint)CanFlags.Carry) != 0);
                    Assert.IsTrue(Templates.Value.TryGet(item.LowId, out ItemTemplate low));
                    Assert.IsTrue(Templates.Value.TryGet(item.HighId, out ItemTemplate high));
                    Assert.IsTrue(item.Quality >= Math.Min(low.Quality, high.Quality)
                        && item.Quality <= Math.Max(low.Quality, high.Quality));
                    count++;
                }
            }
            elapsed.Stop();
            TestContext.WriteLine("Full item database missionQL={0} chests=100 items={1} first-pool-plus-roll-ms={2}",
                missionQuality, count, elapsed.ElapsedMilliseconds);
        }
    }

    sealed class SmokeItemIds : IItemInstanceIdAllocator
    {
        int _next;
        public int Allocate() => ++_next;
    }

    static void AssertClear(Vector3 position, Vector3 excluded, float minimum, float radius, float step)
    {
        if (MathF.Abs(position.Y - excluded.Y) <= radius * 2 + step)
            Assert.IsTrue(Vector2.Distance(new Vector2(position.X, position.Z), new Vector2(excluded.X, excluded.Z)) >= minimum);
    }

    static (DungeonLayout, DungeonWorldLayout) Build(int style, int floors)
    {
        var generator = new DungeonGenerator(RuntimeRoot());
        var loaded = generator.LoadStyle(style);
        DungeonGenerationResult generated = generator.Generate(new DungeonGenerationRequest
        {
            StyleId = style, Seed = 42, FloorCount = floors, FloorSize = 10, FloorHeight = 64,
            UseFloorTransitions = true, BossFloor = floors > 1, BuildingInstance = 1234,
            DescendingFloors = loaded.Transitions?.DescendingFloors ?? false
        });
        var layout = new DungeonLayout(generated.Generator, generated.Generator.ToByteArray(),
            new AoVector3(generated.Spawn.X, generated.Spawn.Y, generated.Spawn.Z), 42, null)
        {
            FloorTransitions = generated.Transitions, BossRoomIndex = generated.BossRoomIndex
        };
        return (layout, DungeonCollisionBuilder.BuildLayout(RuntimeRoot(), generated.Generator));
    }

    static PlayfieldWorldSimulation World(DungeonWorldLayout dungeon)
        => PlayfieldWorldSimulation.Create(1, new PlayfieldGeometryData { Collision = dungeon.Collision }, null,
            new DestinationsCatalog(), Data.Value, new StubLogger(), Templates.Value);

    static MissionChestPolicy ReadPolicy()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "AI_START_HERE.md")))
            directory = Directory.GetParent(directory)?.FullName;
        Assert.IsNotNull(directory, "The focused test must run from the authorized worktree.");
        string path = Path.Combine(directory!, "AORebirth", "GameData", "DungeonGeneration.json");
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        MissionChestPolicy? policy = JsonSerializer.Deserialize<MissionChestPolicy>(json.RootElement.GetProperty("Chests"),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.IsNotNull(policy);
        Assert.IsTrue(policy.IsValid);
        Assert.IsNotNull(policy.SpawnDefinition);
        Assert.IsNotNull(policy.Placement);
        return policy;
    }

    static string RuntimeRoot()
    {
        string? path = Environment.GetEnvironmentVariable(GameDataPaths.EnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(path))
            Assert.Inconclusive("Set AO_REBIRTH_GAMEDATA_PATH to the existing full GameData for real geometry checks.");
        Assert.IsTrue(Directory.Exists(path));
        return path!;
    }
}
