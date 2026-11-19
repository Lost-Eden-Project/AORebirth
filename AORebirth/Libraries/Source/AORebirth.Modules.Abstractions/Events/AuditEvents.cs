namespace AORebirth.Modules.Events
{
    using System;
    using System.Collections.Generic;

    using AORebirth.Modules.World;

    /// <summary>
    /// What the engine was doing when an audited stat changed (module API 1.16). A GM command or a module operation stays
    /// the reason for everything it causes, so a GM-given level or a module payout never reads as earned.
    /// </summary>
    public enum StatChangeReason
    {
        /// <summary>A path the engine does not label. For an audit this is the one worth a second look.</summary>
        Other,

        /// <summary>Experience for a kill, with the death pool recovered by it.</summary>
        Kill,

        /// <summary>A quest's or mission's reward.</summary>
        Quest,

        /// <summary>A daily reward item's share of the level bar.</summary>
        Daily,

        /// <summary>Experience for a tradeskill combine.</summary>
        Tradeskill,

        /// <summary>Experience for picking a lock.</summary>
        Lockpick,

        /// <summary>Credits moved by a trade between two players.</summary>
        Trade,

        /// <summary>Credits paid or taken by a shop or vending machine.</summary>
        Shop,

        /// <summary>Credits paid in an NPC dialogue.</summary>
        Dialogue,

        /// <summary>Credits taken from a corpse.</summary>
        Loot,

        /// <summary>Credits paid into or out of an organization bank.</summary>
        OrgBank,

        /// <summary>A fee the engine charged (saving at a save point).</summary>
        Fee,

        /// <summary>Experience lost on death.</summary>
        Death,

        /// <summary>A GM command.</summary>
        Gm,

        /// <summary>A module operation (<c>player.stat-add</c>, <c>player.purchase</c>, ...).</summary>
        Module
    }

    /// <summary>
    /// The stored value of one of a player's audited stats changed while the player was in play (module API 1.16):
    /// credits (Cash, 61), experience (XP, 52), level (Level, 54), alien experience (AlienXP, 40) and alien level
    /// (AlienLevel, 169). Loading a character raises nothing; a buff never changes a stored value, so it raises nothing
    /// either.
    /// </summary>
    public sealed class PlayerStatChangedEvent
    {
        public int CharacterId { get; init; }

        public int PlayfieldId { get; init; }

        /// <summary>When the engine raised it, on <see cref="IZoneClock"/>.</summary>
        public long Timestamp { get; init; }

        /// <summary>The <see cref="Network.MessageSample.Timestamp"/> of the client message that caused it; 0 when none.</summary>
        public long RequestTimestamp { get; init; }

        /// <summary>The stat id (CharacterStat).</summary>
        public int Stat { get; init; }

        public int Before { get; init; }

        public int After { get; init; }

        /// <summary><see cref="After"/> minus <see cref="Before"/>: positive for a gain.</summary>
        public long Delta => (long)After - Before;

        public StatChangeReason Reason { get; init; }
    }

    /// <summary>How a GM command ended (module API 1.16). Only commands that ran are raised.</summary>
    public enum GmCommandOutcome
    {
        /// <summary>The command ran. It may still have answered with its usage when its arguments were wrong.</summary>
        Executed
    }

    /// <summary>
    /// A character ran a GM command (module API 1.16): typed in vicinity chat or relayed from a chat channel or tell.
    /// Unknown command names, help requests, refusals for GM level and commands that threw raise nothing.
    /// </summary>
    public sealed class GmCommandUsedEvent
    {
        /// <summary>The character who issued it.</summary>
        public int CharacterId { get; init; }

        public int PlayfieldId { get; init; }

        /// <summary>When the engine raised it, on <see cref="IZoneClock"/>; after the command ran.</summary>
        public long Timestamp { get; init; }

        /// <summary>The <see cref="Network.MessageSample.Timestamp"/> of the chat message that carried it; 0 when none.</summary>
        public long RequestTimestamp { get; init; }

        /// <summary>The command's name, without the leading '.'.</summary>
        public string Command { get; init; } = string.Empty;

        /// <summary>The arguments as typed, split on whitespace.</summary>
        public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

        /// <summary>The issuer's GM level when it ran.</summary>
        public int GmLevel { get; init; }

        /// <summary>The level the command needs.</summary>
        public int RequiredGmLevel { get; init; }

        /// <summary>
        /// What the issuer had selected, which most commands act on; empty when nothing or themselves. A command that
        /// names its subject in its arguments carries it in <see cref="Args"/>.
        /// </summary>
        public EntityRef Target { get; init; }

        public GmCommandOutcome Outcome { get; init; }
    }
}
