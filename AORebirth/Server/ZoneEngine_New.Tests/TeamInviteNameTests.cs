namespace ZoneEngine_New.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;

    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

    using ZoneEngine_New.Core.Chat;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Playfield.Locality;
    using ZoneEngine_New.Core.Teams;
    using ZoneEngine_New.Core.WorldSimulation;

    /// <summary>
    /// Bug #41: an LFT invite showed no inviter name. The invitee's client names the inviter from its own view of
    /// them, and the named TeamInviteMessage only went out across playfields.
    /// </summary>
    [TestClass]
    public sealed class TeamInviteNameTests
    {
        [TestMethod]
        public void InviterOutOfTheInviteesViewIsNamedInTheInvite()
        {
            var f = new Fixture();
            Player inviter = f.Player(1);
            Player invitee = f.Player(2);
            f.Locality.RegisterDynel(invitee);
            f.Locality.ActivatePlayerVisibility(invitee);
            Assert.IsFalse(f.Locality.IsVisibleTo(inviter, invitee));

            f.Invite(inviter, invitee);

            TeamInviteMessage named = f.Session(invitee).Sent.OfType<TeamInviteMessage>().Single();
            Assert.AreEqual("Member1", named.Name);
            Assert.AreEqual(inviter.Identity, named.Inviter);
            Assert.AreEqual(invitee.Identity, named.Identity);
            Assert.AreEqual(1, f.Session(invitee).Sent.OfType<CharacterActionMessage>()
                .Count(action => action.Action == CharacterActionType.TeamRequestInvite));
        }

        [TestMethod]
        public void InviterInTheInviteesViewNeedsNoNamedInvite()
        {
            var f = new Fixture();
            Player inviter = f.Player(1);
            Player invitee = f.Player(2);
            f.Locality.RegisterDynel(inviter);
            f.Locality.RegisterDynel(invitee);
            f.Locality.ActivatePlayerVisibility(invitee);
            Assert.IsTrue(f.Locality.IsVisibleTo(inviter, invitee));

            f.Invite(inviter, invitee);

            Assert.AreEqual(0, f.Session(invitee).Sent.OfType<TeamInviteMessage>().Count());
            Assert.AreEqual(1, f.Session(invitee).Sent.OfType<CharacterActionMessage>()
                .Count(action => action.Action == CharacterActionType.TeamRequestInvite));
        }

        sealed class Fixture
        {
            public readonly TeamService Teams;
            public readonly Playfield Playfield;
            public readonly PlayfieldLocality Locality = new(4582, null);
            readonly DynelRegistry _registry = new();
            readonly Dictionary<int, RecordingZoneSession> _sessions = new();

            public Fixture()
            {
                Teams = new TeamService(new NoChat(), () => DateTime.UtcNow, (_, action) => action());
                Playfield = BarePlayfield.Create(4582, new ServiceCollection()
                    .AddSingleton(_registry)
                    .AddSingleton(Locality)
                    .AddSingleton(new WorldSimulationAccess())
                    .BuildServiceProvider());
            }

            public Player Player(int id)
            {
                Player player = TestWorld.CreatePlayer(id);
                player.Name = "Member" + id;
                player.Playfield = Playfield;
                player.Stats.Set(CharacterStat.Level, 60);
                player.Stats.Set(CharacterStat.Profession, 3);
                player.Stats.Set(CharacterStat.MaxHealth, 450);
                player.Stats.Set(CharacterStat.MaxNanoEnergy, 200);
                var session = new RecordingZoneSession { State = SessionState.InPlay };
                session.BindPlayer(player);
                player.Session = session;
                _sessions[id] = session;
                _registry.Register(player);
                Teams.AttachPlayer(player);
                return player;
            }

            public RecordingZoneSession Session(Player player) => _sessions[player.Identity.Instance];

            public void Invite(Player inviter, Player invitee)
                => Teams.TryHandle(inviter, new CharacterActionMessage
                {
                    Identity = inviter.Identity,
                    Action = CharacterActionType.TeamRequestInvite,
                    Target = invitee.Identity
                });
        }

        sealed class NoChat : IChatEngineLink
        {
            public void Start()
            {
            }

            public bool SendChatCommand(int characterId, string command) => true;

            public bool SendVicinity(int senderId, string? senderName, byte messageType, string text, IReadOnlyList<int> recipients) => true;

            public bool SendAnonymousVicinity(string text, IReadOnlyList<int> recipients, byte[]? blob = null) => true;

            public bool SendOrganizationMessage(int organizationId, string text) => true;

            public void Dispose()
            {
            }
        }
    }
}
