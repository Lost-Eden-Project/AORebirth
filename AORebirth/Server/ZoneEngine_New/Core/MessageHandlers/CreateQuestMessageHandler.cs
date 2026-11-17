namespace ZoneEngine_New.Core.MessageHandlers;

using System;
using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;
using LostEden.Protocol.Messages.N3Messages;
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
            || message.MissionId.Type != IdentityType.Mission)
            return;
        rolls.Accept(player, message.MissionId, session);
    }
}
