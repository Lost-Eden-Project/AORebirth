namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

using AORebirth.Core.GameData;
using AORebirth.Enums;
using AORebirth.World.Collision;

using LostEden.Protocol.GameData;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Ai;
using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Configuration;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.WorldSimulation;

using NVector3 = System.Numerics.Vector3;
using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// In a mission dungeon, characters in rooms that no chain of open doors joins are out of each other's sight, whatever
/// a sight ray would say. The rooms here have no walls at all, so only the closed doors between rooms can hide anyone:
/// three rooms in a row (0 - 1 - 2, a door in each doorway, standing aside of the sight lines) and, a floor above
/// room 0, rooms 3 and 4 that only a lift reaches. As placed rooms do (DungeonRoomBounds.FromPlaced), each room's box
/// reaches 1 m past its own tiles into the next room, so the metre on either side of a doorway's wall line belongs to
/// both rooms' boxes.
/// </summary>
[TestClass]
public sealed class DungeonRoomSightTests
{
    /// <summary>
    /// Lost Eden: players in Rubi-Ka missions lost line of sight on monsters standing in front of them. A character who
    /// walks through a door keeps the room they came from as their cell while that room's box still holds them (the
    /// client's RoomSpace_t rule), and about a third of a generated mission's spawn points lie where two rooms' boxes
    /// overlap. Once the door closed behind the player, the rooms-apart rule took the stale cell for the truth and
    /// hid the monster. Where a position is held by two rooms, the sight ray decides.
    /// </summary>
    [TestMethod]
    public void AFighterJustThroughTheDoorStillSeesTheMonsterInTheRoomTheyEntered()
    {
        using var rig = new Rig();
        // Standing 0.5 m inside room 1's tiles, cell still room 0 (room 0's box reaches to x = 21). The door behind
        // them closed again as they left its touch.
        Player player = rig.Fighter(1, new Vector3(20.5f, 0, 0), cell: 0);
        NpcCharacter monster = rig.Monster(1001, new Vector3(30, 0, 0));
        Assert.AreEqual(0, player.Cell!.Id);
        Assert.AreEqual(1, monster.Cell!.Id);

        Assert.IsTrue(player.HasLineOfSightTo(monster), "the fighter stands in the monster's room: nothing between them");
        Assert.IsTrue(monster.HasLineOfSightTo(player));
    }

    [TestMethod]
    public void AMonsterSpawnedAgainstTheSharedWallIsSeenFromItsOwnRoom()
    {
        using var rig = new Rig();
        // A spawn takes the smaller of the boxes holding it: this monster stands in room 1, 0.5 m from the wall it
        // shares with room 0, and its cell says room 0. The door between the rooms is closed.
        NpcCharacter monster = rig.Monster(1001, new Vector3(20.5f, 0, 0), cell: 0);
        Player player = rig.Fighter(1, new Vector3(30, 0, 0));

        Assert.IsTrue(player.HasLineOfSightTo(monster), "a monster in the player's own room is in sight");
        Assert.IsTrue(monster.HasLineOfSightTo(player), "and it sees the player it will attack");
    }

    [TestMethod]
    public void WhereTwoRoomsOverlapTheClosedDoorStillStopsTheSightRay()
    {
        using var rig = new Rig();
        // In the overlap in front of the door (x = 19.5, the door's panel at x = 20 across z = 8): the ray decides,
        // and it runs through the closed panel.
        Player player = rig.Fighter(1, new Vector3(19.5f, 0, 8));
        NpcCharacter monster = rig.Monster(1001, new Vector3(30, 0, 8));

        Assert.IsFalse(player.HasLineOfSightTo(monster), "the closed door's panel is on the sight line");
        rig.Open(rig.Door01);
        Assert.IsTrue(player.HasLineOfSightTo(monster));
    }

    [TestMethod]
    public void AMonsterCannotSeeIntoTheNextRoomWhileTheDoorBetweenIsClosed()
    {
        using var rig = new Rig();
        NpcCharacter monster = rig.Monster(1001, new Vector3(10, 0, 0));
        Player player = rig.Fighter(1, new Vector3(30, 0, 0));

        Assert.IsFalse(monster.HasLineOfSightTo(player), "seen through the closed door into the next room");
        Assert.IsFalse(player.HasLineOfSightTo(monster));

        rig.Open(rig.Door01);
        Assert.IsTrue(monster.HasLineOfSightTo(player), "the open door lets them see each other");
        Assert.IsTrue(player.HasLineOfSightTo(monster));

        rig.Close(rig.Door01);
        Assert.IsFalse(monster.HasLineOfSightTo(player), "closed again: out of sight again");
    }

