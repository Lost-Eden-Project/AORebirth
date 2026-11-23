namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using AORebirth.Core.GameData;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Mobs;
using ZoneEngine_New.Core.Quests.Dungeons;

/// <summary>
/// A generated Rubi-Ka mission's regular monsters stay in the mission's level range. A family whose Families.json Levels
/// hold the quality can still have no member near it (members at 9-11, then 21-24); its spawn then came up at the nearest
/// member's level, a level 21 monster in a QL 15 mission. The pick at acceptance (<see cref="DungeonPresets.Pick"/>) and the
/// dungeon's spawn roll (<see cref="DungeonPopulationRoll"/>, what QuestDungeonPlayfield.AddDistrictSpawns spawns from) now
/// both keep such families out, and the spawns resolve through the same CollectMobSpawns the dungeon's branches use.
/// </summary>
[TestClass]
public sealed class MissionPopulationLevelRangeTests
{
    // GAP: members 9-11 and 21-24 (no one near 14-17). FIT: a member covers 14-17. ALL: SpawnAll with a stray 21-24 branch.
    const string GapJson =
        """
        {
            "GAP1": { "Templates": [ { "Name": "Low", "Level": 9, "Stats": { "54": 9 } }, { "Name": "Low", "Level": 11, "Stats": { "54": 11 } } ] },
            "GAP2": { "Templates": [ { "Name": "High", "Level": 21, "Stats": { "54": 21 } }, { "Name": "High", "Level": 24, "Stats": { "54": 24 } } ] },
            "GAP": { "Children": ["GAP1", "GAP2"] },
            "FIT1": { "Templates": [ { "Name": "Mid", "Level": 12, "Stats": { "54": 12 } }, { "Name": "Mid", "Level": 18, "Stats": { "54": 18 } } ] },
            "FIT": { "Children": ["GAP1", "FIT1"] },
            "ALL": { "Children": ["FIT1", "GAP2"], "SpawnAll": true },
            "LOOP": { "Children": ["LOOP"] }
        }
        """;

    static readonly Lazy<GameDataStore?> RuntimeData = new(() =>
    {
        string? root = Environment.GetEnvironmentVariable(GameDataPaths.EnvironmentVariableName);
        return string.IsNullOrWhiteSpace(root) || !File.Exists(Path.Combine(root, "Dungeons", "Families.json"))
            ? null : new GameDataStore(new Logger(), null, root);
    });

    [TestMethod]
    public void AFamilyWithNoMemberNearTheMissionLevelCameUpFarAboveIt()
    {
        NpcTemplateCatalog catalog = NpcTemplateCatalog.Parse(GapJson, new Random(1));
        // The defect as seen: at a rolled 17 the closest member is 21-24, and its spawn comes up at 21.
        var spawned = new List<MobTemplate>();
        catalog.CollectSpawns("GAP", 17, spawned);
        Assert.AreEqual(1, spawned.Count);
        Assert.AreEqual(21, spawned[0].Stats[(int)CharacterStat.Level]);

        for (int level = 14; level <= 17; level++)
            Assert.IsFalse(catalog.SpawnsWithin("GAP", level, 14, 17), "GAP fields nothing in 14-17 at " + level);
        for (int level = 14; level <= 17; level++)
            Assert.IsTrue(catalog.SpawnsWithin("FIT", level, 14, 17), "FIT's 12-18 member covers " + level);
        Assert.IsFalse(catalog.SpawnsWithin("ALL", 15, 14, 17), "Every SpawnAll branch spawns, the stray 21-24 one too.");
        Assert.IsFalse(catalog.SpawnsWithin("LOOP", 15, 14, 17), "Nothing spawns from a cycle.");
        Assert.IsFalse(catalog.SpawnsWithin("NONE", 15, 14, 17), "Nothing spawns from an unknown hash.");
        // A leaf below the range clamps up to its lowest level, as the spawn does.
        Assert.IsTrue(catalog.SpawnsWithin("GAP2", 14, 21, 24));
    }

    [TestMethod]
    public void TheDungeonSpawnsOnlyFamiliesThatStayInTheMissionRange()
    {
        GameDataStore data = Data();
        string[] gapFamilies = MissionFamilies(data, 15).Where(hash => FieldsNothing(data, hash, 15)).ToArray();
        string[] fitFamilies = MissionFamilies(data, 15).Except(gapFamilies).ToArray();
        if (gapFamilies.Length == 0 || fitFamilies.Length == 0)
            Assert.Inconclusive("The runtime GameData has no QL 15 mission family with a level gap to reproduce against.");

        var block = new DungeonPopulationBlock { Npcs = [.. gapFamilies, .. fitFamilies], LevelSpreadPercent = 10 };
        (int low, int high) = block.LevelRange(15);
        // Before: the dungeon drew from every named family, and a gap family's spawn came up outside the range.
        Assert.IsTrue(gapFamilies.Any(hash => Enumerable.Range(low, high - low + 1).Any(level => SpawnLevels(data, hash, level).Any(l => l < low || l > high))),
            "A gap family must resolve outside the range on the old path.");

        var fielding = new DungeonPopulationRoll(block, data, 15);
        Assert.IsTrue(fielding.FieldsRange);
        CollectionAssert.AreEquivalent(gapFamilies, fielding.LeftOut.ToArray());
        CollectionAssert.AreEquivalent(fitFamilies, fielding.Npcs.Distinct().ToArray());
        AssertSpawnsStayInRange(data, fielding, low, high, new Random(1140174067), "QL 15");
    }

