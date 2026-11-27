namespace ZoneEngine_New.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
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
    using ZoneEngine_New.Core.Mobs;
    using ZoneEngine_New.Core.Movement;
    using ZoneEngine_New.Core.Nanos;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Playfield.Locality;
    using ZoneEngine_New.Core.WorldSimulation;

    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// Live threat rules (Gameplay.json threat): 2 points of healing make 1 point of threat for the healer on every NPC
    /// hating the healed one; a player under 15% health in a boss's reach holds its aggro until healed; a player sitting in
    /// front of a boss risks a flicker on each aggro decision; a rooted or NoMove NPC fights the highest threat it can
    /// reach from where it stands.
    /// </summary>
    [TestClass]
    public sealed class ThreatRulesTests
    {
        // Enough game time for one brain think, whatever the brain's start phase.
        const double Think = 0.6;

        // ---- Healing threat ----

        [TestMethod]
        public void HealingAPlayerTheMobHatesGivesTheHealerHalfTheHealAsThreat()
        {
            using var world = new ThreatWorld();
            Player tank = world.Player(10, new Vector3(2, 0, 0), health: 500);
            Player healer = world.Player(11, new Vector3(20, 0, 0));
            world.Brain.AddThreat(tank.Identity, 100f, fromDamage: true);

            Assert.IsTrue(world.Heal(healer, tank, 150));

            Assert.AreEqual(650, tank.Stats.GetOrZero(CharacterStat.Health));
            Assert.AreEqual(75f, world.Brain.Hate.ThreatOf(healer.Identity));
            Assert.AreEqual(100f, world.Brain.Hate.ThreatOf(tank.Identity), "the healed one's own threat is unchanged");
        }

        [TestMethod]
        public void OnlyTheHealthActuallyRestoredCountsNotTheOverheal()
        {
            using var world = new ThreatWorld();
            Player tank = world.Player(10, new Vector3(2, 0, 0), health: 960);
            Player healer = world.Player(11, new Vector3(20, 0, 0));
            world.Brain.AddThreat(tank.Identity, 100f, fromDamage: true);

            world.Heal(healer, tank, 150);

            Assert.AreEqual(20f, world.Brain.Hate.ThreatOf(healer.Identity));
        }

        [TestMethod]
        public void HealingAtFullHealthDrawsNoThreat()
        {
            using var world = new ThreatWorld();
            Player tank = world.Player(10, new Vector3(2, 0, 0), health: 1000);
            Player healer = world.Player(11, new Vector3(20, 0, 0));
            world.Brain.AddThreat(tank.Identity, 100f, fromDamage: true);

            world.Heal(healer, tank, 150);

            Assert.IsFalse(world.Brain.Hate.Contains(healer.Identity));
        }

        [TestMethod]
        public void HealingSomeoneTheMobDoesNotHateDrawsNothing()
        {
            using var world = new ThreatWorld();
            Player tank = world.Player(10, new Vector3(2, 0, 0));
            Player bystander = world.Player(12, new Vector3(6, 0, 0), health: 500);
            Player healer = world.Player(11, new Vector3(20, 0, 0));
            world.Brain.AddThreat(tank.Identity, 100f, fromDamage: true);

            world.Heal(healer, bystander, 150);

            Assert.IsFalse(world.Brain.Hate.Contains(healer.Identity));
        }

        [TestMethod]
        public void EveryMobHatingTheHealedOneGetsTheFullHealThreat()
        {
            using var world = new ThreatWorld();
            NpcCharacter second = world.Mob(1002, new Vector3(-2, 0, 0));
            NpcBrain secondBrain = NpcBrain.Create(second, second.Position);
            Player tank = world.Player(10, new Vector3(2, 0, 0), health: 500);
            Player healer = world.Player(11, new Vector3(20, 0, 0));
            world.Brain.AddThreat(tank.Identity, 100f, fromDamage: true);
            secondBrain.AddThreat(tank.Identity, 100f, fromDamage: true);

            world.Heal(healer, tank, 100);

            Assert.AreEqual(50f, world.Brain.Hate.ThreatOf(healer.Identity));
            Assert.AreEqual(50f, secondBrain.Hate.ThreatOf(healer.Identity));
        }

        [TestMethod]
        public void HealThreatAddsUpUntilTheHealerOutranksTheTank()
        {
            using var world = new ThreatWorld();
            Player tank = world.Player(10, new Vector3(2, 0, 0), health: 100);
            Player healer = world.Player(11, new Vector3(3, 0, 0));
            world.Brain.AddThreat(tank.Identity, 100f, fromDamage: true);
            world.Npc.Tick(Think);
            Assert.AreEqual(tank.Identity, world.Npc.FightingTarget);

            // 900 healed = 450 threat, well past the tank's 100: the next aggro decision takes the mob to the healer.
            for (int i = 0; i < 3; i++)
                world.Heal(healer, tank, 300);
            Assert.AreEqual(450f, world.Brain.Hate.ThreatOf(healer.Identity));

            for (int i = 0; i < 4; i++)
                world.Npc.Tick(Think);
            Assert.AreEqual(healer.Identity, world.Npc.FightingTarget);
        }

        [TestMethod]
        public void HealThreatOffInConfigDrawsNothing()
        {
            using var world = new ThreatWorld(new GameplaySettings { Threat = new GameplayThreat { HealThreatPercent = 0 } });
            Player tank = world.Player(10, new Vector3(2, 0, 0), health: 500);
            Player healer = world.Player(11, new Vector3(20, 0, 0));
            world.Brain.AddThreat(tank.Identity, 100f, fromDamage: true);

            world.Heal(healer, tank, 150);

            Assert.AreEqual(650, tank.Stats.GetOrZero(CharacterStat.Health), "the heal itself still lands");
            Assert.IsFalse(world.Brain.Hate.Contains(healer.Identity));
        }

        // ---- Boss: nearly dead player holds aggro ----

        [TestMethod]
        public void ABossLocksOntoAPlayerUnder15PercentInItsReach()
        {
            using var world = new ThreatWorld(boss: true);
            Player tank = world.Player(10, new Vector3(2, 0, 0));
            Player low = world.Player(12, new Vector3(-2, 0, 0), health: 140);
            world.Brain.AddThreat(tank.Identity, 1000f, fromDamage: true);
            world.Brain.AddThreat(low.Identity, 5f, fromDamage: true);

            for (int i = 0; i < 8; i++)
            {
                world.Npc.Tick(Think);
                Assert.AreEqual(low.Identity, world.Npc.FightingTarget, "think " + i);
            }
        }

        [TestMethod]
        public void TheLockTakesHoldAtOnceInsideTheAggroInterval()
        {
            using var world = new ThreatWorld(boss: true);
            Player tank = world.Player(10, new Vector3(2, 0, 0));
            Player low = world.Player(12, new Vector3(-2, 0, 0));
            world.Brain.AddThreat(tank.Identity, 1000f, fromDamage: true);
            world.Brain.AddThreat(low.Identity, 5f, fromDamage: true);
            world.Npc.Tick(Think);
            Assert.AreEqual(tank.Identity, world.Npc.FightingTarget);

            low.Stats.Set(CharacterStat.Health, 100);
            world.Npc.Tick(Think);

            Assert.AreEqual(low.Identity, world.Npc.FightingTarget);
        }

        [TestMethod]
        public void HealedBackTo15PercentTheLockEndsAtTheNextAggroDecision()
        {
            using var world = new ThreatWorld(boss: true);
            Player tank = world.Player(10, new Vector3(2, 0, 0));
            Player low = world.Player(12, new Vector3(-2, 0, 0), health: 100);
            world.Brain.AddThreat(tank.Identity, 1000f, fromDamage: true);
            world.Brain.AddThreat(low.Identity, 5f, fromDamage: true);
            world.Npc.Tick(Think);
            Assert.AreEqual(low.Identity, world.Npc.FightingTarget);

            // Healed well clear of 15%: the boss's own swings in the meantime must not drop them back under it.
            low.Stats.Set(CharacterStat.Health, 900);
            for (int i = 0; i < 4; i++)
                world.Npc.Tick(Think);

            Assert.AreEqual(tank.Identity, world.Npc.FightingTarget);
        }

        [TestMethod]
        public void Exactly15PercentIsNotLocked()
        {
            using var world = new ThreatWorld(boss: true);
            Player tank = world.Player(10, new Vector3(2, 0, 0));
            Player edge = world.Player(12, new Vector3(-2, 0, 0), health: 150);
            world.Brain.AddThreat(tank.Identity, 1000f, fromDamage: true);
            world.Brain.AddThreat(edge.Identity, 5f, fromDamage: true);

            world.Npc.Tick(Think);

            Assert.AreEqual(tank.Identity, world.Npc.FightingTarget);
        }

        [TestMethod]
        public void ANearlyDeadPlayerOutOfTheBossReachIsNotLocked()
        {
            using var world = new ThreatWorld(boss: true);
            Player tank = world.Player(10, new Vector3(2, 0, 0));
            Player low = world.Player(12, new Vector3(15, 0, 0), health: 100);
            world.Brain.AddThreat(tank.Identity, 1000f, fromDamage: true);
            world.Brain.AddThreat(low.Identity, 5f, fromDamage: true);

            world.Npc.Tick(Think);

            Assert.AreEqual(tank.Identity, world.Npc.FightingTarget);
        }

        [TestMethod]
        public void ANearlyDeadPlayerOutOfTheBossSightIsNotLocked()
        {
            using var world = new ThreatWorld(boss: true);
            Player tank = world.Player(10, new Vector3(2, 0, 0));
            Player low = world.Player(12, new Vector3(-2, 0, 0), health: 100);
            low.Playfield = ThreatWorld.CreatePlayfield(ThreatWorld.PlayfieldId + 1, world.Services);
            Assert.IsFalse(world.Npc.HasLineOfSightTo(low));
            world.Brain.AddThreat(tank.Identity, 1000f, fromDamage: true);
            world.Brain.AddThreat(low.Identity, 5f, fromDamage: true);

            world.Npc.Tick(Think);

            Assert.AreEqual(tank.Identity, world.Npc.FightingTarget);
        }

        [TestMethod]
        public void AnOrdinaryMobDoesNotLockOntoANearlyDeadPlayer()
        {
            using var world = new ThreatWorld();
            Player tank = world.Player(10, new Vector3(2, 0, 0));
            Player low = world.Player(12, new Vector3(-2, 0, 0), health: 100);
            world.Brain.AddThreat(tank.Identity, 1000f, fromDamage: true);
            world.Brain.AddThreat(low.Identity, 5f, fromDamage: true);

            world.Npc.Tick(Think);

            Assert.AreEqual(tank.Identity, world.Npc.FightingTarget);
        }

        // ---- Boss: sitting in front of it ----

        [TestMethod]
        public void APlayerSittingInTheBossReachDrawsItOnALowRoll()
        {
            using var world = new ThreatWorld(boss: true);
            world.Brain.AggroRandom = new FixedRoll(0.10);
            Player tank = world.Player(10, new Vector3(2, 0, 0));
            Player sitter = world.Player(12, new Vector3(-2, 0, 0));
            Sit(sitter);
            world.Brain.AddThreat(tank.Identity, 1000f, fromDamage: true);
            world.Brain.AddThreat(sitter.Identity, 5f);

            world.Npc.Tick(Think);

            Assert.AreEqual(sitter.Identity, world.Npc.FightingTarget);
        }

        [TestMethod]
        public void APlayerSittingInTheBossReachIsLeftAloneOnAHighRoll()
        {
            using var world = new ThreatWorld(boss: true);
            world.Brain.AggroRandom = new FixedRoll(0.90);
            Player tank = world.Player(10, new Vector3(2, 0, 0));
            Player sitter = world.Player(12, new Vector3(-2, 0, 0));
            Sit(sitter);
            world.Brain.AddThreat(tank.Identity, 1000f, fromDamage: true);
            world.Brain.AddThreat(sitter.Identity, 5f);

            world.Npc.Tick(Think);

            Assert.AreEqual(tank.Identity, world.Npc.FightingTarget);
        }

        [TestMethod]
        public void TheSittingFlickerLastsOneAggroDecision()
        {
            using var world = new ThreatWorld(boss: true);
            var roll = new FixedRoll(0.10);
            world.Brain.AggroRandom = roll;
            Player tank = world.Player(10, new Vector3(2, 0, 0));
            Player sitter = world.Player(12, new Vector3(-2, 0, 0));
            Sit(sitter);
            world.Brain.AddThreat(tank.Identity, 1000f, fromDamage: true);
            world.Brain.AddThreat(sitter.Identity, 5f);
            world.Npc.Tick(Think);
            Assert.AreEqual(sitter.Identity, world.Npc.FightingTarget);

            roll.Value = 0.90;
            for (int i = 0; i < 4; i++)
                world.Npc.Tick(Think);

            Assert.AreEqual(tank.Identity, world.Npc.FightingTarget);
        }

        [TestMethod]
        public void StandingOrOutOfReachThereIsNoSittingFlicker()
        {
            using var world = new ThreatWorld(boss: true);
            world.Brain.AggroRandom = new FixedRoll(0.0);
            Player tank = world.Player(10, new Vector3(2, 0, 0));
            Player standing = world.Player(12, new Vector3(-2, 0, 0));
            Player farSitter = world.Player(13, new Vector3(15, 0, 0));
            Sit(farSitter);
            world.Brain.AddThreat(tank.Identity, 1000f, fromDamage: true);
            world.Brain.AddThreat(standing.Identity, 5f);
            world.Brain.AddThreat(farSitter.Identity, 5f);

            world.Npc.Tick(Think);

            Assert.AreEqual(tank.Identity, world.Npc.FightingTarget);
        }

        [TestMethod]
        public void AnOrdinaryMobIgnoresASittingPlayer()
        {
            using var world = new ThreatWorld();
            world.Brain.AggroRandom = new FixedRoll(0.0);
            Player tank = world.Player(10, new Vector3(2, 0, 0));
            Player sitter = world.Player(12, new Vector3(-2, 0, 0));
            Sit(sitter);
            world.Brain.AddThreat(tank.Identity, 1000f, fromDamage: true);
            world.Brain.AddThreat(sitter.Identity, 5f);

            world.Npc.Tick(Think);

            Assert.AreEqual(tank.Identity, world.Npc.FightingTarget);
        }

        // ---- Rooted / NoMove ----

        [TestMethod]
        public void ARootedMobSwapsToTheHighestThreatItCanReach()
        {
            using var world = new ThreatWorld();
            Player far = world.Player(10, new Vector3(15, 0, 0));
            Player near = world.Player(12, new Vector3(-2, 0, 0));
            Player nearer = world.Player(13, new Vector3(0, 0, 2));
            world.Brain.AddThreat(far.Identity, 1000f, fromDamage: true);
            world.Brain.AddThreat(near.Identity, 50f, fromDamage: true);
            world.Brain.AddThreat(nearer.Identity, 10f, fromDamage: true);
            world.Npc.Tick(Think);
            Assert.AreEqual(far.Identity, world.Npc.FightingTarget, "free to move, it chases the top threat");

            Root(world.Npc);
            Assert.IsTrue(world.Npc.IsRooted);
            world.Npc.Tick(Think);

            Assert.AreEqual(near.Identity, world.Npc.FightingTarget, "rooted, at once, without waiting out the aggro interval");
        }

        [TestMethod]
        public void ARootedMobWithNobodyInReachKeepsItsTarget()
        {
            using var world = new ThreatWorld();
            Player far = world.Player(10, new Vector3(15, 0, 0));
            world.Brain.AddThreat(far.Identity, 1000f, fromDamage: true);
            Root(world.Npc);

            world.Npc.Tick(Think);

            Assert.AreEqual(far.Identity, world.Npc.FightingTarget);
        }

        [TestMethod]
        public void ANoMoveMobFightsTheHighestThreatInItsReach()
        {
            using var world = new ThreatWorld(features: [NpcFeature.NoMove]);
            Assert.IsTrue(world.Npc.IsImmobile);
            Player far = world.Player(10, new Vector3(15, 0, 0));
            Player near = world.Player(12, new Vector3(-2, 0, 0));
            world.Brain.AddThreat(far.Identity, 1000f, fromDamage: true);
            world.Brain.AddThreat(near.Identity, 50f, fromDamage: true);

            world.Npc.Tick(Think);

            Assert.AreEqual(near.Identity, world.Npc.FightingTarget);
        }

        [TestMethod]
        public void ARootedBossStillLocksOntoANearlyDeadPlayerInReach()
        {
            using var world = new ThreatWorld(boss: true);
            Player near = world.Player(10, new Vector3(2, 0, 0));
            Player low = world.Player(12, new Vector3(-2, 0, 0), health: 100);
            world.Brain.AddThreat(near.Identity, 1000f, fromDamage: true);
            world.Brain.AddThreat(low.Identity, 5f, fromDamage: true);
            Root(world.Npc);

            world.Npc.Tick(Think);

            Assert.AreEqual(low.Identity, world.Npc.FightingTarget);
        }

        // ---- Config ----

        [TestMethod]
        public void ShippedGameplayConfigCarriesTheLiveThreatRules()
        {
            GameplaySettings shipped = GameplaySettings.Load(ShippedGameplayConfig());

            Assert.AreEqual(50f, shipped.Threat.HealThreatPercent);
            Assert.AreEqual(15f, shipped.Threat.BossLowHealthPercent);
            Assert.IsTrue(shipped.Threat.BossSittingAggroPercent > 0);
            Assert.IsTrue(shipped.Threat.ImmobileFightsInReach);
            Assert.AreEqual(50f, new GameplaySettings().Threat.HealThreatPercent);
        }

        [TestMethod]
        public void OutOfRangeThreatPercentsAreRejected()
        {
            Assert.IsFalse(new GameplayThreat { HealThreatPercent = -1 }.IsValid);
            Assert.IsFalse(new GameplayThreat { BossLowHealthPercent = 101 }.IsValid);
            Assert.IsFalse(new GameplayThreat { BossSittingAggroPercent = double.NaN }.IsValid);
            Assert.IsTrue(new GameplayThreat().IsValid);
        }

        static string ShippedGameplayConfig([CallerFilePath] string here = "")
            => System.IO.Path.GetFullPath(System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(here)!, "..", "..", "Config", "Gameplay.json"));

        static void Sit(Player player)
            => player.Stats.Set(CharacterStat.CurrentMovementMode, (int)MovementState.Sit);

        static void Root(NpcCharacter npc)
        {
            ItemSpell root = new()
            {
                FunctionType = (int)FunctionType.RestrictAction,
                Arguments = new List<object> { Character.RestrictMovementBit },
                Requirements = new List<ItemRequirement>()
            };
            NanoSpell spell = NanoSpell.From(new ItemTemplate
            {
                Id = 290100,
                Name = "Root",
                Quality = 1,
                Stats = new Dictionary<CharacterStat, int>
                {
                    [CharacterStat.TimeExist] = 6000,
                    [CharacterStat.Level] = 1,
                    [CharacterStat.Can] = unchecked((int)CanFlags.ApplyOnFriendly)
                },
                SpellList = new Dictionary<EventType, List<ItemSpell>> { [EventType.OnUse] = [root] },
                CanCancel = true
            });
            BuffApplyDecision decision = npc.TryApplyBuff(spell, Identity.None, DateTime.UtcNow, out _, out _);
            Assert.AreEqual(BuffApplyDecision.Apply, decision);
        }

        sealed class FixedRoll : Random
        {
            public FixedRoll(double value) => Value = value;

            public double Value { get; set; }

            public override double NextDouble() => Value;
        }

        sealed class ThreatWorld : IDisposable
        {
            public const int PlayfieldId = 4582;

            readonly DynelRegistry _registry = new();
            readonly PlayfieldLocality _locality = new(PlayfieldId, null);

            public ThreatWorld(GameplaySettings? settings = null, bool boss = false, NpcFeature[]? features = null)
            {
                Services = new ServiceCollection()
                    .AddSingleton(_registry)
                    .AddSingleton(_locality)
                    .AddSingleton(new WorldSimulationAccess())
                    .AddSingleton(settings ?? new GameplaySettings())
                    .BuildServiceProvider();
                Playfield = CreatePlayfield(PlayfieldId, Services);

                Npc = Mob(1001, new Vector3(0, 0, 0));
                var template = new MobTemplate();
                if (boss)
                    template.Features.Add(NpcFeature.Boss);
                foreach (NpcFeature feature in features ?? [])
                    template.Features.Add(feature);
                Npc.MobTemplate = template;
                Brain = NpcBrain.Create(Npc, Npc.Position);
            }

            public ServiceProvider Services { get; }

            public Playfield Playfield { get; }

            public NpcCharacter Npc { get; }

            public NpcBrain Brain { get; }

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

            public Player Player(int id, Vector3 at, int health = 1000)
            {
                var items = new Items();
                var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new Logger(), items) { FirstName = "Threat" + id };
                player.Inventory.Apply(new CharacterHydrationResult(), id, items);
                player.Playfield = Playfield;
                player.Position = at;
                player.Stats.Set(CharacterStat.Level, 60);
                player.Stats.Set(CharacterStat.MaxHealth, 1000);
                player.Stats.Set(CharacterStat.Health, health);
                _registry.Register(player);
                return player;
            }

            public NpcCharacter Mob(int id, Vector3 at)
            {
                var npc = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new Items())
                {
                    Playfield = Playfield,
                    Attackable = true,
                    Position = at
                };
                npc.Stats.Set(CharacterStat.Level, 60);
                npc.Stats.Set(CharacterStat.MaxHealth, 1000);
                npc.Stats.Set(CharacterStat.Health, 1000);
                npc.SetWeapon(WeaponSlot.MainHand, new CharacterWeapon());
                _registry.Register(npc);
                _locality.RegisterDynel(npc);
                return npc;
            }

            /// <summary>A flat Hit on Health from <paramref name="healer"/>, the way a heal nano or item lands.</summary>
            public bool Heal(Character healer, Character target, int amount)
            {
                var template = new ItemTemplate
                {
                    Id = 290000,
                    SpellList = new Dictionary<EventType, List<ItemSpell>>
                    {
                        [EventType.OnUse] =
                        [
                            new ItemSpell
                            {
                                FunctionType = (int)FunctionType.Hit,
                                Target = (int)ItemTarget.Target,
                                Arguments = new object[] { (int)CharacterStat.Health, amount, amount, 0 }.ToList(),
                                Requirements = []
                            }
                        ]
                    }
                };
                return template.ExecuteSpells(EventType.OnUse, target, NoInventoryRepository(), new Items(), source: healer);
            }

            public void Dispose() => Services.Dispose();
        }

        sealed class Logger : IZoneLogger
        {
            public void Debug(string message) { }
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message) => throw new AssertFailedException("ERROR " + message);
            public void Error(Exception exception, string message) => throw new AssertFailedException("ERROR " + message + " " + exception);
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

        static IInventoryRepository NoInventoryRepository()
            => Fake<IInventoryRepository>((method, _) => method.ReturnType.IsValueType && method.ReturnType != typeof(void)
                ? Activator.CreateInstance(method.ReturnType)
                : null);

        public class FakeProxy : DispatchProxy
        {
            internal Func<MethodInfo, object?[]?, object?>? Handler;

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
                => Handler != null ? Handler(targetMethod!, args) : throw new AssertFailedException("Unexpected call " + targetMethod!.Name);
        }

        static T Fake<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
        {
            T proxy = DispatchProxy.Create<T, FakeProxy>();
            ((FakeProxy)(object)proxy).Handler = handler;
            return proxy;
        }
    }
}
