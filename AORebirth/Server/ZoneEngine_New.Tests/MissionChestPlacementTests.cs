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
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Quests.Dungeons;
using ZoneEngine_New.Core.WorldSimulation;
using AoVector3 = AORebirth.Core.Vector.Vector3;
using CharacterStat = SmokeLounge.AOtomation.Messaging.GameData.CharacterStat;
using Vector3 = System.Numerics.Vector3;

[TestClass]
[DoNotParallelize]
public sealed class MissionChestPlacementTests
{
    internal static readonly Lazy<GameDataStore> Data = new(() => new GameDataStore(new StubLogger(), null, RuntimeRoot()));
    internal static readonly Lazy<ItemTemplateCatalog> Templates = new(() => new ItemTemplateCatalog(Data.Value, new StubLogger()));
    static readonly object CorpusOutputLock = new();

    public TestContext TestContext { get; set; } = null!;

    public static IEnumerable<object[]> RealGeometryCorpusCases()
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(RuntimeRoot(), "DungeonGeneration.json")));
        var configurations = json.RootElement.GetProperty("Profiles").EnumerateArray()
            .SelectMany(profile => profile.GetProperty("Styles").EnumerateArray().Select(style => (
                Style: style.GetInt32(), Floors: profile.GetProperty("Floors").GetInt32(),
                Minimum: profile.GetProperty("MinFloorSize").GetInt32(), Maximum: profile.GetProperty("MaxFloorSize").GetInt32())))
            .Distinct().OrderBy(configuration => configuration.Style).ThenBy(configuration => configuration.Floors).ToArray();
        foreach (var configuration in configurations)
        {
            for (int index = 0; index < 64; index++)
            {
                int seed = index switch { 0 => 42, 1 => 1, 2 => 7,
                    _ => unchecked(index * 104729 + configuration.Style * 7919 + configuration.Floors * 15485863) };
                int size = configuration.Minimum + index % (configuration.Maximum - configuration.Minimum + 1);
                yield return [configuration.Style, configuration.Floors, seed, size];
            }
        }
    }

    [TestMethod]
    [TestCategory("MissionChestGeometryCorpus")]
    [DynamicData(nameof(RealGeometryCorpusCases), DynamicDataSourceType.Method)]
    public void RealGeometryCorpusVerifiesDensitySafetyAndEntranceRoutes(int style, int floors, int seed, int floorSize)
    {
        MissionChestPolicy policy = ReadPolicy();
        var elapsed = Stopwatch.StartNew();
        DungeonLayout layout;
        DungeonWorldLayout dungeon;
        try
        {
            (layout, dungeon) = Build(style, floors, seed, floorSize);
        }
        catch (Exception exception)
        {
            WriteCorpusResult(new
            {
                Generated = false, Style = style, Floors = floors, LayoutSeed = seed, FloorSize = floorSize,
                Error = exception.Message
            });
            throw;
        }
        using PlayfieldWorldSimulation world = World(dungeon);
        Assert.IsTrue(MissionChestPlacement.TryGetChestSphere(Data.Value, Templates.Value, policy, out var sphere));
        Dictionary<int, RoomShape> eligible = EligibleRooms(layout, dungeon, policy);
        int placementSeed = unchecked(seed ^ (style * 7919) ^ (floorSize * 104729));
        int requested = policy.RollChestCount(eligible.Count, new Random(placementSeed));
        var diagnostics = new MissionChestPlacementDiagnostics();
        var placementElapsed = Stopwatch.StartNew();
        var positions = MissionChestPlacement.Plan(dungeon, layout, world, Data.Value, Templates.Value,
            [], policy, new Random(placementSeed), diagnostics);
        placementElapsed.Stop();
        elapsed.Stop();
        WallMetric[] walls = positions.Select(position => MeasureWalls(dungeon, position, sphere.CenterY)).ToArray();
        int adjacentPairs = positions.SelectMany((position, index) => positions.Skip(index + 1).Select(other => (position, other)))
            .Count(pair => dungeon.RoomLinks.TryGetValue(pair.position.RoomIndex, out var links) && links.Contains(pair.other.RoomIndex));
        int[] safeRooms = diagnostics.ReachableCandidateCounts.Where(pair => pair.Value > 0).Select(pair => pair.Key).ToArray();
        WriteCorpusResult(new
        {
            Generated = true, Style = style, Floors = floors, LayoutSeed = seed, FloorSize = floorSize,
            PlacementSeed = placementSeed, TotalCells = dungeon.Rooms.Count, EligibleRooms = eligible.Count,
            Requested = requested, Placed = positions.Count, DistinctRooms = positions.Select(position => position.RoomIndex).Distinct().Count(),
            Coverage = requested == 0 ? 1 : positions.Count / (double)requested,
            CandidateRooms = diagnostics.CandidateCounts.Count(pair => pair.Value > 0), ReachableCandidateRooms = safeRooms.Length,
            EligibleFloors = eligible.Keys.GroupBy(room => layout.Generator.Rooms[room].Floor).ToDictionary(group => group.Key, group => group.Count()),
            ReachableFloors = safeRooms.GroupBy(room => layout.Generator.Rooms[room].Floor).ToDictionary(group => group.Key, group => group.Count()),
            PlacedFloors = positions.GroupBy(position => layout.Generator.Rooms[position.RoomIndex].Floor).ToDictionary(group => group.Key, group => group.Count()),
            AdjacentPairs = adjacentPairs, WallMinimum = walls.Length == 0 ? (float?)null : walls.Min(wall => wall.Nearest),
            WallMean = walls.Length == 0 ? (float?)null : walls.Average(wall => wall.Nearest),
            WallMaximum = walls.Length == 0 ? (float?)null : walls.Max(wall => wall.Nearest),
            RuntimeMilliseconds = elapsed.ElapsedMilliseconds, PlacementMilliseconds = placementElapsed.ElapsedMilliseconds, diagnostics.Rejections,
            diagnostics.ReferenceHeights, diagnostics.LocalHeightSeeds, diagnostics.OffReferencePlacements,
            diagnostics.AnchorCounts, diagnostics.ReachableAnchorCounts, diagnostics.LandingHeightOffsets,
            diagnostics.WallSectionCounts, diagnostics.WallSectionsTried,
            diagnostics.InitiallyReachableAnchorGroups, diagnostics.PreservedAnchorGroups, diagnostics.LostAnchorSamples,
            RequiredRouteLengths = diagnostics.RequiredRoutes.Select(route => route.Count).ToArray(),
            RequiredTargetCounts = diagnostics.RequiredRouteTargets.Select(targets => targets.Count).ToArray(),
            diagnostics.SearchPasses, diagnostics.PrimaryPlaced, diagnostics.FallbackPlaced,
            RouteLengths = diagnostics.Routes.ToDictionary(pair => pair.Key, pair => pair.Value.Count)
        });
        Assert.AreEqual(dungeon.Rooms.Count, diagnostics.TotalCells);
        Assert.AreEqual(eligible.Count, diagnostics.EligibleRooms, "Density must count actual eligible rooms separately from generated passage cells.");
        Assert.AreEqual(requested, diagnostics.Requested);
        Assert.IsTrue(positions.Count <= requested);
        Assert.AreEqual(positions.Count, positions.Select(position => position.RoomIndex).Distinct().Count());
        Assert.AreEqual(positions.Count, diagnostics.Routes.Count, "Every selected chest needs a final entrance-to-approach route witness.");
        AssertSearchChoice(diagnostics, positions.Count);
        AssertRequiredRoutes(layout, world, diagnostics, positions, sphere, policy.Placement!);
        if (safeRooms.Length >= requested
            && diagnostics.Rejections.GetValueOrDefault("selected-route-preservation") == 0
            && diagnostics.Rejections.GetValueOrDefault("selected-chest-clearance") == 0)
        {
            Assert.AreEqual(requested, positions.Count, "Enough mutually unblocked verified rooms must meet the density target.");
        }
        int safeFloors = safeRooms.Select(room => layout.Generator.Rooms[room].Floor).Distinct().Count();
        Assert.IsTrue(positions.Select(position => layout.Generator.Rooms[position.RoomIndex].Floor).Distinct().Count()
            >= Math.Min(requested, safeFloors), "Placement must span reachable candidate floors when the requested count permits.");
        foreach (MissionChestPosition position in positions)
        {
            AssertCorpusPose(dungeon, layout, world, position, sphere, policy, eligible, positions);
            AssertEntranceRoute(layout, world, position, diagnostics.Routes[position.RoomIndex], positions, sphere, policy.Placement!);
        }
    }

    [TestMethod]
    [TestCategory("MissionChestGeometryRegression")]
    [DataRow(1, 20804467, 10)]
    [DataRow(1, 24051066, 9)]
    [DataRow(2, 39955845, 8)]
    [DataRow(1, 22270673, 16)]
    public void RealCaveDoorGroupRegressionsKeepVerifiedRoutesWithoutForcingPositions(int floors, int seed, int floorSize)
        => RealGeometryCorpusVerifiesDensitySafetyAndEntranceRoutes(341, floors, seed, floorSize);

    [TestMethod]
    [TestCategory("MissionChestGeometryRegression")]
    public void RealSubwayGeometryRegressionProducesACompleteValidatedResult()
        => RealGeometryCorpusVerifiesDensitySafetyAndEntranceRoutes(351, 1, 24653901, 14);

    [TestMethod]
    [TestCategory("MissionChestGeometryRegression")]
    public void RealCavePocketSupportRegressionProducesACompleteValidatedResult()
        => RealGeometryCorpusVerifiesDensitySafetyAndEntranceRoutes(341, 4, 68937720, 14);

    [TestMethod]
    [TestCategory("MissionChestZeroInvestigation")]
    public void FrozenCaveMissionReportsBoundedVerifiedPlacementRolls()
    {
        string? path = Environment.GetEnvironmentVariable("AO_REBIRTH_CHEST_FROZEN_LAYOUT_PATH");
        if (string.IsNullOrWhiteSpace(path))
            Assert.Inconclusive("Provide the private frozen mission through AO_REBIRTH_CHEST_FROZEN_LAYOUT_PATH.");
        QuestDungeonParameters? parameters = JsonSerializer.Deserialize<QuestDungeonParameters>(File.ReadAllText(path!));
        Assert.IsNotNull(parameters);
        var generator = new DungeonLayoutGenerator(Data.Value, new StubLogger());
        Assert.IsTrue(generator.TryGenerate(parameters, out DungeonLayout layout), "The exact frozen mission must reconstruct through its runtime generator.");
        MissionChestPolicy? policy = generator.ChestPolicy;
        Assert.IsNotNull(policy, "The selected runtime GameData must configure mission chests.");
        DungeonWorldLayout dungeon = DungeonCollisionBuilder.BuildLayout(RuntimeRoot(), layout.Generator);
        using PlayfieldWorldSimulation world = World(dungeon);
        AssertPlacementInputs(world, policy);
        var exclusions = new List<Vector3>();
        if (parameters.ObjectiveItem is { } item)
            exclusions.Add(new Vector3(item.X, item.Y, item.Z));
        if (parameters.ObjectiveNpc is { } npc)
            exclusions.Add(new Vector3(npc.X, npc.Y, npc.Z));
        foreach (int placementSeed in new[] { 7919, 104729, 32452843, 17112432, 24234914, 36845172, 40651165, 80990131 })
            ValidateZeroInvestigationRoll("frozen", layout, dungeon, world, policy, placementSeed, exclusions,
                parameters.LayoutPlan!.FloorCount, parameters.LayoutPlan.FloorSize);
    }

    [TestMethod]
    [TestCategory("MissionChestZeroInvestigation")]
    [DataRow(1, 19024074, 9)]
    [DataRow(1, 22480131, 10)]
    [DataRow(2, 34928853, 10)]
    [DataRow(2, 38384910, 8)]
    [DataRow(5, 82433732, 12)]
    [DataRow(1, 20071364, 11)]
    [DataRow(2, 34719395, 8)]
    public void RealCaveZeroCasesReportBoundedSafeOpportunities(int floors, int seed, int size)
    {
        MissionChestPolicy policy = ReadPolicy();
        var (layout, dungeon) = Build(341, floors, seed, size);
        using PlayfieldWorldSimulation world = World(dungeon);
        AssertPlacementInputs(world, policy);
        foreach (int placementSeed in new[] { 7919, 104729, 32452843, unchecked(seed ^ (341 * 7919) ^ (size * 104729)) })
        {
            var positions = ValidateZeroInvestigationRoll("configured", layout, dungeon, world, policy, placementSeed, [], floors, size);
            if (seed is 19024074 or 38384910 or 22480131)
                Assert.IsTrue(positions.Count > 0, "The bounded recovery must retain a verified chest in this measured safe cave layout.");
            if (policy.RollChestCount(EligibleRooms(layout, dungeon, policy).Count, new Random(placementSeed)) != 0)
                continue;
            MissionChestPolicy probe = policy.Clone();
            probe.RoomDensity = 1;
            probe.CountVariationFraction = 0;
            ValidateZeroInvestigationRoll("zero-target-capacity", layout, dungeon, world, probe, placementSeed, [], floors, size);
        }
    }

    [TestMethod]
    [TestCategory("MissionChestCriticalCaves")]
    [DataRow(2, 34928853, 10)]
    [DataRow(5, 82433732, 12)]
    public void CriticalDocumentedCaveCaseRunsOneVerifiedPlacementRoll(int floors, int seed, int size)
    {
        MissionChestPolicy policy = ReadPolicy();
        var (layout, dungeon) = Build(341, floors, seed, size);
        using PlayfieldWorldSimulation world = World(dungeon);
        AssertPlacementInputs(world, policy);
        ValidateZeroInvestigationRoll("integration-critical", layout, dungeon, world, policy, 7919, [], floors, size);
    }

    [TestMethod]
    [TestCategory("MissionChestZeroInvestigation")]
    public void ProvenSingleEligibleCaveRoomReceivesMinimumSafeTarget()
    {
        MissionChestPolicy policy = ReadPolicy();
        var (layout, dungeon) = Build(341, 2, 34719395, 8);
        using PlayfieldWorldSimulation world = World(dungeon);
        AssertPlacementInputs(world, policy);
        Assert.AreEqual(1, EligibleRooms(layout, dungeon, policy).Count);
        foreach (int placementSeed in new[] { 7919, 104729, 32452843, unchecked(34719395 ^ (341 * 7919) ^ (8 * 104729)) })
        {
            var positions = ValidateZeroInvestigationRoll("minimum-enabled", layout, dungeon, world, policy, placementSeed, [], 2, 8);
            Assert.AreEqual(1, positions.Count, "The measured safe eligible room must not disappear through count rounding.");
        }
    }

    [TestMethod]
    public void CompactPlacementDiagnosticsPreserveResultsWithoutRouteWitnesses()
    {
        MissionChestPolicy policy = ReadPolicy();
        var (layout, dungeon) = Build(324, 1);
        using PlayfieldWorldSimulation world = World(dungeon);
        AssertPlacementInputs(world, policy);
        var full = new MissionChestPlacementDiagnostics();
        var compact = new MissionChestPlacementDiagnostics { CaptureRouteWitnesses = false };
        var first = MissionChestPlacement.Plan(dungeon, layout, world, Data.Value, Templates.Value, [], policy, new Random(7919), full);
        var second = MissionChestPlacement.Plan(dungeon, layout, world, Data.Value, Templates.Value, [], policy, new Random(7919), compact);
        Assert.IsTrue(first.Count > 0, "The compact comparison requires an actual verified population.");
        CollectionAssert.AreEqual(first.ToArray(), second.ToArray());
        CollectionAssert.AreEquivalent(full.CandidateCounts.ToArray(), compact.CandidateCounts.ToArray());
        CollectionAssert.AreEquivalent(full.Rejections.ToArray(), compact.Rejections.ToArray());
        Assert.AreEqual(full.InitiallyReachableAnchorGroups, compact.InitiallyReachableAnchorGroups);
        Assert.AreEqual(full.PreservedAnchorGroups, compact.PreservedAnchorGroups);
        Assert.AreEqual(full.LostAnchorSamples, compact.LostAnchorSamples);
        Assert.AreEqual(0, compact.Routes.Count);
        Assert.AreEqual(0, compact.RequiredRoutes.Count);
        Assert.AreEqual(0, compact.RequiredRouteTargets.Count);
    }

    void AssertPlacementInputs(PlayfieldWorldSimulation world, MissionChestPolicy policy)
    {
        Assert.IsNotNull(policy.SpawnDefinition);
        int templateId = checked((int)policy.SpawnDefinition.Stats.Single(stat => stat.StatId == (int)CharacterStat.StaticInstance).Value);
        bool templateFound = Templates.Value.TryGet(templateId, out ItemTemplate template);
        int Effective(CharacterStat stat, int fallback)
        {
            ChestSpawnStat? placed = policy.SpawnDefinition.Stats.FirstOrDefault(value => value.StatId == (int)stat);
            return placed != null ? unchecked((int)placed.Value)
                : templateFound && template.Stats.TryGetValue(stat, out int value) ? value : fallback;
        }
        int mesh = Effective(CharacterStat.Mesh, 0), scale = Effective(CharacterStat.Scale, 100);
        bool dynelSphere = Data.Value.TryGetDynelCollisionSphere(templateId, mesh, scale, out float radius, out float centerY);
        bool chestSphere = MissionChestPlacement.TryGetChestSphere(Data.Value, Templates.Value, policy, out _);
        TestContext.WriteLine("MISSION_CHEST_INPUTS " + JsonSerializer.Serialize(new
        {
            Surface = world.Surface != null, Player = Data.Value.PlayerCollisionSphere, TemplateId = templateId,
            TemplateFound = templateFound, TemplateStatCount = templateFound ? template.Stats.Count : 0,
            TemplateMesh = templateFound && template.Stats.TryGetValue(CharacterStat.Mesh, out int rawMesh) ? (int?)rawMesh : null,
            TemplateScale = templateFound && template.Stats.TryGetValue(CharacterStat.Scale, out int rawScale) ? (int?)rawScale : null,
            EffectiveMesh = mesh, EffectiveScale = scale, DynelSphere = dynelSphere,
            Radius = radius, CenterY = centerY, ChestSphere = chestSphere
        }));
        Assert.IsNotNull(world.Surface, "The exact layout needs a collision surface before placement.");
        Assert.IsNotNull(Data.Value.PlayerCollisionSphere, "The selected GameData needs player collision geometry.");
        Assert.IsTrue(templateFound, "The configured chest template must exist in the selected current item catalog.");
        Assert.IsTrue(dynelSphere, "The configured chest template/mesh/scale must resolve current collision geometry.");
        Assert.IsTrue(chestSphere, "Chest placement preflight must accept the verified inputs.");
    }

    IReadOnlyList<MissionChestPosition> ValidateZeroInvestigationRoll(string stage, DungeonLayout layout, DungeonWorldLayout dungeon,
        PlayfieldWorldSimulation world, MissionChestPolicy policy, int placementSeed, IReadOnlyList<Vector3> exclusions,
        int layoutFloorCount, int floorSize)
    {
        Assert.IsTrue(MissionChestPlacement.TryGetChestSphere(Data.Value, Templates.Value, policy, out var sphere));
        var diagnostics = new MissionChestPlacementDiagnostics();
        var elapsed = Stopwatch.StartNew();
        var positions = MissionChestPlacement.Plan(dungeon, layout, world, Data.Value, Templates.Value,
            exclusions, policy, new Random(placementSeed), diagnostics);
        elapsed.Stop();
        Dictionary<int, RoomShape> eligible = EligibleRooms(layout, dungeon, policy);
        WallMetric[] walls = positions.Select(position => MeasureWalls(dungeon, position, sphere.CenterY)).ToArray();
        TestContext.WriteLine("MISSION_CHEST_ZERO_BASELINE " + JsonSerializer.Serialize(new
        {
            Stage = stage, Style = layout.Generator.Style, layout.Seed, PlacementSeed = placementSeed,
            LayoutFloorCount = layoutFloorCount, FloorSize = floorSize,
            diagnostics.TotalCells, Eligible = eligible.Count, diagnostics.Requested, Placed = positions.Count,
            DistinctRooms = positions.Select(position => position.RoomIndex).Distinct().Count(),
            WallMinimum = walls.Length == 0 ? (float?)null : walls.Min(wall => wall.Nearest),
            WallMean = walls.Length == 0 ? (float?)null : walls.Average(wall => wall.Nearest),
            WallMaximum = walls.Length == 0 ? (float?)null : walls.Max(wall => wall.Nearest),
            diagnostics.CandidateCounts, diagnostics.ReachableCandidateCounts, diagnostics.AnchorCounts,
            diagnostics.ReachableAnchorCounts, diagnostics.Rejections, diagnostics.SearchPasses,
            diagnostics.PrimaryPlaced, diagnostics.FallbackPlaced, diagnostics.InitiallyReachableAnchorGroups,
            diagnostics.RecoveryPrimaryPlaced, diagnostics.RecoveryFallbackPlaced,
            diagnostics.PreservedAnchorGroups, ElapsedMilliseconds = elapsed.ElapsedMilliseconds,
            Floors = positions.GroupBy(position => layout.Generator.Rooms[position.RoomIndex].Floor)
                .ToDictionary(group => group.Key, group => group.Count()), Positions = positions
        }));
        Assert.AreEqual(policy.RollChestCount(eligible.Count, new Random(placementSeed)), diagnostics.Requested);
        Assert.IsTrue(positions.Count <= diagnostics.Requested);
        Assert.AreEqual(positions.Count, positions.Select(position => position.RoomIndex).Distinct().Count());
        AssertSearchChoice(diagnostics, positions.Count);
        AssertRequiredRoutes(layout, world, diagnostics, positions, sphere, policy.Placement!);
        foreach (MissionChestPosition position in positions)
        {
            AssertCorpusPose(dungeon, layout, world, position, sphere, policy, eligible, positions);
            AssertEntranceRoute(layout, world, position, diagnostics.Routes[position.RoomIndex], positions, sphere, policy.Placement!);
            foreach (Vector3 exclusion in exclusions)
                AssertClear(new Vector3(position.X, position.Y, position.Z), exclusion,
                    policy.Placement!.ObjectiveClearance, sphere.Radius, policy.Placement.MaxWalkStep);
        }
        return positions;
    }

    void WriteCorpusResult(object result)
    {
        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        });
        TestContext.WriteLine("MISSION_CHEST_CORPUS " + json);
        string? output = Environment.GetEnvironmentVariable("AO_REBIRTH_CHEST_VALIDATION_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
            return;
        lock (CorpusOutputLock)
        {
            string path = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, json + Environment.NewLine);
        }
    }

    [TestMethod]
    [TestCategory("MissionChestSearchDiagnostic")]
    [DataRow(5, 1, 10)]
    [DataRow(5, 7, 10)]
    [DataRow(1, 19024074, 9)]
    [DataRow(1, 18709887, 14)]
    [DataRow(1, 20804467, 10)]
    public void RealCaveSearchBudgetsExposeValidatedRoomOpportunities(int floors, int layoutSeed, int floorSize)
    {
        MissionChestPolicy baseline = ReadPolicy();
        var (layout, dungeon) = Build(341, floors, layoutSeed, floorSize);
        using PlayfieldWorldSimulation world = World(dungeon);
        Assert.IsTrue(MissionChestPlacement.TryGetChestSphere(Data.Value, Templates.Value, baseline, out var sphere));
        Dictionary<int, RoomShape> eligible = EligibleRooms(layout, dungeon, baseline);
        int corpusPlacementSeed = unchecked(layoutSeed ^ (341 * 7919) ^ (floorSize * 104729));
        var landingFacts = layout.FloorTransitions.SelectMany(transition => new[] { transition.From, transition.To })
            .Select(endpoint =>
            {
                bool found = world.TryRaycastDown(ToAo(endpoint.Position + Vector3.UnitY * baseline.Placement!.FloorProbeLift),
                    baseline.Placement.FloorProbeLift + baseline.Placement.MaxFloorDrop, out AoVector3 floor);
                return new
                {
                    endpoint.RoomIndex, Floor = layout.Generator.Rooms[endpoint.RoomIndex].Floor,
                    Template = dungeon.Placements[endpoint.RoomIndex].Template.InstanceId,
                    Name = dungeon.Placements[endpoint.RoomIndex].Template.Name,
                    ConfiguredPoint = new[] { endpoint.Position.X, endpoint.Position.Y, endpoint.Position.Z },
                    OwnRoomGroundHeights = GroundHeights(dungeon, endpoint.RoomIndex,
                        new Vector2(endpoint.Position.X, endpoint.Position.Z)).Distinct().ToArray(),
                    ProjectedPoint = found ? new[] { floor.xf, floor.yf, floor.zf } : null,
                    LandingHeightDelta = found ? floor.yf - endpoint.Position.Y : (float?)null,
                    ProjectedOnOwnRoom = found && HasOwnFloor(dungeon, endpoint.RoomIndex,
                        new Vector3(floor.xf, floor.yf, floor.zf), 0.03f)
                };
            }).ToArray();
        foreach (int attempts in new[] { 128, 256 })
        {
            foreach (bool allRooms in new[] { false, true })
            {
                foreach (int placementSeed in new[] { corpusPlacementSeed, 7919, 104729, 32452843 }.Distinct())
                {
                    MissionChestPolicy policy = baseline.Clone();
                    policy.Placement!.CandidateAttemptsPerRoom = attempts;
                    if (allRooms)
                    {
                        policy.RoomDensity = 1;
                        policy.CountVariationFraction = 0;
                    }
                    Assert.IsTrue(policy.IsValid, "Diagnostics must stay within the existing supported policy bounds.");
                    TestContext.WriteLine("search diagnostic floors={0} layoutSeed={1} floorSize={2} attempts={3} allRooms={4} placementSeed={5}",
                        floors, layoutSeed, floorSize, attempts, allRooms, placementSeed);
                    var diagnostics = new MissionChestPlacementDiagnostics();
                    var elapsed = Stopwatch.StartNew();
                    var positions = MissionChestPlacement.Plan(dungeon, layout, world, Data.Value, Templates.Value,
                        [], policy, new Random(placementSeed), diagnostics);
                    elapsed.Stop();
                    Assert.AreEqual(eligible.Count, diagnostics.EligibleRooms);
                    Assert.AreEqual(policy.RollChestCount(eligible.Count, new Random(placementSeed)), diagnostics.Requested);
                    Assert.AreEqual(positions.Count, positions.Select(position => position.RoomIndex).Distinct().Count());
                    Assert.AreEqual(positions.Count, diagnostics.Routes.Count);
                    AssertSearchChoice(diagnostics, positions.Count);
                    AssertRequiredRoutes(layout, world, diagnostics, positions, sphere, policy.Placement);
                    foreach (MissionChestPosition position in positions)
                    {
                        AssertCorpusPose(dungeon, layout, world, position, sphere, policy, eligible, positions);
                        AssertEntranceRoute(layout, world, position, diagnostics.Routes[position.RoomIndex], positions, sphere, policy.Placement);
                    }
                    TestContext.WriteLine("MISSION_CHEST_SEARCH " + JsonSerializer.Serialize(new
                    {
                        Style = 341, Floors = floors, LayoutSeed = layoutSeed, FloorSize = floorSize,
                        PlacementSeed = placementSeed, AttemptsPerRoom = attempts, AllRooms = allRooms,
                        TotalCells = dungeon.Rooms.Count, EligibleRooms = eligible.Count, diagnostics.Requested,
                        Placed = positions.Count, PhysicalPosesValidated = true, RuntimeMilliseconds = elapsed.ElapsedMilliseconds,
                        diagnostics.CandidateCounts, diagnostics.ReachableCandidateCounts, diagnostics.Rejections,
                        diagnostics.ReferenceHeights, diagnostics.LocalHeightSeeds, diagnostics.OffReferencePlacements,
                        diagnostics.AnchorCounts, diagnostics.ReachableAnchorCounts, diagnostics.LandingHeightOffsets,
                        diagnostics.WallSectionCounts, diagnostics.WallSectionsTried,
                        diagnostics.InitiallyReachableAnchorGroups, diagnostics.PreservedAnchorGroups, diagnostics.LostAnchorSamples,
                        RequiredRouteLengths = diagnostics.RequiredRoutes.Select(route => route.Count).ToArray(),
                        RequiredTargetCounts = diagnostics.RequiredRouteTargets.Select(targets => targets.Count).ToArray(),
                        diagnostics.SearchPasses, diagnostics.PrimaryPlaced, diagnostics.FallbackPlaced,
                        LandingFacts = landingFacts,
                        CandidateFloors = diagnostics.CandidateCounts.Where(pair => pair.Value > 0)
                            .GroupBy(pair => layout.Generator.Rooms[pair.Key].Floor).ToDictionary(group => group.Key, group => group.Count()),
                        ReachableFloors = diagnostics.ReachableCandidateCounts.Where(pair => pair.Value > 0)
                            .GroupBy(pair => layout.Generator.Rooms[pair.Key].Floor).ToDictionary(group => group.Key, group => group.Count()),
                        PlacedFloors = positions.GroupBy(position => layout.Generator.Rooms[position.RoomIndex].Floor)
                            .ToDictionary(group => group.Key, group => group.Count()),
                        Positions = positions
                    }));
                }
            }
        }
    }

    [TestMethod]
    [TestCategory("MissionChestSearchDiagnostic")]
    public void RealCaveWalkingAnchorBudgetsProbeLowerFloorReachability()
    {
        MissionChestPolicy baseline = ReadPolicy();
        var (layout, dungeon) = Build(341, 5, 7, 10);
        using PlayfieldWorldSimulation world = World(dungeon);
        Assert.IsTrue(MissionChestPlacement.TryGetChestSphere(Data.Value, Templates.Value, baseline, out var sphere));
        Dictionary<int, RoomShape> eligible = EligibleRooms(layout, dungeon, baseline);
        int corpusPlacementSeed = unchecked(7 ^ (341 * 7919) ^ (10 * 104729));
        foreach (int candidates in new[] { 8, 16, 32 })
        {
            foreach (int placementSeed in new[] { 7919, corpusPlacementSeed })
            {
                MissionChestPolicy policy = baseline.Clone();
                policy.RoomDensity = 1;
                policy.CountVariationFraction = 0;
                policy.Placement!.CandidateAttemptsPerRoom = 256;
                policy.Placement.CandidatesPerRoom = candidates;
                Assert.IsTrue(policy.IsValid);
                TestContext.WriteLine("walking-anchor diagnostic candidatesPerRoom={0} placementSeed={1}", candidates, placementSeed);
                var diagnostics = new MissionChestPlacementDiagnostics();
                var elapsed = Stopwatch.StartNew();
                var positions = MissionChestPlacement.Plan(dungeon, layout, world, Data.Value, Templates.Value,
                    [], policy, new Random(placementSeed), diagnostics);
                elapsed.Stop();
                Assert.AreEqual(eligible.Count, diagnostics.Requested);
                Assert.AreEqual(positions.Count, positions.Select(position => position.RoomIndex).Distinct().Count());
                AssertSearchChoice(diagnostics, positions.Count);
                AssertRequiredRoutes(layout, world, diagnostics, positions, sphere, policy.Placement);
                foreach (MissionChestPosition position in positions)
                {
                    AssertCorpusPose(dungeon, layout, world, position, sphere, policy, eligible, positions);
                    AssertEntranceRoute(layout, world, position, diagnostics.Routes[position.RoomIndex], positions, sphere, policy.Placement);
                }
                TestContext.WriteLine("MISSION_CHEST_GRAPH " + JsonSerializer.Serialize(new
                {
                    Style = 341, Floors = 5, LayoutSeed = 7, FloorSize = 10, PlacementSeed = placementSeed,
                    AttemptsPerRoom = 256, CandidatesPerRoom = candidates, TotalCells = dungeon.Rooms.Count,
                    EligibleRooms = eligible.Count, diagnostics.Requested, Placed = positions.Count,
                    PhysicalPosesAndRequiredRoutesValidated = true, RuntimeMilliseconds = elapsed.ElapsedMilliseconds,
                    diagnostics.SearchPasses, diagnostics.PrimaryPlaced, diagnostics.FallbackPlaced,
                    diagnostics.AnchorCounts, diagnostics.ReachableAnchorCounts, diagnostics.LandingHeightOffsets,
                    diagnostics.CandidateCounts, diagnostics.ReachableCandidateCounts, diagnostics.Rejections,
                    diagnostics.InitiallyReachableAnchorGroups, diagnostics.PreservedAnchorGroups, diagnostics.LostAnchorSamples,
                    RoomFloors = dungeon.Placements.ToDictionary(room => room.Index, room => layout.Generator.Rooms[room.Index].Floor),
                    PlacedFloors = positions.GroupBy(position => layout.Generator.Rooms[position.RoomIndex].Floor)
                        .ToDictionary(group => group.Key, group => group.Count()),
                    LowerFloorRouteLengths = diagnostics.Routes.Where(pair => layout.Generator.Rooms[pair.Key].Floor == -3)
                        .ToDictionary(pair => pair.Key, pair => pair.Value.Count),
                    Positions = positions
                }));
            }
        }
    }

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
        var diagnostics = new MissionChestPlacementDiagnostics();
        var positions = MissionChestPlacement.Plan(dungeon, layout, world, Data.Value, Templates.Value, [], policy, new Random(7919), diagnostics);
        var restored = MissionChestPlacement.Plan(dungeon, layout, world, Data.Value, Templates.Value, [], policy, new Random(7919));
        CollectionAssert.AreEqual(positions.ToArray(), restored.ToArray());
        Dictionary<int, RoomShape> eligible = EligibleRooms(layout, dungeon, policy);
        int requested = policy.RollChestCount(eligible.Count, new Random(7919));
        WallMetric[] metrics = positions.Select(position => MeasureWalls(dungeon, position, sphere.CenterY)).ToArray();
        WritePlacementStatistics(style, floors, seed, layout, dungeon, eligible, requested, positions, metrics);
        TestContext.WriteLine("placement diagnostics " + JsonSerializer.Serialize(new
        {
            diagnostics.EligibleRooms, diagnostics.Requested, diagnostics.CandidateCounts,
            diagnostics.ReachableCandidateCounts, diagnostics.Rejections,
            diagnostics.ReferenceHeights, diagnostics.LocalHeightSeeds, diagnostics.OffReferencePlacements,
            diagnostics.AnchorCounts, diagnostics.ReachableAnchorCounts, diagnostics.LandingHeightOffsets,
            diagnostics.WallSectionCounts, diagnostics.WallSectionsTried,
            diagnostics.InitiallyReachableAnchorGroups, diagnostics.PreservedAnchorGroups, diagnostics.LostAnchorSamples,
            RequiredRouteLengths = diagnostics.RequiredRoutes.Select(route => route.Count).ToArray(),
            RequiredTargetCounts = diagnostics.RequiredRouteTargets.Select(targets => targets.Count).ToArray(),
            diagnostics.SearchPasses, diagnostics.PrimaryPlaced, diagnostics.FallbackPlaced,
            Routes = diagnostics.Routes.ToDictionary(pair => pair.Key, pair => pair.Value.Count)
        }));
        Assert.IsTrue(positions.Count > 0, "No verified positions in style " + style + " floors=" + floors);
        AssertSearchChoice(diagnostics, positions.Count);
        Assert.IsTrue(positions.Count <= requested && positions.Count <= eligible.Count,
            "A density target cannot override the one-chest-per-eligible-room capacity or geometric safety.");
        if (style is 321 or 324)
            Assert.AreEqual(requested, positions.Count,
                "These measured representative layouts have enough safe ordinary rooms to meet the configured target.");
        Assert.AreEqual(positions.Count, positions.Select(position => position.RoomIndex).Distinct().Count(),
            "Separate coordinates in the same actual generated room are not separate chest rooms.");
        Assert.AreEqual(positions.Count, positions.Select(position => (position.X, position.Y, position.Z)).Distinct().Count());
        AssertRequiredRoutes(layout, world, diagnostics, positions, sphere, policy.Placement!);
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
            Assert.IsTrue(diagnostics.Routes.TryGetValue(position.RoomIndex, out var route));
            AssertEntranceRoute(layout, world, position, route!, positions, sphere, policy.Placement);
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
            int requested = policy.RollChestCount(eligible.Count, new Random(randomSeed));
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
        WritePlacementStatistics(324, 1, 12345, layout, dungeon, eligible, eligible.Count, positions,
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

    static void AssertCorpusPose(DungeonWorldLayout dungeon, DungeonLayout layout, PlayfieldWorldSimulation world,
        MissionChestPosition position, CollisionSphereData sphere, MissionChestPolicy policy,
        IReadOnlyDictionary<int, RoomShape> eligible, IReadOnlyList<MissionChestPosition> positions)
    {
        MissionChestPlacementPolicy placement = policy.Placement!;
        Vector3 point = new(position.X, position.Y, position.Z);
        Assert.IsTrue(eligible.ContainsKey(position.RoomIndex), "A chest is in a passage, hall, entrance or transition cell.");
        Assert.IsTrue(dungeon.Rooms[position.RoomIndex].Contains(point));
        Assert.IsTrue(HasOwnFloor(dungeon, position.RoomIndex, point, 0.03f));
        Assert.IsTrue(world.TryRaycastDown(ToAo(point + Vector3.UnitY), 2, out AoVector3 floor));
        Assert.AreEqual(position.Y, floor.yf, 0.003f);
        Assert.IsTrue(position.YawDegrees is >= 0 and < 360);
        WallMetric wall = MeasureWalls(dungeon, position, sphere.CenterY);
        Assert.IsTrue(wall.Nearest >= sphere.Radius + placement.CollisionPadding - 0.03f
            && wall.Nearest <= sphere.Radius + 2 * placement.CollisionPadding + 0.03f,
            "The chest is not tightly adjacent to a finite transformed wall triangle: " + wall.Nearest);
        AssertNoTriangleOverlap(dungeon, position, sphere);
        AssertRoomSideApproach(world, dungeon, position, sphere, placement);
        AssertClear(point, new Vector3(layout.Spawn.xf, layout.Spawn.yf, layout.Spawn.zf),
            placement.EntryClearance, sphere.Radius, placement.MaxWalkStep);
        foreach (DungeonDoorPlacement door in dungeon.Doors)
        {
            AssertClear(point, door.Position, placement.DoorClearance, sphere.Radius, placement.MaxWalkStep);
            foreach (float height in DoorFloorHeights(dungeon, door))
            {
                AssertClear(point, new Vector3(door.Position.X, height, door.Position.Z),
                    placement.DoorClearance, sphere.Radius, placement.MaxWalkStep);
            }
        }
        foreach (DungeonFloorTransition transition in layout.FloorTransitions)
        {
            AssertClear(point, transition.From.Position, placement.DoorClearance, sphere.Radius, placement.MaxWalkStep);
            AssertClear(point, transition.To.Position, placement.DoorClearance, sphere.Radius, placement.MaxWalkStep);
        }
        foreach (MissionChestPosition other in positions.Where(other => other.RoomIndex != position.RoomIndex))
        {
            AssertClear(point, new Vector3(other.X, other.Y, other.Z),
                MathF.Max(placement.ChestClearance, 2 * sphere.Radius + placement.CollisionPadding), sphere.Radius, placement.MaxWalkStep);
        }
    }

    static void AssertRequiredRoutes(DungeonLayout layout, PlayfieldWorldSimulation world,
        MissionChestPlacementDiagnostics diagnostics, IReadOnlyList<MissionChestPosition> positions,
        CollisionSphereData chest, MissionChestPlacementPolicy policy)
    {
        Assert.AreEqual(diagnostics.InitiallyReachableAnchorGroups, diagnostics.PreservedAnchorGroups,
            "Every initially reachable doorway side, entrance and supported floor landing must remain reachable.");
        Assert.AreEqual(diagnostics.PreservedAnchorGroups, diagnostics.RequiredRoutes.Count,
            "Every preserved required group needs a final physical route witness.");
        Assert.AreEqual(diagnostics.RequiredRoutes.Count, diagnostics.RequiredRouteTargets.Count);
        var checkedEdges = new HashSet<(Vector3 From, Vector3 To, bool Transition)>();
        for (int index = 0; index < diagnostics.RequiredRoutes.Count; index++)
        {
            IReadOnlyList<MissionChestRouteStep> route = diagnostics.RequiredRoutes[index];
            Assert.IsTrue(route.Count > 0);
            Assert.IsTrue(diagnostics.RequiredRouteTargets[index].Any(target => Vector3.Distance(route[^1].Point, target) < 0.03f),
                "A physical route must end at a member of its own required doorway/landing group.");
            AssertEntranceRoute(layout, world, null, route, positions, chest, policy, checkedEdges);
        }
    }

    static void AssertSearchChoice(MissionChestPlacementDiagnostics diagnostics, int placed)
    {
        if (diagnostics.Requested == 0)
            return;
        Assert.IsTrue(placed >= diagnostics.PrimaryPlaced, "A fallback cannot reduce the primary valid population.");
        Assert.AreEqual(new[] { diagnostics.PrimaryPlaced, diagnostics.FallbackPlaced ?? 0,
                diagnostics.RecoveryPrimaryPlaced ?? 0, diagnostics.RecoveryFallbackPlaced ?? 0 }.Max(), placed,
            "Choose one complete verified population, rather than merging unchecked positions from separate searches.");
        Assert.AreEqual(1 + (diagnostics.FallbackPlaced.HasValue ? 1 : 0)
            + (diagnostics.RecoveryPrimaryPlaced.HasValue ? 1 : 0) + (diagnostics.RecoveryFallbackPlaced.HasValue ? 1 : 0),
            diagnostics.SearchPasses);
        Assert.IsTrue(diagnostics.SearchPasses is >= 1 and <= 4);
        if (diagnostics.RecoveryPrimaryPlaced.HasValue)
        {
            Assert.AreEqual(0, diagnostics.PrimaryPlaced);
            Assert.AreEqual(0, diagnostics.FallbackPlaced);
            Assert.AreEqual(diagnostics.RecoveryPrimaryPlaced == 0, diagnostics.RecoveryFallbackPlaced.HasValue,
                "Zero recovery stops at its first positive complete population.");
        }
    }

    static void AssertEntranceRoute(DungeonLayout layout, PlayfieldWorldSimulation world,
        MissionChestPosition? position, IReadOnlyList<MissionChestRouteStep> route,
        IReadOnlyList<MissionChestPosition> positions, CollisionSphereData chest, MissionChestPlacementPolicy policy,
        HashSet<(Vector3 From, Vector3 To, bool Transition)>? checkedEdges = null)
    {
        Assert.IsTrue(route.Count > 0, "A selected chest lacks an entrance route witness.");
        CollisionSphereData player = Data.Value.PlayerCollisionSphere!;
        Vector3 spawn = new(layout.Spawn.xf, layout.Spawn.yf, layout.Spawn.zf);
        Assert.IsTrue(world.TryRaycastDown(ToAo(spawn + Vector3.UnitY * policy.FloorProbeLift),
            policy.FloorProbeLift + policy.MaxFloorDrop, out AoVector3 start));
        Assert.IsTrue(Vector3.Distance(route[0].Point, new Vector3(start.xf, start.yf, start.zf)) < 0.03f,
            "The route witness must begin at the actual supported mission entrance.");
        for (int index = 1; index < route.Count; index++)
        {
            Vector3 from = route[index - 1].Point, to = route[index].Point;
            if (checkedEdges != null && !checkedEdges.Add((from, to, route[index].Transition)))
                continue;
            if (route[index].Transition)
            {
                Assert.IsTrue(layout.FloorTransitions.Any(transition => ValidTravel(transition.From, transition.To)
                    || ValidTravel(transition.To, transition.From)), "An unverified teleport cannot bridge a failed walking segment.");
            }
            else
            {
                AssertWalkingSegment(world, from, to, positions, chest, player, policy);
            }

            bool ValidTravel(DungeonTransitionEndpoint source, DungeonTransitionEndpoint target)
                => Vector3.Distance(from, source.Position) <= LootableDynel.OpenRange + 0.005f
                    && Vector2.Distance(new Vector2(to.X, to.Z), new Vector2(target.Position.X, target.Position.Z)) < 0.03f
                    && MathF.Abs(to.Y - target.Position.Y) <= policy.MaxWalkStep + 0.03f
                    && world.HasLineOfSight(ToAo(from + Vector3.UnitY * player.CenterY),
                        ToAo(source.Position + Vector3.UnitY * player.CenterY));
        }
        if (position == null)
            return;
        Vector3 feet = new(position.X, position.Y, position.Z), approach = route[^1].Point;
        Assert.IsTrue(Vector3.Distance(approach, feet) <= LootableDynel.OpenRange);
        Vector2 direction = Vector2.Normalize(new Vector2(approach.X - feet.X, approach.Z - feet.Z));
        float yaw = position.YawDegrees * MathF.PI / 180;
        Assert.IsTrue(Vector2.Dot(direction, new Vector2(MathF.Sin(yaw), MathF.Cos(yaw))) > 0.99f,
            "The persisted yaw must face the reachable interaction area.");
        Assert.IsTrue(world.HasLineOfSight(ToAo(approach + Vector3.UnitY * player.CenterY),
            ToAo(feet + Vector3.UnitY * chest.CenterY)));
    }

    static void AssertWalkingSegment(PlayfieldWorldSimulation world, Vector3 from, Vector3 to,
        IReadOnlyList<MissionChestPosition> positions, CollisionSphereData chest, CollisionSphereData player,
        MissionChestPlacementPolicy policy)
    {
        int samples = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(from, to) / policy.WalkSampleSpacing));
        Assert.IsTrue(samples <= MissionChestPlacement.MaximumWalkSamplesPerLink);
        Vector3 previous = from;
        for (int sample = 1; sample <= samples; sample++)
        {
            Vector3 expected = Vector3.Lerp(from, to, sample / (float)samples);
            expected.Y = previous.Y;
            Assert.IsTrue(world.TryRaycastDown(ToAo(expected + Vector3.UnitY * policy.FloorProbeLift),
                policy.FloorProbeLift + policy.MaxFloorDrop, out AoVector3 floor), "A route segment crosses unsupported space.");
            Vector3 current = new(floor.xf, floor.yf, floor.zf);
            Assert.IsTrue(MathF.Abs(current.Y - previous.Y) <= policy.MaxWalkStep + 0.005f,
                $"Route step exceeds the supported height limit: {previous} -> {current}.");
            Vector3 a = previous + Vector3.UnitY * (player.CenterY + policy.CollisionPadding);
            Vector3 b = current + Vector3.UnitY * (player.CenterY + policy.CollisionPadding);
            Assert.IsFalse(world.TrySphereCast(ToAo(a), ToAo(b), player.Radius, out _, out _), $"Route blocked: {a} -> {b}.");
            Assert.IsFalse(world.TrySphereCast(ToAo(b), ToAo(a), player.Radius, out _, out _), $"Reverse route blocked: {b} -> {a}.");
            foreach (MissionChestPosition position in positions)
            {
                if (MathF.Abs(position.Y - previous.Y) > 2 * chest.Radius + policy.MaxWalkStep
                    && MathF.Abs(position.Y - current.Y) > 2 * chest.Radius + policy.MaxWalkStep)
                    continue;
                Vector2 delta = new(current.X - previous.X, current.Z - previous.Z);
                Vector2 offset = new(position.X - previous.X, position.Z - previous.Z);
                float fraction = delta.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(offset, delta) / delta.LengthSquared(), 0, 1) : 0;
                Assert.IsTrue((offset - delta * fraction).Length() >= chest.Radius + player.Radius + policy.CollisionPadding - 0.005f,
                    "A final entrance route crosses a placed chest's occupied footprint.");
            }
            previous = current;
        }
        Assert.IsTrue(MathF.Abs(previous.Y - to.Y) <= policy.MaxWalkStep + 0.005f);
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

    internal readonly record struct WallMetric(float Nearest, float Second);

    internal static WallMetric MeasureWalls(DungeonWorldLayout dungeon, MissionChestPosition position, float centerY)
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

    static IEnumerable<string> SupportingSurfaces(DungeonWorldLayout dungeon, Vector3 point, float tolerance)
    {
        foreach (CollisionTriangleMesh mesh in dungeon.Collision.SurfaceMeshes)
        {
            int index = 0;
            foreach (CollisionTriangle triangle in mesh.Triangles)
            {
                int triangleIndex = index++;
                Vector3 a = mesh.Vertices[triangle.A], b = mesh.Vertices[triangle.B], c = mesh.Vertices[triangle.C];
                Vector2 ab = new(b.X - a.X, b.Z - a.Z), ac = new(c.X - a.X, c.Z - a.Z);
                Vector2 delta = new(point.X - a.X, point.Z - a.Z);
                float determinant = ab.X * ac.Y - ab.Y * ac.X;
                if (MathF.Abs(determinant) < 1e-8f)
                    continue;
                float u = (delta.X * ac.Y - delta.Y * ac.X) / determinant;
                float v = (ab.X * delta.Y - ab.Y * delta.X) / determinant;
                float height = a.Y + u * (b.Y - a.Y) + v * (c.Y - a.Y);
                if (u < -0.001f || v < -0.001f || u + v > 1.001f || MathF.Abs(height - point.Y) > tolerance)
                    continue;
                Vector3 normal = Vector3.Cross(b - a, c - a);
                if (normal.LengthSquared() < 1e-8f)
                    continue;
                yield return $"cell={mesh.CellId}, source={mesh.Source}, triangle={triangleIndex}, "
                    + $"height={height}, normalY={Vector3.Normalize(normal).Y}";
            }
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
                Vector3 current = new(intermediate.xf, intermediate.yf, intermediate.zf);
                bool hasOwnFloor = HasOwnFloor(dungeon, position.RoomIndex, current, 0.03f);
                string ownFloorFailure = "A valid endpoint cannot bridge an unsupported gap in the room-side pocket.";
                if (!hasOwnFloor)
                {
                    Vector3 runtimeProjection = approach;
                    bool runtimeSupported = true;
                    for (int replay = 1; replay <= sample; replay++)
                    {
                        Vector3 runtimeExpected = Vector3.Lerp(approach, supported, replay / (float)samples);
                        runtimeExpected.Y = runtimeProjection.Y;
                        if (!world.TryRaycastDown(ToAo(runtimeExpected + Vector3.UnitY * policy.FloorProbeLift),
                                policy.FloorProbeLift + policy.MaxFloorDrop, out AoVector3 runtimeHit))
                        {
                            runtimeSupported = false;
                            break;
                        }
                        runtimeProjection = new Vector3(runtimeHit.xf, runtimeHit.yf, runtimeHit.zf);
                    }
                    float[] ownHeights = GroundHeights(dungeon, position.RoomIndex, new Vector2(current.X, current.Z)).ToArray();
                    int[] otherGroundRooms = Enumerable.Range(0, dungeon.Rooms.Count)
                        .Where(room => room != position.RoomIndex && HasOwnFloor(dungeon, room, current, 0.03f)).ToArray();
                    ownFloorFailure += $" Room={position.RoomIndex}, chest={feet}, yaw={position.YawDegrees}, "
                        + $"approach={approach}, endpoint={supported}, sample={sample}/{samples}, expected={between}, "
                        + $"projected={current}, previous={previous}, ownGroundHeights=[{string.Join(",", ownHeights)}], "
                        + $"otherGroundRooms=[{string.Join(",", otherGroundRooms)}], "
                        + $"supportingSurfaces=[{string.Join(" | ", SupportingSurfaces(dungeon, current, 0.03f))}], "
                        + $"runtimePreviousYProjection={runtimeProjection}, runtimeSupported={runtimeSupported}, "
                        + $"runtimeHasOwnFloor={runtimeSupported && HasOwnFloor(dungeon, position.RoomIndex, runtimeProjection, 0.03f)}, "
                        + $"runtimeSupportingSurfaces=[{string.Join(" | ", SupportingSurfaces(dungeon, runtimeProjection, 0.03f))}].";
                }
                Assert.IsTrue(hasOwnFloor, ownFloorFailure);
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

    internal static (DungeonLayout, DungeonWorldLayout) Build(int style, int floors, int seed = 42, int floorSize = 10)
    {
        var generator = new DungeonGenerator(RuntimeRoot());
        var loaded = generator.LoadStyle(style);
        DungeonGenerationResult generated = generator.Generate(new DungeonGenerationRequest
        {
            StyleId = style, Seed = seed, FloorCount = floors, FloorSize = floorSize, FloorHeight = 64,
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

    internal static PlayfieldWorldSimulation World(DungeonWorldLayout dungeon)
        => PlayfieldWorldSimulation.Create(1, new PlayfieldGeometryData { Collision = dungeon.Collision }, null,
            new DestinationsCatalog(), Data.Value, new StubLogger(), Templates.Value);

    internal static MissionChestPolicy ReadPolicy()
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