    [TestMethod]
    public void APopulationThatCannotFieldTheRangeStillSpawnsAsNamed()
    {
        GameDataStore data = Data();
        string? gap = MissionFamilies(data, 15).FirstOrDefault(hash => FieldsNothing(data, hash, 15));
        if (gap == null)
            Assert.Inconclusive("The runtime GameData has no QL 15 mission family with a level gap.");

        var fielding = new DungeonPopulationRoll(new DungeonPopulationBlock { Npcs = [gap], LevelSpreadPercent = 10 }, data, 15);
        Assert.IsFalse(fielding.FieldsRange);
        CollectionAssert.AreEqual(new[] { gap }, fielding.Npcs.ToArray(), "The dungeon is not left empty.");
        CollectionAssert.AreEqual(new[] { gap }, fielding.LeftOut.ToArray());
        (string hash, int level) = fielding.Next(new Random(7));
        Assert.AreEqual(gap, hash);
        Assert.IsTrue(level is >= 14 and <= 17, "The rolled level is kept.");
    }

    /// <summary>
    /// The mission terminal's population, as accepted (the Rubi-Ka preset's family pick for the quality, side and seed)
    /// and as its dungeon spawns it: every NPC at every quality stays in the mission's level range.
    /// </summary>
    [TestMethod]
    public void AcceptedRubiKaMissionsSpawnOnlyMonstersInTheirLevelRange()
    {
        GameDataStore data = Data();
        DungeonPresets presets = DungeonPresets.Load(data, new Logger());
        int checkedMissions = 0;
        for (int quality = 1; quality <= 300; quality++)
        {
            ResolvedDungeon? resolved = presets.Resolve(new DungeonRequest { Population = new DungeonPopulationBlock { Preset = "rubika" } },
                quality, out string? problem);
            if (resolved == null)
            {
                if (problem != null && problem.Contains("rubika", StringComparison.Ordinal))
                    Assert.Inconclusive("The runtime GameData has no rubika population: " + problem);
                continue;
            }
            Assert.IsNotNull(resolved.Population.Mix, "The rubika population picks its families.");
            foreach (int seed in new[] { 1140174067, quality * 7919, 42 })
            {
                foreach (int side in new[] { 0, 1, 2 })
                {
                    DungeonPopulationBlock? picked = presets.Pick(resolved.Population, quality, side,
                        new Random(unchecked(seed * 92821 + 17)), out problem);
                    Assert.IsNotNull(picked, "QL " + quality + ": " + problem);
                    var fielding = new DungeonPopulationRoll(picked, data, quality);
                    Assert.AreEqual(0, fielding.LeftOut.Count,
                        "QL " + quality + " picked families that cannot stay in its range: " + string.Join(",", fielding.LeftOut));
                    (int low, int high) = picked.LevelRange(quality);
                    AssertSpawnsStayInRange(data, fielding, low, high, new Random(seed ^ quality), "QL " + quality + " seed " + seed);
                    checkedMissions++;
                }
            }
        }
        Assert.IsTrue(checkedMissions > 0, "No rubika mission resolved.");
    }

    static void AssertSpawnsStayInRange(GameDataStore data, DungeonPopulationRoll fielding, int low, int high, Random roll, string what)
    {
        for (int spawn = 0; spawn < 60; spawn++)
        {
            (string hash, int level) = fielding.Next(roll);
            Assert.IsTrue(level >= low && level <= high, what + ": " + hash + " requested at " + level);
            // The branches a dungeon point freezes (ResolveBranches -> CollectMobSpawns); a family draws its member at random.
            foreach (int spawned in SpawnLevels(data, hash, level))
                Assert.IsTrue(spawned >= low && spawned <= high,
                    what + ": " + hash + " at " + level + " spawned a level " + spawned + " monster outside " + low + "-" + high);
        }
    }

    static IEnumerable<int> SpawnLevels(GameDataStore data, string hash, int level)
    {
        for (int draw = 0; draw < 8; draw++)
        {
            var branches = new List<MobTemplate>();
            data.CollectMobSpawns(hash, level, branches);
            foreach (MobTemplate branch in branches)
                yield return branch.Stats[(int)CharacterStat.Level];
        }
    }

    static bool FieldsNothing(GameDataStore data, string hash, int quality)
        => new DungeonPopulationBlock { LevelSpreadPercent = 10 }.FieldableLevels(data, hash, quality).Length == 0;

    /// <summary>The Families.json mission families whose Levels hold <paramref name="quality"/>.</summary>
    static IEnumerable<string> MissionFamilies(GameDataStore data, int quality)
    {
        using JsonDocument families = JsonDocument.Parse(File.ReadAllText(Path.Combine(data.RootPath, "Dungeons", "Families.json")));
        var hashes = new List<string>();
        foreach (JsonProperty family in families.RootElement.EnumerateObject())
        {
            JsonElement row = family.Value;
            if (row.TryGetProperty("Mission", out JsonElement mission) && mission.ValueKind == JsonValueKind.True
                && row.TryGetProperty("Levels", out JsonElement levels) && levels.ValueKind == JsonValueKind.Array && levels.GetArrayLength() == 2
                && levels[0].GetInt32() <= quality && quality <= levels[1].GetInt32() && data.HasMobTemplate(family.Name))
                hashes.Add(family.Name);
        }
        return hashes;
    }

    static GameDataStore Data()
    {
        GameDataStore? data = RuntimeData.Value;
        if (data == null)
            Assert.Inconclusive("Set AO_REBIRTH_GAMEDATA_PATH to the runtime GameData with Dungeons/Families.json.");
        return data!;
    }

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
