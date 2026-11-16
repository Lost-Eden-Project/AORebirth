namespace ZoneEngine_New.Core.MessageHandlers
{
    using System;

    using SmokeLounge.AOtomation.Messaging.Messages;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Trade;

    /// <summary>
    /// Lost Eden's own <c>IccPurchase</c>: an ICC Commendations shop purchase, run by the playfield's
    /// <see cref="TradeService"/> on its tick thread, answered with <c>IccPurchaseResult</c>.
    /// </summary>
    public sealed class IccPurchaseMessageHandler(IccShopCatalog shop) : IMessageHandler<IccPurchaseMessage>
    {
        public Type MessageBodyType => typeof(IccPurchaseMessage);

        public void Handle(MessageBody body, IZoneSession session)
            => Handle((IccPurchaseMessage)body, session);

        public void Handle(IccPurchaseMessage message, IZoneSession session)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(session);

            if (session.State != SessionState.InPlay)
                return;

            Player? player = session.Player;
            if (player?.Playfield == null)
                return;

            IccPurchaseStatus status = player.Playfield.GetRequiredService<TradeService>()
                .BuyIcc(player, message.Terminal, message.ItemId, message.Quality, shop, out int price);
            session.Send(new IccPurchaseResultMessage
            {
                Identity = player.Identity,
                ItemId = message.ItemId,
                Quality = message.Quality,
                Status = (int)status,
                Price = price,
            });
        }
    }
}
