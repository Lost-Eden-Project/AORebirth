namespace ZoneEngine_New.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;

    using AORebirth.Enums;

    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

    using ZoneEngine_New.Core.Characters;
    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Nanos;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Playfield.Locality;
    using ZoneEngine_New.Core.WorldSimulation;

    /// <summary>
    /// Bugs #302/#385/#390 (perk actions resisted by any mob with a higher check), #211 (nanos that grant an
    /// action, Keeper Wrath/Fury) and #259 (a perk's pulsing Hit, Notum Siphon, never ran).
    /// </summary>
    [TestClass]
    public sealed class PerkActionLandingTests
    {
        const int ActionHash = 0x54455354;
        const int ActionTemplateId = 900002;
        const int GrantTemplateId = 900001;
        const int GrantNanoId = 910001;

        [TestMethod]
        public void PerkActionLandsWhenAttackRatingDwarfsTheDefense()
        {
            Player player = TestWorld.CreatePlayer(1);
            NpcCharacter target = Target(2);
            player.Stats.Set(CharacterStat.MartialArts, 2000);
            target.Stats.Set(CharacterStat.NanoAC, 10);

            for (int i = 0; i < 25; i++)
                Assert.IsTrue(player.PerkActionLands(Use(player, target)));
        }

        [TestMethod]
        public void PerkActionIsResistedWithNoAttackRatingAtAll()
        {
            Player player = TestWorld.CreatePlayer(1);
            NpcCharacter target = Target(2);
            target.Stats.Set(CharacterStat.NanoAC, 5000);

            for (int i = 0; i < 25; i++)
                Assert.IsFalse(player.PerkActionLands(Use(player, target)));
        }

        [TestMethod]
        public void PerkActionBelowTheDefenseRatingStillLandsSometimes()
        {
            // The old hard AR >= DR threshold never let this land; the nano curve gives about 64%.
            Player player = TestWorld.CreatePlayer(1);
            NpcCharacter target = Target(2);
            player.Stats.Set(CharacterStat.MartialArts, 900);
            target.Stats.Set(CharacterStat.NanoAC, 1000);

            int landed = 0;
            const int trials = 400;
            for (int i = 0; i < trials; i++)
            {
                if (player.PerkActionLands(Use(player, target)))
                    landed++;
            }

            Assert.IsTrue(landed > 0, "never landed");
            Assert.IsTrue(landed < trials, "never resisted");
        }

        [TestMethod]
        public void SelfTargetedAndUncheckedActionsAlwaysLand()
        {
            Player player = TestWorld.CreatePlayer(1);
            NpcCharacter target = Target(2);
            target.Stats.Set(CharacterStat.NanoAC, 5000);

            Assert.IsTrue(player.PerkActionLands(Use(player, player)));
            Assert.IsTrue(player.PerkActionLands(new Player.PerkActionUse(
                ActionHash, new ItemTemplate { Id = ActionTemplateId }, ActionTemplateId, ActionTemplateId, 1, target)));
        }

        [TestMethod]
        public void RunningNanoGrantsItsActionUntilItEnds()
        {
            Player player = TestWorld.CreatePlayer(1);
            player.Stats.Set(CharacterStat.MaxNCU, 60);
            var session = new RecordingZoneSession { State = SessionState.InPlay };
            session.BindPlayer(player);
            player.Session = session;
            NanoSpell wrath = TestNanos.Create(GrantNanoId, modifiers: [AddAction()]);
            Assert.AreEqual(0, player.PerkActions.Count);

            player.TryApplyBuff(wrath, player.Identity, DateTime.UtcNow, out Buff? applied, out _);

            Assert.IsNotNull(applied);
            Assert.AreEqual(1, player.PerkActions.Count);
            Assert.AreEqual(new Player.PerkAction(ActionTemplateId, 0, ActionHash), player.PerkActions[0]);
            CharacterActionMessage added = session.Sent.OfType<CharacterActionMessage>()
                .Single(action => action.Action == CharacterActionType.AddPerkAction);
            Assert.AreEqual(ActionTemplateId, added.Target.Instance);
            Assert.AreEqual(ActionHash, added.Parameter2);

            player.RemoveBuffs(buff => buff.Id == GrantNanoId);

            Assert.AreEqual(0, player.PerkActions.Count);
            CharacterActionMessage removed = session.Sent.OfType<CharacterActionMessage>()
                .Single(action => action.Action == CharacterActionType.RemovePerkAction);
            Assert.AreEqual(ActionHash, removed.Parameter2);
        }

        [TestMethod]
        public void NanoGrantedActionIsNotListedTwiceWhenAPerkGrantsItToo()
        {
            var items = new StubItemBuilder().Add(new ItemTemplate
            {
                Id = GrantTemplateId,
                SpellList = new Dictionary<EventType, List<ItemSpell>> { [EventType.OnWear] = [AddAction()] }
            });
            using var world = new PerkWorld(items);
            Player player = world.Player;
            player.Stats.Set(CharacterStat.MaxNCU, 60);
            Assert.AreEqual(1, player.PerkActions.Count);

            player.TryApplyBuff(TestNanos.Create(GrantNanoId, modifiers: [AddAction()]), player.Identity, DateTime.UtcNow, out Buff? applied, out _);

            Assert.IsNotNull(applied);
            Assert.AreEqual(1, player.PerkActions.Count);
        }

        [TestMethod]
        public void PerkPulseHitRestoresNanoOnTheWearer()
        {
            var items = new StubItemBuilder().Add(new ItemTemplate
            {
                Id = GrantTemplateId,
                SpellList = new Dictionary<EventType, List<ItemSpell>>
                {
                    [EventType.OnWear] =
                    [
                        new ItemSpell
                        {
                            FunctionType = (int)FunctionType.Hit,
                            Target = (int)ItemTarget.Wearer,
                            Arguments = [(int)CharacterStat.CurrentNano, 50, 50],
                            TickCount = -1,
                            TickInterval = 1000,
                            Requirements = new List<ItemRequirement>()
                        }
                    ]
                }
            });
            using var world = new PerkWorld(items);
            Player player = world.Player;
            player.Stats.Set(CharacterStat.MaxNanoEnergy, 100);
            player.Stats.Set(CharacterStat.CurrentNano, 10);

            player.Tick(0.1);

            Assert.AreEqual(60, player.Stats.GetOrZero(CharacterStat.CurrentNano));

            // Its next pulse is 10 s away.
            player.Tick(0.1);
            Assert.AreEqual(60, player.Stats.GetOrZero(CharacterStat.CurrentNano));
        }

        static Player.PerkActionUse Use(Player player, Character target)
        {
            var template = new ItemTemplate
            {
                Id = ActionTemplateId,
                Attack = { [CharacterStat.MartialArts] = 100 },
                Defend = { [CharacterStat.NanoAC] = 100 }
            };
            return new Player.PerkActionUse(ActionHash, template, ActionTemplateId, ActionTemplateId, 1, target);
        }

        static NpcCharacter Target(int id)
            => new(new Identity { Type = IdentityType.CanbeAffected, Instance = id }, new StubItemBuilder())
            {
                Attackable = true
            };

        static ItemSpell AddAction()
            => new()
            {
                FunctionType = (int)FunctionType.AddAction,
                Arguments = [0, ActionHash, 0, ActionTemplateId],
                Requirements = new List<ItemRequirement>()
            };

        /// <summary>A player on a bare playfield with perk 1120 (item 900001) trained.</summary>
        sealed class PerkWorld : IDisposable
        {
            readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "AORebirth-perk-fix-test-" + Guid.NewGuid().ToString("N"));
            readonly ServiceProvider _services;
            readonly DynelRegistry _registry = new();

            public Playfield Playfield { get; }

            public Player Player { get; }

            public RecordingZoneSession Session { get; } = new() { State = SessionState.InPlay };

            public PerkWorld(StubItemBuilder items)
            {
                Directory.CreateDirectory(_dataRoot);
                File.WriteAllText(Path.Combine(_dataRoot, "Perks.json"),
                    "{\"PerkLines\":[{\"Perks\":[{\"Id\":1120,\"ItemId\":900001,\"Tier\":1}]}]}");
                _services = new ServiceCollection()
                    .AddSingleton(_registry)
                    .AddSingleton(new PlayfieldLocality(4582, null))
                    .AddSingleton(new WorldSimulationAccess())
                    .AddSingleton<IItemBuilder>(items)
                    .AddSingleton<IInventoryRepository>(new QuietInventory())
                    .BuildServiceProvider();
                Playfield = BarePlayfield.Create(4582, _services);
                Player = new Player(new Identity { Type = IdentityType.CanbeAffected, Instance = 1 }, new StubLogger(), items)
                {
                    Playfield = Playfield,
                    PerkCatalog = PerkCatalog.Load(_dataRoot),
                    PerkActionCatalog = PerkActionCatalog.Empty,
                    Session = Session
                };
                Player.Inventory.Apply(new CharacterHydrationResult(), 1, items);
                Session.BindPlayer(Player);
                _registry.Register(Player);
                Player.LoadTrainedPerks([1120]);
            }

            public void Dispose()
            {
                _services.Dispose();
                Directory.Delete(_dataRoot, recursive: true);
            }
        }

        sealed class QuietInventory : IInventoryRepository
        {
            public IReadOnlyList<ItemInstanceRecord> GetCarriedItems(int characterId) => [];

            public IReadOnlyList<ItemInstanceRecord> GetBankItems(int characterId) => [];

            public IReadOnlyList<ItemInstanceRecord> GetContainerItems(int containerInstanceId) => [];

            public int LeaseInstanceIdBlock(int count) => 1;

            public ItemInstanceRecord Insert(ItemInstanceRecord item) => item;

            public void UpdateLocation(int instanceId, int containerType, int containerInstance, int containerPlacement)
            {
            }

            public void UpdateLocations(IReadOnlyList<ItemLocationUpdate> locations)
            {
            }

            public void PersistNewAndUpdateLocations(
                IReadOnlyList<ItemInstanceRecord> inserts,
                IReadOnlyList<ItemLocationUpdate> updates)
            {
            }
        }
    }
}
