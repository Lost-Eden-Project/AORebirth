namespace ZoneEngine_New.Tests
{
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;

    using AORebirth.Enums;

    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Helpers;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Playfield.Locality;
    using ZoneEngine_New.Core.WorldSimulation;

    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// Bug #80: a Brawl with a weapon that cannot brawl was queued and then dropped at fire time without a word.
    /// It is refused up front with "Action is not available." and starts no fight.
    /// </summary>
    [TestClass]
    public sealed class SpecialAttackAvailabilityTests
    {
        [TestMethod]
        public void SpecialNoWieldedWeaponCanPerformIsRefusedWithActionNotAvailable()
        {
            using var world = new ArenaWorld();
            Player attacker = world.Attacker(CanFlags.FastAttack);
            NpcCharacter target = world.Target();

            Assert.IsFalse(SpecialAttacks.Queue(attacker, target, CharacterStat.Brawl));

            FeedbackMessage feedback = world.Session.Sent.OfType<FeedbackMessage>().Single();
            Assert.AreEqual(ClientFeedback.ActionNotAvailable, feedback.MessageId);
            Assert.AreEqual(ClientFeedback.CategoryId, feedback.CategoryId);
            Assert.AreEqual(0, attacker.FightingTarget.Instance);
        }

        [TestMethod]
        public void SpecialTheWieldedWeaponCanPerformIsQueued()
        {
            using var world = new ArenaWorld();
            Player attacker = world.Attacker(CanFlags.Brawl);
            NpcCharacter target = world.Target();

            Assert.IsTrue(SpecialAttacks.Queue(attacker, target, CharacterStat.Brawl));

            Assert.AreEqual(0, world.Session.Sent.OfType<FeedbackMessage>().Count());
            Assert.AreEqual(target.Identity, attacker.FightingTarget);
        }

        sealed class ArenaWorld : System.IDisposable
        {
            readonly ServiceProvider _services;
            readonly DynelRegistry _registry = new();

            public Playfield Playfield { get; }

            public RecordingZoneSession Session { get; } = new() { State = SessionState.InPlay };

            public ArenaWorld()
            {
                _services = new ServiceCollection()
                    .AddSingleton(_registry)
                    .AddSingleton(new PlayfieldLocality(4582, null))
                    .AddSingleton(new WorldSimulationAccess())
                    .BuildServiceProvider();
                Playfield = BarePlayfield.Create(4582, _services);
            }

            public Player Attacker(CanFlags weaponCan)
            {
                Player player = TestWorld.CreatePlayer(1);
                player.Playfield = Playfield;
                player.Position = new Vector3(0, 0, 0);
                player.Stats.Set(CharacterStat.Level, 20);
                player.Stats.Set(CharacterStat.MaxHealth, 100);
                player.Stats.Set(CharacterStat.Health, 100);
                Session.BindPlayer(player);
                player.Session = Session;
                player.SetWeapon(WeaponSlot.MainHand, new CharacterWeapon
                {
                    Item = TestWorld.CreateItem(lowId: 2001, highId: 2001, name: "Starter Sword", can: weaponCan)
                });
                _registry.Register(player);
                return player;
            }

            public NpcCharacter Target()
            {
                var npc = new NpcCharacter(
                    new Identity { Type = IdentityType.CanbeAffected, Instance = 2 },
                    new StubItemBuilder())
                {
                    Playfield = Playfield,
                    Attackable = true,
                    Position = new Vector3(0, 0, 0)
                };
                npc.Stats.Set(CharacterStat.Level, 20);
                npc.Stats.Set(CharacterStat.MaxHealth, 100);
                npc.Stats.Set(CharacterStat.Health, 100);
                _registry.Register(npc);
                return npc;
            }

            public void Dispose() => _services.Dispose();
        }
    }
}
