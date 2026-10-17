namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
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
    [DataRow(321, 1, 42)]
    [DataRow(324, 1, 42)]
    [DataRow(341, 1, 42)]
    [DataRow(351, 1, 42)]
    [DataRow(341, 2, 42)]
    [DataRow(341, 5, 42)]
    [DataRow(341, 5, 1)]
    [DataRow(341, 5, 7)]
    public void RealFrozenGeometryProducesStableFloorSupportedClearPositions(int style, int floors, int seed)
    {
        MissionChestPolicy policy = ReadPolicy();
        var (layout, dungeon) = Build(style, floors, seed);
        using PlayfieldWorldSimulation world = World(dungeon);
        Assert.IsTrue(MissionChestPlacement.TryGetChestSphere(Data.Value, Templates.Value, policy, out var sphere));
        var positions = MissionChestPlacement.Plan(dungeon, layout, world, Data.Value, Templates.Value, [], policy, new Random(7919));
        var restored = MissionChestPlacement.Plan(dungeon, layout, world, Data.Value, Templates.Value, [], policy, new Random(7919));
        CollectionAssert.AreEqual(positions.ToArray(), restored.ToArray());
        int requested = policy.RollChestCount(dungeon.Rooms.Count, new Random(7919));
        Dictionary<int, RoomShape> eligible = EligibleRooms(layout, dungeon, policy);
        WallMetric[] metrics = positions.Select(position => MeasureWalls(dungeon, position, sphere.CenterY)).ToArray();
        WritePlacementStatistics(style, floors, seed, layout, dungeon, eligible, requested, positions, metrics);
        Assert.IsTrue(positions.Count > 0, "No verified positions in style " + style + " floors=" + floors);
        Assert.IsTrue(positions.Count <= requested && positions.Count <= eligible.Count,
            "A density target cannot override the one-chest-per-eligible-room capacity or geometric safety.");
        if (style is 321 or 324)
            Assert.AreEqual(requested, positions.Count,
                "These measured representative layouts have enough safe ordinary rooms to meet the configured target.");
        Assert.AreEqual(positions.Count, positions.Select(position => position.RoomIndex).Distinct().Count(),
            "Separate coordinates in the same actual generated room are not separate chest rooms.");
        Assert.AreEqual(positions.Count, positions.Select(position => (position.X, position.Y, position.Z)).Distinct().Count());
        Assert.IsTrue(metrics.Any(metric => metric.Second <= 3 * (sphere.Radius + 2 * policy.Placement!.CollisionPadding)),
            "No selected chest used a corner formed by two nearby nonparallel collision walls.");
        int eligibleFloors = eligible.Keys.Select(room => layout.Generator.Rooms[room].Floor).Distinct().Count();
        if (eligibleFloors > 1 && requested > 1)
            Assert.IsTrue(positions.Select(position => layout.Generator.Rooms[position.RoomIndex].Floor).Distinct().Count() > 1,
                "A multi-floor layout with ordinary eligible rooms must distribute chests across floors.");
        foreach (MissionChestPosition position in positions)
        {
            var point = new Vector3(position.X, position.Y, position.Z);
            Assert.IsTrue(eligible.ContainsKey(position.RoomIndex),
                "A chest was placed in an entrance, main hall, authored passage or actual transition endpoint room.");
            Assert.IsTrue(dungeon.Rooms[position.RoomIndex].Contains(point));
            Assert.IsTrue(HasOwnFloor(dungeon, position.RoomIndex, point, 0.03f),
                "The chest must rest on its own transformed room triangles, rather than another cell inside its AABB.");
            Assert.IsTrue(world.TryRaycastDown(new AoVector3(position.X, position.Y + 1, position.Z), 2, out AoVector3 floor));
            Assert.AreEqual(position.Y, floor.yf, 0.001f);
            Assert.IsTrue(position.YawDegrees is >= 0 and < 360);
            WallMetric metric = MeasureWalls(dungeon, position, sphere.CenterY);
            Assert.IsTrue(metric.Nearest >= sphere.Radius + policy.Placement!.CollisionPadding - 0.03f
                && metric.Nearest <= sphere.Radius + 2 * policy.Placement.CollisionPadding + 0.03f,
                "A chest must sit tightly beside an actual collision wall: distance=" + metric.Nearest);
            AssertNoTriangleOverlap(dungeon, position, sphere);
            AssertRoomSideApproach(world, dungeon, position, sphere, policy.Placement);
            foreach (DungeonDoorPlacement door in dungeon.Doors)
            {
                AssertClear(point, door.Position, policy.Placement!.DoorClearance, sphere.Radius, policy.Placement.MaxWalkStep);
                foreach (float doorY in DoorFloorHeights(dungeon, door))
                    AssertClear(point, new Vector3(door.Position.X, doorY, door.Position.Z),
                        policy.Placement.DoorClearance, sphere.Radius, policy.Placement.MaxWalkStep);
            }
            foreach (DungeonFloorTransition transition in layout.FloorTransitions)
            {
                AssertClear(point, transition.From.Position, policy.Placement!.DoorClearance, sphere.Radius, policy.Placement.MaxWalkStep);
                AssertClear(point, transition.To.Position, policy.Placement!.DoorClearance, sphere.Radius, policy.Placement.MaxWalkStep);
            }
        }
    }

    [TestMethod]
    public void RealCaveDoorClearanceUsesGroundHeightAboveItsTemplateOrigin()
    {
        MissionChestPolicy policy = ReadPolicy();
        var (layout, dungeon) = Build(341, 5);
        int elevatedDoors = dungeon.Doors.Count(door => DoorFloorHeights(dungeon, door)
            .Any(height => MathF.Abs(height - door.Position.Y) > policy.Placement!.FloorProbeLift));
        Assert.IsTrue(elevatedDoors > 0,
            "The real cave regression must contain a doorway whose authored raw Y cannot reach its actual ground with the normal short probe.");
        using PlayfieldWorldSimulation world = World(dungeon);
        Assert.IsTrue(MissionChestPlacement.TryGetChestSphere(Data.Value, Templates.Value, policy, out var sphere));
        var positions = MissionChestPlacement.Plan(dungeon, layout, world, Data.Value, Templates.Value, [], policy, new Random(7919));
        Assert.IsTrue(positions.Count > 0);
        foreach (MissionChestPosition position in positions)
            foreach (DungeonDoorPlacement door in dungeon.Doors)
                foreach (float height in DoorFloorHeights(dungeon, door))
                    AssertClear(new Vector3(position.X, position.Y, position.Z),
                        new Vector3(door.Position.X, height, door.Position.Z), policy.Placement!.DoorClearance,
                        sphere.Radius, policy.Placement.MaxWalkStep);
        TestContext.WriteLine("actual-ground door-height regression elevatedDoors={0} placed={1}", elevatedDoors, positions.Count);
    }

    [TestMethod]
    [DataRow(321)]
    [DataRow(324)]
    public void SameRealLayoutRollsDifferentSafeWallPosesWithoutLosingItsChestTarget(int style)
    {
        MissionChestPolicy policy = ReadPolicy();
        var (layout, dungeon) = Build(style, 1);
        using PlayfieldWorldSimulation world = World(dungeon);
        Assert.IsTrue(MissionChestPlacement.TryGetChestSphere(Data.Value, Templates.Value, policy, out var sphere));
        Dictionary<int, RoomShape> eligible = EligibleRooms(layout, dungeon, policy);
        var all = new List<MissionChestPosition>();
        foreach (int randomSeed in new[] { 19, 7919, 104729, 32452843 })
        {
            int requested = policy.RollChestCount(dungeon.Rooms.Count, new Random(randomSeed));
            var positions = MissionChestPlacement.Plan(dungeon, layout, world, Data.Value, Templates.Value, [], policy, new Random(randomSeed));
            WallMetric[] metrics = positions.Select(position => MeasureWalls(dungeon, position, sphere.CenterY)).ToArray();
            TestContext.WriteLine("same-layout placement randomSeed={0}", randomSeed);
            WritePlacementStatistics(style, 1, 42, layout, dungeon, eligible, requested, positions, metrics);
            Assert.AreEqual(requested, positions.Count,
                "Known available room capacity should keep the requested density across different rolls.");
            Assert.AreEqual(positions.Count, positions.Select(position => position.RoomIndex).Distinct().Count());
            foreach (MissionChestPosition position in positions)
            {
                Assert.IsTrue(eligible.ContainsKey(position.RoomIndex));
                Assert.IsTrue(HasOwnFloor(dungeon, position.RoomIndex, new Vector3(position.X, position.Y, position.Z), 0.03f));
                WallMetric wall = MeasureWalls(dungeon, position, sphere.CenterY);
                Assert.IsTrue(wall.Nearest >= sphere.Radius + policy.Placement!.CollisionPadding - 0.03f
                    && wall.Nearest <= sphere.Radius + 2 * policy.Placement.CollisionPadding + 0.03f);
                AssertNoTriangleOverlap(dungeon, position, sphere);
                AssertRoomSideApproach(world, dungeon, position, sphere, policy.Placement);
            }
            all.AddRange(positions);
        }
        Assert.IsTrue(all.GroupBy(position => position.RoomIndex)
            .Any(room => room.Select(position => (position.X, position.Y, position.Z, position.YawDegrees)).Distinct().Count() > 1),
            "The same actual room must support different rolled wall poses, rather than a predetermined chest position.");
    }

    [TestMethod]
    public void HighDensityStillUsesAtMostOneChestInEachOrdinaryRoom()
    {
        MissionChestPolicy policy = ReadPolicy();
        policy.RoomDensity = 1;
        policy.CountVariationFraction = 0;
        var (layout, dungeon) = Build(324, 1, 12345);
        using PlayfieldWorldSimulation world = World(dungeon);
        var positions = MissionChestPlacement.Plan(dungeon, layout, world, Data.Value, Templates.Value, [], policy, new Random(104729));
        Dictionary<int, RoomShape> eligible = EligibleRooms(layout, dungeon, policy);
        Assert.IsTrue(MissionChestPlacement.TryGetChestSphere(Data.Value, Templates.Value, policy, out var sphere));
        WritePlacementStatistics(324, 1, 12345, layout, dungeon, eligible, dungeon.Rooms.Count, positions,
            positions.Select(position => MeasureWalls(dungeon, position, sphere.CenterY)).ToArray());
        Assert.IsTrue(positions.Count > 0);
        Assert.IsTrue(eligible.Count < dungeon.Rooms.Count, "The real layout must exercise authored room exclusions.");
        Assert.IsTrue(positions.Count <= eligible.Count);
        Assert.AreEqual(positions.Count, positions.Select(position => position.RoomIndex).Distinct().Count());
        Assert.IsTrue(positions.All(position => eligible.ContainsKey(position.RoomIndex)));
    }

    [TestMethod]
    public void RealPartialMainHallDoesNotBecomeAChestRoomBecauseItsBoundingBoxIsLarge()
    {
        MissionChestPolicy policy = ReadPolicy();
        var (layout, dungeon) = Build(324, 1);
        StyleRoomSet styles = StyleRoomSet.Load(RuntimeRoot(), 324);
        var partial = dungeon.Placements.Select(placed => (Placed: placed,
                Shape: styles.All.Single(shape => shape.RoomId == placed.Template.InstanceId)))
            .Where(room => room.Shape.FootprintBlocks < room.Shape.BlocksX * room.Shape.BlocksZ).ToArray();
        Assert.IsTrue(partial.Length > 0, "This real layout must include a nonrectangular DCGA footprint.");
        int emptyBlocksInsideBounds = 0;
        foreach (var room in partial)
        {
            Assert.IsTrue(styles.IsMainHall(room.Shape), "The selected regression geometry is an authored main hall.");
            for (int x = 0; x < room.Shape.BlocksX; x++)
                for (int z = 0; z < room.Shape.BlocksZ; z++)
                {
                    if (room.Shape.Footprint[x, z])
                        continue;
                    Vector3 local = room.Shape.Template.TemplatePos + new Vector3(
                        ((x + 0.5f) * RoomShape.TilesPerBlock - room.Shape.Template.LocalOrigin.X) * 2,
                        0, ((z + 0.5f) * RoomShape.TilesPerBlock - room.Shape.Template.LocalOrigin.Z) * 2);
                    Vector3 missing = room.Placed.TransformTemplatePoint(local);
                    missing.Y = Math.Clamp(missing.Y, dungeon.Rooms[room.Placed.Index].Min.Y, dungeon.Rooms[room.Placed.Index].Max.Y);
                    if (dungeon.Rooms[room.Placed.Index].Contains(missing))
                        emptyBlocksInsideBounds++;
                }
        }
        Assert.IsTrue(emptyBlocksInsideBounds > 0,
            "An AABB contains the real main hall's missing block and therefore cannot prove room occupancy.");
        using PlayfieldWorldSimulation world = World(dungeon);
        var positions = MissionChestPlacement.Plan(dungeon, layout, world, Data.Value, Templates.Value, [], policy, new Random(7919));
        Assert.IsTrue(positions.Count > 0);
        Assert.IsFalse(positions.Any(position => partial.Any(room => room.Placed.Index == position.RoomIndex)));
        TestContext.WriteLine("real partial-mainhall rooms={0} empty-DCGA-blocks-inside-AABB={1} chest-count={2}",
            partial.Length, emptyBlocksInsideBounds, positions.Count);
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

    static Dictionary<int, RoomShape> EligibleRooms(DungeonLayout layout, DungeonWorldLayout dungeon, MissionChestPolicy policy)
    {
        StyleRoomSet styles = StyleRoomSet.Load(RuntimeRoot(), layout.Generator.Style);
        var transitions = layout.FloorTransitions.SelectMany(link => new[] { link.From.RoomIndex, link.To.RoomIndex }).ToHashSet();
        return dungeon.Placements.Select(placed => (Placed: placed,
                Shape: styles.All.Single(shape => shape.RoomId == placed.Template.InstanceId)))
            .Where(room => room.Shape.Role is not (RoomRole.Entrance or RoomRole.Start)
                && !styles.IsMainHall(room.Shape) && !transitions.Contains(room.Placed.Index)
                && !policy.Placement!.ExcludedRoomNameParts.Any(part => room.Shape.Name.Contains(part, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(room => room.Placed.Index, room => room.Shape);
    }

    void WritePlacementStatistics(int style, int floors, int seed, DungeonLayout layout, DungeonWorldLayout dungeon,
        IReadOnlyDictionary<int, RoomShape> eligible, int requested, IReadOnlyList<MissionChestPosition> positions, WallMetric[] walls)
    {
        string floorCounts = string.Join(",", positions.GroupBy(position => layout.Generator.Rooms[position.RoomIndex].Floor)
            .OrderBy(group => group.Key).Select(group => group.Key + ":" + group.Count()));
        string eligibleFloors = string.Join(",", eligible.Keys.GroupBy(room => layout.Generator.Rooms[room].Floor)
            .OrderBy(group => group.Key).Select(group => group.Key + ":" + group.Count()));
        int adjacentPairs = positions.SelectMany((position, index) => positions.Skip(index + 1).Select(other => (position, other)))
            .Count(pair => dungeon.RoomLinks.TryGetValue(pair.position.RoomIndex, out var links) && links.Contains(pair.other.RoomIndex));
        TestContext.WriteLine("style={0} floors={1} seed={2} rooms={3} requested={4} placed={5} shortfall={6} uniqueRooms={7} metadataEligible={8} eligibleFloorRooms=[{9}] chestFloors=[{10}] adjacentRoomPairs={11}",
            style, floors, seed, dungeon.Rooms.Count, requested, positions.Count, requested - positions.Count,
            positions.Select(position => position.RoomIndex).Distinct().Count(), eligible.Count, eligibleFloors, floorCounts, adjacentPairs);
        TestContext.WriteLine("wall-center-distance min={0:F5} mean={1:F5} max={2:F5}; second-nonparallel-wall min={3:F5} max={4:F5}",
            walls.Length == 0 ? float.NaN : walls.Min(wall => wall.Nearest),
            walls.Length == 0 ? float.NaN : walls.Average(wall => wall.Nearest),
            walls.Length == 0 ? float.NaN : walls.Max(wall => wall.Nearest),
            walls.Length == 0 ? float.NaN : walls.Min(wall => wall.Second),
            walls.Length == 0 ? float.NaN : walls.Max(wall => wall.Second));
        for (int index = 0; index < positions.Count; index++)
        {
            MissionChestPosition position = positions[index];
            WallMetric wall = walls[index];
            TestContext.WriteLine("chest room={0} floor={1} template={2} name={3} xyz=({4:F3},{5:F3},{6:F3}) yaw={7} wall={8:F5} secondWall={9:F5}",
                position.RoomIndex, layout.Generator.Rooms[position.RoomIndex].Floor,
                dungeon.Placements[position.RoomIndex].Template.InstanceId,
                dungeon.Placements[position.RoomIndex].Template.Name, position.X, position.Y, position.Z,
                position.YawDegrees, wall.Nearest, wall.Second);
        }
    }

    readonly record struct WallMetric(float Nearest, float Second);

    static WallMetric MeasureWalls(DungeonWorldLayout dungeon, MissionChestPosition position, float centerY)
    {
        Vector2 chest = new(position.X, position.Z);
        float height = position.Y + centerY;
        var distances = new List<(float Distance, Vector2 Normal)>();
        foreach (var triangle in RoomTriangles(dungeon, position.RoomIndex))
        {
            Vector3 normal = Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A);
            if (normal.LengthSquared() < 1e-8f || MathF.Abs(Vector3.Normalize(normal).Y) > 0.3f)
                continue;
            Vector3[] vertices = [triangle.A, triangle.B, triangle.C];
            var section = new List<Vector2>(3);
            for (int edge = 0; edge < 3; edge++)
            {
                Vector3 a = vertices[edge], b = vertices[(edge + 1) % 3];
                if (height < MathF.Min(a.Y, b.Y) || height > MathF.Max(a.Y, b.Y) || MathF.Abs(a.Y - b.Y) < 1e-6f)
                    continue;
                float t = (height - a.Y) / (b.Y - a.Y);
                Vector2 cut = new(a.X + t * (b.X - a.X), a.Z + t * (b.Z - a.Z));
                if (section.All(existing => Vector2.DistanceSquared(existing, cut) > 1e-8f))
                    section.Add(cut);
            }
            if (section.Count < 2 || Vector2.DistanceSquared(section[0], section[1]) < 0.04f)
                continue;
            Vector2 segment = section[1] - section[0];
            float fraction = Math.Clamp(Vector2.Dot(chest - section[0], segment) / segment.LengthSquared(), 0, 1);
            distances.Add((Vector2.Distance(chest, section[0] + segment * fraction), Vector2.Normalize(new Vector2(normal.X, normal.Z))));
        }
        if (distances.Count == 0)
            return new WallMetric(float.PositiveInfinity, float.PositiveInfinity);
        var nearest = distances.MinBy(wall => wall.Distance);
        float second = distances.Where(wall => MathF.Abs(Vector2.Dot(wall.Normal, nearest.Normal)) < 0.7f)
            .Select(wall => wall.Distance).DefaultIfEmpty(float.PositiveInfinity).Min();
        return new WallMetric(nearest.Distance, second);
    }

    static IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> RoomTriangles(DungeonWorldLayout dungeon, int room, bool groundOnly = false)
    {
        foreach (CollisionTriangleMesh mesh in dungeon.Collision.SurfaceMeshes.Where(mesh => mesh.CellId == room
            && (!groundOnly || mesh.Source?.StartsWith("ground-room", StringComparison.Ordinal) == true)))
            foreach (CollisionTriangle triangle in mesh.Triangles)
                yield return (mesh.Vertices[triangle.A], mesh.Vertices[triangle.B], mesh.Vertices[triangle.C]);
    }

    static bool HasOwnFloor(DungeonWorldLayout dungeon, int room, Vector3 point, float tolerance)
        => GroundHeights(dungeon, room, new Vector2(point.X, point.Z)).Any(height => MathF.Abs(height - point.Y) <= tolerance);

    static IEnumerable<float> DoorFloorHeights(DungeonWorldLayout dungeon, DungeonDoorPlacement door)
        => new[] { door.Room, door.LinkedRoom }.Where(room => room >= 0 && room < dungeon.Rooms.Count).Distinct()
            .SelectMany(room => GroundHeights(dungeon, room, new Vector2(door.Position.X, door.Position.Z))).Distinct();

    static IEnumerable<float> GroundHeights(DungeonWorldLayout dungeon, int room, Vector2 point)
    {
        foreach (var triangle in RoomTriangles(dungeon, room, groundOnly: true))
        {
            Vector2 a = new(triangle.A.X, triangle.A.Z), b = new(triangle.B.X, triangle.B.Z), c = new(triangle.C.X, triangle.C.Z);
            float determinant = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (MathF.Abs(determinant) < 1e-6f)
                continue;
            float u = ((b.Y - c.Y) * (point.X - c.X) + (c.X - b.X) * (point.Y - c.Y)) / determinant;
            float v = ((c.Y - a.Y) * (point.X - c.X) + (a.X - c.X) * (point.Y - c.Y)) / determinant;
            float w = 1 - u - v;
            if (u >= -0.001f && v >= -0.001f && w >= -0.001f)
                yield return u * triangle.A.Y + v * triangle.B.Y + w * triangle.C.Y;
        }
    }

    static void AssertRoomSideApproach(PlayfieldWorldSimulation world, DungeonWorldLayout dungeon,
        MissionChestPosition position, CollisionSphereData chest, MissionChestPlacementPolicy policy)
    {
        CollisionSphereData? player = Data.Value.PlayerCollisionSphere;
        Assert.IsNotNull(player);
        float angle = position.YawDegrees * MathF.PI / 180;
        Vector3 forward = new(MathF.Sin(angle), 0, MathF.Cos(angle));
        Vector3 side = new(forward.Z, 0, -forward.X);
        Vector3 feet = new(position.X, position.Y, position.Z);
        Vector3 approach = feet + forward * (chest.Radius + player.Radius + 2 * policy.CollisionPadding);
        Assert.IsTrue(world.TryRaycastDown(ToAo(approach + Vector3.UnitY * policy.FloorProbeLift),
            policy.FloorProbeLift + policy.MaxFloorDrop, out AoVector3 frontFloor));
        approach = new Vector3(frontFloor.xf, frontFloor.yf, frontFloor.zf);
        float width = MathF.Max(policy.DoorClearance, chest.Radius + player.Radius + policy.CollisionPadding);
        foreach (Vector3 expected in new[] { approach, approach + forward * width, approach + side * width, approach - side * width })
        {
            Assert.IsTrue(world.TryRaycastDown(ToAo(expected + Vector3.UnitY * policy.FloorProbeLift),
                policy.FloorProbeLift + policy.MaxFloorDrop, out AoVector3 hit), "The yaw-facing room-side pocket needs floor support.");
            Vector3 supported = new(hit.xf, hit.yf, hit.zf);
            Assert.IsTrue(MathF.Abs(supported.Y - feet.Y) <= policy.MaxWalkStep + 0.03f);
            Assert.IsTrue(HasOwnFloor(dungeon, position.RoomIndex, supported, 0.03f),
                "The yaw-facing approach must remain on this room's actual triangles.");
            int samples = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(approach, supported) / policy.WalkSampleSpacing));
            Vector3 previous = approach;
            for (int sample = 0; sample <= samples; sample++)
            {
                Vector3 between = Vector3.Lerp(approach, supported, sample / (float)samples);
                Assert.IsTrue(world.TryRaycastDown(ToAo(between + Vector3.UnitY * policy.FloorProbeLift),
                    policy.FloorProbeLift + policy.MaxFloorDrop, out AoVector3 intermediate));
                Assert.IsTrue(MathF.Abs(intermediate.yf - feet.Y) <= policy.MaxWalkStep + 0.03f);
                Assert.IsTrue(HasOwnFloor(dungeon, position.RoomIndex,
                    new Vector3(intermediate.xf, intermediate.yf, intermediate.zf), 0.03f),
                    "A valid endpoint cannot bridge an unsupported gap in the room-side pocket.");
                Vector3 current = new(intermediate.xf, intermediate.yf, intermediate.zf);
                Assert.IsTrue(MathF.Abs(current.Y - previous.Y) <= policy.MaxWalkStep + 0.03f,
                    $"Room {position.RoomIndex} pocket step exceeds the height limit: {previous} -> {current}.");
                if (sample > 0)
                {
                    Vector3 from = previous + Vector3.UnitY * (player.CenterY + policy.CollisionPadding);
                    Vector3 to = current + Vector3.UnitY * (player.CenterY + policy.CollisionPadding);
                    Assert.IsFalse(world.TrySphereCast(ToAo(from), ToAo(to), player.Radius, out _, out var forwardNormal),
                        $"Room {position.RoomIndex} pocket forward segment blocked: {from} -> {to}; normal={forwardNormal}.");
                    Assert.IsFalse(world.TrySphereCast(ToAo(to), ToAo(from), player.Radius, out _, out var reverseNormal),
                        $"Room {position.RoomIndex} pocket reverse segment blocked: {to} -> {from}; normal={reverseNormal}.");
                }
                previous = current;
            }
        }
        Assert.IsTrue(world.HasLineOfSight(ToAo(approach + Vector3.UnitY * player.CenterY),
            ToAo(feet + Vector3.UnitY * chest.CenterY)), "A player on the room side must see the chest.");
    }

    static void AssertNoTriangleOverlap(DungeonWorldLayout dungeon, MissionChestPosition position, CollisionSphereData sphere)
    {
        Vector3 center = new(position.X, position.Y + sphere.CenterY, position.Z);
        float radiusSquared = (sphere.Radius - 0.005f) * (sphere.Radius - 0.005f);
        foreach (CollisionTriangleMesh mesh in dungeon.Collision.SurfaceMeshes)
        {
            foreach (CollisionTriangle triangle in mesh.Triangles)
            {
                Vector3 a = mesh.Vertices[triangle.A], b = mesh.Vertices[triangle.B], c = mesh.Vertices[triangle.C];
                if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-8f)
                    continue;
                Assert.IsTrue(PointTriangleDistanceSquared(center, a, b, c) >= radiusSquared,
                    "The actual chest collision sphere overlaps a transformed static triangle in " + mesh.Source);
            }
        }
    }

    static float PointTriangleDistanceSquared(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        // Closest-point regions of the finite triangle, independent of the runtime's sweep rays.
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0)
            return ap.LengthSquared();
        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3)
            return bp.LengthSquared();
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
            return Vector3.DistanceSquared(p, a + ab * (d1 / (d1 - d3)));
        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6)
            return cp.LengthSquared();
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
            return Vector3.DistanceSquared(p, a + ac * (d2 / (d2 - d6)));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            return Vector3.DistanceSquared(p, b + (c - b) * ((d4 - d3) / (d4 - d3 + d5 - d6)));
        float denominator = 1 / (va + vb + vc);
        return Vector3.DistanceSquared(p, a + ab * (vb * denominator) + ac * (vc * denominator));
    }

    static AoVector3 ToAo(Vector3 point) => new(point.X, point.Y, point.Z);

    static void AssertClear(Vector3 position, Vector3 excluded, float minimum, float radius, float step)
    {
        if (MathF.Abs(position.Y - excluded.Y) <= radius * 2 + step)
            Assert.IsTrue(Vector2.Distance(new Vector2(position.X, position.Z), new Vector2(excluded.X, excluded.Z)) >= minimum);
    }

    static (DungeonLayout, DungeonWorldLayout) Build(int style, int floors, int seed = 42)
    {
        var generator = new DungeonGenerator(RuntimeRoot());
        var loaded = generator.LoadStyle(style);
        DungeonGenerationResult generated = generator.Generate(new DungeonGenerationRequest
        {
            StyleId = style, Seed = seed, FloorCount = floors, FloorSize = 10, FloorHeight = 64,
            UseFloorTransitions = true, BossFloor = floors > 1, BuildingInstance = 1234,
            DescendingFloors = loaded.Transitions?.DescendingFloors ?? false
        });
        var layout = new DungeonLayout(generated.Generator, generated.Generator.ToByteArray(),
            new AoVector3(generated.Spawn.X, generated.Spawn.Y, generated.Spawn.Z), seed, null)
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
