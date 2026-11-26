namespace ZoneEngine_New.Tests;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

using AORebirth.Core.GameData;
using AORebirth.DungeonGenerator;
using AORebirth.Modules.Content;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Quests;
using ZoneEngine_New.Core.Quests.Dungeons;

/// <summary>
/// Team request 1 (website overlays for quest dungeons): Missions/Missions.json, Dungeons/*.json and
/// DungeonEntrances.json are read through the GameData override folder and reload while the zone runs, and a
/// Quests.json override can give a quest that had no dungeon one. Runs against the runtime GameData
/// (AO_REBIRTH_GAMEDATA_PATH); Inconclusive when it cannot generate mission dungeons.
/// </summary>
[TestClass]
public sealed class DungeonDefinitionsReloadTests
{
    const string TestMission = "reloadTestMission";
    const string TestLayout = "reloadTestLayout";
    const int ShadowlandsStyle = 362;

    static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    [TestMethod]
    public void MissionsReloadAddsAnOverrideMissionAndABadFileKeepsTheOldOnes()
    {
        (string root, DungeonLayoutGenerator generator) = Generator();
        using var overrides = new OverrideFolder();
        Assert.IsNull(generator.Mission(TestMission));

        JsonObject missions = ReadObject(GameDataPaths.ResolveFile(root, MissionDefinitions.RelativePath));
        missions[TestMission] = missions["rkSolo"]!.DeepClone();
        overrides.Write(MissionDefinitions.RelativePath, missions.ToJsonString());

        GameDataReloadResult reload = new MissionsReload(generator).Reload();
        Assert.IsTrue(reload.Ok, string.Join("; ", reload.Problems));
        Assert.AreEqual(MissionDefinitions.ReloadName, reload.Name);
        Assert.AreEqual(missions.Count, reload.Entries);
        Assert.IsNotNull(generator.Mission(TestMission), "The override's mission is served after the reload.");

        // Not JSON: refused, the loaded missions stay.
        overrides.Write(MissionDefinitions.RelativePath, "{ \"broken\": ");
        GameDataReloadResult broken = new MissionsReload(generator).Reload();
        Assert.IsFalse(broken.Ok);
        Assert.AreEqual(1, broken.Problems.Count);
        Assert.IsNotNull(generator.Mission(TestMission), "A failed reload keeps the old snapshot.");
        Assert.IsTrue(generator.IsAvailable);

        // Valid JSON that does not validate (no playfields): refused whole, naming the mission.
        missions[TestMission]!["Playfields"] = new JsonArray();
        overrides.Write(MissionDefinitions.RelativePath, missions.ToJsonString());
        GameDataReloadResult invalid = new MissionsReload(generator).Reload();
        Assert.IsFalse(invalid.Ok);
        StringAssert.Contains(invalid.Problems[0], TestMission);
        Assert.IsNotNull(generator.Mission(TestMission));

        // The override removed: back to the shipped file.
        overrides.Delete(MissionDefinitions.RelativePath);
        Assert.IsTrue(new MissionsReload(generator).Reload().Ok);
        Assert.IsNull(generator.Mission(TestMission));
        Assert.IsNotNull(generator.Mission("rkSolo"));
    }

