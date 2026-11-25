namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using AORebirth.Core.GameData;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Quests.Dungeons;

/// <summary>
/// Team request 14: an RK terminal mission's monster count did not follow its level. Every quality got the same 9-16
/// rooms and every room with spawn points at least one monster at the slider's SpawnChancePercent, so a QL 10 mission
/// held as many monsters as a QL 200 one (15-26 at the middle slider). The rkSolo layout and the rubika population now
/// have quality bands: smaller layouts, a share of rooms left empty (RoomChancePercent) and a scaled spawn chance
/// (SpawnScalePercent) at low levels. QuestDungeonPlayfield.AddDistrictSpawns spawns each room from
/// <see cref="DungeonPopulationBlock.RollOccupiedRooms"/> and <see cref="DungeonPopulationBlock.RollRoomCount"/>, which
/// these tests drive with the runtime presets, the mission's sliders and the styles' real spawn points.
/// </summary>
[TestClass]
public sealed class RkMissionDensityByLevelTests
{
    const int Dungeons = 4000;

    static readonly Lazy<GameDataStore?> RuntimeData = new(() =>
    {
        string? root = Environment.GetEnvironmentVariable(GameDataPaths.EnvironmentVariableName);
        return string.IsNullOrWhiteSpace(root) || !File.Exists(Path.Combine(root, "Missions", MissionDefinitions.FileName))
            ? null : new GameDataStore(new Logger(), null, root);
    });

    [TestMethod]
    public void BeforeTheBandsEveryRoomGotAMonsterAtEveryQuality()
    {
        // The population a mission froze before the bands: no room chance, no scale.
        var frozen = new DungeonPopulationBlock { SpawnChancePercent = 35 };
        bool[] occupied = frozen.RollOccupiedRooms(16, 1140174067);
        Assert.IsTrue(occupied.All(o => o), "Without a room chance every room is occupied.");
        var roll = new Random(5);
        for (int room = 0; room < 500; room++)
            Assert.IsTrue(frozen.RollRoomCount(roll, 1 + (room % 6), holdsTarget: false, occupied: true) >= 1,
                "Every room with spawn points got at least one monster.");
    }

    [TestMethod]
    public void RkSoloMonsterCountFollowsTheMissionLevel()
    {
        (DungeonPresets presets, MissionDefinition solo, Dictionary<int, int[]> points) = Load("rkSolo");
        var middle = new MissionSliderSettings();
        double previous = 0;
        double lowest = 0;
        foreach (int quality in new[] { 10, 50, 100, 200 })
        {
            Stats now = Simulate(presets, solo, points, quality, middle);
            Assert.IsTrue(now.PerMission > previous,
                "QL " + quality + ": " + now.PerMission.ToString("0.0") + " monsters, not more than the band below's " + previous.ToString("0.0"));
            // Live captures: about 1.0-1.3 a room overall, 1.2-1.6 in a room that has any, 10-40% of rooms empty.
            Assert.IsTrue(now.PerRoom <= 1.3, "QL " + quality + ": " + now.PerRoom.ToString("0.00") + " monsters a room");
            Assert.IsTrue(now.PerOccupiedRoom <= 1.6, "QL " + quality + ": " + now.PerOccupiedRoom.ToString("0.00") + " in an occupied room");
            Assert.IsTrue(now.EmptyShare is >= 0.10 and <= 0.45, "QL " + quality + ": " + now.EmptyShare.ToString("P0") + " of rooms empty");
            Assert.AreEqual(0, now.WithoutMonsters, "QL " + quality + ": a mission came out with no monster at all.");
            if (quality == 10)
                lowest = now.PerMission;
            previous = now.PerMission;
        }

        // Today's rule at QL 10: the same 9-16 rooms and every room occupied at the full chance.
        Stats before = Simulate(presets, solo, points, 10, middle, frozenBefore: true);
        Assert.IsTrue(lowest <= before.PerMission / 2,
            "QL 10: " + lowest.ToString("0.0") + " monsters against " + before.PerMission.ToString("0.0") + " before the bands");
        Assert.IsTrue(before.EmptyShare == 0, "Before the bands no room was left empty.");
    }