    [TestMethod]
    public void SightAcrossTwoRoomsNeedsEveryDoorOnTheWayOpen()
    {
        using var rig = new Rig();
        NpcCharacter monster = rig.Monster(1001, new Vector3(10, 0, 0));
        Player player = rig.Fighter(1, new Vector3(50, 0, 0));

        rig.Open(rig.Door01);
        Assert.IsFalse(monster.HasLineOfSightTo(player), "the door into the far room is still closed");

        rig.Open(rig.Door12);
        Assert.IsTrue(monster.HasLineOfSightTo(player), "both doors open: the ray decides, and it is clear");

        rig.Close(rig.Door01);
        Assert.IsFalse(monster.HasLineOfSightTo(player));
    }

    [TestMethod]
    public void EitherDoorwayBetweenTwoRoomsOpensTheirSight()
    {
        using var rig = new Rig();
        NpcCharacter monster = rig.Monster(1001, new Vector3(10, 0, 0));
        Player player = rig.Fighter(1, new Vector3(30, 0, 0));

        rig.Open(rig.Door01Twin);
        Assert.IsTrue(monster.HasLineOfSightTo(player), "the second doorway between the rooms is open");
    }

    [TestMethod]
    public void ARoomOnAnotherFloorIsOutOfSight()
    {
        using var rig = new Rig();
        NpcCharacter monster = rig.Monster(1001, new Vector3(10, 0, 0));
        // Straight above, on the floor only a lift reaches: no doorway at all leads there.
        Player player = rig.Fighter(1, new Vector3(10, 64, 0));
        rig.Open(rig.Door01);
        rig.Open(rig.Door12);

        Assert.IsFalse(monster.HasLineOfSightTo(player), "seen through the floor into a room no doorway leads to");
    }

    [TestMethod]
    public void OneRoomIsLeftToTheSightRay()
    {
        using var rig = new Rig();
        NpcCharacter monster = rig.Monster(1001, new Vector3(5, 0, 0));
        Player player = rig.Fighter(1, new Vector3(15, 0, 0));

        Assert.IsTrue(monster.HasLineOfSightTo(player), "closed doors elsewhere do not hide a room from itself");
    }

    /// <summary>
    /// The player's report: a call for help reached a monster behind a closed door. With the door closed the next
    /// room's monster stays out of it; with the door open it answers.
    /// </summary>
    [TestMethod]
    public void ACallForHelpStopsAtAClosedDoor()
    {
        using var rig = new Rig();
        NpcCharacter caller = rig.Monster(1001, new Vector3(17, 0, 0));
        NpcCharacter behindTheDoor = rig.Monster(1002, new Vector3(23, 0, 0));
        Player attacker = rig.Fighter(1, new Vector3(14, 0, 0));
        Player second = rig.Fighter(2, new Vector3(14, 0, 1));

        caller.Brain!.AddThreatFrom(attacker, 50f, fromDamage: true);
        Assert.IsFalse(behindTheDoor.Brain!.Hate.Contains(attacker.Identity), "answered a call from behind a closed door");

        rig.Open(rig.Door01);
        caller.Brain.AddThreatFrom(second, 50f, fromDamage: true);
        Assert.IsTrue(behindTheDoor.Brain.Hate.Contains(second.Identity), "with the door open the call reaches it");
    }

    sealed class Rig : IDisposable
    {
        const int PlayfieldId = 0x0FF00633;

        readonly ServiceProvider _services;
        readonly PlayfieldWorldSimulation _world;
        readonly Playfield _playfield;

