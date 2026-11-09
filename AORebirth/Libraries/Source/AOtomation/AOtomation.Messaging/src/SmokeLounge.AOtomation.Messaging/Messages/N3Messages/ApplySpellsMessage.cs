namespace SmokeLounge.AOtomation.Messaging.Messages.N3Messages
{
    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Serialization.MappingAttributes;

    /// <summary>
    /// Client class ApplySpellsIIR_t (Gamecode.dll vtable 0x10171644). Body, read at 0x10128c7d: int32 spell count
    /// encoded as (n + 1) * 0x3F1, n <see cref="InlineSpell"/>s, then one byte. Activate (0x10128c19) runs the list on
    /// the SimpleChar whose identity sits at +0x18 and passes "byte == 0" as the buff flag, so a nonzero byte runs the
    /// spells now. Written by <c>ApplySpellsMessageSerializer</c>.
    /// <para>
    /// Unverified live: the client's Read never fills +0x18 (the header identity lands at +4), so a server-sent
    /// message may find no character and do nothing.
    /// </para>
    /// </summary>
    [AoContract((int)N3MessageType.ApplySpells)]
    public class ApplySpellsMessage : N3Message
    {
        public ApplySpellsMessage()
        {
            this.N3MessageType = N3MessageType.ApplySpells;
            this.Spells = new InlineSpell[0];
            this.Execute = 1;
        }

        public InlineSpell[] Spells { get; set; }

        /// <summary>Nonzero runs the spells immediately; zero hands them to the buff path.</summary>
        public byte Execute { get; set; }
    }
}
