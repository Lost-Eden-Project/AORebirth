namespace ZoneEngine_New.Core.MessageHandlers;

using System;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;

/// <summary>Retail world-item pickup; the accepted objective owns eligibility and its atomic transfer.</summary>
public sealed class ClientGetItemMessageHandler : IMessageHandler<ClientGetItemMessage>
{
    public Type MessageBodyType => typeof(ClientGetItemMessage);

    public void Handle(MessageBody body, IZoneSession session) => Handle((ClientGetItemMessage)body, session);

    public void Handle(ClientGetItemMessage message, IZoneSession session)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(session);
        Player? player = session.Player;
        if (session.State != SessionState.InPlay || player == null || player.IsDead || player.IsPersistenceQuarantined
            || !ReferenceEquals(player.Session, session) || message.Identity != player.Identity
            || player.Playfield is not QuestDungeonPlayfield dungeon)
            return;

        if (dungeon.GetRequiredService<DynelRegistry>().TryGet(message.Identity1, out Dynel? dynel)
            && dynel is MissionObjectiveItem objective)
            objective.TryPickup(player);
    }
}