        public Rig()
        {
            var logger = new Logger();
            _world = (PlayfieldWorldSimulation)Activator.CreateInstance(typeof(PlayfieldWorldSimulation),
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
                args: new object?[] { 1, null, new PlayfieldGeometryData(), null, new GameDataStore(logger), logger }, culture: null)!;

            // Rooms 20 m long in a row along X, each box 1 m past its tiles as FromPlaced makes them; rooms 3 and 4 a
            // floor up (base 64), joined by their own doorway.
            var rooms = new List<DungeonRoomBounds>
            {
                new(0, new NVector3(-1, -1, -11), new NVector3(21, 64, 11)),
                new(1, new NVector3(19, -1, -11), new NVector3(41, 64, 11)),
                new(2, new NVector3(39, -1, -11), new NVector3(61, 64, 11)),
                new(3, new NVector3(-1, 63, -11), new NVector3(21, 128, 11)),
                new(4, new NVector3(19, 63, -11), new NVector3(41, 128, 11))
            };
            // Each door stands at the edge of its doorway's wall line, aside of the sight lines along z = 0.
            var doors = new List<DungeonDoorPlacement>
            {
                new(new NVector3(20, 0, 8), 90, 0, 1),
                new(new NVector3(20, 0, -8), 90, 0, 1),
                new(new NVector3(40, 0, 8), 90, 1, 2),
                new(new NVector3(20, 64, 8), 90, 3, 4)
            };
            var layout = new DungeonWorldLayout(new PlayfieldCollisionSet(PlayfieldId, Array.Empty<CollisionTriangleMesh>(), null),
                rooms, doors);
            var locality = new PlayfieldLocality(PlayfieldId, null);
            locality.ApplyDungeonRooms(layout);

            _services = new ServiceCollection()
                .AddSingleton(new DynelRegistry())
                .AddSingleton(locality)
                .AddSingleton(new WorldSimulationAccess { Instance = _world })
                .AddSingleton(new GameplaySettings { SocialAggro = new GameplaySocialAggro { DefaultRadius = 20, MaxRadius = 30 } })
                .BuildServiceProvider();
            _playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
            Set("<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = PlayfieldId });
            Set("_serviceProvider", _services);
            Set("_pendingStatRebases", new ConcurrentDictionary<Character, byte>());
            Set("_lifecycleSync", new System.Threading.Lock());
            Set("<FightModes>k__BackingField", DistrictFightModes.None);

            Door01 = SpawnDoor(0, doors[0]);
            Door01Twin = SpawnDoor(1, doors[1]);
            Door12 = SpawnDoor(2, doors[2]);
            SpawnDoor(3, doors[3]);
        }

        public Door Door01 { get; }

        public Door Door01Twin { get; }

        public Door Door12 { get; }

        void Set(string field, object value)
            => typeof(Playfield).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_playfield, value);

        /// <summary>As AcgDungeonPlayfield.SpawnDoors: closed, in its owning room's cell, blocking sight.</summary>
        Door SpawnDoor(int index, DungeonDoorPlacement placement)
        {
            var door = new Door(new Identity { Type = IdentityType.Door, Instance = 0x10000002 + index }, 41557, 0,
                (placement.Room << 16) | (placement.LinkedRoom & 0xFFFF))
            {
                Playfield = _playfield,
                Position = new Vector3(placement.Position.X, placement.Position.Y, placement.Position.Z),
                Rotation = Door.Heading(placement.YawDegrees)
            };
            _services.GetRequiredService<DynelRegistry>().Register(door);
            _services.GetRequiredService<PlayfieldLocality>().RegisterDynel(door, placement.Room);
            _world.Doors.Add(door, 1.5f, 1.4f);
            return door;
        }

        public void Open(Door door) => door.ApplyFlags(door.Flags | Door.OpenFlag);

        public void Close(Door door) => door.ApplyFlags(door.Flags & ~Door.OpenFlag);

        /// <param name="cell">The room the character's cell says, when it is not the room its position resolves to.</param>
        public NpcCharacter Monster(int id, Vector3 at, int? cell = null)
        {
            var npc = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new Items())
            {
                Playfield = _playfield,
                Attackable = true,
                Position = at
            };
            npc.Stats.Set(CharacterStat.Level, 60);
            npc.Stats.Set(CharacterStat.MaxHealth, 1000);
            npc.Stats.Set(CharacterStat.Health, 1000);
            npc.Stats.Set(CharacterStat.BreedHostility, 100);
            Place(npc, cell);
            NpcBrain.Create(npc, at);
            return npc;
        }

        public Player Fighter(int id, Vector3 at, int? cell = null)
        {
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new Logger(), new Items())
            {
                Playfield = _playfield,
                Position = at
            };
            player.Stats.Set(CharacterStat.Level, 60);
            player.Stats.Set(CharacterStat.MaxHealth, 100);
            player.Stats.Set(CharacterStat.Health, 100);
            Place(player, cell);
            return player;
        }

        void Place(Character character, int? cell)
        {
            _services.GetRequiredService<DynelRegistry>().Register(character);
            PlayfieldLocality locality = _services.GetRequiredService<PlayfieldLocality>();
            if (cell is { } room)
                locality.RegisterDynel(character, room);
            else
                locality.RegisterDynel(character);
        }

        public void Dispose()
        {
            _world.Dispose();
            _services.Dispose();
        }
    }

}

/// <summary>
/// Against the runtime GameData (AO_REBIRTH_GAMEDATA_PATH): in generated Rubi-Ka missions, two monsters spawned in one
/// room are never taken for standing in separate rooms, whichever room's box each spawn point fell into.
/// </summary>
[TestClass]
public sealed class RkMissionRoomSightTests
{
    const int PlayfieldId = 0x0FF00633;

