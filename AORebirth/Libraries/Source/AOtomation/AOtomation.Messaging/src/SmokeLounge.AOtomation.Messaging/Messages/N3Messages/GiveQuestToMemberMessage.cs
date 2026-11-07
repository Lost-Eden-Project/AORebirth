namespace SmokeLounge.AOtomation.Messaging.Messages.N3Messages
{
    using SmokeLounge.AOtomation.Messaging.Serialization.MappingAttributes;

    /// <summary>
    /// The journal's "Update team members" on a team mission (Gamecode.dll N3Msg_UpdateNearbyTeamMembers, which sends
    /// GiveQuestToMembersIIR_t). Identity is the character; the body is the mission's journal instance only (its type is
    /// always 0xDAC3; GiveQuestToMembersIIR_t's stream reader reads one int).
    /// </summary>
    [AoContract((int)N3MessageType.GiveQuestToMember)]
    public class GiveQuestToMemberMessage : N3Message
    {
        public GiveQuestToMemberMessage()
        {
            this.N3MessageType = N3MessageType.GiveQuestToMember;
        }

        [AoMember(0)]
        public int QuestInstance { get; set; }
    }
}
