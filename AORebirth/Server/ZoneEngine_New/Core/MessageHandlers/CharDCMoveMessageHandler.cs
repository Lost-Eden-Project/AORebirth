namespace ZoneEngine_New.Core.MessageHandlers
{
    using System;
    using System.Globalization;

    using SmokeLounge.AOtomation.Messaging.Messages;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

    using Utility;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Network;

    public sealed class CharDCMoveMessageHandler : IMessageHandler<CharDCMoveMessage>
    {
        public Type MessageBodyType => typeof(CharDCMoveMessage);

        public void Handle(MessageBody body, IZoneSession session)
        {
            Handle((CharDCMoveMessage)body, session);
        }

        public void Handle(CharDCMoveMessage message, IZoneSession session)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(session);

            if (session.State != SessionState.InPlay)
                return;

            Player? player = session.Player;
            if (player == null)
                return;

            // A neighbor's announced move can come back on this session. That packet still
            // names the other character; applying it here is what makes the two clients bounce.
            if (message.Identity.Instance != 0
                && (message.Identity.Type != player.Identity.Type
                    || message.Identity.Instance != player.Identity.Instance))
                return;

            if (message.Coordinates != null && LogUtil.HasDetail(DebugInfoDetail.Network))
            {
                LogUtil.Debug(
                    DebugInfoDetail.Network,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "CharDCMove character={0} playfield={1} action={2} position=({3:R},{4:R},{5:R})",
                        player.Identity.Instance,
                        player.Playfield?.Identity.Instance,
                        message.MoveType,
                        message.Coordinates.X,
                        message.Coordinates.Y,
                        message.Coordinates.Z));
            }

            // Only once the client says it is in play (CharInPlay): a client still loading a zone reports where it
            // was, not where it landed.
            if (!player.AcceptsClientMoves(DateTime.UtcNow))
                return;

            // A rooted move is rewritten to the held position: keep what the client reported for a correction event.
            SmokeLounge.AOtomation.Messaging.GameData.Vector3? reported = message.Coordinates;
            bool accepted = player.Motor.Consume(message);

            // Rooted but the client moved, or a hop the character could not have made: snap it back in place with a
            // soft intrazone teleport; heading is kept.
            if ((player.Motor.RootCorrectionDue || player.Motor.JumpCorrectionDue) && player.Playfield != null)
            {
                session.SendIntrazoneTeleport(player.Position, player.Rotation, player.Playfield.ClientPlayfieldId);
                if (reported != null)
                    Modules.ModuleHooks.MovementCorrected(player,
                        new AORebirth.Core.Vector.Vector3(reported.X, reported.Y, reported.Z), player.Position,
                        player.Motor.RootCorrectionDue
                            ? AORebirth.Modules.Events.MovementCorrection.Rooted
                            : AORebirth.Modules.Events.MovementCorrection.Jump);
            }
            if (!accepted)
                return;

            message.Identity = player.Identity;
            // S2C CharDCMove uses unknown 0. The client default of 1 is a local direct-control
            // command, and forwarding it makes the other client apply the move to itself.
            message.Unknown = 0;
            player.Cell?.Announce(message, player);
        }
    }
}
