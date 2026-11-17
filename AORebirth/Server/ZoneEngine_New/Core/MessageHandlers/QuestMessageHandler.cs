namespace ZoneEngine_New.Core.MessageHandlers;

using System;
using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;
using LostEden.Protocol.Messages.N3Messages;
using ZoneEngine_New.Core.Network;

public sealed class QuestMessageHandler(Quests.QuestService quests) : IMessageHandler<QuestMessage>
{
    public Type MessageBodyType => typeof(QuestMessage);
    public void Handle(MessageBody body, IZoneSession session) => Handle((QuestMessage)body, session);
    public void Handle(QuestMessage message, IZoneSession session)
    {
        var player = session.Player;
        if (session.State != SessionState.InPlay || player == null || player.IsPersistenceQuarantined || player.IsDead
            || !message.Identity.Equals(player.Identity) || message.Version != 1 || (int)message.Quest.Type != 0xDAC3) return;
        // Quests.json and generated quests (accepted missions) are the only journal entries a player can delete.
        quests.TryAbandon(player, message.Quest.Instance);
    }
}
