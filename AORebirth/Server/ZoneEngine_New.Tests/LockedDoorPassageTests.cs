namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

using AORebirth.Core.GameData;
using AORebirth.DungeonGenerator;
using AORebirth.Enums;
using AORebirth.World.Collision;
using AORebirth.World.Pathfinding;

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
using ZoneEngine_New.Core.Movement;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.WorldSimulation;

using NVector3 = System.Numerics.Vector3;

/// <summary>
/// Bug #632: in a Rubi-Ka mission a mob aggroed through a locked door and a guarding pet walked through it to kill the
/// mob. Locked doors block sight but the navmesh runs through every doorway, so NPC and pet routes and reach checks
/// went through a locked door, and a sight line cached from the near side of a door was handed to a point just past
/// its panel. Each test builds a generated mission dungeon of every door style from the runtime GameData
/// (AO_REBIRTH_GAMEDATA_PATH): its collision, its baked navmesh and its doors, as QuestDungeonPlayfield builds them,
/// and asks the same pathfinder and sight questions the NPC brain and the pet tree ask.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LockedDoorPassageTests
{
    const int PlayfieldId = 0x0FF00632;

    /// <summary>How far either side of a door the two rooms' test points stand.</summary>
    const float Step = 2.5f;

    public static IEnumerable<object[]> Styles() => [[321], [324], [341], [351], [386]];

    [TestMethod]
    [DynamicData(nameof(Styles), DynamicDataSourceType.Method)]
    public void NoRouteOrReachThroughALockedDoorAndTheDoorwayOpensAgainWhenUnlocked(int style)
    {
        using Rig rig = Rig.Build(style);
        int proven = 0;
        foreach (DoorPair pair in rig.Pairs())
        {
            if (!rig.TryRoomPoints(pair.First, out NVector3 a, out NVector3 b) || !rig.Route(a, b, out _))
                continue;

            pair.Lock();
            Assert.IsTrue(rig.World.Doors.AnyShut);
            // The defect: the navmesh alone walks straight through the locked doorway.
            rig.Finder.PassageGate = null;
            bool ungated = rig.Route(a, b, out List<NVector3> through) && rig.World.Doors.BlocksRoute(a, through);
            rig.Finder.PassageGate = rig.World.Doors;

            bool found = rig.Route(a, b, out List<NVector3> around);
            // A layout with a loop may route around through another doorway; never through this one.
            if (found)
            {
                Assert.IsFalse(rig.World.Doors.BlocksRoute(a, around), "style " + style + ": a route crossed a locked door");
                pair.Unlock();
                continue;
            }

            Assert.IsTrue(ungated, "style " + style + ": without the gate the route goes through the locked door");
            Assert.AreEqual(PathSearchOutcome.ShutPassage, rig.Finder.LastOutcome, "style " + style);
            Assert.IsFalse(rig.Route(b, a, out _), "style " + style + ": the other side must not walk through either");
            Assert.IsFalse(rig.Finder.TryCanReach(a, b), "style " + style);
            // The chase check (NpcBrain.CanPathTo) floods from the target; a pet or mob on the far side is not reachable.
            ReachMap map = rig.Finder.BuildReachMap(b, 40f, 4096);
            Assert.AreEqual(ReachMapKind.Flooded, map.Kind, "style " + style);
            Assert.AreEqual(ReachAnswer.Unreachable, rig.Finder.Reaches(map, a), "style " + style + ": reached through a locked door");

            pair.Unlock();
            Assert.IsFalse(rig.World.Doors.AnyShut);
            Assert.IsTrue(rig.Route(a, b, out _), "style " + style + ": an unlocked door is walked through again");
            Assert.AreEqual(ReachAnswer.Reachable, rig.Finder.Reaches(rig.Finder.BuildReachMap(b, 40f, 4096), a), "style " + style);
            proven++;
        }

        if (proven == 0)
            Assert.Inconclusive("style " + style + ": no doorway in this layout is the only way between its rooms");
    }

    [TestMethod]
    [DynamicData(nameof(Styles), DynamicDataSourceType.Method)]
    public void AClosedUnlockedDoorStillLetsNpcsThrough(int style)
    {
        using Rig rig = Rig.Build(style);
        int checkedDoors = 0;
        foreach (DoorPair pair in rig.Pairs())
        {
            if (!rig.TryRoomPoints(pair.First, out NVector3 a, out NVector3 b) || !rig.Route(a, b, out List<NVector3> route))
                continue;
            Assert.IsFalse(pair.First.IsOpen);
            Assert.IsFalse(rig.World.Doors.BlocksRoute(a, route), "an unlocked door opens to a touch and must not block");
            checkedDoors++;
        }

        Assert.IsTrue(checkedDoors > 0, "style " + style + ": no doorway was routable");
    }

    [TestMethod]
    [DynamicData(nameof(Styles), DynamicDataSourceType.Method)]
    public void NoSightThroughALockedDoorEvenFromAWarmSightCache(int style)
    {
        using Rig rig = Rig.Build(style);
        int proven = 0;
        foreach (DoorPair pair in rig.Pairs())
        {
            pair.Lock();
            Door door = pair.First;
            NVector3 facing = Facing(door);
            var panel = new NVector3(door.Position.xf, door.Position.yf, door.Position.zf);
            const float eye = MovementConfig.LineOfSightEyeHeight;
            // The mob stands in the room in front of the door; the player presses against the door from the other room,
            // a hair past its panel, in the same quarter-metre sight-cache cell as a point on the mob's side.
            NVector3 mob = panel + (facing * 3f) + new NVector3(0, eye, 0);
            if (!TryStraddle(panel, facing, out NVector3 near, out NVector3 far))
                continue;
            near.Y += eye;
            far.Y += eye;

            if (!rig.World.HasLineOfSight(mob.X, mob.Y, mob.Z, near.X, near.Y, near.Z))
                continue;
            Assert.IsFalse(rig.World.HasLineOfSight(mob.X, mob.Y, mob.Z, far.X, far.Y, far.Z),
                "style " + style + ": the mob saw through the locked door from a cached sight line");
            Assert.IsFalse(rig.World.HasLineOfSight(far.X, far.Y, far.Z, mob.X, mob.Y, mob.Z), "style " + style);

            NVector3 beyond = panel - (facing * 3f) + new NVector3(0, eye, 0);
            Assert.IsFalse(rig.World.HasLineOfSight(mob.X, mob.Y, mob.Z, beyond.X, beyond.Y, beyond.Z), "style " + style);
            proven++;
        }

        Assert.IsTrue(proven > 0, "style " + style + ": no door had a clear sight line on its own side");
    }

    /// <summary>
    /// A closed door hides everything its open doorway shows, at every height. The Alien Mothership door (262487) has
    /// a collision sphere 2 m tall in a doorway nearly 6 m tall, so a sight line crossing it above 2 m went over the
    /// closed door.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(Styles), DynamicDataSourceType.Method)]
    public void AClosedDoorHidesItsWholeDoorway(int style)
    {
        using Rig rig = Rig.Build(style);
        int proven = 0;
        foreach (DoorPair pair in rig.Pairs())
        {
            Door door = pair.First;
            NVector3 facing = Facing(door);
            var across = new NVector3(facing.Z, 0f, -facing.X);
            var panel = new NVector3(door.Position.xf, door.Position.yf, door.Position.zf);
            bool Clear(NVector3 a, NVector3 b) => rig.World.HasLineOfSight(a.X, a.Y, a.Z, b.X, b.Y, b.Z);
            for (float height = 0.25f; height <= 8f; height += 0.25f)
            {
                NVector3 front = panel + (facing * 2f) + new NVector3(0, height, 0);
                NVector3 back = panel - (facing * 2f) + new NVector3(0, height, 0);
                // Inside the doorway: the wall beside it stands at this height. Above the wall's top (a room open to
                // the one next door over it) there is no doorway left for the door to close.
                NVector3 aside = across * 2.5f;
                pair.Open();
                bool doorway = Clear(front, back) && !Clear(front + aside, back + aside) && !Clear(front - aside, back - aside);
                pair.Unlock();
                if (!doorway)
                    continue;
                Assert.IsFalse(rig.World.HasLineOfSight(front.X, front.Y, front.Z, back.X, back.Y, back.Z),
                    "style " + style + ": seen over the closed door " + height + " m above its base");
                proven++;
            }
        }

        Assert.IsTrue(proven > 0, "style " + style + ": no doorway was open to sight");
    }

    /// <summary>
    /// The player's report from an Alien Mothership: a monster behind a closed door answered a call for help from the
    /// room in front of it. The two stand either side of the door, the helper on a raised spot so their sight line
    /// crosses the doorway above 2 m; with the door open it answers (proving the call reaches it), closed it does not.
    /// </summary>
    [TestMethod]
    public void NoCallForHelpReachesAMonsterBehindAClosedMothershipDoor()
    {
        using Rig rig = Rig.Build(386);
        using ServiceProvider services = rig.BrainServices(new GameplaySettings
        {
            SocialAggro = new GameplaySocialAggro { DefaultRadius = 20, MaxRadius = 30 }
        });
        Playfield playfield = Rig.BrainPlayfield(services);
        int proven = 0;
        foreach (DoorPair pair in rig.Pairs())
        {
            if (!rig.TryRoomPoints(pair.First, out NVector3 front, out NVector3 back))
                continue;

            int id = 0x6320 + (proven * 8);
            NpcCharacter caller = Rig.Monster(services, playfield, id + 1, front);
            NpcCharacter helper = Rig.Monster(services, playfield, id + 2, back + new NVector3(0, 2.5f, 0));
            Player attacker = Rig.Fighter(services, playfield, id + 3, front);
            Player second = Rig.Fighter(services, playfield, id + 4, front);

            pair.Open();
            bool doorway = helper.HasLineOfSightTo(caller);
            pair.Unlock();
            if (!doorway)
                continue;

            Assert.IsFalse(helper.HasLineOfSightTo(caller), "the closed door hides the caller");
            caller.Brain!.AddThreatFrom(attacker, 50f, fromDamage: true);
            Assert.IsFalse(helper.Brain!.Hate.Contains(attacker.Identity), "a monster behind a closed door answered a call for help");

            pair.Open();
            caller.Brain.AddThreatFrom(second, 50f, fromDamage: true);
            Assert.IsTrue(helper.Brain.Hate.Contains(second.Identity), "with the door open the call reaches it");
            pair.Unlock();
            proven++;
        }

        Assert.IsTrue(proven > 0, "no mothership doorway had a sight line through it");
    }

    /// <summary>
    /// The room rule (characters in rooms no chain of open doors joins cannot see each other) never hides a sight line
    /// the dungeon's own geometry leaves clear: with every door open, two characters anywhere in different rooms see
    /// each other exactly when the ray does. With every door closed, neighbours either side of a doorway do not.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(Styles), DynamicDataSourceType.Method)]
    public void RoomsHideOnlyWhatClosedDoorsHide(int style)
    {
        using Rig rig = Rig.Build(style);
        using ServiceProvider services = rig.BrainServices(new GameplaySettings());
        Playfield playfield = Rig.BrainPlayfield(services);
        var random = new Random(style);
        var standing = new List<Player>();
        foreach (DungeonRoomBounds room in rig.Layout.Rooms)
        {
            for (int k = 0; k < 8; k++)
            {
                var wanted = new NVector3(room.Min.X + ((float)random.NextDouble() * (room.Max.X - room.Min.X)), room.Min.Y + 1f,
                    room.Min.Z + ((float)random.NextDouble() * (room.Max.Z - room.Min.Z)));
                if (rig.Finder.TrySnap(wanted, out NVector3 at) && MathF.Abs(at.X - wanted.X) < 0.5f && MathF.Abs(at.Z - wanted.Z) < 0.5f)
                    standing.Add(Rig.Fighter(services, playfield, 0x7000 + standing.Count, at));
            }
        }

        foreach (DoorPair pair in rig.Pairs())
            pair.Open();
        const float eye = MovementConfig.LineOfSightEyeHeight;
        int acrossRooms = 0;
        for (int i = 0; i < standing.Count; i++)
        {
            for (int j = i + 1; j < standing.Count; j++)
            {
                Player a = standing[i], b = standing[j];
                if (a.Cell?.Id == b.Cell?.Id || AORebirth.Core.Vector.Vector3.Abs(a.Position - b.Position) > 60)
                    continue;
                bool ray = rig.World.HasLineOfSight(a.Position.xf, a.Position.yf + eye, a.Position.zf,
                    b.Position.xf, b.Position.yf + eye, b.Position.zf);
                Assert.AreEqual(ray, a.HasLineOfSightTo(b), $"style {style}: rooms {a.Cell?.Id} and {b.Cell?.Id}, doors open");
                if (ray)
                    acrossRooms++;
            }
        }

        Assert.IsTrue(acrossRooms > 0, "style " + style + ": no sight line ran between two rooms");

        int closed = 0;
        foreach (DoorPair pair in rig.Pairs())
        {
            foreach (DoorPair other in rig.Pairs())
                other.Unlock();
            if (!rig.TryRoomPoints(pair.First, out NVector3 front, out NVector3 back))
                continue;
            Player a = Rig.Fighter(services, playfield, 0x7F00 + closed * 2, front);
            Player b = Rig.Fighter(services, playfield, 0x7F01 + closed * 2, back);
            if (a.Cell?.Id == b.Cell?.Id)
                continue;
            Assert.IsFalse(a.HasLineOfSightTo(b), $"style {style}: seen across the closed doorway between rooms {a.Cell?.Id} and {b.Cell?.Id}");
            closed++;
        }

        Assert.IsTrue(closed > 0, "style " + style + ": no doorway had a room on each side");
    }

    /// <summary>
    /// A player holding forward into a locked door stays in front of it, as the client holds him. The server's own step
    /// only met the floor, so between client updates it ran the player on into the locked room: the mobs there aggroed
    /// and the pet, with no route to that position, warped in after him. The same run through the door once it is
    /// unlocked proves the step itself goes through the doorway.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(Styles), DynamicDataSourceType.Method)]
    public void APlayerRunningIntoALockedDoorStopsAtItAndRunsThroughOnceUnlocked(int style)
    {
        using Rig rig = Rig.Build(style);
        Playfield playfield = rig.CreatePlayfield();
        int proven = 0;
        foreach (DoorPair pair in rig.Pairs())
        {
            if (!rig.TryRoomPoints(pair.First, out NVector3 a, out NVector3 b) || !rig.Route(a, b, out _))
                continue;

            NVector3 facing = Facing(pair.First);
            var panel = new NVector3(pair.First.Position.xf, pair.First.Position.yf, pair.First.Position.zf);
            float Side(Player p) => NVector3.Dot(new NVector3(p.Position.xf, p.Position.yf, p.Position.zf) - panel, facing);

            // Holding forward from one room at the door into the other, for three seconds (15 m at run speed).
            Player RunThrough(out float furthest)
            {
                var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = 0x632 }, new Logger(), new Items())
                {
                    Playfield = playfield
                };
                float yaw = MathF.Atan2(b.X - a.X, b.Z - a.Z);
                player.Motor.Warp(new AORebirth.Core.Vector.Vector3(a.X, a.Y, a.Z),
                    new AORebirth.Core.Vector.Quaternion(0, MathF.Sin(yaw / 2f), 0, MathF.Cos(yaw / 2f)));
                for (int i = 0; i < 20; i++)
                    player.Motor.Tick(0.05);
                Assert.IsTrue(player.Motor.ApplyAction(ZoneEngine_New.Core.Movement.MovementAction.ForwardStart), "forward start");
                furthest = float.MinValue;
                for (int i = 0; i < 60; i++)
                {
                    player.Motor.Tick(0.05);
                    furthest = MathF.Max(furthest, Side(player));
                }

                return player;
            }

            // A closed, unlocked door opens to the touch: the run goes through. A doorway the straight run cannot make
            // (a frame or step in the way) proves nothing about the lock.
            if (Side(RunThrough(out _)) <= 0.5f)
                continue;

            pair.Lock();
            Player blocked = RunThrough(out float reached);
            Assert.IsTrue(reached < 0f, "style " + style + ": the server ran the player " + reached + " m past a locked door");
            Assert.IsTrue(Side(blocked) > -1.5f, "style " + style + ": the player never reached the door (" + Side(blocked) + ")");
            pair.Unlock();
            proven++;
        }

        if (proven == 0)
            Assert.Inconclusive("style " + style + ": no doorway had a straight run through it");
    }

    /// <summary>
    /// Two points on one line through the door, in the same sight-cache cell (PlayfieldWorldSimulation quantises every
    /// coordinate to a quarter metre): <paramref name="near"/> on the facing side of the panel, <paramref name="far"/>
    /// just past it.
    /// </summary>
    static bool TryStraddle(NVector3 panel, NVector3 facing, out NVector3 near, out NVector3 far)
    {
        foreach (float nearOffset in new[] { 0.2f, 0.1f, 0.05f, 0.02f, 0.01f, 0.005f })
        {
            foreach (float farOffset in new[] { 0.0004f, 0.002f, 0.01f, 0.05f, 0.1f, 0.2f })
            {
                near = panel + (facing * nearOffset);
                far = panel - (facing * farOffset);
                if ((int)(near.X * 4f) == (int)(far.X * 4f) && (int)(near.Z * 4f) == (int)(far.Z * 4f))
                    return true;
            }
        }

        near = far = default;
        return false;
    }

    /// <summary>The door's facing: +Z turned by its yaw, as DoorOcclusion reads it.</summary>
    static NVector3 Facing(Door door)
    {
        float yaw = 2f * MathF.Atan2(door.Rotation.yf, door.Rotation.wf);
        return new NVector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
    }

    /// <summary>Every doorway between the same two rooms: one lock to the client (QuestDungeonPlayfield.SpawnDoors).</summary>
    sealed class DoorPair(List<Door> doors)
    {
        public Door First => doors[0];

        public void Lock()
        {
            foreach (Door door in doors)
                door.ApplyFlags((door.Flags & ~Door.OpenFlag) | Door.LockedFlag);
        }

        public void Unlock()
        {
            foreach (Door door in doors)
                door.ApplyFlags(door.Flags & ~(Door.OpenFlag | Door.LockedFlag));
        }

        public void Open()
        {
            foreach (Door door in doors)
                door.ApplyFlags((door.Flags & ~Door.LockedFlag) | Door.OpenFlag);
        }
    }

    sealed class Rig : IDisposable
    {
        readonly List<DoorPair> _pairs = [];
        readonly List<NVector3> _route = new(32);

        Rig(PlayfieldWorldSimulation world, NavMeshPathfinder finder)
        {
            World = world;
            Finder = finder;
            finder.PassageGate = world.Doors;
        }

        public PlayfieldWorldSimulation World { get; }

        public NavMeshPathfinder Finder { get; }

        public IEnumerable<DoorPair> Pairs() => _pairs;

        /// <summary>A playfield whose world is this dungeon's: all a character's motor asks of its playfield.</summary>
        public Playfield CreatePlayfield()
        {
            var playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
            var services = new ServiceCollection().AddSingleton(new WorldSimulationAccess { Instance = World }).BuildServiceProvider();
            typeof(Playfield).GetField("_serviceProvider", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(playfield, services);
            return playfield;
        }

        /// <summary>What NPC brains ask of their playfield on this dungeon's world: registry, locality and gameplay rules.</summary>
        public ServiceProvider BrainServices(GameplaySettings gameplay)
        {
            // The dungeon's rooms over the locality cells, as AcgDungeonPlayfield.BuildWorld lays them.
            var locality = new PlayfieldLocality(PlayfieldId, null);
            locality.ApplyDungeonRooms(Layout);
            return new ServiceCollection()
                .AddSingleton(new DynelRegistry())
                .AddSingleton(locality)
                .AddSingleton(new WorldSimulationAccess { Instance = World })
                .AddSingleton(gameplay)
                .BuildServiceProvider();
        }

        public DungeonWorldLayout Layout { get; private init; } = null!;

        /// <summary>A playfield built without its constructor, enough for brains, combat and line of sight.</summary>
        public static Playfield BrainPlayfield(IServiceProvider services)
        {
            var playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
            void Set(string field, object value)
                => typeof(Playfield).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(playfield, value);
            Set("<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = PlayfieldId });
            Set("_serviceProvider", services);
            Set("_pendingStatRebases", new System.Collections.Concurrent.ConcurrentDictionary<Character, byte>());
            Set("_lifecycleSync", new System.Threading.Lock());
            Set("<FightModes>k__BackingField", DistrictFightModes.None);
            return playfield;
        }

        /// <summary>An aggressive monster with a brain, standing at <paramref name="at"/>.</summary>
        public static NpcCharacter Monster(IServiceProvider services, Playfield playfield, int id, NVector3 at)
        {
            var npc = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new Items())
            {
                Playfield = playfield,
                Attackable = true,
                Position = new AORebirth.Core.Vector.Vector3(at.X, at.Y, at.Z)
            };
            npc.Stats.Set(CharacterStat.Level, 60);
            npc.Stats.Set(CharacterStat.MaxHealth, 1000);
            npc.Stats.Set(CharacterStat.Health, 1000);
            npc.Stats.Set(CharacterStat.BreedHostility, 100);
            Place(services, npc);
            NpcBrain.Create(npc, npc.Position);
            return npc;
        }

        public static Player Fighter(IServiceProvider services, Playfield playfield, int id, NVector3 at)
        {
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new Logger(), new Items())
            {
                Playfield = playfield,
                Position = new AORebirth.Core.Vector.Vector3(at.X, at.Y, at.Z)
            };
            player.Stats.Set(CharacterStat.Level, 60);
            player.Stats.Set(CharacterStat.MaxHealth, 100);
            player.Stats.Set(CharacterStat.Health, 100);
            Place(services, player);
            return player;
        }

        static void Place(IServiceProvider services, Character character)
        {
            services.GetRequiredService<DynelRegistry>().Register(character);
            services.GetRequiredService<PlayfieldLocality>().RegisterDynel(character);
        }

        public static Rig Build(int style)
        {
            var log = new Logger();
            var data = new GameDataStore(log);
            string root = data.RootPath;
            string doorStyles = Path.Combine(root, "DungeonDoorStyles.json");
            string policyPath = Path.Combine(root, "DungeonGeneration.json");
            if (!File.Exists(doorStyles) || !File.Exists(policyPath))
                Assert.Inconclusive("The runtime GameData at " + root + " cannot generate mission dungeons.");

            int doorTemplate = 0;
            using (JsonDocument styles = JsonDocument.Parse(File.ReadAllText(doorStyles)))
            {
                foreach (JsonElement row in styles.RootElement.GetProperty("Styles").EnumerateArray())
                {
                    if (row.GetProperty("Style").GetInt32() == style)
                        doorTemplate = row.GetProperty("DoorTemplate").GetInt32();
                }
            }

            Assert.AreNotEqual(0, doorTemplate, "style " + style + " has no door template");
            DungeonGenerationResult result = Generate(root, policyPath, style);
            DungeonWorldLayout layout = DungeonCollisionBuilder.BuildLayout(root, result.Generator);
            Assert.IsTrue(layout.Collision.HasCollision, "style " + style + " built no collision");
            var world = PlayfieldWorldSimulation.Create(PlayfieldId, new PlayfieldGeometryData { Collision = layout.Collision },
                null, DestinationsCatalog.Instance, data, log);

            Assert.IsTrue(NavMeshPathfinder.TryLoadSettings(root, out NavMeshBuildSettings? settings, out string? failure) && settings != null,
                failure);
            byte[] mesh = NavMeshFile.ToBytes(NavMeshBaker.Bake(DungeonCollisionBuilder.Build(root, result.Generator), settings));
            var rig = new Rig(world, NavMeshPathfinder.FromBytes(PlayfieldId, mesh, settings!)) { Layout = layout };

            // The doors QuestDungeonPlayfield.SpawnDoors spawns, closed and unlocked to start.
            var byRooms = new Dictionary<int, List<Door>>();
            for (int i = 0; i < layout.Doors.Count; i++)
            {
                DungeonDoorPlacement placement = layout.Doors[i];
                if (placement.IsExit)
                    continue;
                var door = new Door(new Identity { Type = IdentityType.Door, Instance = 0x10000001 + i }, doorTemplate, 0,
                    (placement.Room << 16) | (placement.LinkedRoom & 0xFFFF))
                {
                    Position = new AORebirth.Core.Vector.Vector3(placement.Position.X, placement.Position.Y, placement.Position.Z),
                    Rotation = Door.Heading(placement.YawDegrees)
                };
                Assert.IsTrue(world.RegisterDoorBlocker(door), "door template " + doorTemplate + " has no collision sphere");
                int key = (Math.Min(placement.Room, placement.LinkedRoom) << 16) | Math.Max(placement.Room, placement.LinkedRoom);
                if (!byRooms.TryGetValue(key, out List<Door>? doors))
                    byRooms[key] = doors = [];
                doors.Add(door);
            }

            foreach (List<Door> doors in byRooms.Values)
                rig._pairs.Add(new DoorPair(doors));
            Assert.IsTrue(rig._pairs.Count > 0, "style " + style + " has no interior doors");
            return rig;
        }

        static DungeonGenerationResult Generate(string root, string policyPath, int style)
        {
            using JsonDocument policy = JsonDocument.Parse(File.ReadAllText(policyPath));
            var generator = new DungeonGenerator(root);
            foreach (JsonElement profile in policy.RootElement.GetProperty("Profiles").EnumerateArray())
            {
                if (!profile.GetProperty("Styles").EnumerateArray().Any(s => s.GetInt32() == style))
                    continue;
                int height = profile.TryGetProperty("FloorHeight", out JsonElement own)
                    ? own.GetInt32() : policy.RootElement.GetProperty("FloorHeight").GetInt32();
                return generator.Generate(new DungeonGenerationRequest
                {
                    StyleId = style, Seed = 632, FloorCount = 1, FloorSize = profile.GetProperty("MaxFloorSize").GetInt32(),
                    FloorHeight = height, BossFloor = false, LockedDoorChancePercent = 0,
                    DescendingFloors = generator.LoadStyle(style).Transitions?.DescendingFloors ?? false,
                    UseFloorTransitions = true, BuildingInstance = unchecked((int)0xC00001F9)
                });
            }

            Assert.Inconclusive("No generation profile uses style " + style);
            throw new InvalidOperationException();
        }

        /// <summary>Navmesh points <see cref="Step"/> either side of the door, one in each room.</summary>
        public bool TryRoomPoints(Door door, out NVector3 a, out NVector3 b)
        {
            NVector3 facing = Facing(door);
            var panel = new NVector3(door.Position.xf, door.Position.yf, door.Position.zf);
            a = b = default;
            return Snap(panel - (facing * Step), out a) && Snap(panel + (facing * Step), out b);
        }

        bool Snap(NVector3 wanted, out NVector3 onMesh)
            => Finder.TrySnap(wanted, out onMesh)
               && MathF.Abs(onMesh.X - wanted.X) < 0.75f && MathF.Abs(onMesh.Z - wanted.Z) < 0.75f
               && MathF.Abs(onMesh.Y - wanted.Y) < 1.5f;

        /// <summary>The NPC brain's walk: a complete route that ends at <paramref name="to"/>.</summary>
        public bool Route(NVector3 from, NVector3 to, out List<NVector3> route)
        {
            route = _route;
            return Finder.TryFindPath(from, to, _route) && _route.Count > 0
                && NVector3.Distance(_route[_route.Count - 1], to) < 1f;
        }

        public void Dispose()
        {
            Finder.Dispose();
            World.Dispose();
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
            Identity? identity = null, byte[]? statsBlob = null) => throw new NotSupportedException();

        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
            => throw new NotSupportedException();

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => throw new NotSupportedException();

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }
}
