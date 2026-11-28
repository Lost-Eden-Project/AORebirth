namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

using AORebirth.Enums;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;
using LostEden.Protocol.Messages.N3Messages;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.MessageHandlers;
using ZoneEngine_New.Core.Movement;
using ZoneEngine_New.Core.Nanos;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;

using MovementAction = ZoneEngine_New.Core.Movement.MovementAction;
using Quaternion = AORebirth.Core.Vector.Quaternion;
using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Client moves a modified client could abuse (LE-08, LE-09). Swimming skips the height check, so a client may not
/// declare it on dry land: the server's own liquid detection puts a body that enters deep water into Swim. A position
/// or heading with a non-finite component is refused whole, and a heading that is not a unit rotation is ignored.
/// </summary>
[TestClass]
public sealed class MovementCheatGuardTests
{
    [TestMethod]
    public void SwitchToSwimOnDryLandIsRefused()
    {
        var rig = new Rig();

        rig.Move(MovementAction.SwitchToSwim);

        Assert.AreNotEqual(MovementState.Swim, rig.Player.Motor.State, "a client declaring swim on dry land is refused");
        Assert.AreEqual(MovementState.Run, rig.Player.Motor.State);
    }

    [TestMethod]
    public void SwitchToSwimInLiquidIsTakenAndEndsACast()
    {
        var rig = new Rig();
        rig.BeginCast();
        rig.EnterLiquid();

        rig.Move(MovementAction.SwitchToSwim);

        Assert.AreEqual(MovementState.Swim, rig.Player.Motor.State, "a body in water swims");
        Assert.IsFalse(rig.Player.IsCastingNano, "swimming ends the cast bar");
    }

    [TestMethod]
    [DataRow(float.NaN)]
    [DataRow(float.PositiveInfinity)]
    [DataRow(float.NegativeInfinity)]
    public void ANonFinitePositionIsRefusedWhole(float bad)
    {
        var rig = new Rig();
        rig.Player.Position = new Vector3(100, 10, 100);
        Vector3 before = rig.Player.Position;
        Quaternion heading = rig.Player.Rotation;

        rig.Move(MovementAction.Update, y: bad);
        rig.Move(MovementAction.ForwardStart, x: bad);

        Assert.AreEqual(before.x, rig.Player.Position.x, 0.0001, "a non-finite coordinate changes nothing");
        Assert.AreEqual(before.y, rig.Player.Position.y, 0.0001, "a non-finite coordinate changes nothing");
        Assert.AreEqual(before.z, rig.Player.Position.z, 0.0001, "a non-finite coordinate changes nothing");
        AssertRotation(heading, rig.Player.Rotation, "the heading is not applied either");
        Assert.AreEqual(MovementState.Run, rig.Player.Motor.State, "the action is not applied either");
    }

    [TestMethod]
    public void ANonFiniteHeadingIsRefusedAndADegenerateOneIgnored()
    {
        var rig = new Rig();
        var facing = new Quaternion(0, 0.70710677f, 0, 0.70710677f);
        rig.Player.Rotation = facing;

        rig.Move(MovementAction.Update, heading: new LostEden.Protocol.GameData.Quaternion { X = float.NaN, Y = 0, Z = 0, W = 1 });
        AssertRotation(facing, rig.Player.Rotation, "a non-finite heading is refused");

        rig.Move(MovementAction.Update, heading: new LostEden.Protocol.GameData.Quaternion { X = 0, Y = 0, Z = 0, W = 0 });
        AssertRotation(facing, rig.Player.Rotation, "a zero heading is ignored");

        rig.Move(MovementAction.Update, heading: new LostEden.Protocol.GameData.Quaternion { X = 0, Y = 0, Z = 0, W = 40 });
        AssertRotation(facing, rig.Player.Rotation, "a scaled heading is ignored");

        rig.Move(MovementAction.Update, heading: new LostEden.Protocol.GameData.Quaternion { X = 0, Y = 0, Z = 0, W = 1 });
        AssertRotation(new Quaternion(0, 0, 0, 1), rig.Player.Rotation, "a unit heading is taken");
    }

    static void AssertRotation(Quaternion expected, Quaternion actual, string because)
    {
        Assert.AreEqual(expected.x, actual.x, 0.0001, because);
        Assert.AreEqual(expected.y, actual.y, 0.0001, because);
        Assert.AreEqual(expected.z, actual.z, 0.0001, because);
        Assert.AreEqual(expected.w, actual.w, 0.0001, because);
    }

    sealed class Rig
    {
        internal readonly Session Session = new();
        internal readonly Player Player;
        readonly CharDCMoveMessageHandler _moves = new();

        internal Rig()
        {
            var items = new StubItemBuilder();
            Player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = 4300 }, new StubLogger(), items)
            {
                FirstName = "Mover"
            };
            Player.Inventory.Apply(new CharacterHydrationResult(), 4300, items);
            Player.Stats.Set(CharacterStat.Level, 60);
            Player.Stats.Set(CharacterStat.CurrentNano, 500);
            Player.EnterOnline(Session);
            Player.MarkClientInPlay(DateTime.UtcNow);
            var locality = new PlayfieldLocality(0x160001, null);
            locality.RegisterDynel(Player);
        }

        /// <summary>The server's body is in deep water, as its liquid detection reports after a step into it.</summary>
        internal void EnterLiquid()
        {
            object body = typeof(CharacterMotor).GetField("_sim", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Player.Motor)!;
            PropertyInfo? property = body.GetType().GetProperty("InLiquid");
            if (property != null)
                property.SetValue(body, true);
            else
                body.GetType().GetField("InLiquid")!.SetValue(body, true);
        }

        internal void BeginCast()
        {
            NanoSpell spell = NanoSpell.From(new ItemTemplate
            {
                Id = 7778,
                Name = "Nano 7778",
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

        internal void Move(MovementAction action, float? x = null, float? y = null, float? z = null,
            LostEden.Protocol.GameData.Quaternion? heading = null) => _moves.Handle(
            new CharDCMoveMessage
            {
                Identity = Player.Identity,
                MoveType = (LostEden.Protocol.GameData.MovementAction)(byte)action,
                Position = new LostEden.Protocol.GameData.Vector3
                {
                    X = x ?? (float)Player.Position.x, Y = y ?? (float)Player.Position.y, Z = z ?? (float)Player.Position.z
                },
                Heading = heading ?? new LostEden.Protocol.GameData.Quaternion
                {
                    X = (float)Player.Rotation.x, Y = (float)Player.Rotation.y, Z = (float)Player.Rotation.z, W = (float)Player.Rotation.w
                }
            },
            Session);
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
}
