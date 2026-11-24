namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

using AORebirth.Enums;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;
using LostEden.Protocol.Messages.N3Messages;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.MessageHandlers;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// /follow follows players only. A FollowTarget naming an NPC gets no ack, so the follower's client never starts the
/// follow and the server's motor does not follow it either; a FollowTarget naming another player is acked as before.
/// </summary>
[TestClass]
public sealed class FollowTargetPlayerOnlyTests
{
    [TestMethod]
    public void FollowOnAnotherPlayerIsAcknowledgedAndFollowed()
    {
        using var rig = new Rig();
        (Player follower, Session session) = rig.Online(51, new Vector3(10, 0, 10));
        (Player leader, _) = rig.Online(52, new Vector3(14, 0, 10));

        rig.Follow(session, follower, leader.Identity);

        Assert.AreSame(leader, follower.Motor.FollowTarget, "the server follows the other player");
        FollowTargetMessage? ack = session.Sent.Find(body => body is FollowTargetMessage) as FollowTargetMessage;
        Assert.IsNotNull(ack, "the follower's client gets the ack that starts its follow");
        Assert.AreEqual(follower.Identity, ack.Identity);
        Assert.AreEqual(leader.Identity, ((FollowTargetMessage.TargetInfo)ack.Info!).Target);
    }

    [TestMethod]
    public void FollowOnAnNpcIsIgnored()
    {
        using var rig = new Rig();
        (Player follower, Session session) = rig.Online(53, new Vector3(10, 0, 10));
        NpcCharacter npc = rig.Npc(1001, new Vector3(14, 0, 10));
        Assert.IsTrue(rig.Locality.IsVisibleTo(npc, follower), "fixture: the player can see the NPC");

        rig.Follow(session, follower, npc.Identity);

        Assert.IsNull(follower.Motor.FollowTarget, "the server does not follow an NPC");
        Assert.IsFalse(session.Sent.Exists(body => body is FollowTargetMessage), "no ack: the client never follows the NPC");
    }

    sealed class Rig : IDisposable
    {
        readonly ServiceProvider _services;
        readonly Playfield _playfield;
        readonly Items _items = new();

        internal PlayfieldLocality Locality { get; }

        internal Rig()
        {
            Locality = new PlayfieldLocality(1, null);
            _services = new ServiceCollection()
                .AddSingleton(new DynelRegistry())
                .AddSingleton(Locality)
                .BuildServiceProvider();
            _playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
            typeof(Playfield).GetField("<Identity>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_playfield, new Identity { Type = IdentityType.Playfield2, Instance = 800 });
            typeof(Playfield).GetField("_serviceProvider", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_playfield, _services);
        }

        internal (Player Player, Session Session) Online(int id, Vector3 at)
        {
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new Logger(), _items)
            {
                FirstName = "Player" + id
            };
            player.Inventory.Apply(new CharacterHydrationResult(), id, _items);
            player.Stats.Set(CharacterStat.MaxHealth, 100);
            player.Stats.Set(CharacterStat.Health, 100);
            player.Position = at;
            var session = new Session();
            player.EnterOnline(session);
            Place(player);
            // As the engine does after the player's own spawn packets.
            Locality.ActivatePlayerVisibility(player);
            Locality.ReconcileVisibility(player);
            return (player, session);
        }

        internal NpcCharacter Npc(int id, Vector3 at)
        {
            var npc = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = id }, _items);
            npc.Stats.Set(CharacterStat.MaxHealth, 100);
            npc.Stats.Set(CharacterStat.Health, 100);
            npc.Position = at;
            Place(npc);
            return npc;
        }

        /// <summary>The follower's client passing on its /follow, as it arrives from the wire.</summary>
        internal void Follow(Session session, Player follower, Identity target)
            => new FollowTargetMessageHandler().Handle(
                new FollowTargetMessage
                {
                    Identity = follower.Identity,
                    Info = new FollowTargetMessage.TargetInfo { Target = target }
                },
                session);

        void Place(Character character)
        {
            character.Playfield = _playfield;
            _services.GetRequiredService<DynelRegistry>().Register(character);
            Locality.RegisterDynel(character);
        }

        public void Dispose() => _services.Dispose();
    }

    internal sealed class Session : IZoneSession
    {
        internal readonly List<MessageBody> Sent = [];
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public bool TransferToPlayfield(Playfield destination, Vector3 landing) => throw new NotSupportedException();
        public void Send(byte[] packet) => throw new NotSupportedException();
        public void Send(MessageBody body) => Sent.Add(body);
        public void Send(MessageBody body, int sender, int receiver) => Send(body);
        public void SendInitiateCompression() => throw new NotSupportedException();
        public void Close() => State = SessionState.Closed;
    }

    sealed class Items : IItemBuilder
    {
        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null)
            => new()
            {
                LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount, InstanceId = instanceId,
                Identity = identity ?? Identity.None, Definition = CreateTemplate(lowId, highId, quality)
            };

        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
            => Create(lowId, highId, quality, source, stackCount);

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
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
}
