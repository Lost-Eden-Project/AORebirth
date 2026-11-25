namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.Net;

using AORebirth.World.Contracts.V1;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using WorldEngine;
using WorldEngine.State;
using WorldEngine.Teams;

/// <summary>
/// World mode (the live server's team path) of <see cref="TeamSizeCapTests"/>: seven players stood in one ordinary team
/// after LFT invites. World holds an ordinary team to six; only a raid grows past that, six to a raid group.
/// </summary>
[TestClass]
public sealed class WorldTeamSizeCapTests
{
    long _now = 1_000_000;

    [TestMethod]
    public void ASixthMemberClosesTheTeamToFurtherInvites()
    {
        var w = new World(this);
        for (int id = 2; id <= 6; id++) w.Join(1, id);

        TeamCommandReply invite = w.Invite(1, 7);

        Assert.IsFalse(invite.Ok);
        Assert.AreEqual("Your team is full.", invite.Text);
        Assert.AreEqual("No pending team invite.", w.Accept(7, 1).Text, "The seventh was never invited.");
    }

    [TestMethod]
    public void InvitesSentWhileTheTeamHadRoomCannotAllBeAccepted()
    {
        var w = new World(this);
        for (int id = 2; id <= 7; id++) Assert.IsTrue(w.Invite(1, id).Ok);
        for (int id = 2; id <= 6; id++) Assert.IsTrue(w.Accept(id, 1).Ok);

        TeamCommandReply seventh = w.Accept(7, 1);

        Assert.IsFalse(seventh.Ok);
        Assert.AreEqual("The team is full.", seventh.Text);
        Assert.AreEqual("No pending team invite.", w.Accept(7, 1).Text, "The refused invite is spent.");
        // The seventh is in no team: someone else can still team with them.
        Assert.IsTrue(w.Invite(8, 7).Ok);
        Assert.IsTrue(w.Accept(7, 8).Ok);
    }

    [TestMethod]
    public void ARaidGrowsPastSixAndItsMovesCannotOverfillAGroup()
    {
        var w = new World(this);
        for (int id = 2; id <= 6; id++) w.Join(1, id);
        Assert.IsTrue(w.Command(1, TeamOp.TeamConvertRaid).Ok);
        w.Join(1, 7);
        w.Join(1, 8);

        // Group 0 holds six (1-6); 7 and 8 went to group 1.
        TeamCommandReply full = w.Command(1, TeamOp.TeamMoveRaidMember, target: 7, raidTeam: 0);
        Assert.IsFalse(full.Ok);
        Assert.AreEqual("That raid team is full.", full.Text);
        Assert.IsFalse(w.Command(1, TeamOp.TeamMoveRaidMember, target: 7, raidTeam: 40).Ok);
        Assert.IsFalse(w.Command(1, TeamOp.TeamMoveRaidMember, target: 7, raidTeam: -1).Ok);
        Assert.IsTrue(w.Command(1, TeamOp.TeamMoveRaidMember, target: 7, raidTeam: 1).Ok, "Staying in its own group is no overfill.");
        Assert.IsTrue(w.Command(1, TeamOp.TeamMoveRaidMember, target: 2, raidTeam: 1).Ok);
        Assert.IsTrue(w.Command(1, TeamOp.TeamMoveRaidMember, target: 7, raidTeam: 0).Ok, "Group 0 has room once member 2 left it.");
    }

    sealed class World
    {
        readonly WorldTeamSizeCapTests _clock;
        readonly WorldState _state;
        readonly ServiceInstance _zone;
        readonly ServiceInstance _chat;
        readonly TeamRegistry _teams;

        public World(WorldTeamSizeCapTests clock)
        {
            _clock = clock;
            var options = new WorldOptions
            {
                ListenAddress = IPAddress.Loopback,
                Port = 7600,
                StatusPort = 0,
                LeaseTtl = TimeSpan.FromSeconds(60),
                LeaseGrace = TimeSpan.FromSeconds(5),
                Heartbeat = TimeSpan.FromSeconds(1),
                HandoffLifetime = TimeSpan.FromSeconds(30),
                Token = string.Empty
            };
            _state = new WorldState(options, NullLogger<WorldState>.Instance, () => _clock._now);
            _chat = Register("chat", ServiceRole.RoleChat, null);
            _zone = Register("zone-a", ServiceRole.RoleZone, new Endpoint { Address = "127.0.0.1", Port = 7501 });
            // Past World's startup recovery, both still heartbeating.
            _clock._now += (long)options.RecoveryWindow.TotalMilliseconds + 1;
            _state.Heartbeat(_chat, new Heartbeat { Ready = true });
            _state.Heartbeat(_zone, new Heartbeat { Ready = true });
            _teams = new TeamRegistry(_state, NullLogger<TeamRegistry>.Instance, () => _clock._now);
            for (int id = 1; id <= 8; id++) LogIn(id);
        }

        ServiceInstance Register(string name, ServiceRole role, Endpoint? game)
        {
            var instance = new ServiceInstance(name, role, Guid.NewGuid().ToString("N"), _clock._now);
            var register = new Register { Role = role, Instance = name, Build = "build", GameData = "gamedata", Ready = true };
            if (game != null) register.Game = game;
            _state.Register(instance, register);
            _state.AfterRegister();
            return instance;
        }

        void LogIn(int id)
        {
            string account = "account" + id;
            string generation = _state.BeginLogin(account, out string? refused);
            Assert.IsNull(refused, refused);
            SelectCharacterReply selected = _state.SelectCharacter(new SelectCharacterRequest
            {
                Account = account, Generation = generation, CharacterId = id, StoredPlayfield = 4582
            });
            Assert.IsTrue(selected.Accepted, selected.Reason);
            ClaimReply claimed = _state.Claim(_zone, new ClaimRequest { CharacterId = id, Cookie1 = selected.Cookie1, Cookie2 = selected.Cookie2 });
            Assert.IsTrue(claimed.Accepted, claimed.Reason);
            Ack presence = _state.UpdatePresence(_zone, new PresenceUpdate
            {
                CharacterId = id, Token = claimed.Token, Name = "Member" + id, PlayfieldId = 4582, Level = 60, Profession = 3
            });
            Assert.IsTrue(presence.Ok, presence.Reason);
        }

        public TeamCommandReply Command(int actor, TeamOp op, int target = 0, int raidTeam = 0)
            => _teams.Command(_zone, new TeamCommand { Op = op, Actor = actor, Target = target, RaidTeam = raidTeam });

        /// <summary>A confirmed invite: the zone has already checked the level range World handed back.</summary>
        public TeamCommandReply Invite(int inviter, int invitee)
            => _teams.Command(_zone, new TeamCommand { Op = TeamOp.TeamInvite, Actor = inviter, Target = invitee, ConfirmedRange = true });

        public TeamCommandReply Accept(int invitee, int inviter) => Command(invitee, TeamOp.TeamAccept, inviter);

        public void Join(int inviter, int invitee)
        {
            TeamCommandReply invite = Invite(inviter, invitee);
            Assert.IsTrue(invite.Ok, invite.Text);
            TeamCommandReply accept = Accept(invitee, inviter);
            Assert.IsTrue(accept.Ok, accept.Text);
        }
    }
}
