namespace ZoneEngine_New.Core.MessageHandlers
{
    using System;
    using SmokeLounge.AOtomation.Messaging.Messages;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// A mission terminal roll. Terminal-rolled missions are not offered: dungeon missions come from Quests.json
    /// quests with a Dungeon input. The client gets a line of feedback and no offers, so no credits are charged.
    /// </summary>
    public sealed class QuestAlternativeMessageHandler : IMessageHandler<QuestAlternativeMessage>
    {
        public Type MessageBodyType => typeof(QuestAlternativeMessage);
        public void Handle(MessageBody body, IZoneSession session) => Handle((QuestAlternativeMessage)body, session);

        public void Handle(QuestAlternativeMessage message, IZoneSession session)
        {
            if (session.State != SessionState.InPlay || session.Player is not { } player
                || !ReferenceEquals(player.Session, session) || player.Playfield is not { } playfield
                || message.Identity != player.Identity)
                return;
            if (message.QuestInfos?.Length > 0 || (int)message.MissionTerminalIdentity.Type != MissionTerminal.LiveIdentityType
                || !playfield.GetRequiredService<DynelRegistry>().TryGet(message.MissionTerminalIdentity, out var dynel)
                || dynel is not MissionTerminal terminal || terminal.GetEdgeDistanceTo(player) > LootableDynel.OpenRange)
                return;
            session.Send(new ChatTextMessage { Identity = player.Identity, Text = "This mission terminal is out of service." });
        }
    }
}
