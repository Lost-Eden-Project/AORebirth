namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

using AORebirth.Core.GameData;
using AORebirth.DungeonGenerator;
using AORebirth.Interfaces.Persistence.Missions;
using AORebirth.World.Collision;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages.N3Messages;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Quests.Dungeons;
using ZoneEngine_New.Core.WorldSimulation;

/// <summary>
/// Every Alien Mothership mission chest is the alien cocoon (team request 18): item template 10000022, the mission chest
/// 162447 with its mesh replaced by the ai_cocoon CAT body (CATMesh 257279), so state machine 7 plays the burst open and
/// closed. Only the chests' appearance changes; loot, lock, Need or Greed and persistence are any mission chest's.
/// Against the runtime GameData (AO_REBIRTH_GAMEDATA_PATH): its Dungeons presets, Missions.json, items.dat and
/// MonsterData.json, as the dungeon's chest population reads them.
/// </summary>
[TestClass]
public sealed class MothershipCocoonChestTests
{
    // Not 10000002: the site's items.dat build lets nano 10000002 replace item 10000002, which dropped the cocoon.
    const int Cocoon = 10000022;
    const int CocoonBody = 257279;

    static readonly Lazy<(GameDataStore Data, ItemTemplateCatalog Templates)?> Runtime = new(() =>
    {
        string? root = Environment.GetEnvironmentVariable(GameDataPaths.EnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(root) || !File.Exists(Path.Combine(root, "Missions", "Missions.json")))
            return null;
        var data = new GameDataStore(new Logger(), null, root);
        return (data, new ItemTemplateCatalog(data, new Logger()));
    });

    [TestMethod]
    public void EveryMothershipMissionChestIsTheCocoon()
    {
        (GameDataStore data, _) = Data();
        DungeonChestBlock chests = MothershipChests(data);
        Assert.IsTrue(chests.Places, "The Mothership mission places chests.");
        Assert.AreEqual(Cocoon, chests.ChestTemplate);
        Assert.AreEqual(ChestSpawnDefinition.MissionChestTemplate, new DungeonChestBlock().ChestTemplate,
            "A chest block without a template keeps the mission chest.");
    }

    [TestMethod]
    public void TheChestTemplateIsKeptWithTheAcceptedMission()
    {
        (GameDataStore data, _) = Data();
        // The resolved chest block is stored with the accepted mission (QuestDungeonParameters.Dungeon) and rebuilt from it.
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        DungeonChestBlock stored = JsonSerializer.Deserialize<DungeonChestBlock>(JsonSerializer.Serialize(MothershipChests(data), options), options)!;
        Assert.AreEqual(Cocoon, stored.ChestTemplate);
        Assert.IsNull(stored.Problem());
        // A mission stored before the template existed (or with today's retired per-room field) rebuilds with the mission chest.
        DungeonChestBlock older = JsonSerializer.Deserialize<DungeonChestBlock>(
            """{ "Enabled": true, "RoomDensity": 2, "CountVariation": 0.25, "LockedChancePercent": 0, "ExcludedRooms": [], "RoomTemplates": { "bossroom": 10000002 } }""",
            options)!;
        Assert.AreEqual(ChestSpawnDefinition.MissionChestTemplate, older.ChestTemplate);
        Assert.IsNull(older.Problem());
        Assert.IsNotNull(new DungeonChestBlock { Enabled = true, RoomDensity = 1, CountVariation = 0, LockedChancePercent = 0,
            ExcludedRooms = [], Template = 0 }.Problem(), "A template id of 0 is refused.");
    }