    [TestMethod]
    public void DungeonsReloadAddsAnOverridePresetAndMissionsSwapWithIt()
    {
        (string root, DungeonLayoutGenerator generator) = Generator();
        using var overrides = new OverrideFolder();
        var request = new DungeonRequest { Layout = new DungeonLayoutBlock { Preset = TestLayout } };
        Assert.IsNull(generator.Resolve(request, 50, out string? problem));
        StringAssert.Contains(problem, TestLayout);

        const string layoutsFile = DungeonPresets.Folder + "/Layouts.json";
        JsonObject layouts = ReadObject(GameDataPaths.ResolveFile(root, layoutsFile));
        if (layouts.Count == 0)
            Assert.Inconclusive("Dungeons/Layouts.json has no preset to copy.");
        layouts[TestLayout] = layouts.First().Value!.DeepClone();
        overrides.Write(layoutsFile, layouts.ToJsonString());

        GameDataReloadResult reload = new DungeonsReload(generator).Reload();
        Assert.IsTrue(reload.Ok, string.Join("; ", reload.Problems));
        Assert.AreEqual(DungeonPresets.ReloadName, reload.Name);
        Assert.IsNotNull(generator.Resolve(request, 50, out problem), problem);

        // A mission in the override names the new preset: it loads with the presets it was written for.
        JsonObject missions = ReadObject(GameDataPaths.ResolveFile(root, MissionDefinitions.RelativePath));
        JsonObject mission = (JsonObject)missions["rkSolo"]!.DeepClone();
        mission["Dungeon"] = new JsonObject { ["Layout"] = TestLayout, ["Population"] = "rubika", ["Chests"] = "none", ["Objective"] = "boss" };
        missions[TestMission] = mission;
        overrides.Write(MissionDefinitions.RelativePath, missions.ToJsonString());
        GameDataReloadResult missionsReload = new MissionsReload(generator).Reload();
        Assert.IsTrue(missionsReload.Ok, string.Join("; ", missionsReload.Problems));
        Assert.AreEqual(TestLayout, generator.Mission(TestMission)!.Dungeon!.Layout!.Preset);

        // Taking the preset away under that mission is refused whole: the presets and missions stay as one snapshot.
        overrides.Delete(layoutsFile);
        GameDataReloadResult refused = new DungeonsReload(generator).Reload();
        Assert.IsFalse(refused.Ok);
        StringAssert.Contains(refused.Problems[0], TestLayout);
        Assert.IsNotNull(generator.Resolve(request, 50, out problem), "A failed reload keeps the old presets.");
        Assert.IsNotNull(generator.Mission(TestMission));

        overrides.Delete(MissionDefinitions.RelativePath);
        Assert.IsTrue(new DungeonsReload(generator).Reload().Ok);
        Assert.IsNull(generator.Resolve(request, 50, out _));
        Assert.IsNull(generator.Mission(TestMission));
    }

    [TestMethod]
    public void EntrancesReloadGivesAStyleWithoutEntrancesItsOverrideRooms()
    {
        (string root, DungeonLayoutGenerator generator) = Generator();
        var library = new DungeonGenerator(root);
        library.ForgetStyles();
        try
        {
            try
            {
                library.LoadStyle(ShadowlandsStyle);
                Assert.Inconclusive("Style " + ShadowlandsStyle + " already has entrances in the runtime GameData.");
            }
            catch (InvalidDataException)
            {
                // No entrance mapped: what the shipped DungeonEntrances.json says for the Shadowlands ACG style.
            }
            catch (Exception exception) when (exception is not AssertInconclusiveException)
            {
                Assert.Inconclusive("The runtime GameData at " + root + " has no rooms for style " + ShadowlandsStyle + ": " + exception.Message);
            }

            using var overrides = new OverrideFolder();
            JsonObject entrances = ReadObject(GameDataPaths.ResolveFile(root, DungeonEntranceMap.FileName));
            JsonArray styles = (JsonArray)entrances["Styles"]!;
            JsonObject? row = styles.OfType<JsonObject>().FirstOrDefault(s => s["Style"]?.GetValue<int>() == ShadowlandsStyle);
            if (row == null)
                styles.Add(row = new JsonObject { ["Style"] = ShadowlandsStyle, ["MainHalls"] = new JsonArray(0, 3, 4, 5) });
            // Room 55 (SL_TM_entrance_1_BRINK) with door 0 outside, as the captured live runs show it.
            row["Entrances"] = new JsonArray(new JsonObject { ["Room"] = 55, ["ExitDoor"] = 0 });
            overrides.Write(DungeonEntranceMap.FileName, entrances.ToJsonString());

            GameDataReloadResult reload = new DungeonEntrancesReload(generator).Reload();
            Assert.IsTrue(reload.Ok, string.Join("; ", reload.Problems));
            Assert.AreEqual(styles.Count, reload.Entries);
            StyleRoomSet set = library.LoadStyle(ShadowlandsStyle);
            Assert.AreEqual(1, set.Entrances.Count);
            Assert.AreEqual(55, set.Entrances[0].RoomId);

            // Not JSON: refused, and the styles already loaded are kept.
            overrides.Write(DungeonEntranceMap.FileName, "{ \"Styles\": [ ");
            GameDataReloadResult broken = new DungeonEntrancesReload(generator).Reload();
            Assert.IsFalse(broken.Ok);
            Assert.AreSame(set, library.LoadStyle(ShadowlandsStyle), "A failed reload drops no loaded style.");

            // The override removed: the style reads the shipped file again and has no entrance.
            overrides.Delete(DungeonEntranceMap.FileName);
            Assert.IsTrue(new DungeonEntrancesReload(generator).Reload().Ok);
            Assert.ThrowsExactly<InvalidDataException>(() => library.LoadStyle(ShadowlandsStyle));
        }
        finally
        {
            library.ForgetStyles();
        }
    }

