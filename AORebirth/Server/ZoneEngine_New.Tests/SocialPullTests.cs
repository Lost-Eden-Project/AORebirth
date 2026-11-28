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
    using ZoneEngine_New.Core.Pets;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Playfield.Locality;
    using ZoneEngine_New.Core.WorldSimulation;

    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// A face pull is quieter than a taunt (Gameplay.json socialAggro). A mob that notices a player on its own calls its
    /// kind from proximityRadiusPercent of its radius; a taunt (an Aggression Enhancer's TauntNpc) calls from
    /// tauntRadiusPercent at once, where the mob stands, before it runs to the taunter; a debuff keeps
    /// threatOnlyRadiusPercent; damage calls at the full radius from wherever the mob is. Each louder act on the same
    /// mob calls again, so the call always reaches the loudest act so far, whatever order the acts came in.
    /// Here the call radius is 20 m: proximity 25% (5 m), threat 60% (12 m), taunt 100% (20 m). The pack stands west of
    /// the puller's mob at about 2 m, 8 m and 15 m edge to edge.
    /// </summary>
    [TestClass]
    public sealed class SocialPullTests
    {
        [TestMethod]
        public void AFacePullOnlyBringsThePackMateRightBesideTheMob()
        {
            using var world = new PullWorld();
            Player player = world.Player(10, new Vector3(4, 0, 0));

            world.Caller.Brain!.TryProximityAggro(player);

            Assert.IsTrue(world.Caller.Brain.Hate.Contains(player.Identity), "the mob noticed the player");
            Assert.IsTrue(world.Hates(world.Near, player), "the mate right beside it comes");
            Assert.IsFalse(world.Hates(world.Mid, player), "a face pull reached the mate 8 m off");
            Assert.IsFalse(world.Hates(world.Far, player), "a face pull reached the mate 15 m off");
        }

        [TestMethod]
        public void AFacePulledMobHitAtCampLeavesThePackHome()
        {
            using var world = new PullWorld();
            Player player = world.Player(10, new Vector3(4, 0, 0));
            world.FacePull(player);

            // The player backs off to camp, the mob follows, and the fight starts there.
            player.Position = new Vector3(42, 0, 0);
            world.Caller.Position = new Vector3(40, 0, 0);
            world.Caller.Brain.AddThreatFrom(player, 50f, fromDamage: true);

            Assert.IsFalse(world.Hates(world.Mid, player), "the hit at camp reached the pack at home");
            Assert.IsFalse(world.Hates(world.Far, player), "the hit at camp reached the pack at home");
        }

        [TestMethod]
        public void AFacePulledMobHitBesideItsPackStillBringsThePack()
        {
            using var world = new PullWorld();
            Player player = world.Player(10, new Vector3(4, 0, 0));
            world.FacePull(player);

            world.Caller.Brain!.AddThreatFrom(player, 50f, fromDamage: true);

            Assert.IsTrue(world.Hates(world.Mid, player) && world.Hates(world.Far, player),
                "fighting where the pack stands is not made safe by a face pull");
        }

        [TestMethod]
        public void ATauntFromRangeBringsThePackBeforeTheMobMoves()
        {
            using var world = new PullWorld();
            Player player = world.Player(10, new Vector3(30, 0, 0));

            Assert.IsTrue(world.Taunt(player, world.Caller));

            Assert.AreEqual(25f, world.Caller.Brain!.Hate.ThreatOf(player.Identity), "the taunt's own threat");
            Assert.IsTrue(world.Hates(world.Near, player));
            Assert.IsTrue(world.Hates(world.Mid, player), "the taunt is a loud pull");
            Assert.IsTrue(world.Hates(world.Far, player), "a taunt from range left the pack behind");
        }

        [TestMethod]
        public void TauntingAFacePulledMobIsAsLoudAsTauntingItFirst()
        {
            using var world = new PullWorld();
            Player player = world.Player(10, new Vector3(4, 0, 0));
            world.FacePull(player);
            Assert.IsFalse(world.Hates(world.Far, player));

            Assert.IsTrue(world.Taunt(player, world.Caller));

            Assert.IsTrue(world.Hates(world.Mid, player) && world.Hates(world.Far, player),
                "a face pull then a taunt kept the taunt quiet");
        }

        [TestMethod]
        public void ADebuffPullKeepsTheThreatOnlyRadius()
        {
            using var world = new PullWorld();
            Player player = world.Player(10, new Vector3(30, 0, 0));

            // A hostile nano that did no damage (NanoRuntime.AddHostileThreat).
            world.Caller.Brain!.AddThreatFrom(player, NpcAiRules.ProximityHate);

            Assert.IsTrue(world.Hates(world.Near, player) && world.Hates(world.Mid, player));
            Assert.IsFalse(world.Hates(world.Far, player), "a debuff called as loud as a taunt");
        }

        [TestMethod]
        public void AQuieterActNeverCallsAgain()
        {
            using var world = new PullWorld();
            Player player = world.Player(10, new Vector3(30, 0, 0));
            world.Caller.Brain!.AddThreatFrom(player, 50f, fromDamage: true);
            // The pack has gone home: a later debuff or taunt on the same mob must not reach them again.
            world.Mid.Brain!.ClearHate();
            world.Far.Brain!.ClearHate();

            world.Caller.Brain.AddThreatFrom(player, NpcAiRules.ProximityHate);
            Assert.IsTrue(world.Taunt(player, world.Caller));

            Assert.IsFalse(world.Hates(world.Mid, player) || world.Hates(world.Far, player),
                "an act quieter than the damage already done called the pack again");
        }

        [TestMethod]
        public void APetsTauntIsLoudForItsOwnerToo()
        {
            using var world = new PullWorld();
            Player owner = world.Player(10, new Vector3(30, 0, 0));
            NpcCharacter pet = world.Pet(2001, new Vector3(28, 0, 0), owner);

            Assert.IsTrue(world.Taunt(pet, world.Caller));

            Assert.IsTrue(world.Hates(world.Far, pet), "the pet's taunt is a loud pull");
            Assert.IsTrue(world.Caller.Brain!.Hate.Contains(owner.Identity), "the owner is on the list");
            Assert.IsTrue(world.Hates(world.Far, owner), "the owner's call was quieter than the pet's taunt");
        }

        [TestMethod]
        public void AFirstHitWithNoPullCallsAtTheFullRadius()
        {
            // No proximity, no taunt: a first hit is the full call, as before.
            using var world = new PullWorld();
            Player player = world.Player(10, new Vector3(30, 0, 0));

            world.Caller.Brain!.AddThreatFrom(player, 50f, fromDamage: true);

            Assert.IsTrue(world.Hates(world.Near, player) && world.Hates(world.Mid, player) && world.Hates(world.Far, player));
        }

        [TestMethod]
        public void ShippedConfigMakesTheFacePullTheQuietestPull()
        {
            GameplaySocialAggro shipped = GameplaySettings.Load(ShippedGameplayConfig()).SocialAggro;

            Assert.IsTrue(shipped.ProximityRadiusPercent < shipped.ThreatOnlyRadiusPercent);
            Assert.IsTrue(shipped.ThreatOnlyRadiusPercent <= shipped.TauntRadiusPercent);
            Assert.IsTrue(shipped.RadiusFor(20, call: SocialCall.Proximity) < shipped.RadiusFor(20, call: SocialCall.Taunt));
            Assert.AreEqual(shipped.RadiusFor(20, guards: true), shipped.RadiusFor(20, guards: true, call: SocialCall.Proximity),
                "guards answering a guard keep the full radius");
        }

        [TestMethod]
        public void OutOfRangePullPercentsAreRejected()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SocialPullTests_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                string shipped = System.IO.File.ReadAllText(ShippedGameplayConfig());
                Assert.IsTrue(shipped.Contains("\"proximityRadiusPercent\": 25,") && shipped.Contains("\"tauntRadiusPercent\": 100,"));
                foreach ((string good, string bad) in new[]
                {
                    ("\"proximityRadiusPercent\": 25,", "\"proximityRadiusPercent\": -1,"),
                    ("\"tauntRadiusPercent\": 100,", "\"tauntRadiusPercent\": 101,")
                })
                {
                    string path = System.IO.Path.Combine(dir, "Gameplay.json");
                    System.IO.File.WriteAllText(path, shipped.Replace(good, bad));
                    Assert.ThrowsExactly<System.IO.InvalidDataException>(() => GameplaySettings.Load(path), bad);
                }
            }
            finally
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
        }

        static string ShippedGameplayConfig([CallerFilePath] string here = "")
            => System.IO.Path.GetFullPath(System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(here)!, "..", "..", "Config", "Gameplay.json"));

        sealed class PullWorld : IDisposable
        {
            const int PlayfieldId = 4583;

            readonly DynelRegistry _registry = new();
            readonly PlayfieldLocality _locality = new(PlayfieldId, null);

            public PullWorld(GameplaySocialAggro? social = null)
            {
                Services = new ServiceCollection()
                    .AddSingleton(_registry)
                    .AddSingleton(_locality)
                    .AddSingleton(new WorldSimulationAccess())
                    .AddSingleton(new GameplaySettings
                    {
                        SocialAggro = social ?? new GameplaySocialAggro
                        {
                            DefaultRadius = 20, MaxRadius = 30, ProximityRadiusPercent = 25, ThreatOnlyRadiusPercent = 60,
                            TauntRadiusPercent = 100
                        }
                    })
                    .BuildServiceProvider();
                Playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
                Set("<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = PlayfieldId });
                Set("_serviceProvider", Services);
                Set("_pendingStatRebases", new ConcurrentDictionary<Character, byte>());
                Set("_lifecycleSync", new System.Threading.Lock());
                Set("<FightModes>k__BackingField", DistrictFightModes.None);

                Caller = Mob(1001, new Vector3(0, 0, 0));
                Near = Mob(1002, new Vector3(-3, 0, 0));
                Mid = Mob(1003, new Vector3(-9, 0, 0));
                Far = Mob(1004, new Vector3(-16, 0, 0));
            }

            public ServiceProvider Services { get; }

            public Playfield Playfield { get; }

            public NpcCharacter Caller { get; }

            public NpcCharacter Near { get; }

            public NpcCharacter Mid { get; }

            public NpcCharacter Far { get; }

            void Set(string field, object value)
                => typeof(Playfield).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Playfield, value);

            public bool Hates(NpcCharacter mob, Character enemy) => mob.Brain!.Hate.Contains(enemy.Identity);

            /// <summary>The caller notices <paramref name="player"/> on its own, as its proximity scan does.</summary>
            public void FacePull(Player player)
            {
                Caller.Brain!.TryProximityAggro(player);
                Assert.AreEqual(NpcAiRules.ProximityHate, Caller.Brain.Hate.ThreatOf(player.Identity), "the face pull did not happen");
                Assert.IsFalse(Hates(Mid, player) || Hates(Far, player), "the face pull reached past the mate beside the mob");
            }

            public Player Player(int id, Vector3 at)
            {
                var items = new Items();
                var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new Logger(), items) { FirstName = "Puller" + id };
                player.Inventory.Apply(new CharacterHydrationResult(), id, items);
                player.Playfield = Playfield;
                player.Position = at;
                player.Stats.Set(CharacterStat.Level, 60);
                player.Stats.Set(CharacterStat.MaxHealth, 1000);
                player.Stats.Set(CharacterStat.Health, 1000);
                _registry.Register(player);
                return player;
            }

            /// <summary>A hostile level 60 mob of the pack's kind, with its brain.</summary>
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
                npc.Stats.Set(CharacterStat.BreedHostility, 100);
                // Monster side: a Neutral NPC never aggroes on its own.
                npc.Stats.Set(CharacterStat.Side, 3);
                npc.SetWeapon(WeaponSlot.MainHand, new CharacterWeapon());
                _registry.Register(npc);
                _locality.RegisterDynel(npc);
                NpcBrain.Create(npc, at);
                return npc;
            }

            /// <summary>A player's attack pet: no brain of its own here, so it never answers the pack's calls.</summary>
            public NpcCharacter Pet(int id, Vector3 at, Player owner)
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
                _registry.Register(npc);
                npc.BindPet(new PetController(owner, PetTypes.Attack, "synthetic-pet", 60, expiresUtc: null));
                return npc;
            }

            /// <summary>The Aggression Enhancer's TauntNpc (amount 25) from <paramref name="source"/> on <paramref name="mob"/>.</summary>
            public bool Taunt(Character source, NpcCharacter mob)
            {
                var template = new ItemTemplate
                {
                    Id = 83920,
                    SpellList = new Dictionary<EventType, List<ItemSpell>>
                    {
                        [EventType.OnUse] =
                        [
                            new ItemSpell
                            {
                                FunctionType = (int)FunctionType.TauntNpc,
                                Target = (int)ItemTarget.Target,
                                Arguments = new object[] { 25 }.ToList(),
                                Requirements = []
                            }
                        ]
                    }
                };
                return template.ExecuteSpells(EventType.OnUse, mob, NoInventoryRepository(), new Items(), source: source);
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
            int _next = 710000;

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