    [TestMethod]
    public void AMothershipChestStandsOnTheFloorUnderItsSpawnPoint()
    {
        (GameDataStore data, _) = Data();
        DungeonChestBlock chests = MothershipChests(data);
        var generator = new DungeonGenerator(data.RootPath);
        PlayfieldDistrictsData districts = Core.Playfield.AcgDungeonPlayfield.LoadStyleDistricts(data.RootPath, 386, new Logger())
            ?? throw new AssertFailedException("style 386 has no Districts.json");
        int checkedSites = 0;
        for (int seed = 1; seed <= 3; seed++)
        {
            // A team Mothership as DungeonLayoutGenerator plans it, its collision as QuestDungeonPlayfield builds it.
            DungeonGenerationResult result = generator.Generate(new DungeonGenerationRequest
            {
                StyleId = 386, Seed = seed, FloorCount = 2, FloorSize = 6, FloorHeight = 128, BossFloor = true,
                DescendingFloors = generator.LoadStyle(386).Transitions?.DescendingFloors ?? false,
                UseFloorTransitions = true, LockedDoorChancePercent = 0, BuildingInstance = unchecked((int)0xC00010F4),
                Layout = DungeonLayoutKind.Planned
            });
            var layout = new DungeonLayout(result.Generator, result.Identity, result.GeneratorBlob(),
                new AORebirth.Core.Vector.Vector3(result.Spawn.X, result.Spawn.Y, result.Spawn.Z), seed, null)
            {
                FloorTransitions = result.Transitions, BossRoomIndex = result.BossRoomIndex
            };
            DungeonWorldLayout dungeon = DungeonCollisionBuilder.BuildLayout(data.RootPath, result.Generator);
            using var world = PlayfieldWorldSimulation.Create(0x0FF00386, new PlayfieldGeometryData { Collision = dungeon.Collision },
                null, DestinationsCatalog.Instance, data, new Logger());
            HashSet<int> eligible = MissionChestPlacement.EligibleRooms(dungeon, layout, data.RootPath, chests);
            foreach (DungeonRoomPlacement placement in dungeon.Placements.Where(placed => eligible.Contains(placed.Index)))
            {
                // The chest sites QuestDungeonPlayfield.ChestSites takes from the room's district.
                int zone = placement.Template.InstanceId;
                int districtIndex = districts.ZoneToDistrictMap is { } map && zone >= 0 && zone < map.Length ? map[zone] : zone;
                PlayfieldDistrictEntry? district = districts.Districts?.FirstOrDefault(entry => entry?.DistrictIndex == districtIndex);
                foreach (PlayfieldDistrictSpawnPoint? point in district?.SpawnPoints ?? [])
                {
                    if (point?.Position is not { Length: >= 3 } raw)
                        continue;
                    System.Numerics.Vector3 placed = placement.TransformTemplatePoint(new System.Numerics.Vector3(raw[0], raw[1], raw[2]));
                    var site = new AORebirth.Core.Vector.Vector3(placed.X, placed.Y, placed.Z);
                    // The defect: the chest stood at the point, about 0.4 m above the floor, and was drawn in the air.
                    Assert.IsTrue(world.TrySnapToFloor(site, out AORebirth.Core.Vector.Vector3 floor), "no floor under " + site);
                    double lift = site.y - floor.y;
                    Assert.IsTrue(lift is > 0.05 and < 1.0, "the point is " + lift + " m over the floor found (seed " + seed + ")");
                    Assert.AreEqual(site.x, floor.x);
                    Assert.AreEqual(site.z, floor.z);
                    checkedSites++;
                }
            }
        }
        Assert.IsTrue(checkedSites > 0, "No Mothership chest site was checked.");
    }

    [TestMethod]
    public void TheCocoonTemplateIsInTheServerCatalogAsTheMissionChestDrawnAsTheCocoonBody()
    {
        (_, ItemTemplateCatalog templates) = Data();
        Assert.IsTrue(templates.TryGet(Cocoon, out ItemTemplate cocoon), "items.dat has no template " + Cocoon);
        ItemTemplate chest = templates.Require(ChestSpawnDefinition.MissionChestTemplate);
        Assert.AreEqual(CocoonBody, cocoon.Stats[CharacterStat.CATMesh]);
        Assert.IsFalse(cocoon.Stats.ContainsKey(CharacterStat.Mesh), "The cocoon is drawn by its CAT body, not a mesh.");
        foreach (CharacterStat stat in new[] { CharacterStat.Flags, CharacterStat.Can, CharacterStat.StateMachine })
            Assert.AreEqual(chest.Stats[stat], cocoon.Stats[stat], stat.ToString());
    }

