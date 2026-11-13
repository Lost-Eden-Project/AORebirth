namespace ZoneEngine_New.Core.MessageHandlers;

using System;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using ZoneEngine_New.Core.Modules;
using ZoneEngine_New.Core.Network;

/// <summary>Accepting a mission terminal offer: the client names the offer, the mission roll module decides what it gives.</summary>
public sealed class CreateQuestMessageHandler(ModuleMissionRolls rolls) : IMessageHandler<CreateQuestMessage>
{
    public Type MessageBodyType => typeof(CreateQuestMessage);
    public void Handle(MessageBody body, IZoneSession session) => Handle((CreateQuestMessage)body, session);
    public void Handle(CreateQuestMessage message, IZoneSession session)
    {
        if (session.State != SessionState.InPlay || session.Player is not { } player || !ReferenceEquals(player.Session, session)
            || player.IsDead || player.IsPersistenceQuarantined || message.Identity != player.Identity
            || message.QuestIdentity.Type != IdentityType.Mission)
            return;
        rolls.Accept(player, message.QuestIdentity, session);
    }
}
