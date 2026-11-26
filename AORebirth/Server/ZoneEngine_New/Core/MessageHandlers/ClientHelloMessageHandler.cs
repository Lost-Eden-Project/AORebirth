namespace ZoneEngine_New.Core.MessageHandlers
{
    using System;

    using LostEden.Protocol.Messages;
    using LostEden.Protocol.Messages.N3Messages;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Teams;

    /// <summary>
    /// An updated client's hello (<see cref="ClientHelloMessage"/>) after its FullCharacter: the player is sent the
    /// server's own messages from now on, beginning with its bag names and its team's loot order.
    /// </summary>
    /// <remarks>
    /// Answered once per connection. A client that logs back in while its character is link-dead, or zones, is a new
    /// connection to the same character: its hello is answered again, since that client starts with nothing.
    /// </remarks>
    public sealed class ClientHelloMessageHandler(TeamService teams, ZoneEngine_New.Core.PlayerShops.PlayerShopService? playerShops = null)
        : IMessageHandler<ClientHelloMessage>
    {
        public Type MessageBodyType => typeof(ClientHelloMessage);

        public void Handle(MessageBody body, IZoneSession session) => Handle((ClientHelloMessage)body, session);

        public void Handle(ClientHelloMessage message, IZoneSession session)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(session);
            if (session.State != SessionState.InPlay || session.Player is not Player player || !ReferenceEquals(player.Session, session)
                || message.Identity != player.Identity || player.HasHelloFrom(session))
                return;
            player.OnClientHello(session);
            teams.SendCurrentLootOrder(player);
            // The names of the player shops rented on this playfield.
            playerShops?.SendNames(player);
        }
    }
}
