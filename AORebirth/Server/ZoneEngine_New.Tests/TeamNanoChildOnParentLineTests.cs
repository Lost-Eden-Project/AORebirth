namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using AORebirth.Enums;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;
using LostEden.Protocol.Messages.N3Messages;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Helpers;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Nanos;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Lost Eden 2026-11-25: a Trader casting Refresh Outfit (Team) 223241 saw "NCU error: Better nano program already
/// running." on every tick and never got nano back. The parent (line 588, order 11, 60 s) team-casts its nano-heal
/// 216580 (line 588, order 10, 8 s) on the land and every 8 s. It runs on the caster alone, so on the caster the child
/// met its own, stronger parent and was refused. A buff never blocks, and is never replaced by, the child it launches.
/// </summary>
[TestClass]
public sealed class TeamNanoChildOnParentLineTests
{
    const int ParentId = 223241;
    const int ChildId = 216580;
    const int Line = 588;

    static readonly Identity Caster = new() { Type = IdentityType.SimpleChar, Instance = 451 };

    [TestMethod]
    public void ABuffNeitherBlocksNorLeavesForTheChildNanoItLaunchesOnItsOwnLine()
    {
        Buff parent = Buff.Create(Parent(), Caster, nanoInstance: 1, DateTime.UtcNow);

        BuffApplyDecision decision = BuffApplyRules.Evaluate(Child(), [parent], maxNcu: 60, out IReadOnlyList<Buff> replaced);

        Assert.AreEqual(BuffApplyDecision.Apply, decision);
        Assert.AreEqual(0, replaced.Count);
    }

    [TestMethod]
    public void AStrongerBuffThatLaunchesSomeOtherNanoStillBlocksAWeakerOneOnItsLine()
    {
        Buff other = Buff.Create(
            Nano(223300, order: 11, flags: 65538, modifiers: [TeamCast(216581, ticks: 7)]),
            Caster,
            nanoInstance: 1,
            DateTime.UtcNow);

        BuffApplyDecision decision = BuffApplyRules.Evaluate(Child(), [other], maxNcu: 60, out _);

        Assert.AreEqual(BuffApplyDecision.RefusedStrainStronger, decision);
    }

    [TestMethod]
    public void TheCasterGetsTheTeamChildOnTheLandAndOnEveryTickWithoutAnNcuError()
    {
        var items = new Items(Parent(), Child());
        var inventory = new Inventory();
        var session = new Session();
        Player caster = NewPlayer(session, items);
        DateTime start = DateTime.UtcNow;

        Assert.IsTrue(NanoRuntime.TryApplyImmediate(caster, caster, ParentId, items, inventory, start));
        Buff parent = Require(caster, ParentId);
        Buff first = Require(caster, ChildId);

        // The 8 s tick, run the way NanoRuntime.Tick runs it from the parent's NCU entry.
        ItemSpell tick = parent.SpellList[EventType.OnUse].Single(spell => spell.TickCount == 7);
        parent.ExecutePeriodicSpell(caster, caster, tick, inventory, items);

        Assert.AreSame(parent, Require(caster, ParentId), "The child must not replace its parent.");
        Assert.AreNotSame(first, Require(caster, ChildId), "The tick must land the child on the caster again.");

        int ncuError = ClientFeedback.Create(caster.Identity, "Feedback_NCUErrorBetterProgramRunning").MessageId;
        Assert.IsFalse(
            session.Sent.OfType<FeedbackMessage>().Any(message => message.MessageId == ncuError),
            "No \"Better nano program already running\" for the caster's own child.");
    }

    static Buff Require(Character character, int nanoId)
    {
        Assert.IsTrue(character.TryGetBuff(nanoId, out Buff? buff), "Nano " + nanoId + " must run on the character.");
        return buff!;
    }

    /// <summary>Refresh Outfit (Team) 223241: flags 65538, line 588, order 11, 60 s, NCU 1; team-casts 216580 on the land and 7x every 8 s.</summary>
    static NanoSpell Parent()
        => Nano(ParentId, order: 11, flags: 65538, duration: 6000,
            modifiers: [TeamCast(ChildId, ticks: 1), TeamCast(ChildId, ticks: 7)]);

