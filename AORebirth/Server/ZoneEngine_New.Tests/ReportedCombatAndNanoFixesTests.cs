namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using AORebirth.Enums;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages.N3Messages;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Helpers;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Nanos;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.WorldSimulation;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Player-reported combat and nano defects from the rubi-ka.org tracker, each driven the way the engine runs it:
/// #691 (a queued special on a mob that died left the client waiting), #539 (a calmed monster never regenerated),
/// #323 (a resisted cast finished with a result the client did not know), #625/#318 (no "executes ... within your
/// NCU" line for the player a nano landed on), #204 (a debuff landing mid-bar failed the cast) and #308 (a hostile
/// nano's end-of-run Self function acted on the caster instead of the holder).
/// </summary>
[TestClass]
public sealed class ReportedCombatAndNanoFixesTests
{
    const int CalmId = 31001;
    const int BuffId = 31002;
    const int GatedId = 31003;
    const int ParentId = 31004;
    const int ChildId = 31005;

    static readonly DateTime Start = new(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void AQueuedSpecialWhoseTargetIsGoneTellsTheClientTheAttackEnded()
    {
        using var world = new World();
        Player attacker = world.Player(1, CanFlags.Brawl);
        NpcCharacter target = world.Npc(2);
        Assert.IsTrue(SpecialAttacks.Queue(attacker, target, CharacterStat.Brawl));
        world.Session(attacker).Sent.Clear();

        world.Forget(target);
        attacker.TickQueuedSpecials();

        CharacterActionMessage ended = world.Session(attacker).Sent.OfType<CharacterActionMessage>()
            .Single(m => m.Action == CharacterActionType.StartingAttackFailed);
        Assert.AreEqual(2, ended.Parameter2, "the same close-out a refused attack start sends");
        Assert.AreEqual(attacker.Identity, ended.Identity);
    }

    [TestMethod]
    public void ACalmedMonsterIsOutOfCombatUnlessSomeoneIsFightingIt()
    {
        using var world = new World();
        Player player = world.Player(1, CanFlags.Brawl);
        NpcCharacter npc = world.Npc(2);
        npc.SetFightingTarget(player.Identity);
        Assert.IsTrue(npc.IsInCombat);

        NanoSpell calm = TestNanos.Create(CalmId, can: CanFlags.ApplyOnHostile,
            modifiers: [new ItemSpell { FunctionType = (int)FunctionType.Pacify, Arguments = [] }]);
        Assert.AreEqual(BuffApplyDecision.Apply, npc.TryApplyBuff(calm, player.Identity, Start, out _, out _));
        Assert.IsTrue(npc.IsPacified);

        Assert.IsFalse(npc.IsInCombat, "A calmed monster has no fight of its own, so its health regenerates.");

        player.SetFightingTarget(npc.Identity);
        Assert.IsTrue(npc.IsInCombat, "Being fought keeps it in combat, calm or not.");
    }

    [TestMethod]
    public void AResistedCastFinishesWithLiveResultThree()
    {
        using var world = new World();
        Player caster = world.Player(1);
        MethodInfo finish = typeof(NanoRuntime).GetMethod("AnnounceCastFinished", BindingFlags.Static | BindingFlags.NonPublic)!;

        finish.Invoke(null, [caster, BuffId, false]);
        finish.Invoke(null, [caster, BuffId, true]);

        List<CharacterActionMessage> finishes = world.Session(caster).Sent.OfType<CharacterActionMessage>()
            .Where(m => m.Action == CharacterActionType.FinishNanoCasting).ToList();
        Assert.AreEqual(2, finishes.Count);
        Assert.AreEqual(3, finishes[0].Parameter1, "resisted");
        Assert.AreEqual(1, finishes[1].Parameter1, "landed");
    }

    [TestMethod]
    public void ThePlayerANanoLandsOnIsToldWhoExecutedWhatWithinTheirNcu()
    {
        NanoSpell buff = TestNanos.Create(BuffId, can: CanFlags.ApplyOnFriendly | CanFlags.ApplyOnSelf, attackDelay: 100);
        using var world = new World(new StubItemBuilder().Add(buff));
        Player caster = world.Player(1);
        caster.Name = "Buffer";
        Player mate = world.Player(2, x: 3);
        Assert.IsTrue(caster.TryAddUploadedNano(BuffId));
        string line = ClientFeedback.FormatText(ClientFeedback.ExecutesWithinYourNcu, "Buffer", buff.Name);

        caster.BeginNanoCast(new PendingNanoCast(buff, mate.Identity, 0, 100, Start));
        NanoRuntime.Tick(caster, Start.AddSeconds(2));

        Assert.AreEqual(1, mate.Buffs.Count, "the cast landed");
        Assert.IsTrue(world.Session(mate).Sent.OfType<FormatFeedbackMessage>().Any(m => m.Message == line),
            "the recipient reads who executed what within their NCU");
        Assert.IsFalse(world.Session(caster).Sent.OfType<FormatFeedbackMessage>().Any(m => m.Message == line),
            "the caster keeps its own executed line");

        caster.BeginNanoCast(new PendingNanoCast(buff, caster.Identity, 0, 100, Start.AddSeconds(30)));
        NanoRuntime.Tick(caster, Start.AddSeconds(32));
        Assert.AreEqual(1, caster.Buffs.Count);
        Assert.IsFalse(world.Session(caster).Sent.OfType<FormatFeedbackMessage>().Any(m => m.Message.Contains("Buffer")),
            "a self cast gets no second line");
    }

    [TestMethod]
    public void ADebuffLandingWhileTheBarRunsDoesNotFailTheCast()
    {
        NanoSpell gated = NanoSpell.From(new ItemTemplate
        {
            Id = GatedId,
            Name = "Gated buff",
            Quality = 10,
            Stats = new Dictionary<CharacterStat, int>
            {
                [CharacterStat.TimeExist] = 6000,
                [CharacterStat.Level] = 10,
                [CharacterStat.AttackDelay] = 100,
                [CharacterStat.Can] = unchecked((int)(CanFlags.ApplyOnFriendly | CanFlags.ApplyOnSelf))
            },
            Actions =
            [
                new ItemAction
                {
                    ActionType = (int)ActionType.ToUse,
                    Requirements =
                    [
                        new ItemRequirement
                        {
                            StatNumber = (int)CharacterStat.NanoProgramming, Operator = (int)Operator.GreaterThan, Value = 100
                        }
                    ]
                }
            ],
            SpellList = new Dictionary<EventType, List<ItemSpell>>
            {
                [EventType.OnUse] = [TestNanos.Modify(CharacterStat.MaxHealth, 10)]
            }
        });
        using var world = new World(new StubItemBuilder().Add(gated));
        Player caster = world.Player(1);
        Assert.IsTrue(caster.TryAddUploadedNano(GatedId));

        caster.Stats.Set(CharacterStat.NanoProgramming, 50);
        Assert.IsFalse(NanoRuntime.CanStartCast(caster, gated, caster, Start), "the up-front gate still reads the requirement");
        caster.Stats.Set(CharacterStat.NanoProgramming, 150);
        Assert.IsTrue(NanoRuntime.CanStartCast(caster, gated, caster, Start));

        caster.BeginNanoCast(new PendingNanoCast(gated, caster.Identity, 0, 100, Start));
        // A skill drain lands while the bar runs.
        caster.Stats.Set(CharacterStat.NanoProgramming, 50);
        NanoRuntime.Tick(caster, Start.AddSeconds(2));

        Assert.AreEqual(1, caster.Buffs.Count, "the cast that started with its requirements met lands");
        Assert.IsFalse(world.Session(caster).Sent.OfType<CharacterActionMessage>()
            .Any(m => m.Action == CharacterActionType.InterruptNanoCasting));
        Assert.IsFalse(world.Session(caster).Sent.OfType<FeedbackMessage>()
            .Any(m => m.MessageId == ClientFeedback.CheckItemRequirements));
    }

    [TestMethod]
    public void AHostileNanoEndingCastsItsSelfFunctionOnTheHolderNotTheCaster()
    {
        NanoSpell child = TestNanos.Create(ChildId, durationCentiseconds: 30000,
            can: CanFlags.ApplyOnHostile | CanFlags.ApplyOnFriendly | CanFlags.ApplyOnSelf);
        NanoSpell parent = TestNanos.Create(ParentId, durationCentiseconds: 500, can: CanFlags.ApplyOnHostile,
            terminate:
            [
                new ItemSpell { FunctionType = (int)FunctionType.CastNano, Target = (int)ItemTarget.Self, Arguments = [ChildId] }
            ]);
        Assert.IsTrue(parent.IsHostile);
        using var world = new World(new StubItemBuilder().Add(child).Add(parent));
        Player victim = world.Player(1);
        NpcCharacter boss = world.Npc(2);

        Assert.AreEqual(BuffApplyDecision.Apply, victim.TryApplyBuff(parent, boss.Identity, Start, out _, out _));
        NanoRuntime.Tick(victim, Start.AddSeconds(6));

        Assert.IsTrue(victim.Buffs.Any(b => b.Id == ChildId), "the end-of-run field lands on the player it held");
        Assert.IsFalse(victim.Buffs.Any(b => b.Id == ParentId));
        Assert.IsFalse(boss.Buffs.Any(b => b.Id == ChildId), "not on the boss who cast it");
    }

    /// <summary>A bare playfield with a dynel registry, locality, item and inventory stubs: enough for casts and specials.</summary>
    sealed class World : IDisposable
    {
        readonly ServiceProvider _services;
        readonly DynelRegistry _registry = new();
        readonly Dictionary<int, RecordingZoneSession> _sessions = new();

        public Playfield Playfield { get; }

        public World(StubItemBuilder? items = null)
        {
            _services = new ServiceCollection()
                .AddSingleton(_registry)
                .AddSingleton(new PlayfieldLocality(4582, null))
                .AddSingleton(new WorldSimulationAccess())
                .AddSingleton<IItemBuilder>(items ?? new StubItemBuilder())
                .AddSingleton<IInventoryRepository>(new StubInventoryRepository())
                .BuildServiceProvider();
            Playfield = BarePlayfield.Create(4582, _services);
        }

        public Player Player(int id, CanFlags weaponCan = 0, double x = 0)
        {
            Player player = TestWorld.CreatePlayer(id);
            player.Playfield = Playfield;
            player.Position = new Vector3(x, 0, 0);
            player.Stats.Set(CharacterStat.Level, 60);
            player.Stats.Set(CharacterStat.Profession, 3);
            player.Stats.Set(CharacterStat.MaxHealth, 100);
            player.Stats.Set(CharacterStat.Health, 100);
            player.Stats.Set(CharacterStat.MaxNCU, 60);
            player.Stats.Set(CharacterStat.MaxNanoEnergy, 500);
            player.Stats.Set(CharacterStat.CurrentNano, 500);
            var session = new RecordingZoneSession { State = SessionState.InPlay };
            session.BindPlayer(player);
            player.Session = session;
            _sessions[id] = session;
            if (weaponCan != 0)
            {
                player.SetWeapon(WeaponSlot.MainHand, new CharacterWeapon
                {
                    Item = TestWorld.CreateItem(lowId: 2001, highId: 2001, name: "Starter Sword", can: weaponCan)
                });
            }

            _registry.Register(player);
            return player;
        }

        public NpcCharacter Npc(int id)
        {
            var npc = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new StubItemBuilder())
            {
                Playfield = Playfield,
                Attackable = true,
                Position = new Vector3(1, 0, 0)
            };
            npc.Stats.Set(CharacterStat.Level, 60);
            npc.Stats.Set(CharacterStat.MaxHealth, 100);
            npc.Stats.Set(CharacterStat.Health, 100);
            npc.Stats.Set(CharacterStat.MaxNCU, 60);
            _registry.Register(npc);
            return npc;
        }

        public RecordingZoneSession Session(Player player) => _sessions[player.Identity.Instance];

        public void Forget(NpcCharacter npc) => _registry.Unregister(npc.Identity);

        public void Dispose() => _services.Dispose();
    }
}
