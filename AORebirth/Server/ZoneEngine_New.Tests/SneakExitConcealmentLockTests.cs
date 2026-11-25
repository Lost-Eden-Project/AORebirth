namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

using AORebirth.Enums;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;
using LostEden.Protocol.Messages.N3Messages;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.MessageHandlers;
using ZoneEngine_New.Core.Movement;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.Teams;

using MovementAction = ZoneEngine_New.Core.Movement.MovementAction;
using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Leaving sneak, however it ends, tells the sneaker's own client (StopSneaking), locks Concealment for
/// <see cref="Character.SneakExitLockSeconds"/> and shows that lock on the client (SpecialUsed). StartSneak is refused
/// while the lock runs, and a raw SwitchToSneak move cannot start sneak around those checks.
/// </summary>
[TestClass]
public sealed class SneakExitConcealmentLockTests
{
    const int Concealment = (int)CharacterStat.Concealment;

    [TestMethod]
    public void WalkToggleWhileSneakingTellsTheClientAndLocksConcealment()
    {
        var rig = new Rig();
        rig.StartSneak();
        Assert.AreEqual(MovementState.Sneak, rig.Player.Motor.State, "StartSneak puts the player in sneak");
        rig.Session.Sent.Clear();

        rig.Move(MovementAction.SwitchToWalk);

        Assert.AreEqual(MovementState.Walk, rig.Player.Motor.State, "the walk toggle leaves sneak");
        List<CharacterActionMessage> actions = rig.Session.Sent.OfType<CharacterActionMessage>().ToList();
        Assert.IsTrue(actions.Any(a => a.Action == CharacterActionType.StopSneaking && a.Identity == rig.Player.Identity),
            "the sneaker's own client is told sneak ended");
        CharacterActionMessage? used = actions.FirstOrDefault(a => a.Action == CharacterActionType.SpecialUsed);
        Assert.IsNotNull(used, "the client is shown the Concealment lock");
        Assert.AreEqual(Concealment, used!.Parameter1);
        Assert.AreEqual(Character.SneakExitLockSeconds, used.Parameter2);
        TimeSpan remaining = rig.Player.SkillLocks.Remaining(Concealment, DateTime.UtcNow);
        Assert.IsTrue(remaining > TimeSpan.FromSeconds(8) && remaining <= TimeSpan.FromSeconds(Character.SneakExitLockSeconds),
            "Concealment is locked for the sneak exit cooldown, was " + remaining);
        Assert.IsTrue(rig.Player.SkillLocks.IsDirty, "the lock is persisted, so a relog cannot clear it");
    }

    [TestMethod]
    public void EveryWayOutOfSneakLocksConcealment()
    {
        foreach (Action<Rig> leave in new Action<Rig>[]
                 {
                     r => r.StopSneakRequest(),
                     r => r.Move(MovementAction.SwitchToRun),
                     r => r.Move(MovementAction.LeaveSneak),
                     r => r.Player.BreakSneak()
                 })
        {
            var rig = new Rig();
            rig.StartSneak();
            Assert.AreEqual(MovementState.Sneak, rig.Player.Motor.State);

            leave(rig);

            Assert.AreNotEqual(MovementState.Sneak, rig.Player.Motor.State);
            Assert.IsTrue(rig.Player.SkillLocks.IsLocked(Concealment, DateTime.UtcNow), "Concealment locks on leaving sneak");
            Assert.IsTrue(rig.Session.Sent.OfType<CharacterActionMessage>()
                .Any(a => a.Action == CharacterActionType.StopSneaking), "the client is told sneak ended");
        }
    }

