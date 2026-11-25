namespace ZoneEngine_New.Core.MessageHandlers
{
    using System;

    using LostEden.Protocol.Messages;
    using LostEden.Protocol.Messages.N3Messages;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.PlayerShops;

    /// <summary>
    /// A player shop action from an updated client (<see cref="PlayerShopActionMessage"/>), on the player's playfield tick.
    /// Only the sender's own character acts, in play, with its session current.
    /// </summary>
    public sealed class PlayerShopActionMessageHandler(PlayerShopService shops) : IMessageHandler<PlayerShopActionMessage>
    {
        public Type MessageBodyType => typeof(PlayerShopActionMessage);

        public void Handle(MessageBody body, IZoneSession session) => Handle((PlayerShopActionMessage)body, session);

        public void Handle(PlayerShopActionMessage message, IZoneSession session)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(session);
            if (session.State != SessionState.InPlay || session.Player is not Player player || !ReferenceEquals(player.Session, session)
                || message.Identity != player.Identity)
                return;
            shops.Handle(player, message);
        }
    }
}