    [TestMethod]
    public void MonstersSpawnedInOneRoomAreNeverInSeparateRooms()
    {
        string? root = Environment.GetEnvironmentVariable(GameDataPaths.EnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(root) || !System.IO.File.Exists(System.IO.Path.Combine(root, "Missions", "Missions.json")))
            Assert.Inconclusive("Set AO_REBIRTH_GAMEDATA_PATH to the private GameData (Missions.json, Dungeons).");

        var logger = new Logger();
        var data = new GameDataStore(logger, null, root!);
        var generator = new Core.Quests.Dungeons.DungeonLayoutGenerator(data, logger);
        Assert.IsTrue(generator.IsAvailable, "The dungeon generator is unavailable.");
        var playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
        typeof(Playfield).GetField("<Identity>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(playfield, new Identity { Type = IdentityType.Playfield2, Instance = PlayfieldId });

        int layouts = 0, pairs = 0, overlapping = 0;
        foreach (string mission in new[] { "rkSolo", "rkTeam" })
        {
            Core.Quests.Dungeons.MissionDefinition definition = generator.Mission(mission)
                ?? throw new AssertFailedException("Missions.json has no " + mission + " mission.");
            Core.Quests.Dungeons.ResolvedDungeon resolved = generator.Resolve(definition.Dungeon, 100, out string? problem)
                ?? throw new AssertFailedException(mission + " does not resolve at QL 100: " + problem);
            for (int seed = 1; seed <= 6; seed++)
            {
                var parameters = new Core.Quests.Dungeons.QuestDungeonParameters { Seed = seed, Quality = 100, EntranceInstance = unchecked((int)0xC00010F4) };
                Assert.IsTrue(generator.ConfigureNewDungeon(parameters, resolved, tokenReward: false, playerSide: 0, out problem), problem);
                Assert.IsTrue(generator.TryGenerate(parameters, out Core.Quests.Dungeons.DungeonLayout layout));
                DungeonWorldLayout dungeon = DungeonCollisionBuilder.BuildLayout(data.RootPath, layout.Generator);
                PlayfieldDistrictsData? districts = AcgDungeonPlayfield.LoadStyleDistricts(data.RootPath, layout.Generator.TemplatePlayfield, logger);
                Assert.IsNotNull(districts?.Districts, "style " + layout.Generator.TemplatePlayfield + " has no districts");
                var byIndex = new Dictionary<int, PlayfieldDistrictEntry>();
                foreach (PlayfieldDistrictEntry district in districts.Districts)
                    if (district != null)
                        byIndex[district.DistrictIndex] = district;

                var locality = new PlayfieldLocality(PlayfieldId, null);
                locality.ApplyDungeonRooms(dungeon);
                layouts++;
                int id = 1;
                foreach (DungeonRoomPlacement placement in dungeon.Placements)
                {
                    int zone = placement.Template.InstanceId;
                    int index = districts.ZoneToDistrictMap is { } map && zone >= 0 && zone < map.Length ? map[zone] : zone;
                    if (!byIndex.TryGetValue(index, out PlayfieldDistrictEntry? district))
                        continue;

                    // Every spawn point of the room, as a monster spawned there: its cell is what its position resolves to.
                    var monsters = new List<NpcCharacter>();
                    foreach (PlayfieldDistrictSpawnPoint point in district.SpawnPoints ?? [])
                    {
                        if (point?.Position is not { Length: >= 3 } p)
                            continue;
                        NVector3 at = placement.TransformTemplatePoint(new NVector3(p[0], p[1], p[2]));
                        var monster = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = id++ }, new Items())
                        {
                            Playfield = playfield,
                            Position = new Vector3(at.X, at.Y, at.Z)
                        };
                        locality.RegisterDynel(monster);
                        monsters.Add(monster);
                        if (monster.Cell!.Id != placement.Index)
                            overlapping++;
                    }

                    for (int i = 0; i < monsters.Count; i++)
                    {
                        for (int j = i + 1; j < monsters.Count; j++)
                        {
                            pairs++;
                            Assert.IsFalse(locality.TryGetSeparateRooms(monsters[i], monsters[j], out int roomA, out int roomB),
                                mission + " seed " + seed + " room " + placement.Index + ": monsters at " + monsters[i].Position
                                + " and " + monsters[j].Position + " taken for rooms " + roomA + " and " + roomB);
                        }
                    }
                }
            }
        }

        Assert.IsTrue(layouts > 0 && pairs > 0, "no rooms with spawn points were generated");
        Assert.IsTrue(overlapping > 0, "no spawn point fell into a neighbouring room's box: the layouts no longer exercise the overlap");
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

sealed class Items : IItemBuilder
{
    public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
        Identity? identity = null, byte[]? statsBlob = null) => new()
        {
            LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount,
            InstanceId = instanceId, Identity = identity ?? Identity.None, Definition = CreateTemplate(lowId, highId, quality)
        };

    public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
        => Create(lowId, highId, quality, source, stackCount);

    public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };

    public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
}
