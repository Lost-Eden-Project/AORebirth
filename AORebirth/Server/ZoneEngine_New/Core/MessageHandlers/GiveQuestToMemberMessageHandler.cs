namespace ZoneEngine_New.Core.MessageHandlers;

using System;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using ZoneEngine_New.Core.Network;

/// <summary>
/// The journal's "Update team members" on a team mission: gives it to the roller's teammates on the same playfield who
/// were in the team when it was rolled. The server checks the roller, roster and mission itself; the client names only
/// the journal instance.
/// </summary>
public sealed class GiveQuestToMemberMessageHandler(Quests.Dungeons.QuestDungeonService dungeons) : IMessageHandler<GiveQuestToMemberMessage>
{
    public Type MessageBodyType => typeof(GiveQuestToMemberMessage);
    public void Handle(MessageBody body, IZoneSession session) => Handle((GiveQuestToMemberMessage)body, session);
    public void Handle(GiveQuestToMemberMessage message, IZoneSession session)
    {
        var player = session.Player;
        if (session.State != SessionState.InPlay || player == null || player.IsPersistenceQuarantined || player.IsDead
            || !message.Identity.Equals(player.Identity) || message.QuestInstance == 0) return;
        dungeons.UpdateTeamMembers(player, message.QuestInstance);
    }
}
