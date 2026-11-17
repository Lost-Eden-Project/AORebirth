namespace ZoneEngine_New.Core.Entities
{
    using LostEden.Protocol.GameData;

    /// <summary>
    /// The flag byte an InfoPacket starts with (<see cref="InfoPacket.Flags"/>), by the shapes the engine sends: which
    /// blocks follow the character's info. The values are the client's flags combined.
    /// </summary>
    public enum InfoPacketType : byte
    {
        /// <summary>A character: the info alone.</summary>
        Character = InfoPacket.Unknown1Flag,

        /// <summary>A character in an organization: the org rank block follows.</summary>
        CharacterOrg = InfoPacket.Unknown1Flag | InfoPacket.ClanFlag,

        /// <summary>With the org's tower fields.</summary>
        CharacterOrgSite = CharacterOrg | InfoPacket.DestinationsFlag,

        /// <summary>With the org's towers.</summary>
        CharacterOrgSiteTower = CharacterOrgSite | InfoPacket.ItemsFlag,

        /// <summary>An NPC: the info without the PvP block.</summary>
        Monster = InfoPacket.Unknown1Flag | InfoPacket.NoPvpFlag
    }
}
