namespace ZoneEngine_New.Core.MessageHandlers
{
    using System;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Playfield.Locality;

    /// <summary>
    /// Player /follow. The client's FollowTarget is a pass-on command (<c>FollowTargetIIR_c</c>, Gamecode.dll
    /// 0x1007381e) that only takes effect once the server sends it back: FollowTarget, follower identity, info type 2,
    /// MoveType 0, the followed character, 0x40 0x20000000, zero XYZ (capture 2026-10-05T02:36:46Z). Live sends that ack
    /// to every client that sees the follower, the follower included; each of them then walks the follower 4 m behind
    /// the target itself, and the follower's client sends only a CharDCMove Update about every 5 s. The server runs the
    /// same follow on the follower's motor (<see cref="Movement.CharacterMotor.BeginFollow"/>) so its position keeps up
    /// between Updates. The ack goes out after the follower's earlier CharDCMoves (same tick order, same send queues),
    /// because a watcher that applies a non-Update move after the ack ends the follow. A FollowStop ends the server's
    /// copy only; what live sends for it is not captured.
    /// </summary>
    public sealed class FollowTargetMessageHandler : IMessageHandler<FollowTargetMessage>
    {
        // The two fields after the target as live sends them (together they read as float 2.5).
        const byte AckDummy = 0x40;
        const int AckDummy1 = 0x20000000;

        public Type MessageBodyType => typeof(FollowTargetMessage);

        public void Handle(MessageBody body, IZoneSession session)
        {
            Handle((FollowTargetMessage)body, session);
        }

        public void Handle(FollowTargetMessage message, IZoneSession session)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(session);

            if (session.State != SessionState.InPlay)
                return;

            Player? player = session.Player;
            if (player == null)
                return;

            // The client also passes on FollowTargets it executed for others (NPC paths and other players' follows);
            // only its own is a request.
            if (message.Identity.Instance != 0 && message.Identity.Instance != player.Identity.Instance)
                return;

            if (message.Info is FollowStopInfo)
            {
                player.Motor.EndFollow();
                return;
            }

            if (message.Info is not FollowTargetInfo request)
                return;

            Identity target = request.Target;
            if (target.Instance == 0 || target.Instance == player.Identity.Instance)
                return;

            Playfield? playfield = player.Playfield;
            if (playfield == null
                || !playfield.GetRequiredService<DynelRegistry>().TryGet(target, out Dynel? dynel)
                || dynel is not Character followed
                || followed.IsDead
                || !playfield.GetRequiredService<PlayfieldLocality>().IsVisibleTo(followed, player))
                return;

            if (!player.TryBeginFollowAck(followed.Identity, DateTime.UtcNow))
                return;

            player.Motor.BeginFollow(followed);
            playfield.GetRequiredService<PlayfieldLocality>().Announce(
                player,
                new FollowTargetMessage
                {
                    Identity = player.Identity,
                    Unknown = 0,
                    Info = new FollowTargetInfo
                    {
                        FollowInfoType = 2,
                        MoveType = 0,
                        Target = followed.Identity,
                        Dummy = AckDummy,
                        Dummy1 = AckDummy1,
                        X = 0f,
                        Y = 0f,
                        Z = 0f
                    }
                },
                includeSelf: true);
        }
    }
}
