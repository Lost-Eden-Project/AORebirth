namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AORebirth.Core.GameData;
using AORebirth.DungeonGenerator;
using AORebirth.World.Collision;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Quests.Dungeons;

[TestClass]
[DoNotParallelize]
public sealed class ProceduralDungeonFoundationTests
{
    static readonly int[] Seeds = [42, 12345, 987654];

    public TestContext TestContext { get; set; } = null!;

    static readonly Lazy<GameDataStore> RuntimeData = new(() => new GameDataStore(new StubLogger(), null, RuntimeRoot()));

    static string RuntimeRoot()
    {
        string? root = Environment.GetEnvironmentVariable(GameDataPaths.EnvironmentVariableName);
        Assert.IsFalse(string.IsNullOrWhiteSpace(root), "Set AO_REBIRTH_GAMEDATA_PATH to the existing full GameData.");
        Assert.IsTrue(Directory.Exists(root), "The real room resources are required for foundation validation.");
        return root!;
    }

    [TestMethod]
    public void PreviouslyAcceptedV2SeedsKeepExactGeneratorPayloadAndSpawnBits()
    {
        string path = Path.Combine(RepositoryRoot(), "AORebirth", "Server", "ZoneEngine_New.Tests", "Fixtures", "ProceduralDungeonV2Golden.json");
        using JsonDocument evidence = JsonDocument.Parse(File.ReadAllText(path));
        var generator = new DungeonLayoutGenerator(RuntimeData.Value, new StubLogger());
        foreach (JsonElement row in evidence.RootElement.GetProperty("Layouts").EnumerateArray())
        {
            Assert.IsTrue(generator.TryGenerate(row.GetProperty("Seed").GetInt32(), 2,
                row.GetProperty("BuildingInstance").GetInt32(), out DungeonLayout layout));
            Assert.AreEqual(row.GetProperty("PayloadHex").GetString(), Convert.ToHexString(layout.GeneratorPayload));
            Assert.AreEqual(row.GetProperty("SpawnXBits").GetUInt32(), BitConverter.SingleToUInt32Bits(layout.Spawn.xf));
            Assert.AreEqual(row.GetProperty("SpawnYBits").GetUInt32(), BitConverter.SingleToUInt32Bits(layout.Spawn.yf));
            Assert.AreEqual(row.GetProperty("SpawnZBits").GetUInt32(), BitConverter.SingleToUInt32Bits(layout.Spawn.zf));
        }
    }

    [TestMethod]
    [DataRow(2, false)]
    [DataRow(3, false)]
    [DataRow(220, false)]
    [DataRow(250, false)]
    [DataRow(2, true)]
    [DataRow(3, true)]
    [DataRow(220, true)]
    [DataRow(250, true)]
    public void RuntimeFreezesSoloOrTeamPlanAndRecreatesItsExactLayout(int quality, bool team)
    {
        var generator = new DungeonLayoutGenerator(RuntimeData.Value, new StubLogger());
        var parameters = new QuestDungeonParameters
        {
            Seed = 12345, Quality = quality, EntranceInstance = unchecked((int)0xC00001F9),
            DungeonPlayfield = QuestDungeonIds.FirstPlayfield + 101
        };
        Assert.IsTrue(generator.ConfigureNewDungeon(parameters, team));
        Assert.AreEqual(DungeonLayoutGenerator.CurrentVersion, parameters.GeneratorVersion);
        Assert.AreEqual(team, parameters.IsTeam);
        Assert.IsNotNull(parameters.LayoutPlan);
        string frozen = parameters.ToJson();
        Assert.IsTrue(generator.TryGenerate(parameters, out DungeonLayout original));
        Assert.IsTrue(QuestDungeonParameters.TryParse(frozen, out QuestDungeonParameters restored));
        Assert.AreEqual(frozen, restored.ToJson());
        Assert.IsTrue(new DungeonLayoutGenerator(RuntimeData.Value, new StubLogger()).TryGenerate(restored, out DungeonLayout replay));
        CollectionAssert.AreEqual(original.GeneratorPayload, replay.GeneratorPayload);
        Assert.AreEqual(original.BossRoomIndex, replay.BossRoomIndex);
        Assert.AreEqual(original.FloorTransitions.Count, replay.FloorTransitions.Count);
        Assert.AreEqual(parameters.EntranceInstance, original.Generator.Identity.Instance);
        if (team)
        {
            Assert.IsNotNull(original.BossRoomIndex);
            Assert.IsTrue(original.FloorTransitions.Count > 0);
        }
        else
        {
            Assert.IsNull(original.BossRoomIndex);
            Assert.AreEqual(0, original.FloorTransitions.Count);
        }
        TestContext.WriteLine("Runtime quality={0} team={1} style={2} rooms={3} transitions={4}", quality, team,
            original.Generator.Style, original.Generator.Rooms.Length, original.FloorTransitions.Count);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("invalidStyle")]
    [DataRow("invalidFloors")]
    [DataRow("scopeMismatch")]
    public void V3StoredParametersRejectInvalidOrMismatchedPlans(string invalid)
    {
        var generator = new DungeonLayoutGenerator(RuntimeData.Value, new StubLogger());
        var parameters = new QuestDungeonParameters
        {
            Seed = 42, Quality = 2, EntranceInstance = unchecked((int)0xC00001F9),
            DungeonPlayfield = QuestDungeonIds.FirstPlayfield + 101
        };
        Assert.IsTrue(generator.ConfigureNewDungeon(parameters, team: true));
        switch (invalid)
        {
            case "missing": parameters.LayoutPlan = null; break;
            case "invalidStyle": parameters.LayoutPlan!.StyleId = 0; break;
            case "invalidFloors": parameters.LayoutPlan!.FloorCount = 0; break;
            case "scopeMismatch": parameters.IsTeam = false; break;
        }
        Assert.IsFalse(QuestDungeonParameters.TryParse(parameters.ToJson(), out _));
        Assert.IsFalse(generator.TryGenerate(parameters, out _));
    }

