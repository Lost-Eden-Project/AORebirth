namespace ZoneEngine_New.Core.MessageHandlers
{
    using System;

    using LostEden.Protocol.Messages;
    using LostEden.Protocol.Messages.N3Messages;

    using ZoneEngine_New.Core.Cities;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Network;

    /// <summary>
    /// The city terminal window's requests (Gamecode ClientRequest*IIR_c): accepted only from the player's own in-play
    /// session; <see cref="CityService"/> checks the rest (an open window for that district, reach, ownership, rank).
    /// </summary>
    public abstract class CityMessageHandler<TBody> : IMessageHandler<TBody>
        where TBody : MessageBody
    {
        protected CityMessageHandler(CityService cities)
            => Cities = cities ?? throw new ArgumentNullException(nameof(cities));

        protected CityService Cities { get; }

        public Type MessageBodyType => typeof(TBody);

        public void Handle(MessageBody body, IZoneSession session) => Handle((TBody)body, session);

        public void Handle(TBody body, IZoneSession session)
        {
            ArgumentNullException.ThrowIfNull(body);
            ArgumentNullException.ThrowIfNull(session);
            if (session.State != SessionState.InPlay || session.Player is not Player player
                || !ReferenceEquals(player.Session, session) || player.IsPersistenceQuarantined)
                return;
            Handle(player, body);
        }

        protected abstract void Handle(Player player, TBody body);
    }

    /// <summary>ClientRequestBuy (0x55796602): "Click to Purchase land area".</summary>
    public sealed class ClientRequestBuyMessageHandler(CityService cities) : CityMessageHandler<ClientRequestBuyMessage>(cities)
    {
        protected override void Handle(Player player, ClientRequestBuyMessage body) => Cities.Buy(player, body.CityInstance);
    }

    /// <summary>ClientRequestBuild (0x53011416): one house dragged onto the map, sent by "Construct Buildings".</summary>
    public sealed class ClientRequestBuildMessageHandler(CityService cities) : CityMessageHandler<ClientRequestBuildMessage>(cities)
    {
        protected override void Handle(Player player, ClientRequestBuildMessage body) => Cities.Build(player, body);
    }

    /// <summary>ClientRequestDemolish (0x3F1B6F70): "Demolish Building" on a built house.</summary>
    public sealed class ClientRequestDemolishMessageHandler(CityService cities) : CityMessageHandler<ClientRequestDemolishMessage>(cities)
    {
        protected override void Handle(Player player, ClientRequestDemolishMessage body) => Cities.Demolish(player, body.CityInstance, body.Tile);
    }

    /// <summary>ClientRequestCloseGUI (0x1B3C614D): the window's close and the buy window's "Cancel".</summary>
    public sealed class ClientRequestCloseGuiMessageHandler(CityService cities) : CityMessageHandler<ClientRequestCloseGuiMessage>(cities)
    {
        protected override void Handle(Player player, ClientRequestCloseGuiMessage body) => Cities.CloseWindow(player, body.CityInstance);
    }

    /// <summary>ClientRqToggleCloaking (0x3F5E4B46): the cloaking device's toggle.</summary>
    public sealed class ToggleCloakMessageHandler(CityService cities) : CityMessageHandler<ToggleCloakMessage>(cities)
    {
        protected override void Handle(Player player, ToggleCloakMessage body) => Cities.ToggleCloak(player, body.CityInstance);
    }
}
