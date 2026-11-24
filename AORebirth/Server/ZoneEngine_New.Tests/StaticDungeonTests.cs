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
/// service that hands out each door's one shared instance. Drives the real catalog, the real style geometry, the real spawn
/// system and the real chest roll; only persistence and the network are doubles.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class StaticDungeonTests
{
    /// <summary>The Nascence door whose capture carried stock's own PlayfieldAnarchyF (A Door(5), 33 rooms).</summary>
    const uint NascenceDoor = 3221360854;

    /// <summary>
    /// Lower Scheol's mausoleum entrance: a door (0xC748) that carries the ACG building of the "A Mauseleum entrance"
    /// capture. Its instance, 0xC0011311, is also the instance of the unrelated "Trial-Area" mission entrance (0xDAC6).
    /// </summary>
    const uint MausoleumDoor = 0xC0011311;

    const int LowerScheol = 4881;

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
            if (blueprint.Chests!.LootTable.Count > 0)
                Assert.IsTrue(chests.Any(chest => chest.Loot.Content.Count > 0), "the loot table rolled something");
            else
                Assert.IsTrue(chests.All(chest => chest.Loot.Content.Count == 0), "a block with no loot rows spawns empty chests");
            Assert.AreEqual(chests.Count, chests.Select(chest => chest.Identity.Instance).Distinct().Count(), "chest identities are unique");
        }
        finally
        {
            Assert.IsTrue(R.Manager.TryReleaseStaticDungeon(world.Identity.Instance));
        }
    }

    [TestMethod]
    public void AChestBlockWithNoLootRowsKeepsTheDungeonAndSpawnsEmptyChests()
    {
        // Every static dungeon that names a chest block still loads when the block has no rows (it used to be left out).
        foreach (string problem in R.Catalog.Problems)
            Assert.IsFalse(problem.Contains("LootTable", StringComparison.Ordinal), "a dungeon was left out over its chest table: " + problem);
        Assert.IsTrue(R.Catalog.TryGet((uint)IdentityType.ACGEntrance, NascenceDoor, out StaticDungeonBlueprint captured));
        Assert.IsNotNull(captured.Chests);

        // The same dungeon with its chest table emptied: the chests still stand on their spots, hold nothing, and the
        // rest of the dungeon builds as before.
        var empty = new StaticDungeonBlueprint(captured.FileName, captured.Name, captured.Entrance, captured.Layout, captured.Quality,
            captured.RespawnSeconds, captured.Npcs, captured.UnresolvedNpcs, captured.Chests! with { LootTable = [] }, captured.Turrets,
            captured.Mines, captured.Controllers, captured.Sites);
        StaticDungeonPlayfield world = R.Manager.GetOrCreateStaticDungeon(StaticDungeonIds.FirstPlayfield + 104, empty, chestSeed: 7);
        try
        {
            List<Chest> chests = world.GetRequiredService<DynelRegistry>().Dynels().OfType<Chest>().ToList();
            Assert.IsTrue(chests.Count > 0, "the chests are placed");
            Assert.AreEqual(chests.Count, world.Placed.Chests);
            Assert.IsTrue(chests.All(chest => chest.Loot.Content.Count == 0), "every chest is empty");
            Assert.AreEqual(empty.Npcs.Count, world.Placed.Npcs, "the monsters are placed as with a full table");
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
    public void OneSharedInstancePerDoorRecordedAndReleased()
    {
        Assert.IsTrue(R.Catalog.TryGet((uint)IdentityType.ACGEntrance, NascenceDoor, out StaticDungeonBlueprint blueprint));
        Assert.AreEqual(0, R.Service.InstanceOf(blueprint.Entrance.Identity), "no instance is left over from another test");

        // Another zone made the door's instance first: this zone adopts its row instead of making a second one.
        (int ownerKind, int ownerId) = StaticDungeonService.SharedOwner;
        var elsewhere = new StaticDungeonInstanceRow
        {
            PlayfieldId = StaticDungeonIds.FirstPlayfield + 777, EntranceType = blueprint.Entrance.IdentityType,
            EntranceInstance = blueprint.Entrance.IdentityInstance, OwnerKind = ownerKind, OwnerId = ownerId, CreatedAtUtcTicks = DateTime.UtcNow.Ticks
        };
        Assert.IsTrue(R.Store.TryInsert(elsewhere));
        Assert.IsTrue(R.Service.TryGetOrCreateInstance(blueprint, out int instance));
        Assert.AreEqual(elsewhere.PlayfieldId, instance);
        Assert.IsTrue(R.Service.TryGetOrCreateInstance(blueprint, out int again));
        Assert.AreEqual(instance, again, "whoever enters later joins the door's one instance");
        Assert.IsTrue(StaticDungeonIds.IsStaticDungeonPlayfield(instance));

        // Built, logged into while live, then released: the row and the door's record go with it.
        StaticDungeonPlayfield world = R.Manager.GetOrCreateStaticDungeon(instance, blueprint, chestSeed: 3, released: R.Service.OnReleased);
        (Playfield login, Vector3? position, PlayfieldAdmission? admission) = R.Service.ResolveLogin(instance);
        Assert.AreSame(world, login, "a login inside a live instance goes back into it");
        Assert.IsNull(position, "the stored position stands");
        Assert.IsNull(admission);
        Assert.IsTrue(R.Service.TryReset(instance));
        Assert.IsNull(R.Store.Load(instance), "a released instance leaves no row");
        Assert.AreEqual(0, R.Service.InstanceOf(blueprint.Entrance.Identity), "the next entry builds a fresh one");
        Assert.IsFalse(R.Manager.TryGetStaticDungeon(instance, out _));
    }

    [TestMethod]
    public void TwoUnteamedPlayersWalkingIntoTheDoorShareOneInstance()
    {
        // Live defect: two players who were not in a team walked through the same door at the same moment and each got
        // a separate instance of the dungeon, so neither could see the other inside.
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
        // A second player, in no team with the first, at the same door.
        var otherSession = new Session();
        Player other = CreatePlayer(4243, "Companion");
        other.Playfield = exterior;
        other.Position = new Vector3(entrance.LocalX - 1f, entrance.LocalY, entrance.LocalZ + 1f);
        other.Stats.Set(CharacterStat.Health, 100);
        other.Stats.Set(CharacterStat.MaxHealth, 100);
        other.Session = otherSession;
        otherSession.BindPlayer(other);
        var target = new Identity { Type = IdentityType.ACGEntrance, Instance = unchecked((int)NascenceDoor) };
        int playfieldId = 0;
        try
        {
            // First use: the entrance is taken (true) and the instance builds off the tick.
            Assert.IsTrue(R.Service.TryEnter(player, target));
            playfieldId = R.Service.InstanceOf(entrance.Identity);
            Assert.IsTrue(playfieldId != 0);
            Assert.IsNotNull(R.Store.Load(playfieldId), "the instance is recorded for other zones and for login after a restart");
            Assert.AreEqual((uint)NascenceDoor, R.Store.Load(playfieldId)!.EntranceInstance);
            // The second player arrives while it builds: they wait on the same build, not a second instance.
            Assert.IsTrue(R.Service.TryEnter(other, target));
            Assert.AreEqual(playfieldId, R.Service.InstanceOf(entrance.Identity));
            Assert.IsTrue(R.Service.WaitForBuild(playfieldId, TimeSpan.FromMinutes(2)), "the build finishes");
            Assert.IsTrue(R.Manager.TryGetStaticDungeon(playfieldId, out StaticDungeonPlayfield live));
            Assert.AreEqual(0, session.Transfers.Count, "nothing moves until the instance is live");
            Assert.AreEqual(1, R.Manager.SnapshotStaticDungeons().Count(d => d.Blueprint.Entrance.Identity.Equals(entrance.Identity)),
                "one instance of the dungeon exists");

            // The retry a live instance gets: each player is handed over to it, facing into the entrance room.
            Thread.Sleep(800); // past the use cooldown
            Assert.IsTrue(R.Service.TryEnter(player, target));
            Assert.AreEqual(1, session.Transfers.Count);
            Assert.AreSame(live, session.Transfers[0].World);
            Assert.AreEqual(blueprint.Layout.Spawn, session.Transfers[0].Position);
            Assert.IsTrue(R.Service.TryEnter(other, target));
            Assert.AreEqual(1, otherSession.Transfers.Count);
            Assert.AreSame(live, otherSession.Transfers[0].World, "both players land in the same instance and see each other");

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
        DynelRegistry registry = exterior.GetRequiredService<DynelRegistry>();
        int playfieldId = 0;
        try
        {
            // Run up to the door from 15 m out, the way the player did, through the engine's own trigger pass.
            player.Position = new Vector3(entrance.LocalX, entrance.LocalY, entrance.LocalZ - 15f);
            registry.Register(player);
            world!.TickSoftTriggers(exterior, 0.1);
            Assert.AreEqual(0, R.Service.InstanceOf(entrance.Identity), "nothing away from the door");

            player.Position = new Vector3(entrance.LocalX, entrance.LocalY, entrance.LocalZ);
            world.TickSoftTriggers(exterior, 0.1);
            playfieldId = R.Service.InstanceOf(entrance.Identity);
            Assert.AreNotEqual(0, playfieldId, "walking into the door makes the door's instance");
            Assert.IsTrue(R.Service.WaitForBuild(playfieldId, TimeSpan.FromMinutes(2)), "the build finishes");
            Assert.IsTrue(R.Manager.TryGetStaticDungeon(playfieldId, out _), "the instance is live to hand the player into");
        }
        finally
        {
            registry.UnregisterExact(player);
            if (playfieldId != 0)
                R.Service.TryReset(playfieldId);
        }
    }

    static StaticDungeonBlueprint MausoleumBlueprint()
    {
        if (!R.Catalog.TryGet((uint)IdentityType.Door, MausoleumDoor, out StaticDungeonBlueprint blueprint))
            Assert.Inconclusive("The runtime GameData has no static dungeon behind Lower Scheol's mausoleum door: "
                + string.Join("\n", R.Catalog.Problems.Where(problem => problem.Contains(MausoleumDoor.ToString(), StringComparison.Ordinal))));
        return blueprint;
    }

    [TestMethod]
    public void ADoorDungeonLoadsBehindItsDoorAndNotTheMissionEntranceSharingItsNumber()
    {
        // Live defect: the mausoleum's dungeon was keyed to the Trial-Area mission entrance (same instance, other type),
        // so walking into Trial-Area opened a dungeon that is not there, and the mausoleum door did nothing.
        StaticDungeonBlueprint blueprint = MausoleumBlueprint();
        Assert.IsFalse(R.Catalog.TryGet((uint)IdentityType.ACGEntrance, MausoleumDoor, out _), "no dungeon is behind the Trial-Area mission entrance");
        Assert.AreEqual((uint)IdentityType.Door, blueprint.Entrance.IdentityType);
        Assert.AreEqual(LowerScheol, blueprint.Entrance.PlayfieldId);
        // The door's own placement (Dynels.dat), not Trial-Area's at (1356.9, 259.5, 1912).
        Assert.AreEqual(1589.6f, blueprint.Entrance.LocalX, 0.1f);
        Assert.AreEqual(1755.0f, blueprint.Entrance.LocalZ, 0.1f);
        Assert.AreEqual("a mausoleum entrance", blueprint.Name);
        foreach (string problem in R.Catalog.Problems)
            Assert.IsFalse(problem.Contains("Entrance.IdentityType must be", StringComparison.Ordinal), "a door dungeon was left out: " + problem);

        // The door stands in for a catalog row exactly as an entrance would: its heading is the Dynels heading, and the
        // exit door puts people down in front of it.
        var placed = R.Data.FindPlacedDynel(LowerScheol, (int)IdentityType.Door, unchecked((int)MausoleumDoor));
        Assert.IsNotNull(placed);
        Assert.IsTrue(AcgBuildingDoors.CarriesOwnBuilding(placed!), "stock's placement carries the door's ACG building");
        Assert.AreEqual(placed!.Heading.W, blueprint.Entrance.RotationComponent0);
        Assert.AreEqual(placed.Heading.X, blueprint.Entrance.RotationComponent1);
        Vector3 landing = AcgDungeonPlayfield.ExitLanding(blueprint.Entrance, out _);
        double away = Math.Sqrt(Math.Pow(landing.x - placed.Position.X, 2) + Math.Pow(landing.z - placed.Position.Z, 2));
        Assert.IsTrue(away > 0.5 && away < 10, "the exit lands just in front of the mausoleum door: " + away);
    }

    [TestMethod]
    public void OnlyDoorsCarryingTheirOwnBuildingAreDungeonDoors()
    {
        // The Trial-Area mission entrance and Lower Scheol's teleporters share instances with the dungeon doors but
        // carry no building of their own; a plain door elsewhere never counts.
        PlayfieldGeometryData geometry = R.Data.GetPlayfieldGeometry(LowerScheol);
        var dynels = geometry.Dynels?.Dynels;
        if (dynels == null)
            Assert.Inconclusive("No Lower Scheol Dynels.dat in the runtime GameData.");
        var doors = dynels.Where(AcgBuildingDoors.CarriesOwnBuilding).Select(d => unchecked((uint)d.IdentityInstance)).OrderBy(i => i).ToList();
        CollectionAssert.AreEqual(new uint[] { 0xC0001311, 0xC0011311, 0xC0021311 }, doors,
            "the swirling brink cave, the mausoleum and the underroot passage");
        Assert.IsTrue(dynels.Where(d => d.IdentityType != (int)IdentityType.Door).All(d => !AcgBuildingDoors.CarriesOwnBuilding(d)));
    }

    [TestMethod]
    public void WalkingIntoTheMausoleumDoorReachesItsDungeonAndTrialAreaDoesNothing()
    {
        StaticDungeonBlueprint blueprint = MausoleumBlueprint();
        MissionEntrancePlacement door = blueprint.Entrance;
        Playfield exterior = R.Manager.GetOrCreate(LowerScheol);
        var world = exterior.GetRequiredService<ZoneEngine_New.Core.WorldSimulation.WorldSimulationAccess>().Instance;
        Assert.IsNotNull(world, "Lower Scheol has its world simulation");
        var session = new Session();
        Player player = CreatePlayer(4444, "Mourner");
        player.Stats.Set(CharacterStat.Health, 100);
        player.Stats.Set(CharacterStat.MaxHealth, 100);
        player.Session = session;
        session.BindPlayer(player);
        player.Playfield = exterior;
        DynelRegistry registry = exterior.GetRequiredService<DynelRegistry>();
        var trialArea = new MissionPlacementIdentity((uint)IdentityType.ACGEntrance, MausoleumDoor);
        int playfieldId = 0;
        try
        {
            registry.Register(player);
            // Through the Trial-Area mission entrance first: nothing is built for it or for the door.
            Assert.IsTrue(R.Entrances.TryGetByIdentity((uint)IdentityType.ACGEntrance, MausoleumDoor, out MissionEntrancePlacement trial));
            player.Position = new Vector3(trial.LocalX, trial.LocalY, trial.LocalZ - 15f);
            world!.TickSoftTriggers(exterior, 0.1);
            player.Position = new Vector3(trial.LocalX, trial.LocalY, trial.LocalZ);
            world.TickSoftTriggers(exterior, 0.1);
            Assert.AreEqual(0, R.Service.InstanceOf(trialArea), "Trial-Area opens no static dungeon");
            Assert.AreEqual(0, R.Service.InstanceOf(door.Identity), "nor the mausoleum's, from across the zone");
            Assert.IsFalse(R.Service.TryEnter(player, new Identity { Type = IdentityType.ACGEntrance, Instance = unchecked((int)MausoleumDoor) }),
                "using the Trial-Area entrance does not reach the door's dungeon");

            // Then up to the mausoleum door from 15 m out, through the engine's own trigger pass.
            Thread.Sleep(800); // past the use cooldown
            player.Position = new Vector3(door.LocalX - 15f, door.LocalY, door.LocalZ);
            world.TickSoftTriggers(exterior, 0.1);
            Assert.AreEqual(0, R.Service.InstanceOf(door.Identity), "nothing away from the door");
            player.Position = new Vector3(door.LocalX, door.LocalY, door.LocalZ);
            world.TickSoftTriggers(exterior, 0.1);
            playfieldId = R.Service.InstanceOf(door.Identity);
            Assert.AreNotEqual(0, playfieldId, "walking into the door makes the door's instance");
            Assert.AreEqual((uint)IdentityType.Door, R.Store.Load(playfieldId)!.EntranceType, "the instance is recorded under the door, not Trial-Area");
            Assert.IsTrue(R.Service.WaitForBuild(playfieldId, TimeSpan.FromMinutes(2)), "the build finishes");
            Assert.IsTrue(R.Manager.TryGetStaticDungeon(playfieldId, out StaticDungeonPlayfield live), "the instance is live to hand the player into");

            // Once it is live the engine hands the player in by itself, once, at the dungeon's arrival point.
            for (int wait = 0; wait < 100 && session.Transfers.Count == 0; wait++)
                Thread.Sleep(50);
            Assert.AreEqual(1, session.Transfers.Count, "handed into the live instance exactly once");
            Assert.AreSame(live, session.Transfers[0].World);
            Assert.AreEqual(blueprint.Layout.Spawn, session.Transfers[0].Position);

            // Nobody enters a dungeon from inside one.
            var target = new Identity { Type = IdentityType.Door, Instance = unchecked((int)MausoleumDoor) };
            Thread.Sleep(800);
            player.Playfield = live;
            Assert.IsFalse(R.Service.TryEnter(player, target), "no dungeon is entered from inside one");
            player.Playfield = exterior;
        }
        finally
        {
            registry.UnregisterExact(player);
            if (playfieldId != 0)
                R.Service.TryReset(playfieldId);
        }
    }

    [TestMethod]
    public void ALoginInAReleasedDoorDungeonLandsInFrontOfTheDoor()
    {
        // After a restart the instance is gone but its row remains: the character comes out at the mausoleum door on
        // Lower Scheol, not at the respawn point and not at Trial-Area.
        StaticDungeonBlueprint blueprint = MausoleumBlueprint();
        Assert.IsTrue(R.Service.TryGetOrCreateInstance(blueprint, out int instance));
        try
        {
            Assert.IsFalse(R.Manager.TryGetStaticDungeon(instance, out _), "not built");
            (Playfield login, Vector3? position, PlayfieldAdmission? admission) = R.Service.ResolveLogin(instance);
            Assert.AreEqual(LowerScheol, login.Identity.Instance);
            Assert.IsNotNull(position);
            Vector3 expected = AcgDungeonPlayfield.ExitLanding(blueprint.Entrance, out _);
            Assert.AreEqual(expected.x, position!.x, 0.01);
            Assert.AreEqual(expected.z, position.z, 0.01);
            admission?.Dispose();
        }
        finally
        {
            R.Store.Delete(instance);
            var released = Blank<StaticDungeonPlayfield>();
            typeof(Playfield).GetField("<Identity>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(released, new Identity { Type = IdentityType.Playfield, Instance = instance });
            R.Service.OnReleased(released);
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
        internal readonly GameDataStore Data;
        internal readonly MissionDestinationCatalog Entrances;
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
            Data = data;
            Entrances = entrances;
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
