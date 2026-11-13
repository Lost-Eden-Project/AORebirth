namespace ZoneEngine_New.Core.MessageHandlers
{
    using System;
    using SmokeLounge.AOtomation.Messaging.Messages;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Modules;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// A mission terminal roll. The module that registered a mission roll hook decides the offers
    /// (<see cref="ModuleMissionRolls"/>); without one the terminal is out of service and no offers are shown.
    /// </summary>
    public sealed class QuestAlternativeMessageHandler(ModuleMissionRolls rolls) : IMessageHandler<QuestAlternativeMessage>
    {
        public Type MessageBodyType => typeof(QuestAlternativeMessage);
        public void Handle(MessageBody body, IZoneSession session) => Handle((QuestAlternativeMessage)body, session);

        public void Handle(QuestAlternativeMessage message, IZoneSession session)
        {
            if (session.State != SessionState.InPlay || session.Player is not { } player
                || !ReferenceEquals(player.Session, session) || player.IsPersistenceQuarantined
                || player.Playfield is not { } playfield || message.Identity != player.Identity)
                return;
            if (message.QuestInfos?.Length > 0 || (int)message.MissionTerminalIdentity.Type != MissionTerminal.LiveIdentityType
                || !playfield.GetRequiredService<DynelRegistry>().TryGet(message.MissionTerminalIdentity, out var dynel)
                || dynel is not MissionTerminal terminal || terminal.GetEdgeDistanceTo(player) > LootableDynel.OpenRange)
                return;
            rolls.Roll(player, playfield, terminal, message, session);
        }
    }
}
