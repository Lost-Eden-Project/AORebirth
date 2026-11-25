namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

using AORebirth.Core.GameData;
using AORebirth.World.Collision;

using LostEden.Protocol.GameData;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.Quests.Dungeons;

/// <summary>
/// Team request 32: players riding a lift in an Alien Mothership mission were met by several aliens as they arrived.
/// The Mothership population now places at most 3 aliens within 16 m of each other and at most 2 in a room a lift
/// stands in (either end), and its chests are 15% fewer. Builds the accepted mission's dungeon the way the engine does
/// (DungeonLayoutGenerator, the collision layout, QuestDungeonPlayfield.AddDistrictSpawns and ChestSites, the chest
/// plan) against the runtime GameData (AO_REBIRTH_GAMEDATA_PATH).
/// </summary>
[TestClass]
public sealed class MothershipLiftArrivalTests
{
    const int PlayfieldId = 0x0FF00386;
    const int Seeds = 12;

    /// <summary>One quality in each of the Mothership's four layout bands (1-24, 25-99, 100-179, 180-250).</summary>
    static readonly int[] Qualities = [12, 60, 140, 215];

    static readonly Lazy<GameDataStore?> Runtime = new(() =>
    {
        string? root = Environment.GetEnvironmentVariable(GameDataPaths.EnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(root) || !File.Exists(Path.Combine(root, "Missions", "Missions.json")))
            return null;
        return new GameDataStore(new Logger(), null, root);
    });

    [TestMethod]
    public void ArrivingByLiftMeetsAtMostTwoAliensAndNoAlienJoinsThreeWithin16m()
    {
        GameDataStore data = Data();
        int liftRooms = 0, aliens = 0;
        foreach (int quality in Qualities)
        {
            for (int seed = 1; seed <= Seeds; seed++)
            {
                (DungeonLayout layout, DungeonWorldLayout dungeon) = Mothership(data, quality, seed);
                Assert.AreEqual(3, layout.Population.MaxGroup);
                Assert.AreEqual(16f, layout.Population.GroupRadius);
                Assert.AreEqual(2, layout.Population.LiftRoomMaxNpcs);
                Spawned spawned = Spawn(data, layout, dungeon, quality);
                Assert.IsTrue(spawned.Points.Count > 0, "QL " + quality + " seed " + seed + " placed no aliens.");
                aliens += spawned.Points.Count;

                foreach (int room in LiftRooms(layout))
                {
                    liftRooms++;
                    int inRoom = spawned.Used.Count(used => used.Room == room);
                    Assert.IsTrue(inRoom <= 2, "QL " + quality + " seed " + seed + ": lift room " + room + " holds " + inRoom + " aliens.");
                }
                // Every alien was placed with at most 2 others already within 16 m of it.
                for (int i = 0; i < spawned.Points.Count; i++)
                {
                    int near = 0;
                    for (int j = 0; j < i; j++)
                        if (System.Numerics.Vector3.Distance(spawned.Points[i], spawned.Points[j]) <= 16f)
                            near++;
                    Assert.IsTrue(near <= 2, "QL " + quality + " seed " + seed + ": an alien joined " + near + " others within 16 m.");
                }
            }
        }
        Assert.IsTrue(liftRooms > 0, "No Mothership layout had a lift.");
        Assert.IsTrue(aliens > 0);
    }

    /// <summary>The defect: under the old population (4 within 8 m, no lift-room limit) lift rooms held 3 or more aliens.</summary>
    [TestMethod]
    public void TheOldPopulationPutMoreThanTwoAliensWhereALiftLands()
    {
        GameDataStore data = Data();
        int worst = 0;
        foreach (int quality in Qualities)
        {
            for (int seed = 1; seed <= Seeds; seed++)
            {
                (DungeonLayout layout, DungeonWorldLayout dungeon) = Mothership(data, quality, seed);
                Spawned spawned = Spawn(data, layout with { Population = Old(layout.Population) }, dungeon, quality);
                foreach (int room in LiftRooms(layout))
                    worst = Math.Max(worst, spawned.Used.Count(used => used.Room == room));
            }
        }
        Assert.IsTrue(worst > 2, "The old population never put more than 2 aliens in a lift room (" + worst + ").");
    }