    [TestMethod]
    public void HistoricalSeedOnlyParametersStillParseAndProduceTheGoldenV2Layout()
    {
        string historical = "{\"Seed\":42,\"GeneratorVersion\":2,\"DungeonPlayfield\":"
            + (QuestDungeonIds.FirstPlayfield + 101) + ",\"EntranceInstance\":-1073741319}";
        Assert.IsTrue(QuestDungeonParameters.TryParse(historical, out QuestDungeonParameters parameters));
        Assert.IsNull(parameters.LayoutPlan);
        var generator = new DungeonLayoutGenerator(RuntimeData.Value, new StubLogger());
        Assert.IsTrue(generator.TryGenerate(parameters, out DungeonLayout restored));
        Assert.IsTrue(generator.TryGenerate(42, 2, parameters.EntranceInstance, out DungeonLayout original));
        CollectionAssert.AreEqual(original.GeneratorPayload, restored.GeneratorPayload);
    }

    static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AI_START_HERE.md"))) return directory.FullName;
        throw new DirectoryNotFoundException("Cannot locate this test build's repository root.");
    }

    [TestMethod]
    [DataRow(321)]
    [DataRow(324)]
    [DataRow(341)]
    [DataRow(351)]
    public void SoloOriginalRoomsAreConnectedWithoutBossFloorOrTransitions(int style)
    {
        string root = RuntimeRoot();
        var generator = new DungeonGenerator(root);
        var payloads = new HashSet<string>(StringComparer.Ordinal);
        foreach (int seed in Seeds)
        {
            var request = new DungeonGenerationRequest
            {
                StyleId = style, Seed = seed, FloorCount = 1, FloorSize = 12,
                FloorHeight = 64, BuildingInstance = unchecked((int)0xC00001F9), UseFloorTransitions = true
            };
            DungeonGenerationResult result = generator.Generate(request);
            Assert.IsTrue(result.Validation.IsValid, "Invalid solo layout for style " + style + " seed " + seed);
            Assert.IsNull(result.BossRoomIndex);
            Assert.AreEqual(0, result.Transitions.Count);
            Assert.IsTrue(result.Rooms.All(room => room.Floor == 0));
            Assert.AreEqual(request.BuildingInstance, result.Generator.Identity.Instance);
            AssertPhysicalPlacementAndReachability(root, result);
            payloads.Add(Convert.ToHexString(result.Generator.ToByteArray()));
            TestContext.WriteLine("Solo style={0} seed={1} rooms={2} attempts={3}", style, seed, result.Rooms.Count, result.Attempts);
        }
        Assert.IsTrue(payloads.Count > 1, "Different seeds must produce different room arrangements.");
    }

    [TestMethod]
    [DataRow(324, 2, false)]
    [DataRow(341, 2, true)]
    [DataRow(341, 5, true)]
    public void TeamFloorsHaveBidirectionalRoutesAndSeparateReachableBossRoom(int style, int floors, bool descending)
    {
        string root = RuntimeRoot();
        var generator = new DungeonGenerator(root);
        foreach (int seed in Seeds)
        {
            var request = new DungeonGenerationRequest
            {
                StyleId = style, Seed = seed, FloorCount = floors, FloorSize = 12,
                FloorHeight = 64, DescendingFloors = descending, BossFloor = true,
                UseFloorTransitions = true, BuildingInstance = unchecked((int)0xC00001F9)
            };
            DungeonGenerationResult result = generator.Generate(request);
            Assert.IsTrue(result.Validation.IsValid, "Invalid team layout for style " + style + " seed " + seed);
            Assert.IsNotNull(result.BossRoomIndex);
            int boss = result.BossRoomIndex.Value;
            Assert.AreEqual(descending ? 1 - floors : floors - 1, result.Rooms[boss].Floor);
            Assert.AreEqual(1, result.Rooms.Count(room => Math.Abs(room.Floor) == floors - 1), "The boss occupies a separate final floor.");
            CollectionAssert.AreEqual(Enumerable.Range(0, floors).ToArray(), result.Rooms.Select(room => Math.Abs(room.Floor)).Distinct().OrderBy(floor => floor).ToArray());
            Assert.AreEqual(floors - 1, result.Transitions.Count);
            foreach (DungeonFloorTransition transition in result.Transitions)
            {
                Assert.AreEqual(Math.Abs(result.Rooms[transition.From.RoomIndex].Floor) + 1, Math.Abs(result.Rooms[transition.To.RoomIndex].Floor));
                Assert.IsTrue(transition.From.PlatformTemplateId > 0 && transition.To.PlatformTemplateId > 0);
                Assert.IsTrue(transition.From.ButtonTemplateId > 0 && transition.To.ButtonTemplateId > 0);
            }
            AssertPhysicalPlacementAndReachability(root, result);
            DungeonGenerationResult replay = new DungeonGenerator(root).Generate(request);
            CollectionAssert.AreEqual(result.Generator.ToByteArray(), replay.Generator.ToByteArray());
            Assert.AreEqual(result.BossRoomIndex, replay.BossRoomIndex);
            Assert.AreEqual(result.Transitions.Count, replay.Transitions.Count);
            for (int i = 0; i < result.Transitions.Count; i++)
            {
                Assert.AreEqual(result.Transitions[i].From, replay.Transitions[i].From);
                Assert.AreEqual(result.Transitions[i].To, replay.Transitions[i].To);
            }
            TestContext.WriteLine("Team style={0} seed={1} floors={2} rooms={3} transitions={4} bossRoom={5} attempts={6}",
                style, seed, floors, result.Rooms.Count, result.Transitions.Count, boss, result.Attempts);
        }
    }

    [TestMethod]
    public void ConnectedValidationRejectsFloorsWithoutTheirTransitionLinks()
    {
        var generator = new DungeonGenerator(RuntimeRoot());
        DungeonGenerationResult result = generator.Generate(new DungeonGenerationRequest
        {
            StyleId = 324, Seed = 42, FloorCount = 2, FloorSize = 12,
            FloorHeight = 64, BossFloor = true, UseFloorTransitions = true,
            BuildingInstance = unchecked((int)0xC00001F9)
        });
        DungeonValidation disconnected = DungeonLayoutValidator.ValidateConnected(result.Rooms,
            result.GridWidth, result.GridHeight, Array.Empty<DungeonFloorTransition>());
        Assert.IsFalse(disconnected.IsValid);
        Assert.IsTrue(disconnected.UnreachableRooms.Count > 0, "An unlinked boss floor must fail the same reachability validation used by generation.");
    }

    [TestMethod]
    public void MultiFloorTransitionsRejectMissingFloorHeight()
    {
        var generator = new DungeonGenerator(RuntimeRoot());
        Assert.ThrowsExactly<ArgumentException>(() => generator.Generate(new DungeonGenerationRequest
        {
            StyleId = 324, Seed = 42, FloorCount = 2, FloorSize = 12,
            BossFloor = true, UseFloorTransitions = true, FloorHeight = 0
        }));
    }

    [TestMethod]
    public void EveryConfiguredProfileBoundaryBuildsConnectedLayoutsAcrossSixteenSeeds()
    {
        string root = RuntimeRoot();
        using JsonDocument policy = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, DungeonLayoutGenerator.GenerationFileName)));
        var generator = new DungeonGenerator(root);
        var failures = new List<string>();
        int checkedLayouts = 0;
        foreach (JsonElement profile in policy.RootElement.GetProperty("Profiles").EnumerateArray())
        {
            bool team = profile.GetProperty("Team").GetBoolean();
            int floors = profile.GetProperty("Floors").GetInt32();
            foreach (JsonElement styleValue in profile.GetProperty("Styles").EnumerateArray())
            {
                int style = styleValue.GetInt32();
                foreach (int size in new[] { profile.GetProperty("MinFloorSize").GetInt32(), profile.GetProperty("MaxFloorSize").GetInt32() }.Distinct())
                {
                    for (int seed = 0; seed < 16; seed++)
                    {
                        string context = "team=" + team + " style=" + style + " floors=" + floors + " size=" + size + " seed=" + seed;
                        try
                        {
                            DungeonGenerationResult result = generator.Generate(new DungeonGenerationRequest
                            {
                                StyleId = style, Seed = seed, FloorCount = floors, FloorSize = size, BossFloor = team,
                                FloorHeight = policy.RootElement.GetProperty("FloorHeight").GetInt32(),
                                LockedDoorChancePercent = policy.RootElement.GetProperty("LockedDoorChancePercent").GetInt32(),
                                DescendingFloors = generator.LoadStyle(style).Transitions?.DescendingFloors ?? false,
                                UseFloorTransitions = true, BuildingInstance = unchecked((int)0xC00001F9)
                            });
                            Assert.IsTrue(result.Validation.IsValid);
                            Assert.AreEqual(team, result.BossRoomIndex.HasValue);
                            Assert.AreEqual(floors - 1, result.Transitions.Count);
                            AssertPhysicalPlacementAndReachability(root, result, buildCollision: false);
                            checkedLayouts++;
                        }
                        catch (Exception exception)
                        {
                            failures.Add(context + ": " + exception.Message);
                        }
                    }
                }
            }
        }
        TestContext.WriteLine("Configured boundary layouts checked={0}; failures={1}", checkedLayouts, failures.Count);
        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
        Assert.IsTrue(checkedLayouts > 0);
    }

    static void AssertPhysicalPlacementAndReachability(string root, DungeonGenerationResult result, bool buildCollision = true)
    {
        Assert.IsTrue(result.Rooms.Count > 1);
        var occupied = new HashSet<(int Floor, int X, int Z)>();
        var adjacency = Enumerable.Range(0, result.Rooms.Count).Select(_ => new HashSet<int>()).ToArray();
        for (int i = 0; i < result.Rooms.Count; i++)
        {
            PlacedRoom room = result.Rooms[i];
            foreach ((int x, int z) in room.Cells)
            {
                Assert.IsTrue(x >= 0 && z >= 0 && x < result.GridWidth && z < result.GridHeight, "Room footprint outside grid.");
                Assert.IsTrue(occupied.Add((room.Floor, x, z)), "Overlapping room footprints.");
            }
            for (int j = i + 1; j < result.Rooms.Count; j++)
            {
                if (room.Floor == result.Rooms[j].Floor && room.Doors.Any(door => result.Rooms[j].Doors.Any(door.Meets)))
                {
                    adjacency[i].Add(j);
                    adjacency[j].Add(i);
                }
            }
        }

        DungeonWorldLayout? collision = buildCollision ? DungeonCollisionBuilder.BuildLayout(root, result.Generator) : null;
        if (collision != null)
        {
            Assert.AreEqual(result.Rooms.Count, collision.Placements.Count);
            Assert.IsTrue(collision.Collision.HasCollision, "Generated room geometry must create real collision.");
        }
        foreach (DungeonFloorTransition transition in result.Transitions)
        {
            if (collision != null)
            {
                AssertEndpoint(collision, transition.From);
                AssertEndpoint(collision, transition.To);
            }
            adjacency[transition.From.RoomIndex].Add(transition.To.RoomIndex);
            adjacency[transition.To.RoomIndex].Add(transition.From.RoomIndex);
        }
        var reached = new HashSet<int> { 0 };
        var queue = new Queue<int>();
        queue.Enqueue(0);
        while (queue.TryDequeue(out int room))
            foreach (int neighbor in adjacency[room])
                if (reached.Add(neighbor)) queue.Enqueue(neighbor);
        Assert.AreEqual(result.Rooms.Count, reached.Count, "Every room must be reachable from the entrance using door or floor-transition links.");
    }

    static void AssertEndpoint(DungeonWorldLayout collision, DungeonTransitionEndpoint endpoint)
    {
        Assert.IsTrue(endpoint.RoomIndex >= 0 && endpoint.RoomIndex < collision.Placements.Count);
        Assert.IsTrue(float.IsFinite(endpoint.Position.X) && float.IsFinite(endpoint.Position.Y) && float.IsFinite(endpoint.Position.Z));
        DungeonRoomBounds bounds = collision.Rooms.Single(room => room.Index == endpoint.RoomIndex);
        Assert.IsTrue(bounds.Contains(endpoint.Position), "Transition endpoint is outside its original room geometry: room " + endpoint.RoomIndex);
    }
}