    [TestMethod]
    public void QuestsReloadGivesAQuestWithoutADungeonTheOverridesMission()
    {
        (string root, DungeonLayoutGenerator generator) = Generator();
        var catalog = new QuestCatalog(Store(root), new Logger());
        JsonArray quests = ReadArray(GameDataPaths.ResolveFile(root, QuestCatalog.FileName));
        JsonObject? quest = quests.OfType<JsonObject>().FirstOrDefault(q =>
            q["Action"]?.GetValue<string>() == QuestTemplate.KillOneAction && q["Dungeon"] is not JsonObject && q["Hash"] != null);
        if (quest == null)
            Assert.Inconclusive("Quests.json has no KillOne quest without a dungeon.");
        string hash = quest["Hash"]!.GetValue<string>();
        Assert.IsTrue(catalog.TryGet(hash, out QuestTemplate before));
        Assert.IsNull(before.Dungeon);

        using var overrides = new OverrideFolder();
        // What the module writes after merging Overlays/Quests.json { "<hash>": { "Dungeon": { "Mission": "rkSolo" } } }.
        quest["Dungeon"] = new JsonObject { ["Mission"] = "rkSolo", ["Quality"] = 35 };
        overrides.Write(QuestCatalog.FileName, quests.ToJsonString());

        GameDataReloadResult reload = catalog.Reload();
        Assert.IsTrue(reload.Ok, string.Join("; ", reload.Problems));
        Assert.IsTrue(catalog.TryGet(hash, out QuestTemplate after));
        Assert.IsNotNull(after.Dungeon);
        Assert.AreEqual("rkSolo", after.Dungeon!.Mission);
        Assert.AreEqual(35, after.Dungeon.QualityFor(null, 200));
        Assert.IsNull(after.LoadProblem, "The dungeon's inputs are valid for a KillOne quest.");
        Assert.IsNotNull(generator.Mission(after.Dungeon.Mission), "The named mission resolves at acceptance.");

        overrides.Delete(QuestCatalog.FileName);
        Assert.IsTrue(catalog.Reload().Ok);
        Assert.IsTrue(catalog.TryGet(hash, out QuestTemplate restored));
        Assert.IsNull(restored.Dungeon);
    }

    static (string Root, DungeonLayoutGenerator Generator) Generator()
    {
        string root = GameDataPaths.ResolveRuntimeRoot();
        var generator = new DungeonLayoutGenerator(Store(root), new Logger());
        if (!generator.IsAvailable || generator.Mission("rkSolo") == null)
            Assert.Inconclusive("The runtime GameData at " + root + " cannot generate mission dungeons (needs Missions.json rkSolo).");
        return (root, generator);
    }

    static GameDataStore Store(string root) => new(new Logger(), null, root);

    static JsonObject ReadObject(string path)
        => JsonNode.Parse(File.ReadAllText(path), documentOptions: Lenient) as JsonObject ?? throw new AssertFailedException(path + " is not a JSON object.");

    static JsonArray ReadArray(string path)
        => JsonNode.Parse(File.ReadAllText(path), documentOptions: Lenient) as JsonArray ?? throw new AssertFailedException(path + " is not a JSON list.");

    /// <summary>Sets AO_REBIRTH_GAMEDATA_OVERRIDE_PATH to a fresh folder for the test, then restores it.</summary>
    sealed class OverrideFolder : IDisposable
    {
        readonly string? _saved = Environment.GetEnvironmentVariable(GameDataPaths.OverrideEnvironmentVariableName);

        internal OverrideFolder()
        {
            Directory.CreateDirectory(Root);
            Environment.SetEnvironmentVariable(GameDataPaths.OverrideEnvironmentVariableName, Root);
        }

        internal string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "aorebirth-dungeon-override-" + Guid.NewGuid().ToString("N")));

        internal void Write(string relativePath, string json)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }

        internal void Delete(string relativePath) => File.Delete(Path.Combine(Root, relativePath));

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(GameDataPaths.OverrideEnvironmentVariableName, _saved);
            Directory.Delete(Root, true);
        }
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