    [TestMethod]
    public void TheCocoonChestGetsTheCollisionSphereOfItsBody()
    {
        (GameDataStore data, ItemTemplateCatalog templates) = Data();
        // The defect: sized from its mesh alone, a template with only a CATMesh had no sphere and its chests were not placed.
        Assert.IsFalse(data.TryGetDynelCollisionSphere(Cocoon, 0, 100, out _, out _));
        Assert.IsTrue(data.TryGetCatMeshCharRadius(CocoonBody, out int charRadius));
        Assert.IsTrue(MissionChestPlacement.TryGetChestSphere(data, templates, ChestSpawnDefinition.MissionChest(Cocoon), out var sphere));
        Assert.AreEqual(charRadius, sphere.Radius, 1e-6);
        Assert.AreEqual(sphere.Radius, sphere.CenterY, 1e-6);
        // The mission chest still comes from its mesh.
        Assert.IsTrue(MissionChestPlacement.TryGetChestSphere(data, templates, ChestSpawnDefinition.MissionChest(), out var chest));
        Assert.IsTrue(data.TryGetDynelCollisionSphere(ChestSpawnDefinition.MissionChestTemplate, 43621, 100, out float meshRadius, out _));
        Assert.AreEqual(meshRadius, chest.Radius, 1e-6);
    }

    [TestMethod]
    public void TheCocoonSpawnsAsTheMissionChestWithOnlyItsTemplateChanged()
    {
        ChestFullUpdateMessage chest = Spawn(ChestSpawnDefinition.MissionChest());
        ChestFullUpdateMessage cocoon = Spawn(ChestSpawnDefinition.MissionChest(Cocoon));
        CharacterStat[] template = [CharacterStat.StaticInstance, CharacterStat.ACGItemTemplateID, CharacterStat.ACGItemTemplateID2];
        Assert.AreEqual(chest.Stats.Length, cocoon.Stats.Length);
        for (int index = 0; index < chest.Stats.Length; index++)
        {
            Assert.AreEqual((int)chest.Stats[index].Value1, (int)cocoon.Stats[index].Value1);
            bool isTemplate = template.Contains((CharacterStat)(int)cocoon.Stats[index].Value1);
            Assert.AreEqual(isTemplate ? ChestSpawnDefinition.MissionChestTemplate : chest.Stats[index].Value2, chest.Stats[index].Value2);
            Assert.AreEqual(isTemplate ? Cocoon : chest.Stats[index].Value2, cocoon.Stats[index].Value2);
        }
        Assert.AreEqual(chest.StateMachine, cocoon.StateMachine, "State machine 7 runs the cocoon's burst as the chest's lid.");
        Assert.AreEqual(chest.Unknown5, cocoon.Unknown5);
        Assert.AreEqual(chest.Unknown8, cocoon.Unknown8, "The cocoon is lockable like any mission chest.");
    }

