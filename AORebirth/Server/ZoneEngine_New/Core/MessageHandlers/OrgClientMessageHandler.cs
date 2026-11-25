namespace ZoneEngine_New.Core.MessageHandlers
{
    using System;

    using LostEden.Protocol.Messages;
    using LostEden.Protocol.Messages.N3Messages;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Organizations;

    /// <summary>/org commands (OrgClient, N3 0x7F4B3108): accepted only from the player's own in-play session.</summary>
    public sealed class OrgClientMessageHandler : IMessageHandler<OrgClientMessage>
    {
        readonly OrganizationService _organizations;
        readonly Cities.CityService? _cities;

        public OrgClientMessageHandler(OrganizationService organizations, Cities.CityService? cities = null)
        {
            _organizations = organizations ?? throw new ArgumentNullException(nameof(organizations));
            _cities = cities;
        }

        public Type MessageBodyType => typeof(OrgClientMessage);

        public void Handle(MessageBody body, IZoneSession session) => Handle((OrgClientMessage)body, session);

        public void Handle(OrgClientMessage message, IZoneSession session)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(session);
            if (session.State != SessionState.InPlay || session.Player is not Player player
                || !ReferenceEquals(player.Session, session) || player.IsPersistenceQuarantined)
                return;
            // "Dump city advantages to chat" (Gamecode N3Msg_OrgShowCity 1001a8be): the district's advantages, Active or Disabled.
            if (message.Command == OrgClientCommand.Benefits && _cities != null)
            {
                _cities.SendAdvantages(player);
                return;
            }

            _organizations.Handle(player, message);
        }
    }
}
