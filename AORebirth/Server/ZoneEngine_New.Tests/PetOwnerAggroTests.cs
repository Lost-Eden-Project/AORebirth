namespace ZoneEngine_New.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Reflection;
    using System.Runtime.CompilerServices;

    using AORebirth.Core.GameData;
    using AORebirth.Enums;

    using LostEden.Protocol.GameData;

    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using ZoneEngine_New.Core.Ai;
    using ZoneEngine_New.Core.Characters;
    using ZoneEngine_New.Core.Configuration;
    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Pets;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Playfield.Locality;
    using ZoneEngine_New.Core.WorldSimulation;

    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// Aggro is decided every Gameplay.json aggroIntervalSeconds (2 s), and a mob fighting a player's pet flickers to
    /// the pet's owner on those decisions: about half the time for an untrimmed pet, less the more Aggressiveness the pet
    /// has, only to an owner it can see, and never between unrelated players.
    /// </summary>
    [TestClass]
    public sealed class PetOwnerAggroTests
    {
        // Enough game time for one brain think, whatever the brain's start phase.
        const double Think = 0.6;

        [TestMethod]
        public void UntrimmedPetLosesTheMobToItsOwnerOnALowRoll()
        {
            using var world = new AggroWorld();
            world.Brain.AggroRandom = new FixedRoll(0.30);
            world.Brain.AddThreat(world.PetNpc.Identity, 20f);
            world.Brain.AddThreat(world.Owner.Identity, 5f);

            world.Mob.Tick(Think);

            Assert.AreEqual(world.Owner.Identity, world.Brain.CurrentTarget);
            Assert.AreEqual(world.Owner.Identity, world.Mob.FightingTarget);
        }

        [TestMethod]
        public void UntrimmedPetKeepsTheMobOnAHighRoll()
        {
            using var world = new AggroWorld();
            world.Brain.AggroRandom = new FixedRoll(0.60);
            world.Brain.AddThreat(world.PetNpc.Identity, 20f);
            world.Brain.AddThreat(world.Owner.Identity, 5f);

            world.Mob.Tick(Think);

            Assert.AreEqual(world.PetNpc.Identity, world.Mob.FightingTarget);
        }

        [TestMethod]
        public void TrimmedAggressivenessKeepsTheMobOnThePet()
        {
            using var world = new AggroWorld();
            world.PetNpc.Stats.Set(CharacterStat.Aggressiveness, 90);
            // 30 would turn an untrimmed pet's mob onto the owner; at Aggressiveness 90 the owner chance is about 15%.
            world.Brain.AggroRandom = new FixedRoll(0.30);
            world.Brain.AddThreat(world.PetNpc.Identity, 20f);
            world.Brain.AddThreat(world.Owner.Identity, 5f);

            world.Mob.Tick(Think);

            Assert.AreEqual(world.PetNpc.Identity, world.Mob.FightingTarget);
        }

        [TestMethod]
        public void OwnerAboveThePetIsAlwaysTheTarget()
        {
            using var world = new AggroWorld();
            world.Brain.AggroRandom = new FixedRoll(0.99);
            world.Brain.AddThreat(world.PetNpc.Identity, 5f);
            world.Brain.AddThreat(world.Owner.Identity, 20f);

            for (int i = 0; i < 8; i++)
            {
                world.Mob.Tick(Think);
                Assert.AreEqual(world.Owner.Identity, world.Mob.FightingTarget);
            }
        }

        [TestMethod]
        public void FlickerRollsOnlyOnTheAggroInterval()
        {
            using var world = new AggroWorld();
            var roll = new FixedRoll(0.30);
            world.Brain.AggroRandom = roll;
            world.Brain.AddThreat(world.PetNpc.Identity, 20f);
            world.Brain.AddThreat(world.Owner.Identity, 5f);
            world.Mob.Tick(Think);
            Assert.AreEqual(world.Owner.Identity, world.Mob.FightingTarget);

            // Within the 2 s the mob stays on the owner even though the next roll would send it back to the pet.
            roll.Value = 0.99;
            world.Mob.Tick(Think);
            world.Mob.Tick(Think);
            world.Mob.Tick(Think);
            Assert.AreEqual(world.Owner.Identity, world.Mob.FightingTarget);

            world.Mob.Tick(Think);
            Assert.AreEqual(world.PetNpc.Identity, world.Mob.FightingTarget);
        }

        [TestMethod]
        public void OwnerOutOfSightIsNotSwitchedTo()
        {
            using var world = new AggroWorld();
            // A playfield of another id: no line of sight from the mob, though still on its hate list and in reach.
            world.Owner.Playfield = AggroWorld.CreatePlayfield(AggroWorld.PlayfieldId + 1, world.Services);
            Assert.IsFalse(world.Mob.HasLineOfSightTo(world.Owner));
            world.Brain.AggroRandom = new FixedRoll(0.0);
            world.Brain.AddThreat(world.PetNpc.Identity, 20f);
            world.Brain.AddThreat(world.Owner.Identity, 5f);

            world.Mob.Tick(Think);

            Assert.AreEqual(world.PetNpc.Identity, world.Mob.FightingTarget);
        }

        [TestMethod]
        public void AnotherPlayerBehindThePetDoesNotFlicker()
        {
            using var world = new AggroWorld();
            Player bystander = world.Player(5, new Vector3(4, 0, 0));
            world.Brain.AggroRandom = new FixedRoll(0.0);
            world.Brain.AddThreat(world.PetNpc.Identity, 20f);
            world.Brain.AddThreat(bystander.Identity, 5f);

            world.Mob.Tick(Think);

            Assert.AreEqual(world.PetNpc.Identity, world.Mob.FightingTarget);
        }

        [TestMethod]
        public void HigherThreatTakesTheMobOnlyAtTheNextAggroDecision()
        {
            using var world = new AggroWorld();
            Player first = world.Player(5, new Vector3(4, 0, 0));
            Player second = world.Player(6, new Vector3(6, 0, 0));
            world.Brain.AddThreat(first.Identity, 5f);
            world.Mob.Tick(Think);
            Assert.AreEqual(first.Identity, world.Mob.FightingTarget);

            world.Brain.AddThreat(second.Identity, 20f);
            world.Mob.Tick(Think);
            Assert.AreEqual(first.Identity, world.Mob.FightingTarget);

            world.Mob.Tick(Think);
            world.Mob.Tick(Think);
            world.Mob.Tick(Think);
            Assert.AreEqual(second.Identity, world.Mob.FightingTarget);
        }

        [TestMethod]
        public void LostTargetIsReplacedWithoutWaitingForTheInterval()
        {
            using var world = new AggroWorld();
            Player first = world.Player(5, new Vector3(4, 0, 0));
            Player second = world.Player(6, new Vector3(6, 0, 0));
            world.Brain.AddThreat(first.Identity, 20f);
            world.Brain.AddThreat(second.Identity, 5f);
            world.Mob.Tick(Think);
            Assert.AreEqual(first.Identity, world.Mob.FightingTarget);

            // The target zones out (or dies, logs off): the mob does not stand idle until the next aggro decision.
            world.Services.GetRequiredService<DynelRegistry>().Unregister(first.Identity);
            world.Mob.Tick(Think);

            Assert.AreEqual(second.Identity, world.Mob.FightingTarget);
        }

        [TestMethod]
        public void PetDamageOnlyPutsItsOwnerOnTheHateListAtTheMinimum()
        {
            using var world = new AggroWorld();
            for (int i = 0; i < 5; i++)
                world.Brain.AddThreatFrom(world.PetNpc, 30f, fromDamage: true);

            Assert.AreEqual(150f, world.Brain.Hate.ThreatOf(world.PetNpc.Identity));
            Assert.AreEqual(NpcAiRules.ProximityHate, world.Brain.Hate.ThreatOf(world.Owner.Identity));
        }

        [TestMethod]
        public void OwnerHealingBehindATankingPetStillGetsTheFlickerRoll()
        {
            using var world = new AggroWorld();
            // The pet tanks for 100 threat; the owner's own heals and nukes add 60. With a 50% share the owner would sit
            // at 111, above the pet, and the mob would leave the pet for them on every decision with no roll.
            world.Brain.AddThreatFrom(world.PetNpc, 100f, fromDamage: true);
            world.Brain.AddThreat(world.Owner.Identity, 60f, fromDamage: true);
            var roll = new FixedRoll(0.99);
            world.Brain.AggroRandom = roll;

            for (int i = 0; i < 8; i++)
            {
                world.Mob.Tick(Think);
                Assert.AreEqual(world.PetNpc.Identity, world.Mob.FightingTarget);
            }

            // A low roll still flickers the mob to the owner.
            roll.Value = 0.0;
            for (int i = 0; i < 4; i++)
                world.Mob.Tick(Think);
            Assert.AreEqual(world.Owner.Identity, world.Mob.FightingTarget);
        }

        [TestMethod]
        public void OwnerIsTheNextTargetWhenThePetIsGone()
        {
            using var world = new AggroWorld();
            world.Brain.AggroRandom = new FixedRoll(0.99);
            world.Brain.AddThreatFrom(world.PetNpc, 100f, fromDamage: true);
            world.Mob.Tick(Think);
            Assert.AreEqual(world.PetNpc.Identity, world.Mob.FightingTarget);

            // The pet dies or is dismissed: the mob turns on the owner instead of walking home.
            world.Services.GetRequiredService<DynelRegistry>().Unregister(world.PetNpc.Identity);
            world.Mob.Tick(Think);

            Assert.AreEqual(world.Owner.Identity, world.Mob.FightingTarget);
        }

        [TestMethod]
        public void ShippedGameplayConfigGivesTheOwnerNoThreatShare()
        {
            GameplaySettings shipped = GameplaySettings.Load(ShippedGameplayConfig());

            Assert.AreEqual(0f, shipped.Pets.OwnerThreatSharePercent);
            Assert.IsTrue(shipped.Pets.OwnerAlwaysOnHateList);
            Assert.AreEqual(0f, new GameplaySettings().Pets.OwnerThreatSharePercent);
        }

        static string ShippedGameplayConfig([CallerFilePath] string here = "")
            => System.IO.Path.GetFullPath(System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(here)!, "..", "..", "Config", "Gameplay.json"));

        [TestMethod]
        public void OwnerChanceFollowsTheMeasuredFit()
        {
            var chance = new GameplayPetOwnerAggroChance();
            Assert.AreEqual(50.5, chance.PercentFor(0), 0.1);
            Assert.AreEqual(15.4, chance.PercentFor(90), 0.2);
            Assert.AreEqual(0.0, chance.PercentFor(200), 0.0);
            Assert.IsTrue(chance.PercentFor(int.MinValue) <= 100);
        }

        sealed class FixedRoll : Random
        {
            public FixedRoll(double value) => Value = value;

            public double Value { get; set; }

            public override double NextDouble() => Value;
        }

        sealed class AggroWorld : IDisposable
        {
            public const int PlayfieldId = 4582;

            readonly DynelRegistry _registry = new();

            public AggroWorld()
            {
                Services = new ServiceCollection()
                    .AddSingleton(_registry)
                    .AddSingleton(new PlayfieldLocality(PlayfieldId, null))
                    .AddSingleton(new WorldSimulationAccess())
                    .AddSingleton(new GameplaySettings())
                    .BuildServiceProvider();
                Playfield = CreatePlayfield(PlayfieldId, Services);

                Mob = Npc(1001, new Vector3(0, 0, 0));
                Mob.SetWeapon(WeaponSlot.MainHand, new CharacterWeapon());
                Brain = NpcBrain.Create(Mob, Mob.Position);

                Owner = Player(1, new Vector3(5, 0, 0));
                PetNpc = Npc(2, new Vector3(1.5, 0, 0));
                PetNpc.BindPet(new PetController(Owner, PetTypes.Attack, "synthetic-pet", 60, expiresUtc: null));
            }

            public ServiceProvider Services { get; }

            public Playfield Playfield { get; }

            public NpcCharacter Mob { get; }

            public NpcBrain Brain { get; }

            public Player Owner { get; }

            public NpcCharacter PetNpc { get; }

            /// <summary>A playfield built without its constructor, enough for brains, combat and line of sight.</summary>
            public static Playfield CreatePlayfield(int playfieldId, IServiceProvider services)
            {
                var playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
                Set(playfield, "<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = playfieldId });
                Set(playfield, "_serviceProvider", services);
                Set(playfield, "_pendingStatRebases", new ConcurrentDictionary<Character, byte>());
                Set(playfield, "_lifecycleSync", new System.Threading.Lock());
                Set(playfield, "<FightModes>k__BackingField", DistrictFightModes.None);
                return playfield;
            }

            static void Set(Playfield playfield, string field, object value)
                => typeof(Playfield).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(playfield, value);

            public Player Player(int id, Vector3 at)
            {
                var items = new Items();
                var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new Logger(), items) { FirstName = "Aggro" };
                player.Inventory.Apply(new CharacterHydrationResult(), id, items);
                player.Playfield = Playfield;
                player.Position = at;
                player.Stats.Set(CharacterStat.Level, 60);
                player.Stats.Set(CharacterStat.MaxHealth, 100);
                player.Stats.Set(CharacterStat.Health, 100);
                _registry.Register(player);
                return player;
            }

            NpcCharacter Npc(int id, Vector3 at)
            {
                var npc = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new Items())
                {
                    Playfield = Playfield,
                    Attackable = true,
                    Position = at
                };
                npc.Stats.Set(CharacterStat.Level, 60);
                npc.Stats.Set(CharacterStat.MaxHealth, 100);
                npc.Stats.Set(CharacterStat.Health, 100);
                _registry.Register(npc);
                return npc;
            }

            public void Dispose() => Services.Dispose();
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
