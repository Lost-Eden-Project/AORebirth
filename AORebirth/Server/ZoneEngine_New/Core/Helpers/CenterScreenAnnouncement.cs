namespace ZoneEngine_New.Core.Helpers
{
    using System;

    using AORebirth.Enums;

    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages.N3Messages;

    /// <summary>
    /// Builds the center-screen announcement: an <see cref="ApplySpellsMessage"/> holding one inline 53247 spell
    /// (<see cref="FunctionType.CenterScreenNotification"/>) whose string argument is the text. The retail client's
    /// ApplySpells handler never resolves a character, so it ignores the message; the AORebirth client shows it.
    /// Wire layout: docs/reference/packets/CenterScreenAnnouncement.md.
    /// </summary>
    public static class CenterScreenAnnouncement
    {
        /// <summary>53247 format version 4 (GameData.dll SpellFormats_c).</summary>
        public const int FormatVersion = 4;

        /// <summary>Longest text sent; longer text is cut so one call can never build an oversized packet.</summary>
        public const int MaxTextLength = 1024;

        /// <summary>Stat 32 target 2 is self, as on every retail 53247 nano.</summary>
        const int TargetSelf = 2;

        /// <summary>Range 0: show only on the recipient's own client.</summary>
        const int RangeOwnCharacter = 0;

        public static ApplySpellsMessage Build(Identity recipient, string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            if (text.Length > MaxTextLength)
                text = text.Substring(0, MaxTextLength);

            var spell = new AppliedSpell
            {
                FunctionType = (int)FunctionType.CenterScreenNotification,
                Header2 = 0,
                Version = FormatVersion,
                Hits = 1,
                Delay = 0,
                Target = TargetSelf,
                Stat35 = -1,
                Text = text,
                Range = RangeOwnCharacter
            };

            return new ApplySpellsMessage
            {
                Identity = recipient,
                Spells = [spell],
                Execute = 1
            };
        }
    }
}
