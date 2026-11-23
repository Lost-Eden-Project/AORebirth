namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;

using AORebirth.Core.GameData;
using AORebirth.Database.Domain.Dungeons;
using AORebirth.Interfaces.Persistence.Shops;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Utility.GameData.Missions;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Metrics;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.Quests.Dungeons;
using ZoneEngine_New.Core.Quests.Dungeons.Static;
using ZoneEngine_New.Core.Teams;
using ZoneEngine_New.Core.Trade;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Static dungeons (team request 2): the captured Shadowlands dungeons behind world doors, read from the runtime
/// GameData's Dungeons/Static files (AO_REBIRTH_GAMEDATA_PATH), built as a player entering would get them, and the
/// service that hands out one instance per team. Drives the real catalog, the real style geometry, the real spawn
/// system and the real chest roll; only persistence and the network are doubles.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class StaticDungeonTests
{
    /// <summary>The Nascence door whose capture carried stock's own PlayfieldAnarchyF (A Door(5), 33 rooms).</summary>
    const uint NascenceDoor = 3221360854;

    static readonly Lazy<Rig> Shared = new(() => new Rig(), LazyThreadSafetyMode.ExecutionAndPublication);

    static Rig R => Shared.Value;

    [TestMethod]
    public void IdRangesAreDisjoint()
    {
        Assert.IsTrue(StaticDungeonIds.TryCreate(1, out int fixedId));
        Assert.IsTrue(QuestDungeonIds.TryCreate(1, out string questId, out int questPlayfield));
        Assert.IsTrue(QuestDungeonIds.IsDungeonPlayfield(fixedId), "a static instance is a dungeon playfield (never built as an ordinary one)");
        Assert.IsTrue(StaticDungeonIds.IsStaticDungeonPlayfield(fixedId));
        Assert.IsFalse(StaticDungeonIds.IsStaticDungeonPlayfield(questPlayfield));
        Assert.IsFalse(QuestDungeonIds.IsQuestDungeonPlayfield(fixedId));
        Assert.IsFalse(QuestDungeonIds.TryGetQuestId(fixedId, out _), "a static instance id is never read as a quest id");
        Assert.AreEqual("D1", questId);
    }

    [TestMethod]
    public void HeadingsTurnAboutY()
    {
        Assert.AreEqual(90, StaticDungeonCatalog.YawDegrees([0f, 0.70710677f, 0f, 0.70710677f]));
        Assert.AreEqual(270, StaticDungeonCatalog.YawDegrees([0f, 0.70710677f, 0f, -0.70710677f]));
        Assert.AreEqual(0, StaticDungeonCatalog.YawDegrees([0f, 0f, 0f, 1f]));
        Assert.AreEqual(0, StaticDungeonCatalog.YawDegrees(null));
    }

    [TestMethod]
    public void TheCatalogLoadsEveryCapturedDungeonItCanPlace()
    {
        StaticDungeonCatalog catalog = R.Catalog;
        int files = Directory.EnumerateFiles(Path.Combine(R.Root, "Dungeons", "Static"), "*.json").Count();
        Assert.IsTrue(files >= 12, "the runtime GameData holds the captured dungeons");
        // A file is left out only for an entrance the placements catalog does not know; every other note is about
        // captured monsters that have no NpcTemplates entry yet, or a missing site pool.
        int leftOut = 0;
        foreach (string problem in catalog.Problems)
        {
            if (problem.Contains("is not in MissionEntrancePlacements.json", StringComparison.Ordinal))
                leftOut++;
            else
                Assert.IsTrue(problem.Contains("no NpcTemplates entry", StringComparison.Ordinal), "unexpected problem: " + problem);
        }
        Assert.AreEqual(files - leftOut, catalog.Dungeons.Count, string.Join("\n", catalog.Problems));
        Assert.IsTrue(catalog.TryGet((uint)IdentityType.ACGEntrance, NascenceDoor, out StaticDungeonBlueprint nascence));
        Assert.AreEqual(4310, nascence.Entrance.PlayfieldId);
        Assert.AreEqual(362, nascence.Style);
        Assert.IsNotNull(nascence.Sites, "the style 362 site pool loads");
        Assert.IsNotNull(nascence.Chests);
        Assert.IsNotNull(nascence.Layout.Doors, "DungeonDoorStyles.json has the style 362 row");
    }

    [TestMethod]
    public void TheBuildingIsSentExactlyAsCaptured()
    {
        Assert.IsTrue(R.Catalog.TryGet((uint)IdentityType.ACGEntrance, NascenceDoor, out StaticDungeonBlueprint blueprint));
        AcgBuildingGenerator generator = blueprint.Layout.Generator;
        Assert.AreEqual(30, generator.Width);
        Assert.AreEqual(30, generator.Height);
        Assert.AreEqual(0, generator.FloorHeight, "stock sends FloorHeight 0 for style 362: floors side by side in X");
        Assert.AreEqual(33, generator.Rooms.Count);
        // The captured PlayfieldAnarchyF's first row: room 51 (SL_TM_entrance_2_NATURAL), floor 0, grid (27, 26), facing 3.
        AcgRoom first = generator.Rooms[0];
        Assert.AreEqual((51, 0, 27, 26, 3), ((int)first.RoomIndex, (int)first.Floor, (int)first.GridX, (int)first.GridZ, (int)first.Rotation));
        // The payload keys the navmesh cache and is what the client parses: the inline building identity, then version 3.
        byte[] payload = blueprint.Layout.GeneratorPayload;
        // identity (8), object version (4), stream version, width, height, floor height (2 each), style (4), ambient (3), count (4), rows.
        Assert.AreEqual(8 + 4 + 8 + 4 + 3 + 4 + 33 * 6, payload.Length);
        Assert.AreEqual(1, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(8)), "DbObject version 1");
        Assert.AreEqual(3, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(12)), "ACG stream version 3");
        Assert.AreEqual(30, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(14)));
        Assert.AreEqual(0, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(18)), "floor height 0 on the wire");
        Assert.AreEqual(362, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(20)));
        Assert.AreEqual(33, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(27)));
        Assert.AreEqual(unchecked((int)NascenceDoor), blueprint.Layout.Entrance.Instance);
        Assert.AreEqual((IdentityType)0xC7A1, blueprint.Layout.Entrance.Type);
        Assert.AreEqual(unchecked((int)NascenceDoor), blueprint.Layout.Seed, "door states roll from the entrance, so every instance of a door has the same doors");
        Assert.AreEqual(2, blueprint.Layout.FloorTransitions.Count);
    }

    [TestMethod]
    public void AnInstancePutsTheMonstersWhereTheCaptureHadThemAndRollsChestsOnPoolSpots()
    {
        Assert.IsTrue(R.Catalog.TryGet((uint)IdentityType.ACGEntrance, NascenceDoor, out StaticDungeonBlueprint blueprint));
        StaticDungeonPlayfield world = R.Manager.GetOrCreateStaticDungeon(StaticDungeonIds.FirstPlayfield + 101, blueprint, chestSeed: 7);
        try
        {
            var registry = world.GetRequiredService<DynelRegistry>();
            var locality = world.GetRequiredService<PlayfieldLocality>();
            var spawns = world.GetRequiredService<HashSpawnSystem>();
            List<Dynel> dynels = registry.Dynels().ToList();

            // The fixed furniture: every doorway a door (the exit first and open), every lift end a platform and a button.
            List<Door> doors = dynels.OfType<Door>().ToList();
            Assert.IsTrue(doors.Count > 0, "doors");
            Assert.IsTrue(doors.Any(door => door.IsOpen), "the exit door starts open");
            Assert.AreEqual(blueprint.Layout.FloorTransitions.Count * 4, dynels.OfType<DungeonFloorTransitionDynel>().Count(), "two lift ends, a platform and a button each");

            // Every resolved monster has a spawn point, and that point is where the capture saw the monster.
            Assert.AreEqual(blueprint.Npcs.Count, world.Placed.Npcs, "every NPC the file names with a known hash got a point");
            Assert.AreEqual(world.Placed.Npcs + world.Placed.Turrets, spawns.PointCount);
            using JsonDocument file = JsonDocument.Parse(File.ReadAllText(GameDataPaths.ResolveFile(R.Root, blueprint.FileName)));
            int checked_ = 0, onSpot = 0;
            foreach (JsonElement npc in file.RootElement.GetProperty("Npcs").EnumerateArray())
            {
                if (npc.GetProperty("Hash").ValueKind != JsonValueKind.String || !npc.TryGetProperty("Captured", out JsonElement captured))
                    continue;
                var at = new Vector3(captured[0].GetSingle(), captured[1].GetSingle(), captured[2].GetSingle());
                checked_++;
                if (spawns.TryFindNearest(npc.GetProperty("Hash").GetString()!, at, out Vector3 point)
                    && Math.Sqrt(Math.Pow(point.xf - at.xf, 2) + Math.Pow(point.zf - at.zf, 2)) <= 1.5)
                    onSpot++;
            }
            Assert.IsTrue(checked_ > 0, "the file carries hashes NpcTemplates knows");
            Assert.IsTrue(onSpot >= checked_ * 9 / 10, "NPC points stand where the capture had them: " + onSpot + "/" + checked_);

            // Chests: on pool spots, inside rooms, with loot rolled in memory and the captured appearance.
            List<Chest> chests = dynels.OfType<Chest>().ToList();
            Assert.IsTrue(chests.Count > 0, "chests");
            foreach (Chest chest in chests)
            {
                Assert.IsFalse(locality.IsOutsideDungeonRooms(chest.Position), "chest outside every room at " + chest.Position);
                Assert.IsNotNull(chest.SpawnDefinition);
                uint template = chest.SpawnDefinition!.Stats.Single(stat => stat.StatId == (int)CharacterStat.StaticInstance).Value;
                CollectionAssert.Contains(blueprint.Chests!.Templates.Select(id => (uint)id).ToList(), template);
                Assert.IsFalse(chest.IsLocked);
                foreach (Item item in chest.Loot.Content.Values)
                    Assert.IsFalse(item.IsPersisted, "chest loot is held in memory until a player takes it");
            }
            Assert.IsTrue(chests.Any(chest => chest.Loot.Content.Count > 0), "the loot table rolled something");
            Assert.AreEqual(chests.Count, chests.Select(chest => chest.Identity.Instance).Distinct().Count(), "chest identities are unique");
        }
        finally
        {
            Assert.IsTrue(R.Manager.TryReleaseStaticDungeon(world.Identity.Instance));
        }
    }

    [TestMethod]
    public void EveryInstanceRollsItsOwnChestSpots()
    {
        Assert.IsTrue(R.Catalog.TryGet((uint)IdentityType.ACGEntrance, NascenceDoor, out StaticDungeonBlueprint blueprint));
        StaticDungeonPlayfield first = R.Manager.GetOrCreateStaticDungeon(StaticDungeonIds.FirstPlayfield + 102, blueprint, chestSeed: 1);
        StaticDungeonPlayfield second = R.Manager.GetOrCreateStaticDungeon(StaticDungeonIds.FirstPlayfield + 103, blueprint, chestSeed: 2);
        try
        {
            HashSet<string> Spots(StaticDungeonPlayfield world) => world.GetRequiredService<DynelRegistry>().Dynels().OfType<Chest>()
                .Select(chest => chest.Position.xf.ToString("0.0") + "," + chest.Position.zf.ToString("0.0")).ToHashSet();
            HashSet<string> a = Spots(first), b = Spots(second);
            Assert.IsTrue(a.Count > 0 && b.Count > 0);
            Assert.IsFalse(a.SetEquals(b), "two instances of one door do not share chest spots");
            // The building, doors and NPC points are the same in both: the door is fixed, only the furniture rolls.
            Assert.AreEqual(first.Placed.Npcs, second.Placed.Npcs);
            Assert.AreEqual(first.GetRequiredService<DynelRegistry>().Dynels().OfType<Door>().Count(),
                second.GetRequiredService<DynelRegistry>().Dynels().OfType<Door>().Count());
        }
        finally
        {
            Assert.IsTrue(R.Manager.TryReleaseStaticDungeon(first.Identity.Instance));
            Assert.IsTrue(R.Manager.TryReleaseStaticDungeon(second.Identity.Instance));
        }
    }

    [TestMethod]
    public void OneInstancePerOwnerRecordedAndReleased()
    {
        Assert.IsTrue(R.Catalog.TryGet((uint)IdentityType.ACGEntrance, NascenceDoor, out StaticDungeonBlueprint blueprint));
        (int character, int team) = StaticDungeonService.OwnerKinds;
        Assert.IsTrue(R.Service.TryGetOrCreateInstance(blueprint, team, 5001, out int teamInstance));
        Assert.IsTrue(R.Service.TryGetOrCreateInstance(blueprint, team, 5001, out int again));
        Assert.AreEqual(teamInstance, again, "a teammate entering later joins the team's instance");
        Assert.IsTrue(R.Service.TryGetOrCreateInstance(blueprint, team, 5002, out int otherTeam));
        Assert.AreNotEqual(teamInstance, otherTeam, "two teams at one door get separate instances");
        Assert.IsTrue(R.Service.TryGetOrCreateInstance(blueprint, character, 9001, out int solo));
        Assert.AreNotEqual(teamInstance, solo);
        Assert.IsTrue(StaticDungeonIds.IsStaticDungeonPlayfield(teamInstance));
        Assert.IsNotNull(R.Store.Load(teamInstance), "the instance is recorded for other zones and for login after a restart");
        Assert.AreEqual((uint)NascenceDoor, R.Store.Load(teamInstance)!.EntranceInstance);

        // Another zone made the team's instance first: this zone adopts its row instead of making a second one.
        var elsewhere = new StaticDungeonInstanceRow
        {
            PlayfieldId = StaticDungeonIds.FirstPlayfield + 777, EntranceType = blueprint.Entrance.IdentityType,
            EntranceInstance = blueprint.Entrance.IdentityInstance, OwnerKind = team, OwnerId = 6001, CreatedAtUtcTicks = DateTime.UtcNow.Ticks
        };
        Assert.IsTrue(R.Store.TryInsert(elsewhere));
        Assert.IsTrue(R.Service.TryGetOrCreateInstance(blueprint, team, 6001, out int adopted));
        Assert.AreEqual(elsewhere.PlayfieldId, adopted);

        // Built, logged into while live, then released: the row and the owner's record go with it.
        StaticDungeonPlayfield world = R.Manager.GetOrCreateStaticDungeon(teamInstance, blueprint, chestSeed: 3, released: R.Service.OnReleased);
        (Playfield login, Vector3? position, PlayfieldAdmission? admission) = R.Service.ResolveLogin(teamInstance);
        Assert.AreSame(world, login, "a login inside a live instance goes back into it");
        Assert.IsNull(position, "the stored position stands");
        Assert.IsNull(admission);
        Assert.IsTrue(R.Service.TryReset(teamInstance));
        Assert.IsNull(R.Store.Load(teamInstance), "a released instance leaves no row");
        Assert.AreEqual(0, R.Service.InstanceOf(blueprint.Entrance.Identity, team, 5001), "the next entry builds a fresh one");
        Assert.IsFalse(R.Manager.TryGetStaticDungeon(teamInstance, out _));
    }

    [TestMethod]
    public void WalkingIntoTheDoorWithoutAKeyBuildsAndEntersTheTeamsInstance()
    {
        Assert.IsTrue(R.Catalog.TryGet((uint)IdentityType.ACGEntrance, NascenceDoor, out StaticDungeonBlueprint blueprint));
        MissionEntrancePlacement entrance = blueprint.Entrance;
        var session = new Session();
        Player player = CreatePlayer(4242, "Walker");
        Playfield exterior = Blank<Playfield>();
        typeof(Playfield).GetField("<Identity>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(exterior, new Identity { Type = IdentityType.Playfield, Instance = entrance.PlayfieldId });
        player.Playfield = exterior;
        player.Position = new Vector3(entrance.LocalX + 1f, entrance.LocalY, entrance.LocalZ + 1f);
        player.Stats.Set(CharacterStat.Health, 100);
        player.Stats.Set(CharacterStat.MaxHealth, 100);
        player.Session = session;
        session.BindPlayer(player);
        R.Service.OwnerOverride = _ => (StaticDungeonService.OwnerKinds.Team, 8080);
        var target = new Identity { Type = IdentityType.ACGEntrance, Instance = unchecked((int)NascenceDoor) };
        int playfieldId = 0;
        try
        {
            // First use: the entrance is taken (true) and the instance builds off the tick.
            Assert.IsTrue(R.Service.TryEnter(player, target));
            playfieldId = R.Service.InstanceOf(entrance.Identity, StaticDungeonService.OwnerKinds.Team, 8080);
            Assert.IsTrue(playfieldId != 0);
            Assert.IsTrue(R.Service.WaitForBuild(playfieldId, TimeSpan.FromMinutes(2)), "the build finishes");
            Assert.IsTrue(R.Manager.TryGetStaticDungeon(playfieldId, out StaticDungeonPlayfield live));
            Assert.AreEqual(0, session.Transfers.Count, "nothing moves until the instance is live");

            // The retry a live instance gets: the player is handed over to it, facing into the entrance room.
            Thread.Sleep(800); // past the use cooldown
            Assert.IsTrue(R.Service.TryEnter(player, target));
            Assert.AreEqual(1, session.Transfers.Count);
            Assert.AreSame(live, session.Transfers[0].World);
            Assert.AreEqual(blueprint.Layout.Spawn, session.Transfers[0].Position);

            // The same door from inside a dungeon, dead, or from another playfield does nothing.
            Thread.Sleep(800);
            player.Playfield = live;
            Assert.IsFalse(R.Service.TryEnter(player, target), "no dungeon is entered from inside one");
            player.Playfield = exterior;
            player.Position = new Vector3(entrance.LocalX + 50f, entrance.LocalY, entrance.LocalZ);
            Assert.IsFalse(R.Service.TryEnter(player, target), "out of the entrance's reach");
            Assert.IsFalse(R.Service.TryEnter(player, new Identity { Type = IdentityType.ACGEntrance, Instance = 12345 }), "not a static dungeon's door");
        }
        finally
        {
            R.Service.OwnerOverride = null;
            if (playfieldId != 0)
                R.Service.TryReset(playfieldId);
        }
    }

    [TestMethod]
    public void WalkingIntoTheDoorOnTheRealPlayfieldReachesTheStaticDungeon()
    {
        // Live defect: the walk-in trigger fired, but the playfield's container had no static dungeon service, so the
        // entrance did nothing (the client sends no use for these doors: walking in is the only way through).
        Assert.IsTrue(R.Catalog.TryGet((uint)IdentityType.ACGEntrance, NascenceDoor, out StaticDungeonBlueprint blueprint));
        MissionEntrancePlacement entrance = blueprint.Entrance;
        Playfield exterior = R.Manager.GetOrCreate(entrance.PlayfieldId);
        Assert.AreSame(R.Service, exterior.GetService<StaticDungeonService>(), "every playfield carries the static dungeon service");

        var world = exterior.GetRequiredService<ZoneEngine_New.Core.WorldSimulation.WorldSimulationAccess>().Instance;
        Assert.IsNotNull(world, "the exterior has its world simulation");
        var session = new Session();
        Player player = CreatePlayer(4343, "Doorwalker");
        player.Stats.Set(CharacterStat.Health, 100);
        player.Stats.Set(CharacterStat.MaxHealth, 100);
        player.Session = session;
        session.BindPlayer(player);
        player.Playfield = exterior;
        R.Service.OwnerOverride = _ => (StaticDungeonService.OwnerKinds.Team, 9090);
        DynelRegistry registry = exterior.GetRequiredService<DynelRegistry>();
        int playfieldId = 0;
        try
        {
            // Run up to the door from 15 m out, the way the player did, through the engine's own trigger pass.
            player.Position = new Vector3(entrance.LocalX, entrance.LocalY, entrance.LocalZ - 15f);
            registry.Register(player);
            world!.TickSoftTriggers(exterior, 0.1);
            Assert.AreEqual(0, R.Service.InstanceOf(entrance.Identity, StaticDungeonService.OwnerKinds.Team, 9090), "nothing away from the door");

            player.Position = new Vector3(entrance.LocalX, entrance.LocalY, entrance.LocalZ);
            world.TickSoftTriggers(exterior, 0.1);
            playfieldId = R.Service.InstanceOf(entrance.Identity, StaticDungeonService.OwnerKinds.Team, 9090);
            Assert.AreNotEqual(0, playfieldId, "walking into the door makes the team's instance");
            Assert.IsTrue(R.Service.WaitForBuild(playfieldId, TimeSpan.FromMinutes(2)), "the build finishes");
            Assert.IsTrue(R.Manager.TryGetStaticDungeon(playfieldId, out _), "the instance is live to hand the player into");
        }
        finally
        {
            registry.UnregisterExact(player);
            R.Service.OwnerOverride = null;
            if (playfieldId != 0)
                R.Service.TryReset(playfieldId);
        }
    }

    [TestMethod]
    public void AReloadReportsABadFileAndKeepsTheRest()
    {
        string overrideRoot = Path.Combine(Path.GetTempPath(), "aorebirth-static-dungeon-test-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(overrideRoot, "Dungeons", "Static");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "9999.json"), "{ \"SchemaVersion\": 1, \"Entrance\": { \"IdentityType\": 56006, \"IdentityInstance\": 9999 } }");
        File.WriteAllText(Path.Combine(folder, "broken.json"), "{ not json");
        string? previous = Environment.GetEnvironmentVariable(GameDataPaths.OverrideEnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(GameDataPaths.OverrideEnvironmentVariableName, overrideRoot);
            int before = R.Catalog.Dungeons.Count;
            var result = R.Catalog.Reload();
            Assert.IsTrue(result.Ok);
            Assert.AreEqual(before, result.Entries, "the good files still load");
            Assert.IsTrue(result.Problems.Any(problem => problem.Contains("9999.json", StringComparison.Ordinal)
                && problem.Contains("MissionEntrancePlacements", StringComparison.Ordinal)), string.Join("\n", result.Problems));
            Assert.IsTrue(result.Problems.Any(problem => problem.Contains("broken.json", StringComparison.Ordinal)));
            Assert.IsTrue(R.Catalog.TryGet((uint)IdentityType.ACGEntrance, NascenceDoor, out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable(GameDataPaths.OverrideEnvironmentVariableName, previous);
            R.Catalog.Reload();
            Directory.Delete(overrideRoot, recursive: true);
        }
    }

    static T Blank<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    sealed class Rig
    {
        internal readonly string Root;
        internal readonly StaticDungeonCatalog Catalog;
        internal readonly PlayfieldManager Manager;
        internal readonly StaticDungeonService Service;
        internal readonly MemoryStore Store = new();

        internal Rig()
        {
            var logger = new StubLogger();
            Root = GameDataPaths.ResolveRuntimeRoot();
            var data = new GameDataStore(logger);
            var templates = new ItemTemplateCatalog(data, logger);
            var builder = new StubItemBuilder();
            var ids = new StubInstanceIdAllocator();
            var layouts = new DungeonLayoutGenerator(data, logger);
            if (!layouts.IsAvailable)
                Assert.Inconclusive("The runtime GameData at " + Root + " cannot generate dungeons.");
            MissionDestinationCatalog entrances = MissionDestinationCatalog.Load(Root);
            Catalog = new StaticDungeonCatalog(data, entrances, layouts, templates, logger);
            if (Catalog.Dungeons.Count == 0)
                Assert.Inconclusive("The runtime GameData at " + Root + " has no Dungeons/Static files: " + string.Join("; ", Catalog.Problems));

            PlayfieldManager? manager = null;
            var lazy = new Lazy<PlayfieldManager>(() => manager ?? throw new InvalidOperationException("The manager is not built yet."));
            var persistence = new Persistence();
            var flush = new InventoryFlushService(lazy, persistence, logger);
            var actions = new InventoryActionService(persistence, flush, ids, logger);
            var minter = new HashItemMinter(data, templates, builder);
            // The service before the manager, as the root container resolves them: every playfield the manager builds
            // carries it, which is how walking into a static dungeon's door reaches it.
            var service = new StaticDungeonService(Catalog, entrances, Store, ids, lazy, data, logger);
            manager = new PlayfieldManager(logger, new MessageRouter([], logger), new PlayerHydrator(builder, data), data, builder, minter,
                new StubInventoryRepository(), ids, new InventoryMoveService(logger, flush, actions), flush,
                new TradeService(logger, data, templates, minter, ids, flush, persistence), Blank<CharacterSnapshotService>(),
                new PlayfieldMetricsRegistry(), new TeamService(), templates, new Shops(), staticDungeons: service);
            Manager = manager;
            Service = service;
        }
    }

    /// <summary>The instance table in memory, with the real table's unique keys.</summary>
    sealed class MemoryStore : IStaticDungeonInstanceStore
    {
        readonly Dictionary<int, StaticDungeonInstanceRow> _rows = new();

        public StaticDungeonInstanceRow? Find(uint entranceType, uint entranceInstance, int ownerKind, int ownerId)
        {
            lock (_rows)
                return _rows.Values.FirstOrDefault(row => row.EntranceType == entranceType && row.EntranceInstance == entranceInstance
                    && row.OwnerKind == ownerKind && row.OwnerId == ownerId);
        }

        public StaticDungeonInstanceRow? Load(int playfieldId)
        {
            lock (_rows)
                return _rows.TryGetValue(playfieldId, out StaticDungeonInstanceRow? row) ? row : null;
        }

        public bool TryInsert(StaticDungeonInstanceRow row)
        {
            lock (_rows)
            {
                if (_rows.ContainsKey(row.PlayfieldId) || Find(row.EntranceType, row.EntranceInstance, row.OwnerKind, row.OwnerId) != null)
                    return false;
                _rows[row.PlayfieldId] = row;
                return true;
            }
        }

        public void Delete(int playfieldId)
        {
            lock (_rows)
                _rows.Remove(playfieldId);
        }

        public int PurgeOlderThan(long cutoffUtcTicks, int limit)
        {
            lock (_rows)
            {
                List<int> stale = _rows.Values.Where(row => row.CreatedAtUtcTicks < cutoffUtcTicks).Take(limit).Select(row => row.PlayfieldId).ToList();
                foreach (int id in stale)
                    _rows.Remove(id);
                return stale.Count;
            }
        }
    }

    sealed class Session : IZoneSession
    {
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        internal readonly List<(Playfield World, Vector3 Position)> Transfers = [];
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public bool TransferToPlayfield(Playfield destination, Vector3 landing) { Transfers.Add((destination, landing)); return true; }
        public bool TransferToPlayfield(Playfield destination, Vector3 landing, AORebirth.Core.Vector.Quaternion heading) => TransferToPlayfield(destination, landing);
        public void Send(byte[] packet) { }
        public void Send(MessageBody body) { }
        public void Send(MessageBody body, int sender, int receiver) { }
        public void SendInitiateCompression() { }
        public void Close() => State = SessionState.Closed;
    }

    sealed class Persistence : ICharacterCoalesceCommit, IInventoryMutationPersistence, ITradePersistence
    {
        public void Persist(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates, int characterId,
            IReadOnlyList<int> uploadedNanoIds, IReadOnlyList<ActiveNanoRecord>? activeNanos, IReadOnlyList<SkillLockRecord>? skillLocks)
            => throw new AssertFailedException("Static dungeons write no character rows.");
        public void Persist(InventoryMutationBatch batch) => throw new AssertFailedException("Static dungeons write no inventory rows.");
        public void Persist(TradePersistenceBatch batch) => throw new AssertFailedException("Static dungeons trade nothing.");
    }

    sealed class Shops : IShopDao
    {
        public IList<ShopVendorData> ListForPlayfield(int playfieldId) => [];
    }

    static Player CreatePlayer(int instance, string firstName)
    {
        var builder = new StubItemBuilder();
        var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = instance }, new StubLogger(), builder)
        {
            FirstName = firstName
        };
        player.Inventory.Apply(new CharacterHydrationResult(), instance, builder);
        return player;
    }

    sealed class StubLogger : IZoneLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(Exception exception, string message) { }
        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }

    /// <summary>Items as the real builder shapes them, minus the template catalog: enough for loot rolls and inventories.</summary>
    sealed class StubItemBuilder : IItemBuilder
    {
        int _nextInstanceId = 90000;

        public Item Create(int lowId, int highId, int quality, AORebirth.Enums.ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null)
            => new()
            {
                LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount, InstanceId = instanceId,
                Identity = identity ?? Identity.None, Definition = CreateTemplate(lowId, highId, quality)
            };

        public Item CreateWithNewInstance(int lowId, int highId, int quality, AORebirth.Enums.ItemSource source, int stackCount = 1)
        {
            Item item = Create(lowId, highId, quality, source, stackCount);
            item.AssignInstanceId(++_nextInstanceId);
            return item;
        }

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }

    /// <summary>Every call throws: a static dungeon build reaches no item rows.</summary>
    sealed class StubInventoryRepository : IInventoryRepository
    {
        public IReadOnlyList<ItemInstanceRecord> GetCarriedItems(int characterId) => throw new NotSupportedException();
        public IReadOnlyList<ItemInstanceRecord> GetBankItems(int characterId) => throw new NotSupportedException();
        public IReadOnlyList<ItemInstanceRecord> GetContainerItems(int containerInstanceId) => throw new NotSupportedException();
        public int LeaseInstanceIdBlock(int count) => throw new NotSupportedException();
        public ItemInstanceRecord Insert(ItemInstanceRecord item) => throw new NotSupportedException();
        public void UpdateLocation(int instanceId, int containerType, int containerInstance, int containerPlacement) => throw new NotSupportedException();
        public void UpdateLocations(IReadOnlyList<ItemLocationUpdate> locations) => throw new NotSupportedException();
        public void PersistNewAndUpdateLocations(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates) => throw new NotSupportedException();
    }

    sealed class StubInstanceIdAllocator : IItemInstanceIdAllocator
    {
        int _next = 500000;
        public int Allocate() => Interlocked.Increment(ref _next);
    }
}