    [TestMethod]
    public void TheObjectiveInALiftRoomCountsAsOneOfItsTwo()
    {
        GameDataStore data = Data();
        int checkedRooms = 0;
        for (int seed = 1; seed <= Seeds; seed++)
        {
            (DungeonLayout layout, DungeonWorldLayout dungeon) = Mothership(data, 140, seed);
            Spawned open = Spawn(data, layout, dungeon, 140);
            // A lift room the population filled to the limit; the objective NPC stands at one of its free points.
            int room = LiftRooms(layout).FirstOrDefault(index => open.Used.Count(used => used.Room == index) == 2, -1);
            if (room < 0)
                continue;
            DungeonRoomPlacement placement = dungeon.Placements[room];
            PlayfieldDistrictEntry district = Core.Playfield.AcgDungeonPlayfield.LoadStyleDistricts(data.RootPath, 386, new Logger())!
                .Districts!.First(entry => entry != null && entry.DistrictIndex == DistrictOf(data, placement));
            int free = Enumerable.Range(0, district.SpawnPoints!.Length)
                .FirstOrDefault(index => district.SpawnPoints[index]?.Position is { Length: >= 3 } && !open.Used.Contains((room, index)), -1);
            if (free < 0)
                continue;
            float[] raw = district.SpawnPoints[free].Position;
            System.Numerics.Vector3 at = placement.TransformTemplatePoint(new System.Numerics.Vector3(raw[0], raw[1], raw[2]));
            var objective = new MissionNpcObjectiveBinding { QuestId = "GLIFT", RoomIndex = room, X = at.X, Y = at.Y, Z = at.Z };

            Spawned withObjective = Spawn(data, layout, dungeon, 140, objective);
            Assert.IsTrue(withObjective.Used.Count(used => used.Room == room) <= 1,
                "Seed " + seed + ": the objective's lift room holds it and " + withObjective.Used.Count(used => used.Room == room) + " aliens.");
            checkedRooms++;
        }
        if (checkedRooms == 0)
            Assert.Inconclusive("No Mothership layout filled a lift room to the limit with a point left free.");
    }

    [TestMethod]
    public void TheMothershipHasAboutFifteenPercentFewerChests()
    {
        GameDataStore data = Data();
        DungeonChestBlock chests = Resolve(data, 140).Chests;
        Assert.AreEqual(1.7, chests.RoomDensity);
        var before = JsonSerializer.Deserialize<DungeonChestBlock>(JsonSerializer.Serialize(chests))!;
        before.RoomDensity = 2.0;

        int now = 0, then = 0;
        foreach (int quality in Qualities)
        {
            for (int seed = 1; seed <= Seeds; seed++)
            {
                (DungeonLayout layout, DungeonWorldLayout dungeon) = Mothership(data, quality, seed);
                // The chest sites the population left free, planned as MissionChestPopulation plans them.
                QuestDungeonPlayfield current = Spawn(data, layout, dungeon, quality).Playfield;
                now += MissionChestPlacement.Plan(current.ChestSites(dungeon, chests), chests, new Random(unchecked(layout.Seed * 48271))).Count;
                QuestDungeonPlayfield old = Spawn(data, layout with { Population = Old(layout.Population) }, dungeon, quality).Playfield;
                then += MissionChestPlacement.Plan(old.ChestSites(dungeon, before), before, new Random(unchecked(layout.Seed * 48271))).Count;
            }
        }
        double ratio = (double)now / then;
        Assert.IsTrue(then > 0);
        Assert.IsTrue(ratio is > 0.80 and < 0.90, "Chests now " + now + ", before " + then + ": " + ratio.ToString("P1") + " of before.");
    }

    [TestMethod]
    public void AMissionAcceptedBeforeTheLimitBuildsWithoutIt()
    {
        // The resolved population is stored with the accepted mission (QuestDungeonParameters.Dungeon) and rebuilt from it.
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        DungeonPopulationBlock stored = JsonSerializer.Deserialize<DungeonPopulationBlock>(
            """{ "Npcs": ["ARZWEH"], "SpawnChancePercent": 35, "LevelSpreadPercent": 10, "MaxGroup": 4, "GroupRadius": 8, "AggroRangePercent": 100, "HelpRangePercent": 100 }""",
            options)!;
        Assert.IsNull(stored.LiftRoomMaxNpcs);
        Assert.IsNull(stored.Problem());
        stored.LiftRoomMaxNpcs = -1;
        Assert.IsNotNull(stored.Problem(), "A negative lift-room limit is refused.");
        stored.LiftRoomMaxNpcs = 0;
        Assert.IsNull(stored.Problem(), "0 keeps lift rooms empty.");
        Assert.AreEqual(2, new DungeonPopulationBlock().Over(new DungeonPopulationBlock { LiftRoomMaxNpcs = 2 }).LiftRoomMaxNpcs,
            "A preset's limit carries through a block that leaves it out.");
    }

    sealed record Spawned(QuestDungeonPlayfield Playfield, List<System.Numerics.Vector3> Points, HashSet<(int Room, int Point)> Used);

    static IEnumerable<int> LiftRooms(DungeonLayout layout)
        => layout.FloorTransitions.SelectMany(transition => new[] { transition.From.RoomIndex, transition.To.RoomIndex }).Distinct();

    /// <summary>The population as it was before the request: 4 within 8 m, no lift-room limit.</summary>
    static DungeonPopulationBlock Old(DungeonPopulationBlock population)
    {
        DungeonPopulationBlock old = JsonSerializer.Deserialize<DungeonPopulationBlock>(JsonSerializer.Serialize(population))!;
        old.MaxGroup = 4;
        old.GroupRadius = 8f;
        old.LiftRoomMaxNpcs = null;
        return old;
    }

