namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

using AORebirth.Core.GameData;
using AORebirth.Enums;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;
using LostEden.Protocol.Messages.N3Messages;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.MessageHandlers;
using ZoneEngine_New.Core.Movement;
using ZoneEngine_New.Core.Nanos;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.Teams;

using MovementAction = ZoneEngine_New.Core.Movement.MovementAction;
using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// A cast in flight cannot carry on into a posture a cast cannot start in: sitting, sleeping, lounging, crawling or
/// swimming ends the cast bar (no nano lands from a sitting caster, no nano is spent), and StartSneak is refused while
/// the bar runs. Standing still and toggling walk/run keeps the cast.
/// </summary>
[TestClass]
public sealed class CastMovementSwitchTests
{
    [TestMethod]
    [DataRow(MovementAction.SwitchToSit)]
    [DataRow(MovementAction.SwitchToSleep)]
    [DataRow(MovementAction.SwitchToLounge)]
    [DataRow(MovementAction.SwitchToCrawl)]
    [DataRow(MovementAction.SwitchToSwim)]
    public void PostureSwitchMidCastInterruptsTheCast(MovementAction action)
    {
        var rig = new Rig();
        rig.BeginCast();
        int nanoBefore = rig.Player.Stats.GetOrZero(CharacterStat.CurrentNano);

        rig.Move(action);

        // Sit, crawl and swim are taken from standing (sit settles its mode on the next tick); sleep and lounge are
        // refused from standing by the movement rules, so the posture never comes and the cast keeps running.
        if (action is MovementAction.SwitchToSleep or MovementAction.SwitchToLounge)
        {
            Assert.AreEqual(MovementState.Run, rig.Player.Motor.State, action + " is refused from standing");
            Assert.IsTrue(rig.Player.IsCastingNano, "a switch the rules refuse leaves the cast running");
        }
        else
        {
            Assert.IsFalse(rig.Player.IsCastingNano, action + " mid-cast ends the cast bar");
        }
        Assert.AreEqual(nanoBefore, rig.Player.Stats.GetOrZero(CharacterStat.CurrentNano),
            "an interrupted cast spends no nano");
        Assert.IsTrue(rig.Player.Buffs.Count == 0, "no nano lands from the interrupted cast");
    }

    [TestMethod]
    [DataRow(MovementAction.SwitchToWalk)]
    [DataRow(MovementAction.SwitchToRun)]
    [DataRow(MovementAction.FullStop)]
    public void StandingStillSwitchesKeepTheCast(MovementAction action)
    {
        var rig = new Rig();
        rig.BeginCast();

        rig.Move(action);

        Assert.IsTrue(rig.Player.IsCastingNano, action + " while standing keeps the cast");
    }

    [TestMethod]
    public void StartSneakIsRefusedWhileCasting()
    {
        var rig = new Rig();
        rig.BeginCast();
        rig.Session.Sent.Clear();

        rig.StartSneak();

        Assert.AreNotEqual(MovementState.Sneak, rig.Player.Motor.State, "no sneak mid-cast");
        Assert.IsFalse(rig.Session.Sent.OfType<CharacterActionMessage>()
            .Any(a => a.Action == CharacterActionType.StartedSneaking), "the client is not told it started sneaking");
        Assert.IsTrue(rig.Player.IsCastingNano, "the refused sneak leaves the cast running");
    }

    [TestMethod]
    public void RawSwitchToSneakMoveMidCastDoesNotSneak()
    {
        var rig = new Rig();
        rig.BeginCast();

        rig.Move(MovementAction.SwitchToSneak);

        Assert.AreNotEqual(MovementState.Sneak, rig.Player.Motor.State, "a raw SwitchToSneak cannot sneak mid-cast");
    }

    [TestMethod]
    public void StartSneakWorksOnceTheCastEnds()
    {
        var rig = new Rig();
        rig.BeginCast();
        NanoRuntime.InterruptCast(rig.Player);

        rig.StartSneak();

        Assert.AreEqual(MovementState.Sneak, rig.Player.Motor.State);
    }

    sealed class Rig
    {
        internal readonly Session Session = new();
        internal readonly Player Player;
        readonly CharacterActionMessageHandler _actions;
        readonly CharDCMoveMessageHandler _moves = new();

        internal Rig()
        {
            Player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = 4200 }, new Logger(), new Items())
            {
                FirstName = "Caster"
            };
            Player.Inventory.Apply(new CharacterHydrationResult(), 4200, new Items());
            Player.Stats.Set(CharacterStat.Level, 60);
            Player.Stats.Set(CharacterStat.Concealment, 300);
            Player.Stats.Set(CharacterStat.CurrentNano, 500);
            Player.EnterOnline(Session);
            Player.MarkClientInPlay(DateTime.UtcNow);
            var locality = new PlayfieldLocality(0x160001, null);
            locality.RegisterDynel(Player);

            _actions = new CharacterActionMessageHandler(
                (InventoryActionService)RuntimeHelpers.GetUninitializedObject(typeof(InventoryActionService)),
                new TeamService(null, () => DateTime.UtcNow, (_, action) => action()));
        }

        internal void BeginCast()
        {
            NanoSpell spell = NanoSpell.From(new ItemTemplate
            {
                Id = 7777,
                Name = "Nano 7777",
                Quality = 1,
                Stats = new Dictionary<CharacterStat, int>
                {
                    [CharacterStat.TimeExist] = 6000,
                    [CharacterStat.Level] = 10,
                    [CharacterStat.AttackDelay] = 400,
                    [CharacterStat.Can] = unchecked((int)CanFlags.ApplyOnFriendly)
                },
                SpellList = new Dictionary<EventType, List<ItemSpell>>(),
                CanCancel = true
            });
            Player.BeginNanoCast(new PendingNanoCast(spell, Player.Identity, 50, 400, DateTime.UtcNow));
            Assert.IsTrue(Player.IsCastingNano);
        }

        internal void StartSneak() => _actions.Handle(
            new CharacterActionMessage
            {
                Identity = Player.Identity, Action = CharacterActionType.StartSneak, Target = Identity.None
            },
            Session);

        internal void Move(MovementAction action) => _moves.Handle(
            new CharDCMoveMessage
            {
                Identity = Player.Identity,
                MoveType = (LostEden.Protocol.GameData.MovementAction)(byte)action,
                Position = new LostEden.Protocol.GameData.Vector3
                {
                    X = (float)Player.Position.x, Y = (float)Player.Position.y, Z = (float)Player.Position.z
                },
                Heading = new LostEden.Protocol.GameData.Quaternion { X = 0, Y = 0, Z = 0, W = 1 }
            },
            Session);
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
        int _next = 51500000;

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

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };

        public bool TryFromInstanceRecord(Core.Data.ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }
}