    [TestMethod]
    public void TheHeadOnStealthSliderStillMovesTheDensityAtEveryLevel()
    {
        (DungeonPresets presets, MissionDefinition solo, Dictionary<int, int[]> points) = Load("rkSolo");
        foreach (int quality in new[] { 10, 100, 200 })
        {
            Stats headOn = Simulate(presets, solo, points, quality, new MissionSliderSettings { HeadOnStealth = -100 });
            Stats stealth = Simulate(presets, solo, points, quality, new MissionSliderSettings { HeadOnStealth = 100 });
            Assert.IsTrue(headOn.PerOccupiedRoom > stealth.PerOccupiedRoom,
                "QL " + quality + ": Head On " + headOn.PerOccupiedRoom.ToString("0.00") + " not above Stealth " + stealth.PerOccupiedRoom.ToString("0.00"));
        }
    }

    [TestMethod]
    public void LowLevelMissionsAreSmallerWithAShorterCallForHelp()
    {
        (DungeonPresets presets, MissionDefinition solo, _) = Load("rkSolo");
        ResolvedDungeon low = Resolve(presets, solo, 10, new MissionSliderSettings());
        ResolvedDungeon high = Resolve(presets, solo, 200, new MissionSliderSettings());
        Assert.IsTrue(low.Layout.Rooms![1] < high.Layout.Rooms![0] && low.Layout.Rooms[1] < 16,
            "QL 10 layout " + string.Join("-", low.Layout.Rooms) + " is not smaller than QL 200's " + string.Join("-", high.Layout.Rooms));
        Assert.IsTrue(low.Population.AssistanceRadius < high.Population.AssistanceRadius,
            "Groups in a small layout's neighbouring rooms must not hear each other's call for help as far.");
        Assert.IsTrue(low.Population.GroupRadius >= high.Population.GroupRadius);
        Assert.IsNull(low.Population.Bands, "A resolved population carries no bands.");
        Assert.IsNull(low.Population.Quality);
        Assert.IsNotNull(low.Population.Mix ?? (object?)low.Population.Npcs, "The band keeps the preset's NPCs.");

        (DungeonPresets teamPresets, MissionDefinition team, _) = Load("rkTeam");
        ResolvedDungeon teamLow = Resolve(teamPresets, team, 10, new MissionSliderSettings());
        Assert.IsTrue(teamLow.Layout.Rooms![1] <= 10, "QL 10 team floors are smaller.");
        Assert.AreEqual(low.Population.RoomChancePercent, teamLow.Population.RoomChancePercent, "Team missions share the rubika bands.");
    }

    [TestMethod]
    public void AMissionAlwaysKeepsAtLeastOneMonsterRoom()
    {
        var none = new DungeonPopulationBlock { SpawnChancePercent = 0, RoomChancePercent = 0 };
        foreach (int seed in new[] { 1, 42, 1140174067, -7 })
        {
            bool[] occupied = none.RollOccupiedRooms(9, seed);
            Assert.AreEqual(1, occupied.Count(o => o), "At least one room keeps monsters, even at a room chance of 0.");
            CollectionAssert.AreEqual(occupied, none.RollOccupiedRooms(9, seed), "A rebuilt dungeon rolls the same rooms.");
            Assert.AreEqual(1, none.RollRoomCount(new Random(seed), 4, holdsTarget: false, occupied: true),
                "An occupied room gets one monster even when no point rolls.");
            Assert.AreEqual(0, none.RollRoomCount(new Random(seed), 4, holdsTarget: false, occupied: false));
            Assert.AreEqual(0, none.RollRoomCount(new Random(seed), 4, holdsTarget: true, occupied: true),
                "The kill target is its room's monster.");
        }
        Assert.AreEqual(0, none.RollOccupiedRooms(0, 3).Length);
    }

