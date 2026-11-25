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
/// room 0, rooms 3 and 4 that only a lift reaches.
/// </summary>
[TestClass]
public sealed class DungeonRoomSightTests
{
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

            // Rooms 20 m long in a row along X; rooms 3 and 4 a floor up (base 64), joined by their own doorway.
            var rooms = new List<DungeonRoomBounds>
            {
                new(0, new NVector3(0, 0, -10), new NVector3(20, 64, 10)),
                new(1, new NVector3(20, 0, -10), new NVector3(40, 64, 10)),
                new(2, new NVector3(40, 0, -10), new NVector3(60, 64, 10)),
                new(3, new NVector3(0, 64, -10), new NVector3(20, 128, 10)),
                new(4, new NVector3(20, 64, -10), new NVector3(40, 128, 10))
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

        public NpcCharacter Monster(int id, Vector3 at)
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
            Place(npc);
            NpcBrain.Create(npc, at);
            return npc;
        }

        public Player Fighter(int id, Vector3 at)
        {
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new Logger(), new Items())
            {
                Playfield = _playfield,
                Position = at
            };
            player.Stats.Set(CharacterStat.Level, 60);
            player.Stats.Set(CharacterStat.MaxHealth, 100);
            player.Stats.Set(CharacterStat.Health, 100);
            Place(player);
            return player;
        }

        void Place(Character character)
        {
            _services.GetRequiredService<DynelRegistry>().Register(character);
            _services.GetRequiredService<PlayfieldLocality>().RegisterDynel(character);
        }

        public void Dispose()
        {
            _world.Dispose();
            _services.Dispose();
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
}
