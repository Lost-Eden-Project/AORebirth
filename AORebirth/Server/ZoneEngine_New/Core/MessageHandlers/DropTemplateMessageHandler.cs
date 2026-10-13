namespace ZoneEngine_New.Core.MessageHandlers;

using System;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Network;

/// <summary>Client item drop (N3Msg_DropItem: inventory slot + world position); <see cref="WorldItemTransfers"/> rules it.</summary>
public sealed class DropTemplateMessageHandler : IMessageHandler<DropTemplateMessage>
{
    readonly InventoryActionService _inventoryActions;

    public DropTemplateMessageHandler(InventoryActionService inventoryActions)
    {
        _inventoryActions = inventoryActions ?? throw new ArgumentNullException(nameof(inventoryActions));
    }

    public Type MessageBodyType => typeof(DropTemplateMessage);

    public void Handle(MessageBody body, IZoneSession session) => Handle((DropTemplateMessage)body, session);

    public void Handle(DropTemplateMessage message, IZoneSession session)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(session);
        Player? player = session.Player;
        if (session.State != SessionState.InPlay || player == null || !ReferenceEquals(player.Session, session)
            || message.Identity != player.Identity || message.Item == null || message.Position == null)
            return;

        WorldItemTransfers.TryDrop(player, message.Item, message.Position, _inventoryActions,
            player.Playfield?.GetService<IZoneLogger>());
    }
}
