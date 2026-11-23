namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using AORebirth.Core.GameData;
using AORebirth.Enums;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;
using LostEden.Protocol.Messages.N3Messages;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Bug #652: healing a teammate printed nothing in the healer's combat window ("You gave health"), and the healed one
/// saw no "healed you" line. A player's heal or nano given to another character now sends the healer CharacterAction
/// 0x82 / 0x8B and a healed player 0x81 / 0x8A, each only to that player, with the amount actually gained.
/// </summary>
[TestClass]
public sealed class HealFeedbackTests
{
    const int Healer = 6521;
    const int Healed = 6522;

    [TestMethod]
    public void AHealOnATeammateTellsTheHealerAndTheHealedTheAmountGained()
    {
        (Player healer, Player healed) = Pair();

        Assert.IsTrue(Cast(Heal(150), healed, healer));

        Assert.AreEqual(650, healed.Stats.GetOrZero(CharacterStat.Health));
        AssertOnly(healer, CharacterActionType.Healed, healer, healed, 150);
        AssertOnly(healed, CharacterActionType.HealedBy, healed, healer, 150);
        Assert.IsTrue(Sent(healed).OfType<HealthDamageMessage>().Any(), "HealthDamage still goes out as before");
    }

    [TestMethod]
    public void TheAmountIsWhatTheHealActuallyRestoredNotItsRoll()
    {
        (Player healer, Player healed) = Pair(healedHealth: 960);

        Cast(Heal(150), healed, healer);

        Assert.AreEqual(1000, healed.Stats.GetOrZero(CharacterStat.Health));
        AssertOnly(healer, CharacterActionType.Healed, healer, healed, 40);
        AssertOnly(healed, CharacterActionType.HealedBy, healed, healer, 40);
    }

    [TestMethod]
    public void AHealOnSomeoneAtFullHealthSendsNoLine()
    {
        (Player healer, Player healed) = Pair(healedHealth: 1000);

        Cast(Heal(150), healed, healer);

        Assert.IsFalse(Actions(healer).Any());
        Assert.IsFalse(Actions(healed).Any());
    }

    [TestMethod]
    public void ASelfHealSendsNoneOfTheGiveLines()
    {
        (Player healer, _) = Pair(healerHealth: 300);

        Cast(Heal(150), healer, healer);

        Assert.AreEqual(450, healer.Stats.GetOrZero(CharacterStat.Health));
        Assert.IsFalse(Actions(healer).Any(), "the self-heal's own line comes from HealthDamage");
    }

    [TestMethod]
    public void NanoGivenToATeammateTellsTheGiverAndTheReceiver()
    {
        (Player giver, Player receiver) = Pair(receiverNano: 900);

        Cast(Nano(250), receiver, giver);

        Assert.AreEqual(1000, receiver.Stats.GetOrZero(CharacterStat.CurrentNano));
        AssertOnly(giver, CharacterActionType.NanoIncreased, giver, receiver, 100);
        AssertOnly(receiver, CharacterActionType.NanoIncreasedBy, receiver, giver, 100);
    }

    [TestMethod]
    public void NanoOnSomeoneAlreadyFullSendsNoLine()
    {
        (Player giver, Player receiver) = Pair(receiverNano: 1000);

        Cast(Nano(250), receiver, giver);

        Assert.IsFalse(Actions(giver).Any());
        Assert.IsFalse(Actions(receiver).Any());
    }

    [TestMethod]
    public void DamageAndDrainsSendNoGiveLines()
    {
        (Player attacker, Player victim) = Pair(healerHealth: 500, healerNano: 100);

        Cast(Heal(-100), victim, attacker);
        Cast(NanoDrain(), victim, attacker);

        Assert.AreEqual(110, attacker.Stats.GetOrZero(CharacterStat.CurrentNano), "the drain returned its share");
        Assert.IsFalse(Actions(attacker).Any(), "a drain's own return is no gift");
        Assert.IsFalse(Actions(victim).Any());
    }