    [TestMethod]
    public void ThePopulationDrawsTheChestsAsTheCocoonAndFallsBackWhenItCannotBeSized()
    {
        (GameDataStore data, ItemTemplateCatalog templates) = Data();
        var log = new Logger();
        Assert.IsTrue(Population(MothershipChests(data), log).TryGetAppearance(data, templates, out var appearance, out var sphere));
        Assert.AreEqual((uint)Cocoon, TemplateOf(appearance));
        Assert.IsTrue(sphere.Radius > 0);
        Assert.AreEqual(0, log.Warnings.Count);

        // Without the template in the server's catalog the dungeon keeps its chests, drawn as the mission chest.
        Assert.IsTrue(Population(MothershipChests(data), log)
            .TryGetAppearance(data, new Catalog(templates, without: Cocoon), out var fallback, out var fallbackSphere));
        Assert.AreEqual((uint)ChestSpawnDefinition.MissionChestTemplate, TemplateOf(fallback));
        Assert.IsTrue(MissionChestPlacement.TryGetChestSphere(data, templates, ChestSpawnDefinition.MissionChest(), out var chest));
        Assert.AreEqual(chest.Radius, fallbackSphere.Radius);
        Assert.AreEqual(1, log.Warnings.Count);

        // A chest block without a template is the mission chest, as before.
        var plain = new DungeonChestBlock { LootTable = MothershipChests(data).LootTable };
        Assert.IsTrue(Population(plain, log).TryGetAppearance(data, templates, out var mission, out _));
        Assert.AreEqual((uint)ChestSpawnDefinition.MissionChestTemplate, TemplateOf(mission));
        Assert.AreEqual(1, log.Warnings.Count);
    }

    static (GameDataStore Data, ItemTemplateCatalog Templates) Data()
    {
        if (Runtime.Value is not { } runtime || !runtime.Templates.TryGet(ChestSpawnDefinition.MissionChestTemplate, out _))
        {
            Assert.Inconclusive("Set AO_REBIRTH_GAMEDATA_PATH to the private GameData (Missions.json, items.dat).");
            throw new InvalidOperationException();
        }
        return runtime;
    }

    /// <summary>The alien Mothership mission's chest block as an accepted mission resolves it.</summary>
    static DungeonChestBlock MothershipChests(GameDataStore data)
    {
        DungeonPresets presets = DungeonPresets.Load(data, new Logger());
        MissionDefinition mothership = MissionDefinitions.Load(data.RootPath, presets, new Logger()).Get("alienMothership")
            ?? throw new AssertFailedException("Missions.json has no alienMothership mission.");
        ResolvedDungeon dungeon = presets.Resolve(mothership.Dungeon, 100, out string? problem)
            ?? throw new AssertFailedException("The Mothership dungeon does not resolve: " + problem);
        return dungeon.Chests;
    }

    static MissionChestPopulation Population(DungeonChestBlock chests, Logger log)
        => new(Fake<IMissionDao>(), 101, "829N", 638_000_000_000_000_000,
            new QuestDungeonParameters { Quality = 100, Seed = 1520976367, Dungeon = new ResolvedDungeon { Chests = chests } },
            Fake<IItemBuilder>(), log);

    static ChestFullUpdateMessage Spawn(ChestSpawnDefinition appearance)
        => (ChestFullUpdateMessage)new Chest(new Identity { Type = IdentityType.Container, Instance = 90001 }) { SpawnDefinition = appearance }
            .BuildSpawnMessage();

    static uint TemplateOf(ChestSpawnDefinition appearance)
        => appearance.Stats.Single(stat => stat.StatId == (int)CharacterStat.StaticInstance).Value;

    sealed class Catalog(IItemTemplateCatalog inner, int without) : IItemTemplateCatalog
    {
        public bool TryGet(int aoid, out ItemTemplate template)
        {
            template = null!;
            return aoid != without && inner.TryGet(aoid, out template);
        }

        public ItemTemplate Require(int aoid)
            => TryGet(aoid, out ItemTemplate template) ? template : throw new KeyNotFoundException(aoid.ToString());
    }

    public class FakeProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new AssertFailedException("Unexpected call " + targetMethod!.Name);
    }

    static T Fake<T>() where T : class => DispatchProxy.Create<T, FakeProxy>();

    sealed class Logger : IZoneLogger
    {
        public List<string> Warnings { get; } = [];
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message) { }
        public void Error(Exception exception, string message) { }
        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }
}