    static ResolvedDungeon Resolve(GameDataStore data, int quality)
    {
        var generator = new DungeonLayoutGenerator(data, new Logger());
        MissionDefinition mothership = generator.Mission("alienMothership")
            ?? throw new AssertFailedException("Missions.json has no alienMothership mission.");
        return generator.Resolve(mothership.Dungeon, quality, out string? problem)
            ?? throw new AssertFailedException("The Mothership dungeon does not resolve at QL " + quality + ": " + problem);
    }

    /// <summary>An accepted Mothership mission's layout (as QuestDungeonService accepts and builds it) and its collision.</summary>
    static (DungeonLayout Layout, DungeonWorldLayout Dungeon) Mothership(GameDataStore data, int quality, int seed)
    {
        var generator = new DungeonLayoutGenerator(data, new Logger());
        Assert.IsTrue(generator.IsAvailable, "The dungeon generator is unavailable.");
        ResolvedDungeon resolved = Resolve(data, quality);
        var parameters = new QuestDungeonParameters { Seed = seed, Quality = quality, EntranceInstance = unchecked((int)0xC00010F4) };
        Assert.IsTrue(generator.ConfigureNewDungeon(parameters, resolved, tokenReward: false, playerSide: 0, out string? problem), problem);
        Assert.IsTrue(generator.TryGenerate(parameters, out DungeonLayout layout));
        return (layout, DungeonCollisionBuilder.BuildLayout(data.RootPath, layout.Generator));
    }

    static int DistrictOf(GameDataStore data, DungeonRoomPlacement placement)
    {
        PlayfieldDistrictsData districts = Core.Playfield.AcgDungeonPlayfield.LoadStyleDistricts(data.RootPath, 386, new Logger())!;
        int zone = placement.Template.InstanceId;
        return districts.ZoneToDistrictMap is { } map && zone >= 0 && zone < map.Length ? map[zone] : zone;
    }

    /// <summary>Runs the dungeon's own population spawn (AddDistrictSpawns) and returns where its aliens stand, in order.</summary>
    static Spawned Spawn(GameDataStore data, DungeonLayout layout, DungeonWorldLayout dungeon, int quality,
        MissionNpcObjectiveBinding? objective = null)
    {
        var logger = new Logger();
        var playfield = (QuestDungeonPlayfield)RuntimeHelpers.GetUninitializedObject(typeof(QuestDungeonPlayfield));
        var locality = new PlayfieldLocality(PlayfieldId, null);
        locality.ApplyDungeonRooms(dungeon);
        var spawns = new HashSpawnSystem(playfield, Blank<SpawnService>(), data, logger);
        Set(typeof(HashSpawnSystem), spawns, "_grid", locality.Grid);
        ServiceProvider services = new ServiceCollection().AddSingleton(spawns).BuildServiceProvider();

        Set(typeof(Playfield), playfield, "<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = PlayfieldId });
        Set(typeof(Playfield), playfield, "_serviceProvider", services);
        Set(typeof(Playfield), playfield, "_gameData", data);
        Set(typeof(Playfield), playfield, "_logger", logger);
        Set(typeof(Playfield), playfield, "_pendingStatRebases", new ConcurrentDictionary<Character, byte>());
        Set(typeof(Playfield), playfield, "_lifecycleSync", new System.Threading.Lock());
        Set(typeof(AcgDungeonPlayfield), playfield, "<Layout>k__BackingField", layout);
        Set(typeof(AcgDungeonPlayfield), playfield, "<Quality>k__BackingField", quality);
        Set(typeof(QuestDungeonPlayfield), playfield, "<QuestId>k__BackingField", "GLIFT");
        Set(typeof(QuestDungeonPlayfield), playfield, "_usedPoints", new HashSet<(int Room, int Point)>());
        if (objective != null)
            Set(typeof(QuestDungeonPlayfield), playfield, "_objectiveNpc", objective);

        typeof(QuestDungeonPlayfield).GetMethod("AddDistrictSpawns", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(playfield, [dungeon]);

        var points = (List<HashSpawnPoint>)typeof(HashSpawnSystem)
            .GetField("_allPoints", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(spawns)!;
        var used = (HashSet<(int Room, int Point)>)typeof(QuestDungeonPlayfield)
            .GetField("_usedPoints", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(playfield)!;
        Assert.AreEqual(points.Count, used.Count, "Every placed alien took one district point.");
        return new Spawned(playfield,
            points.Select(point => new System.Numerics.Vector3(point.Sites[0].Centre.xf, point.Sites[0].Centre.yf, point.Sites[0].Centre.zf)).ToList(),
            used);
    }

    static GameDataStore Data()
    {
        if (Runtime.Value is not { } data || !Directory.Exists(Path.Combine(data.RootPath, "Dungeons")))
        {
            Assert.Inconclusive("Set AO_REBIRTH_GAMEDATA_PATH to the private GameData (Missions.json, Dungeons).");
            throw new InvalidOperationException();
        }
        return data;
    }

    static T Blank<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    static void Set(Type type, object target, string field, object value)
        => (type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(type.Name, field)).SetValue(target, value);

    sealed class Logger : IZoneLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(Exception exception, string message) { }
        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }
}