    /// <summary>Refresh Outfit 216580: flags 606466 (IsBuff, NoRemoveNoNCUFriendly, NotRemovable, NoTimerNotify), line 588, order 10, 8 s, NCU 1.</summary>
    static NanoSpell Child()
        => Nano(ChildId, order: 10, flags: 606466, duration: 800,
            modifiers:
            [
                new ItemSpell
                {
                    FunctionType = (int)FunctionType.Hit,
                    Target = (int)ItemTarget.Target,
                    Arguments = [(int)CharacterStat.CurrentNano, 12, 12]
                }
            ]);

    static NanoSpell Nano(int id, int order, int flags, int duration = 6000, IEnumerable<ItemSpell>? modifiers = null)
        => NanoSpell.From(new ItemTemplate
        {
            Id = id,
            Name = "Nano " + id,
            Quality = 1,
            Flags = flags,
            Stats = new Dictionary<CharacterStat, int>
            {
                [CharacterStat.TimeExist] = duration,
                [CharacterStat.Level] = 1,
                [CharacterStat.MetaType] = Line,
                [CharacterStat.StackingOrder] = order,
                [CharacterStat.Can] = unchecked((int)CanFlags.ApplyOnFriendly)
            },
            SpellList = new Dictionary<EventType, List<ItemSpell>>
            {
                [EventType.OnUse] = modifiers == null ? [] : [.. modifiers]
            },
            CanCancel = true
        });

    static ItemSpell TeamCast(int nanoId, int ticks)
        => new()
        {
            FunctionType = (int)FunctionType.TeamCastNano,
            Target = (int)ItemTarget.Wearer,
            Arguments = [nanoId],
            TickCount = ticks,
            TickInterval = ticks > 1 ? 800u : 0u
        };

    static Player NewPlayer(Session session, Items items)
    {
        var player = new Player(Caster, new Logger(), items) { FirstName = "Ragudd" };
        player.Inventory.Apply(new CharacterHydrationResult(), Caster.Instance, items);
        player.Stats.Set(CharacterStat.Level, 54);
        player.Stats.Set(CharacterStat.MaxNCU, 116);
        player.Stats.Set(CharacterStat.MaxNanoEnergy, 1394);
        player.Stats.Set(CharacterStat.CurrentNano, 1000);
        player.EnterOnline(session);
        player.MarkClientInPlay(DateTime.UtcNow);
        new PlayfieldLocality(0x160001, null).RegisterDynel(player);
        return player;
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

    sealed class Session : IZoneSession
    {
        internal readonly ConcurrentQueue<MessageBody> Sent = new();
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public bool TransferToPlayfield(Playfield destination, Vector3 landing) => false;
        public void Send(byte[] packet) { }
        public void Send(MessageBody body) => Sent.Enqueue(body);
        public void Send(MessageBody body, int sender, int receiver) => Sent.Enqueue(body);
        public void SendInitiateCompression() { }
        public void Close() => State = SessionState.Closed;
    }

    sealed class Items : IItemBuilder
    {
        readonly Dictionary<int, ItemTemplate> _templates;
        int _next = 51500000;

        internal Items(params ItemTemplate[] templates)
            => _templates = templates.ToDictionary(template => template.Id);

        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null) => new()
            {
                LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount,
                InstanceId = instanceId, Identity = identity ?? Identity.None, Definition = CreateTemplate(lowId, highId, quality)
            };

        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
        {
            Item item = Create(lowId, highId, quality, source, stackCount);
            item.AssignInstanceId(Interlocked.Increment(ref _next));
            return item;
        }

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality)
            => _templates.TryGetValue(lowId, out ItemTemplate? template) ? template : new ItemTemplate { Id = lowId, Quality = quality };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }

    sealed class Inventory : IInventoryRepository
    {
        public IReadOnlyList<ItemInstanceRecord> GetCarriedItems(int characterId) => [];
        public IReadOnlyList<ItemInstanceRecord> GetBankItems(int characterId) => [];
        public IReadOnlyList<ItemInstanceRecord> GetContainerItems(int containerInstanceId) => [];
        public int LeaseInstanceIdBlock(int count) => 1;
        public ItemInstanceRecord Insert(ItemInstanceRecord item) => item;
        public void UpdateLocation(int instanceId, int containerType, int containerInstance, int containerPlacement) { }
        public void UpdateLocations(IReadOnlyList<ItemLocationUpdate> locations) { }
        public void PersistNewAndUpdateLocations(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates) { }
    }
}
