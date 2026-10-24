namespace SmokeLounge.AOtomation.Messaging.Messages.N3Messages
{
    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Serialization;
    using SmokeLounge.AOtomation.Messaging.Serialization.MappingAttributes;

    /// <summary>
    /// Renames a dynel the client has (Gamecode.dll SetNameIIR_t: read 0x10076E6E, applied 0x10076FE5). Live sends it
    /// to a pet's identity after /pet rename: Kind 3, Flag 0, the name as the owner sent it, then 16 zero bytes. The
    /// client ignores a name of 32 bytes or more.
    /// </summary>
    [AoContract((int)N3MessageType.SetName)]
    public class SetNameMessage : N3Message
    {
        public SetNameMessage()
        {
            this.N3MessageType = N3MessageType.SetName;
        }

        /// <summary>3 for a pet's name (the client also sets flag 0x40 on the dynel for it).</summary>
        [AoMember(1)]
        public int Kind { get; set; }

        [AoMember(2)]
        public byte Flag { get; set; }

        [AoMember(3, SerializeSize = ArraySizeType.Int32)]
        public string Name { get; set; }

        [AoMember(4)]
        public Identity Unknown1 { get; set; }

        [AoMember(5)]
        public int Unknown2 { get; set; }

        [AoMember(6)]
        public int Unknown3 { get; set; }
    }
}
