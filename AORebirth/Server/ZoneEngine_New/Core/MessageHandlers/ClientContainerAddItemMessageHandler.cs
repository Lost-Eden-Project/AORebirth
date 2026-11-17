namespace ZoneEngine_New.Core.MessageHandlers
{
    using System;

    using LostEden.Protocol.Messages;
    using LostEden.Protocol.Messages.N3Messages;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Network;

    public sealed class ClientContainerAddItemMessageHandler : IMessageHandler<ClientContainerAddItem>
    {
        private readonly InventoryMoveService _moves;

        public ClientContainerAddItemMessageHandler(InventoryMoveService moves)
        {
            ArgumentNullException.ThrowIfNull(moves);
            _moves = moves;
        }

        public Type MessageBodyType => typeof(ClientContainerAddItem);

        public void Handle(MessageBody body, IZoneSession session)
        {
            Handle((ClientContainerAddItem)body, session);
        }

        public void Handle(ClientContainerAddItem message, IZoneSession session)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(session);

            if (session.State != SessionState.InPlay)
                return;

            Player? player = session.Player;
            if (player == null || player.IsDead)
                return;

            _moves.Handle(player, message);
        }
    }
}