    [TestMethod]
    public void StartSneakIsRefusedWhileConcealmentIsLocked()
    {
        var rig = new Rig();
        rig.StartSneak();
        rig.Move(MovementAction.SwitchToWalk);
        rig.Session.Sent.Clear();

        rig.StartSneak();

        Assert.AreEqual(MovementState.Walk, rig.Player.Motor.State, "no new sneak while Concealment is locked");
        Assert.IsFalse(rig.Session.Sent.OfType<CharacterActionMessage>()
            .Any(a => a.Action == CharacterActionType.StartedSneaking), "the client is not told it started sneaking");
        ChatTextMessage? text = rig.Session.Sent.OfType<ChatTextMessage>().LastOrDefault();
        Assert.IsNotNull(text, "the player is told the skill is locked");
        StringAssert.StartsWith(text!.Text, "Unable to perform action, Concealment skill is locked, able in 00:00:");
    }

    [TestMethod]
    public void StartSneakWorksAgainOnceTheLockRunsOut()
    {
        var rig = new Rig();
        rig.Player.SkillLocks.Lock(Concealment, Character.SneakExitLockSeconds,
            DateTime.UtcNow.AddSeconds(-(Character.SneakExitLockSeconds + 1)));

        rig.StartSneak();

        Assert.AreEqual(MovementState.Sneak, rig.Player.Motor.State);
        Assert.IsTrue(rig.Session.Sent.OfType<CharacterActionMessage>()
            .Any(a => a.Action == CharacterActionType.StartedSneaking));
    }

    [TestMethod]
    public void RawSwitchToSneakMoveCannotBypassTheLock()
    {
        var rig = new Rig();
        rig.StartSneak();
        rig.Move(MovementAction.SwitchToWalk);
        rig.Session.Sent.Clear();

        rig.Move(MovementAction.SwitchToSneak);

        Assert.AreEqual(MovementState.Walk, rig.Player.Motor.State, "a SwitchToSneak move does not start sneak");
        Assert.IsFalse(rig.Session.Sent.OfType<CharDCMoveMessage>().Any(), "the refused move is not announced");
    }

    [TestMethod]
    public void RawSwitchToSneakMoveCannotBypassTheFightCheck()
    {
        var rig = new Rig();

        rig.Move(MovementAction.SwitchToSneak);

        Assert.AreEqual(MovementState.Run, rig.Player.Motor.State, "sneak starts only through StartSneak");
    }

    [TestMethod]
    public void SwitchToSneakEchoWhileAlreadySneakingIsAccepted()
    {
        var rig = new Rig();
        rig.StartSneak();

        rig.Move(MovementAction.SwitchToSneak);

        Assert.AreEqual(MovementState.Sneak, rig.Player.Motor.State, "the client's own switch after StartedSneaking stays");
        Assert.IsFalse(rig.Player.SkillLocks.IsLocked(Concealment, DateTime.UtcNow), "staying in sneak locks nothing");
    }

    sealed class Rig
    {
        internal readonly Session Session = new();
        internal readonly Player Player;
        readonly CharacterActionMessageHandler _actions;
        readonly CharDCMoveMessageHandler _moves = new();

        internal Rig()
        {
            Player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = 4100 }, new Logger(), new Items())
            {
                FirstName = "Sneaker"
            };
            Player.Inventory.Apply(new CharacterHydrationResult(), 4100, new Items());
            Player.Stats.Set(CharacterStat.Level, 60);
            Player.Stats.Set(CharacterStat.Concealment, 300);
            Player.EnterOnline(Session);
            Player.MarkClientInPlay(DateTime.UtcNow);
            var locality = new PlayfieldLocality(0x160001, null);
            locality.RegisterDynel(Player);
            Assert.IsNotNull(Player.Cell, "the player stands in a cell, so announcements reach its client");

            _actions = new CharacterActionMessageHandler(
                (InventoryActionService)RuntimeHelpers.GetUninitializedObject(typeof(InventoryActionService)),
                new TeamService(null, () => DateTime.UtcNow, (_, action) => action()));
        }

        internal void StartSneak() => Action(CharacterActionType.StartSneak);

        internal void StopSneakRequest() => Action(CharacterActionType.StopSneaking);

        void Action(CharacterActionType action) => _actions.Handle(
            new CharacterActionMessage { Identity = Player.Identity, Action = action, Target = Identity.None }, Session);

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
        int _next = 51400000;

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
