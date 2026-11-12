namespace ZoneEngine_New.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Reflection;
    using System.Runtime.CompilerServices;

    using AORebirth.Enums;

    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using ZoneEngine_New.Core.Ai;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Nanos;
    using ZoneEngine_New.Core.Pets;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Playfield.Locality;
    using ZoneEngine_New.Core.WorldSimulation;

    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// Bugs #144/#245 (a pet sent at a mob dropped the fight and came back when its owner left the room) and
    /// #203 (a heal pet forgot its ordered target the first time a heal could not go off).
    /// </summary>
    [TestClass]
    public sealed class PetLeashAndHealOrderTests
    {
        const int HealNanoId = 920001;

        [TestMethod]
        public void OrderedAttackHoldsWhileTheOwnerIsPastGuardRange()
        {
            using var world = new PetWorld();
            Player owner = world.Player(1, new Vector3(0, 0, 0));
            NpcCharacter enemy = world.Npc(3, new Vector3(3, 0, 0));
            (NpcCharacter pet, PetController controller) = world.Pet(2, owner, PetTypes.Attack, new Vector3(2, 0, 0));
            NpcBrain brain = NpcBrain.CreatePet(pet, controller);
            controller.Attack(enemy.Identity);

            owner.Position = new Vector3(PetAi.GuardRange + 10, 0, 0);

            Assert.IsFalse(PetAi.ShouldRejoinOwner(brain));
            Assert.IsTrue(PetAi.PickEnemy(brain));
            Assert.AreEqual(enemy.Identity, brain.CurrentTarget);
            Assert.AreEqual(enemy.Identity, controller.AttackTarget);
        }

        [TestMethod]
        public void OrderedAttackEndsPastMaxPetDistance()
        {
            using var world = new PetWorld();
            Player owner = world.Player(1, new Vector3(0, 0, 0));
            NpcCharacter enemy = world.Npc(3, new Vector3(3, 0, 0));
            (NpcCharacter pet, PetController controller) = world.Pet(2, owner, PetTypes.Attack, new Vector3(2, 0, 0));
            NpcBrain brain = NpcBrain.CreatePet(pet, controller);
            controller.Attack(enemy.Identity);

            owner.Position = new Vector3(PetService.MaxPlayerPetDistance + 10, 0, 0);

            Assert.IsTrue(PetAi.ShouldRejoinOwner(brain));
            Assert.IsFalse(PetAi.PickEnemy(brain));
            Assert.AreEqual(Identity.None, controller.AttackTarget);
            Assert.AreEqual(Identity.None, brain.CurrentTarget);
        }

        [TestMethod]
        public void GuardingPetStillDropsItsFightPastGuardRange()
        {
            using var world = new PetWorld();
            Player owner = world.Player(1, new Vector3(0, 0, 0));
            NpcCharacter enemy = world.Npc(3, new Vector3(3, 0, 0));
            (NpcCharacter pet, PetController controller) = world.Pet(2, owner, PetTypes.Attack, new Vector3(2, 0, 0));
            NpcBrain brain = NpcBrain.CreatePet(pet, controller);
            pet.StartFighting(enemy.Identity, 0);
            brain.SetCurrentTarget(enemy.Identity);
            Assert.AreEqual(PetMode.Guard, controller.Mode);

            owner.Position = new Vector3(PetAi.GuardRange + 10, 0, 0);

            Assert.IsFalse(PetAi.ShouldRejoinOwner(brain));
            Assert.IsFalse(PetAi.PickEnemy(brain));
            Assert.AreEqual(Identity.None, brain.CurrentTarget);
        }

        [TestMethod]
        public void HealOrderSurvivesAHealThatIsInReachButCannotStart()
        {
            StubItemBuilder items = HealItems();
            using var world = new PetWorld(items);
            Player owner = world.Player(1, new Vector3(0, 0, 0));
            Player friend = world.Player(4, new Vector3(5, 0, 0));
            friend.Stats.Set(CharacterStat.Health, 50);
            (NpcCharacter pet, PetController controller) = world.Pet(2, owner, PetTypes.Heal, new Vector3(2, 0, 0), items);
            pet.UploadedNanoIds.Add(HealNanoId);
            pet.Stats.Set(CharacterStat.MaxNanoEnergy, 100);
            pet.Stats.Set(CharacterStat.CurrentNano, 0);
            NpcBrain brain = NpcBrain.CreatePet(pet, controller);
            controller.Heal(friend.Identity);
            var tend = new PetTendNode(brain);

            Assert.AreEqual(HealOrderResult.Blocked, brain.TryHeal(friend));
            tend.Evaluate(0.1f);
            Assert.AreEqual(friend.Identity, controller.HealTarget);

            friend.Position = new Vector3(500, 0, 0);

            Assert.AreEqual(HealOrderResult.OutOfRange, brain.TryHeal(friend));
            tend.Evaluate(0.1f);
            Assert.AreEqual(Identity.None, controller.HealTarget);
        }

        static StubItemBuilder HealItems()
            => new StubItemBuilder().Add(TestNanos.Create(
                HealNanoId,
                nanoPointCost: 50,
                modifiers:
                [
                    new ItemSpell
                    {
                        FunctionType = (int)FunctionType.Hit,
                        Arguments = [(int)CharacterStat.Health, 30, 30],
                        Requirements = new System.Collections.Generic.List<ItemRequirement>()
                    }
                ]));

        sealed class PetWorld : IDisposable
        {
            readonly ServiceProvider _services;
            readonly DynelRegistry _registry = new();

            public Playfield Playfield { get; }

            public PetWorld(StubItemBuilder? items = null)
            {
                _services = new ServiceCollection()
                    .AddSingleton(_registry)
                    .AddSingleton(new PlayfieldLocality(4582, null))
                    .AddSingleton(new WorldSimulationAccess())
                    .AddSingleton<IItemBuilder>(items ?? new StubItemBuilder())
                    .BuildServiceProvider();
                Playfield = BarePlayfield.Create(4582, _services);
            }

            public Player Player(int id, Vector3 at)
            {
                Player player = TestWorld.CreatePlayer(id);
                player.Playfield = Playfield;
                player.Position = at;
                player.Stats.Set(CharacterStat.Level, 60);
                player.Stats.Set(CharacterStat.MaxHealth, 100);
                player.Stats.Set(CharacterStat.Health, 100);
                _registry.Register(player);
                return player;
            }

            public NpcCharacter Npc(int id, Vector3 at, StubItemBuilder? items = null)
            {
                var npc = new NpcCharacter(new Identity { Type = IdentityType.CanbeAffected, Instance = id }, items ?? new StubItemBuilder())
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

            public (NpcCharacter Pet, PetController Controller) Pet(int id, Player owner, int type, Vector3 at, StubItemBuilder? items = null)
            {
                NpcCharacter pet = Npc(id, at, items);
                var controller = new PetController(owner, type, "synthetic-pet", 60, expiresUtc: null);
                pet.BindPet(controller);
                return (pet, controller);
            }

            public void Dispose() => _services.Dispose();
        }
    }
}