    /// <summary>
    /// A mission accepted before the change has no room chance or scale in its frozen population: its dungeon rebuilds
    /// with the same monsters in the same rooms, so a kill ledger's killed slots stay killed.
    /// </summary>
    [TestMethod]
    public void MissionsFrozenBeforeTheChangeRebuildWithTheSameSpawns()
    {
        const string frozenJson = """{ "Npcs": ["ANDR"], "SpawnChancePercent": 35, "LevelSpreadPercent": 10, "MaxGroup": 2, "GroupRadius": 8, "AggroRangePercent": 100, "HelpRangePercent": 100, "Loot": [] }""";
        DungeonPopulationBlock frozen = JsonSerializer.Deserialize<DungeonPopulationBlock>(frozenJson, DungeonRequest.JsonOptions)!;
        Assert.IsNull(frozen.Problem(), "An old frozen population still validates.");
        Assert.IsNull(frozen.RoomChancePercent);
        Assert.IsNull(frozen.SpawnScalePercent);
        Assert.AreEqual(35, frozen.PointChancePercent);

        foreach (int seed in new[] { 1, 99, 1140174067 })
        {
            for (int room = 0; room < 30; room++)
            {
                int points = 1 + (room % 7);
                bool holdsTarget = room % 11 == 0;
                var legacy = new Random(unchecked((seed * 7919) ^ ((room + 1) * 104729)));
                var now = new Random(unchecked((seed * 7919) ^ ((room + 1) * 104729)));
                // The rule before the change, as AddDistrictSpawns had it inline.
                int wanted = 0;
                for (int i = 0; i < points; i++)
                    if (legacy.Next(100) < 35)
                        wanted++;
                if (!holdsTarget)
                    wanted = Math.Max(1, wanted);
                Assert.AreEqual(wanted, frozen.RollRoomCount(now, points, holdsTarget, occupied: true));
                Assert.AreEqual(legacy.Next(), now.Next(), "The room's roll goes on to the same NPC and level draws.");
            }
        }
        string written = JsonSerializer.Serialize(frozen, DungeonRequest.JsonOptions);
        Assert.IsFalse(written.Contains("PointChancePercent", StringComparison.Ordinal), "The derived chance is not stored.");
        Assert.IsFalse(written.Contains("RoomChancePercent", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PopulationBandsMustHoldEveryQualityAskedFor()
    {
        var banded = new DungeonPopulationBlock
        {
            SpawnChancePercent = 35, MaxGroup = 2, GroupRadius = 8,
            Bands = [new DungeonPopulationBlock { Quality = [1, 99], RoomChancePercent = 60 }]
        };
        DungeonPopulationBlock? band = banded.ForQuality(50, out string? problem);
        Assert.IsNull(problem);
        Assert.AreEqual(60, band!.RoomChancePercent);
        Assert.AreEqual(35, band.SpawnChancePercent, "A field the band leaves out comes from the block.");
        Assert.IsNull(band.Bands);

        Assert.IsNull(banded.ForQuality(150, out problem));
        StringAssert.Contains(problem, "none for quality 150");

        banded.Bands[0].Bands = [new DungeonPopulationBlock { Quality = [1, 2] }];
        Assert.IsNull(banded.ForQuality(50, out problem));
        StringAssert.Contains(problem, "cannot hold bands");

        Assert.IsNotNull(new DungeonPopulationBlock { Bands = [] }.Over(Valid()).Bands, "Bands pass through Over like the layout's.");
        StringAssert.Contains(new DungeonPopulationBlock { Bands = [new()] }.Over(Valid()).Problem(), "not resolved");
        StringAssert.Contains(new DungeonPopulationBlock { RoomChancePercent = 101 }.Over(Valid()).Problem(), "RoomChancePercent");
        StringAssert.Contains(new DungeonPopulationBlock { SpawnScalePercent = -1 }.Over(Valid()).Problem(), "SpawnScalePercent");
        Assert.AreEqual(100, new DungeonPopulationBlock { SpawnChancePercent = 80, SpawnScalePercent = 400 }.PointChancePercent, "The scaled chance stays a percent.");
    }

    sealed record Stats(double PerMission, double PerRoom, double PerOccupiedRoom, double EmptyShare, int WithoutMonsters);

    /// <summary>
    /// Many dungeons of one mission at one quality and slider setting: each room draws a real room's spawn point count
    /// from one of the layout's styles, every room counted as having points (an upper bound: corridors without points
    /// stay empty in game).
    /// </summary>
    static Stats Simulate(DungeonPresets presets, MissionDefinition mission, Dictionary<int, int[]> points, int quality,
        MissionSliderSettings sliders, bool frozenBefore = false)
    {
        ResolvedDungeon dungeon = Resolve(presets, mission, quality, sliders);
        DungeonPopulationBlock population = dungeon.Population;
        int[] rooms = dungeon.Layout.Rooms!;
        if (frozenBefore)
        {
            population = new DungeonPopulationBlock { SpawnChancePercent = population.SpawnChancePercent };
            rooms = [9, 16];
        }
        var draw = new Random(quality * 31 + sliders.HeadOnStealth);
        long monsters = 0, roomCount = 0, occupiedRooms = 0, emptyRooms = 0;
        int withoutMonsters = 0;
        for (int d = 0; d < Dungeons; d++)
        {
            int seed = draw.Next();
            int[] stylePoints = points[dungeon.Layout.Styles![draw.Next(dungeon.Layout.Styles.Length)]];
            int count = draw.Next(rooms[0], rooms[1] + 1);
            bool[] occupied = population.RollOccupiedRooms(count, seed);
            int inDungeon = 0;
            for (int room = 0; room < count; room++)
            {
                var roll = new Random(unchecked((seed * 7919) ^ ((room + 1) * 104729)));
                int got = population.RollRoomCount(roll, stylePoints[draw.Next(stylePoints.Length)], holdsTarget: false, occupied[room]);
                inDungeon += got;
                if (got > 0)
                    occupiedRooms++;
                else
                    emptyRooms++;
            }
            monsters += inDungeon;
            roomCount += count;
            if (inDungeon == 0)
                withoutMonsters++;
        }
        return new Stats((double)monsters / Dungeons, (double)monsters / roomCount, (double)monsters / Math.Max(1, occupiedRooms),
            (double)emptyRooms / roomCount, withoutMonsters);
    }

    static ResolvedDungeon Resolve(DungeonPresets presets, MissionDefinition mission, int quality, MissionSliderSettings sliders)
    {
        ResolvedDungeon? dungeon = presets.Resolve(mission.Dungeon, quality, out string? problem);
        Assert.IsNotNull(dungeon, "QL " + quality + ": " + problem);
        mission.ApplySliders(dungeon, sliders);
        Assert.IsNull(dungeon.Problem(), "QL " + quality + " after the sliders");
        return dungeon;
    }

    /// <summary>The runtime presets, the named mission (Missions.json must load: every quality resolves) and the point counts of each style's rooms.</summary>
    static (DungeonPresets, MissionDefinition, Dictionary<int, int[]>) Load(string missionName)
    {
        GameDataStore? data = RuntimeData.Value;
        if (data == null)
            Assert.Inconclusive("Set AO_REBIRTH_GAMEDATA_PATH to the runtime GameData with Missions/Missions.json.");
        var logger = new Logger();
        DungeonPresets presets = DungeonPresets.Load(data!, logger);
        MissionDefinitions missions = MissionDefinitions.Load(data!.RootPath, presets, logger);
        MissionDefinition? mission = missions.Get(missionName);
        if (mission == null)
            Assert.Inconclusive("The runtime GameData has no " + missionName + " mission.");
        ResolvedDungeon sample = presets.Resolve(mission!.Dungeon, 100, out _)!;
        var points = new Dictionary<int, int[]>();
        foreach (int style in sample.Layout.Styles!)
        {
            PlayfieldDistrictsData? districts = AcgDungeonPlayfield.LoadStyleDistricts(data.RootPath, style, logger);
            int[] counts = districts?.Districts?.Where(d => d != null)
                .Select(d => (d.SpawnPoints ?? []).Count(p => p?.Position is { Length: >= 3 }))
                .Where(c => c > 0).ToArray() ?? [];
            if (counts.Length == 0)
                Assert.Inconclusive("Style " + style + " has no Districts.json spawn points in the runtime GameData.");
            points[style] = counts;
        }
        return (presets, mission, points);
    }

    static DungeonPopulationBlock Valid() => new()
    {
        Npcs = ["ANDR"], SpawnChancePercent = 35, LevelSpreadPercent = 10, MaxGroup = 2, GroupRadius = 8,
        AggroRangePercent = 100, HelpRangePercent = 100
    };

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
