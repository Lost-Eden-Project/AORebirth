namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.Linq;

using AORebirth.Core.GameData;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;
using LostEden.Protocol.Messages.N3Messages;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Chat;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Teams;

using Quaternion = AORebirth.Core.Vector.Quaternion;
using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Live report: seven players stood in one ordinary team after LFT invites (the client's team window showed six, so the
/// seventh was in the team unseen). An ordinary team holds six; only a raid grows past that, six to a raid group.
/// </summary>
[TestClass]
public sealed class TeamSizeCapTests
{
    [TestMethod]
    public void ASixthMemberClosesTheTeamToFurtherInvites()
    {
        var f = new Fixture();
        Player leader = f.Player(1);
        for (int id = 2; id <= 6; id++) f.Join(leader, f.Player(id));
        Player seventh = f.Player(7);

        f.Invite(leader, seventh);

        Assert.AreEqual(6, f.Teams.GetTeam(leader)!.MemberIds.Count);
        Assert.IsNull(f.Teams.GetTeam(seventh));
        Assert.AreEqual("Your team is full.", f.Texts(leader).Last());
        Assert.IsFalse(f.Session(seventh).Bodies.OfType<CharacterActionMessage>()
            .Any(m => m.Action == CharacterActionType.TeamRequestInvite), "The seventh is never invited.");
    }

    [TestMethod]
    public void InvitesSentWhileTheTeamHadRoomCannotAllBeAccepted()
    {
        // Each invite went out while the team had room; accepting every one would have made a team of seven.
        var f = new Fixture();
        Player leader = f.Player(1);
        Player[] invitees = Enumerable.Range(2, 6).Select(f.Player).ToArray();
        foreach (Player invitee in invitees) f.Invite(leader, invitee);

        foreach (Player invitee in invitees) f.Accept(invitee, leader);

        TeamSnapshot team = f.Teams.GetTeam(leader)!;
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6 }, team.MemberIds.ToArray());
        Player last = invitees[^1];
        Assert.IsNull(f.Teams.GetTeam(last));
        Assert.AreEqual("The team is full.", f.Texts(last).Last());
        Assert.AreEqual("Your team is full.", f.Texts(leader).Last());

        // The refused invite is spent: accepting it again does not get the seventh in.
        f.Accept(last, leader);
        Assert.AreEqual(6, f.Teams.GetTeam(leader)!.MemberIds.Count);
        Assert.IsNull(f.Teams.GetTeam(last));
    }

    [TestMethod]
    public void AMemberLeavingReopensTheTeam()
    {
        var f = new Fixture();
        Player leader = f.Player(1);
        Player[] members = Enumerable.Range(2, 5).Select(f.Player).ToArray();
        foreach (Player member in members) f.Join(leader, member);
        f.Action(members[0], CharacterActionType.LeaveTeam);
        Player newcomer = f.Player(7);

        f.Join(leader, newcomer);

        Assert.AreEqual(6, f.Teams.GetTeam(leader)!.MemberIds.Count);
        Assert.IsNotNull(f.Teams.GetTeam(newcomer));
    }

    [TestMethod]
    public void ARaidGrowsPastSixIntoItsNextGroup()
    {
        var f = new Fixture();
        Player leader = f.Player(1);
        for (int id = 2; id <= 6; id++) f.Join(leader, f.Player(id));
        Assert.IsTrue(f.Teams.ConvertToRaid(leader));
        Player seventh = f.Player(7), eighth = f.Player(8);

        f.Join(leader, seventh);
        f.Join(leader, eighth);

        TeamSnapshot raid = f.Teams.GetTeam(leader)!;
        Assert.IsTrue(raid.IsRaid);
        Assert.AreEqual(8, raid.MemberIds.Count);
        // Group 0 is full, so the joiners went to group 1: moving a group-0 member there leaves group 1 with three.
        Assert.IsTrue(f.Teams.MoveRaidMember(leader, 2, 1));
    }

    [TestMethod]
    public void ARaidMoveCannotOverfillAGroupOrNameOneThatDoesNotExist()
    {
        var f = new Fixture();
        Player leader = f.Player(1);
        for (int id = 2; id <= 6; id++) f.Join(leader, f.Player(id));
        Assert.IsTrue(f.Teams.ConvertToRaid(leader));
        Player seventh = f.Player(7);
        f.Join(leader, seventh);

        // Group 0 holds six: the seventh, in group 1, cannot be moved back into it.
        Assert.IsFalse(f.Teams.MoveRaidMember(leader, 7, 0));
        Assert.AreEqual("That raid team is full.", f.Texts(leader).Last());
        Assert.IsFalse(f.Teams.MoveRaidMember(leader, 7, 40));
        Assert.IsFalse(f.Teams.MoveRaidMember(leader, 7, -1));

        // A member already in the group is not counted against it, and a group with room takes a move.
        Assert.IsTrue(f.Teams.MoveRaidMember(leader, 7, 1));
        Assert.IsTrue(f.Teams.MoveRaidMember(leader, 7, 39));
        Assert.IsTrue(f.Teams.MoveRaidMember(leader, 2, 39));
        Assert.IsTrue(f.Teams.MoveRaidMember(leader, 7, 0), "Group 0 has room again after member 2 left it.");
        Assert.AreEqual(7, f.Teams.GetTeam(leader)!.MemberIds.Count);
    }

    sealed class Fixture
    {
        public readonly TeamService Teams = new(new NoChat(), () => new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc));
        readonly Dictionary<int, Session> _sessions = new();

        public Player Player(int id)
        {
            var builder = new StubItemBuilder();
            var session = new Session();
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new StubLogger(), builder)
            {
                FirstName = "Member" + id,
                Session = session
            };
            session.BindPlayer(player);
            player.Inventory.Apply(new CharacterHydrationResult(), id, builder);
            player.Stats.Set(CharacterStat.Level, 60);
            player.Stats.Set(CharacterStat.Profession, 3);
            player.Stats.Set(CharacterStat.MaxHealth, 450);
            player.Stats.Set(CharacterStat.MaxNanoEnergy, 200);
            _sessions[id] = session;
            Teams.AttachPlayer(player);
            return player;
        }

        public Session Session(Player player) => _sessions[player.Identity.Instance];

        public IEnumerable<string> Texts(Player player) => Session(player).Bodies.OfType<ChatTextMessage>().Select(m => m.Text);

        public void Action(Player player, CharacterActionType action, Identity target = default, int p2 = 0)
            => Teams.TryHandle(player, new CharacterActionMessage { Identity = player.Identity, Action = action, Target = target, Parameter2 = p2 });

        public void Invite(Player inviter, Player invitee) => Action(inviter, CharacterActionType.TeamRequestInvite, invitee.Identity);

        public void Accept(Player invitee, Player inviter) => Action(invitee, CharacterActionType.TeamRequestReply, inviter.Identity, 1);

        public void Join(Player inviter, Player invitee)
        {
            Invite(inviter, invitee);
            Accept(invitee, inviter);
            Assert.IsNotNull(Teams.GetTeam(invitee), invitee.FirstName + " joined.");
        }
    }

    sealed class Session : IZoneSession
    {
        public readonly List<MessageBody> Bodies = new();
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public bool TransferToPlayfield(Playfield destination, Vector3 landing) => false;
        public bool TransferToPlayfield(Playfield destination, Vector3 landing, Quaternion heading) => false;
        public void Send(byte[] packet) { }
        public void Send(MessageBody body) => Bodies.Add(body);
        public void Send(MessageBody body, int sender, int receiver) => Bodies.Add(body);
        public void SendInitiateCompression() { }
        public void Close() => State = SessionState.Closed;
    }

    sealed class NoChat : IChatEngineLink
    {
        public void Start() { }
        public bool SendChatCommand(int characterId, string command) => true;
        public bool SendVicinity(int senderId, string? senderName, byte messageType, string text, IReadOnlyList<int> recipients) => true;
        public bool SendAnonymousVicinity(string text, IReadOnlyList<int> recipients, byte[]? blob = null) => true;
        public bool SendOrganizationMessage(int organizationId, string text) => true;
        public void Dispose() { }
    }

    sealed class StubLogger : IZoneLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(Exception exception, string message) => throw new AssertFailedException(message + ": " + exception, exception);
        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }

    sealed class StubItemBuilder : IItemBuilder
    {
        public Item Create(int lowId, int highId, int quality, AORebirth.Enums.ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null)
            => new()
            {
                LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount, InstanceId = instanceId,
                Identity = identity ?? Identity.None, Definition = CreateTemplate(lowId, highId, quality)
            };

        public Item CreateWithNewInstance(int lowId, int highId, int quality, AORebirth.Enums.ItemSource source, int stackCount = 1)
            => Create(lowId, highId, quality, source, stackCount);

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }
}