    static void AssertOnly(Player owner, CharacterActionType action, Player identity, Player target, int amount)
    {
        CharacterActionMessage[] sent = Actions(owner).ToArray();
        Assert.AreEqual(1, sent.Length, "exactly one give line for " + owner.FirstName);
        CharacterActionMessage message = sent[0];
        Assert.AreEqual(action, message.Action);
        Assert.AreEqual(identity.Identity, message.Identity, "the message's character is the receiving client's own");
        Assert.AreEqual(target.Identity, message.Target, "Target is the other character");
        Assert.AreEqual(0, message.Parameter1);
        Assert.AreEqual(amount, message.Parameter2);
    }

    static IEnumerable<CharacterActionMessage> Actions(Player player)
        => Sent(player).OfType<CharacterActionMessage>()
            .Where(message => message.Action is CharacterActionType.HealedBy or CharacterActionType.Healed
                or CharacterActionType.NanoIncreasedBy or CharacterActionType.NanoIncreased);

    static List<MessageBody> Sent(Player player) => ((Session)player.Session!).Sent;

    static bool Cast(ItemTemplate template, Character target, Character source)
        => template.ExecuteSpells(EventType.OnUse, target, NoInventoryRepository(), new Items(), source: source);

    static (Player Healer, Player Healed) Pair(int healerHealth = 1000, int healedHealth = 500, int healerNano = 1000,
        int receiverNano = 1000)
    {
        Player healer = NewPlayer(Healer, healerHealth, healerNano);
        Player healed = NewPlayer(Healed, healedHealth, receiverNano);
        return (healer, healed);
    }

    static Player NewPlayer(int id, int health, int nano)
    {
        var items = new Items();
        var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new Logger(), items) { FirstName = "Heal" + id };
        player.Inventory.Apply(new CharacterHydrationResult(), id, items);
        player.Stats.Set(CharacterStat.MaxHealth, 1000, StatDetail.Base);
        player.Stats.Set(CharacterStat.Health, health, StatDetail.Base);
        player.Stats.Set(CharacterStat.MaxNanoEnergy, 1000, StatDetail.Base);
        player.Stats.Set(CharacterStat.CurrentNano, nano, StatDetail.Base);
        player.EnterOnline(new Session());
        return player;
    }

    /// <summary>A flat Hit on Health: [Health, amount, amount, 0].</summary>
    static ItemTemplate Heal(int amount) => Template(FunctionType.Hit, (int)CharacterStat.Health, amount, amount, 0);

    /// <summary>A flat Hit on CurrentNano: [CurrentNano, amount, amount, 0].</summary>
    static ItemTemplate Nano(int amount) => Template(FunctionType.Hit, (int)CharacterStat.CurrentNano, amount, amount, 0);

    /// <summary>Trader Accumulator's proc 210941: takes 20 nano and gives the source half of it.</summary>
    static ItemTemplate NanoDrain() => Template(FunctionType.DrainHit, (int)CharacterStat.CurrentNano, -20, -20, 0, 50);

    static ItemTemplate Template(FunctionType function, params int[] arguments) => new()
    {
        Id = 290000,
        SpellList = new Dictionary<EventType, List<ItemSpell>>
        {
            [EventType.OnUse] =
            [
                new ItemSpell
                {
                    FunctionType = (int)function,
                    Target = (int)ItemTarget.Target,
                    Arguments = arguments.Cast<object>().ToList(),
                    Requirements = []
                }
            ]
        }
    };

    sealed class Session : IZoneSession
    {
        internal readonly List<MessageBody> Sent = [];
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public bool TransferToPlayfield(Playfield destination, Vector3 landing) => false;
        public void Send(byte[] packet) { }
        public void Send(MessageBody body) => Sent.Add(body);
        public void Send(MessageBody body, int sender, int receiver) => Send(body);
        public void SendInitiateCompression() { }
        public void Close() => State = SessionState.Closed;
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
        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null) => throw new NotSupportedException();
        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
            => throw new NotSupportedException();
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
