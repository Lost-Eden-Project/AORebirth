namespace ZoneEngine_New.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Runtime.CompilerServices;

    using AORebirth.Core.GameData;
    using AORebirth.Enums;
    using AORebirth.World.Collision;
    using AORebirth.World.Pathfinding;

    using LostEden.Protocol.GameData;

    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using ZoneEngine_New.Core.Ai;
    using ZoneEngine_New.Core.Characters;
    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Pets;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Playfield.Locality;
    using ZoneEngine_New.Core.WorldSimulation;

    using NVector3 = System.Numerics.Vector3;
    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// Lost Eden 2026-11-24: a pet sent at a mob down a slope stood still. Its fighting spot beside the mob kept the
    /// mob's height, which uphill lies under the ground, so no route ended there (EndOffMesh every think) and the pet
    /// never moved. The scene is a 31 degree ramp (the live slope) baked with the live agent settings, next to a flat
    /// slab.
    /// </summary>
    [TestClass]
    public sealed class PetAttackOnSlopeTests
    {
        const int PlayfieldId = 900042;

        /// <summary>Ramp height per meter along X (31 degrees).</summary>
        const float Rise = 0.6f;

        static NavMeshPathfinder? _finder;

        [ClassInitialize]
        public static void Bake(TestContext _)
        {
            // The live NavAgent.json agent: 0.5 m tall, 0.3 m climb, so a spot 1 m under the ground is off the mesh.
            var settings = new NavMeshBuildSettings
            {
                CellSize = 0.13f,
                CellHeight = 0.15f,
                AgentRadius = 0.25f,
                AgentHeight = 0.5f,
                AgentMaxClimb = 0.3f,
                AgentMaxSlope = 60f
            };
            NavMeshBakeResult baked = NavMeshBaker.Bake(Scene(), settings);
            _finder = NavMeshPathfinder.FromBytes(PlayfieldId, NavMeshFile.ToBytes(baked), settings);
        }

        [ClassCleanup]
        public static void Cleanup() => _finder?.Dispose();

        [TestMethod]
        public void PetSentAtAMobDownASlopeSetsOffTowardsIt()
        {
            using var world = new SlopeWorld(_finder!);
            NpcCharacter enemy = world.Npc(3, OnRamp(5, 10));
            Player owner = world.Player(1, OnRamp(26, 10));
            (NpcCharacter pet, PetController controller) = world.Pet(2, owner, OnRamp(25, 10));
            NpcBrain brain = NpcBrain.CreatePet(pet, controller);
            controller.Attack(enemy.Identity);

            // The fixture reproduces the defect: the old spot, at the mob's height, is under the ground.
            Vector3 old = PetFormation.AttackPoint(pet, enemy, 0, PetAi.AttackReach);
            Assert.IsFalse(_finder!.IsDestinationOnMesh(new NVector3(old.xf, old.yf, old.zf)), "fixture: the spot at the mob's height must be off the mesh");

            Assert.IsTrue(PetAi.PickEnemy(brain));
            Assert.AreEqual(enemy.Identity, brain.CurrentTarget);
            new PetFightNode(brain).Evaluate(0.1f);

            Assert.IsTrue(pet.Motor.HasPath, "the pet stands still instead of closing in on its target");
            Assert.AreEqual(enemy.Identity, pet.FightingTarget);
        }

        [TestMethod]
        public void AttackSpotBesideAMobOnASlopeIsOnTheGround()
        {
            using var world = new SlopeWorld(_finder!);
            NpcCharacter enemy = world.Npc(3, OnRamp(5, 10));
            Player owner = world.Player(1, OnRamp(26, 10));
            (NpcCharacter pet, PetController controller) = world.Pet(2, owner, OnRamp(25, 10));
            NpcBrain brain = NpcBrain.CreatePet(pet, controller);

            Vector3 old = PetFormation.AttackPoint(pet, enemy, 0, PetAi.AttackReach);
            Vector3 spot = PetAi.AttackSpot(brain, enemy, 0);

            Assert.AreEqual(old.x, spot.x, 0.05, "the spot stays beside the mob");
            Assert.AreEqual(old.z, spot.z, 0.05, "the spot stays beside the mob");
            Assert.AreEqual(spot.x * Rise, spot.y, 0.3, "the spot is on the ramp's surface");
            Assert.IsTrue(_finder!.IsDestinationOnMesh(new NVector3(spot.xf, spot.yf, spot.zf)));
        }

        [TestMethod]
        public void AttackSpotOnFlatGroundIsUnchanged()
        {
            using var world = new SlopeWorld(_finder!);
            NpcCharacter enemy = world.Npc(3, new Vector3(60, 0, 10));
            Player owner = world.Player(1, new Vector3(72, 0, 10));
            (NpcCharacter pet, PetController controller) = world.Pet(2, owner, new Vector3(70, 0, 10));
            NpcBrain brain = NpcBrain.CreatePet(pet, controller);

            Vector3 old = PetFormation.AttackPoint(pet, enemy, 0, PetAi.AttackReach);
            Vector3 spot = PetAi.AttackSpot(brain, enemy, 0);

            Assert.AreEqual(old.x, spot.x, 1e-6);
            Assert.AreEqual(old.y, spot.y, 1e-6);
            Assert.AreEqual(old.z, spot.z, 1e-6);
        }

        static Vector3 OnRamp(double x, double z) => new(x, x * Rise, z);

        /// <summary>A ramp over x 0..40 rising <see cref="Rise"/> per meter, and a flat slab over x 50..90 at y 0.</summary>
        static PlayfieldCollisionSet Scene()
        {
            var vertices = new List<NVector3>();
            var triangles = new List<CollisionTriangle>();
            Quad(vertices, triangles, 0f, 0f, 40f, 40f * Rise, 0f, 20f);
            Quad(vertices, triangles, 50f, 0f, 90f, 0f, 0f, 20f);
            return new PlayfieldCollisionSet(
                PlayfieldId,
                new[] { new CollisionTriangleMesh(vertices.ToArray(), triangles.ToArray(), cellId: null, source: "slope") },
                terrain: null);
        }

        /// <summary>A quad from (x0, y0) to (x1, y1) along X over z0..z1, both windings.</summary>
        static void Quad(List<NVector3> vertices, List<CollisionTriangle> triangles, float x0, float y0, float x1, float y1, float z0, float z1)
        {
            int start = vertices.Count;
            vertices.Add(new NVector3(x0, y0, z0));
            vertices.Add(new NVector3(x1, y1, z0));
            vertices.Add(new NVector3(x1, y1, z1));
            vertices.Add(new NVector3(x0, y0, z1));
            triangles.Add(new CollisionTriangle(start, start + 1, start + 3));
            triangles.Add(new CollisionTriangle(start + 1, start + 2, start + 3));
            triangles.Add(new CollisionTriangle(start, start + 3, start + 1));
            triangles.Add(new CollisionTriangle(start + 1, start + 3, start + 2));
        }

        sealed class SlopeWorld : IDisposable
        {
            readonly DynelRegistry _registry = new();
            readonly ServiceProvider _services;
            readonly Playfield _playfield;

            public SlopeWorld(NavMeshPathfinder finder)
            {
                _services = new ServiceCollection()
                    .AddSingleton(_registry)
                    .AddSingleton(new PlayfieldLocality(PlayfieldId, null))
                    .AddSingleton(new WorldSimulationAccess())
                    .BuildServiceProvider();
                _playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
                Set("<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = PlayfieldId });
                Set("_serviceProvider", _services);
                Set("_pendingStatRebases", new ConcurrentDictionary<Character, byte>());
                Set("_lifecycleSync", new System.Threading.Lock());
                Set("<FightModes>k__BackingField", DistrictFightModes.None);
                Set("_pathfinder", finder);
            }

            void Set(string field, object value)
                => typeof(Playfield).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_playfield, value);

            public Player Player(int id, Vector3 at)
            {
                var items = new Items();
                var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new Logger(), items) { FirstName = "Slope" };
                player.Inventory.Apply(new CharacterHydrationResult(), id, items);
                player.Playfield = _playfield;
                player.Position = at;
                player.Stats.Set(CharacterStat.Level, 60);
                player.Stats.Set(CharacterStat.MaxHealth, 100);
                player.Stats.Set(CharacterStat.Health, 100);
                _registry.Register(player);
                return player;
            }

            public NpcCharacter Npc(int id, Vector3 at)
            {
                var npc = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new Items())
                {
                    Playfield = _playfield,
                    Attackable = true,
                    Position = at
                };
                npc.Stats.Set(CharacterStat.Level, 60);
                npc.Stats.Set(CharacterStat.MaxHealth, 100);
                npc.Stats.Set(CharacterStat.Health, 100);
                _registry.Register(npc);
                return npc;
            }

            public (NpcCharacter Pet, PetController Controller) Pet(int id, Player owner, Vector3 at)
            {
                NpcCharacter pet = Npc(id, at);
                var controller = new PetController(owner, PetTypes.Attack, "synthetic-pet", 60, expiresUtc: null);
                pet.BindPet(controller);
                return (pet, controller);
            }

            public void Dispose() => _services.Dispose();
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
            int _next = 700000;

            public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
                Identity? identity = null, byte[]? statsBlob = null) => new()
                {
                    LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount,
                    InstanceId = instanceId, Identity = identity ?? Identity.None, Definition = CreateTemplate(lowId, highId, quality)
                };

            public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
            {
                Item item = Create(lowId, highId, quality, source, stackCount);
                item.AssignInstanceId(++_next);
                return item;
            }

            public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };

            public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
        }
    }
}
