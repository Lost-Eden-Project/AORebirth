namespace AORebirth.Modules.Events
{
    using System;
    using System.Collections.Generic;

    using AORebirth.Modules.World;

    /// <summary>
    /// What the engine was doing when an audited stat changed (module API 1.16). A GM command or a module operation stays
    /// the reason for everything it causes, so a GM-given level or a module payout never reads as earned. Each member says
    /// what its <see cref="StatChangeSource"/> holds (1.17); a field not named is empty.
    /// </summary>
    public enum StatChangeReason
    {
        /// <summary>A path the engine does not label. For an audit this is the one worth a second look. Source: empty.</summary>
        Other,

        /// <summary>
        /// Experience for a kill, with the death pool recovered by it. Source: the NPC killed (<c>Entity</c>, <c>Hash</c>,
        /// <c>Name</c>, <c>Level</c>). <c>DetailId</c> is 0 when this player's own team share was credited to them, or the
        /// character id of the teammate it was credited to (the team's highest damager) when this player shared it.
        /// </summary>
        Kill,

        /// <summary>
        /// A quest's or mission's reward. Source: <c>Name</c> the quest's title, <c>Detail</c> its quest id (the stable key).
        /// </summary>
        Quest,

        /// <summary>
        /// A daily reward item's share of the level bar. Source: <c>DetailId</c> the reward item's id, and <c>Name</c> its
        /// name when the daily login reward gave it.
        /// </summary>
        Daily,

        /// <summary>
        /// Experience for a tradeskill combine. Source: <c>DetailId</c> the result item's id, <c>Name</c> its name and
        /// <c>Detail</c> the source and target item ids as <c>source+target</c>.
        /// </summary>
        Tradeskill,

        /// <summary>Experience for picking a lock. Source: the door (<c>Entity</c>, and <c>Level</c> its lock difficulty).</summary>
        Lockpick,

        /// <summary>Credits moved by a trade between two players. Source: the other player (<c>Entity</c>, <c>Name</c>, <c>Level</c>).</summary>
        Trade,

        /// <summary>
        /// Credits paid or taken by a shop or vending machine. Source: the vendor NPC (<c>Entity</c>, <c>Hash</c>,
        /// <c>Name</c>, <c>Level</c>), or the vending terminal itself when no NPC runs it (<c>Entity</c>, <c>Name</c>,
        /// <c>Hash</c> its vendor template hash); either way <c>TemplateId</c> is the terminal's item template id and
        /// <c>DetailId</c> its vendor id (0 for a shop without a vendor row).
        /// </summary>
        Shop,

        /// <summary>Credits paid in an NPC dialogue. Source: the NPC talked to (<c>Entity</c>, <c>Hash</c>, <c>Name</c>, <c>Level</c>).</summary>
        Dialogue,

        /// <summary>
        /// Credits taken from a corpse. Source: who died (<c>Entity</c>, <c>Hash</c>, <c>Name</c>, <c>Level</c>, captured at
        /// death), with <c>DetailId</c> the corpse's instance.
        /// </summary>
        Loot,

        /// <summary>Credits paid into or out of an organization bank. Source: <c>DetailId</c> the org id, <c>Name</c> its name.</summary>
        OrgBank,

        /// <summary>
        /// A fee the engine charged (saving at a save point). Source: <c>TemplateId</c> the save terminal's item template id,
        /// <c>Name</c> its name.
        /// </summary>
        Fee,

        /// <summary>Experience lost on death. Source: the killer when known (<c>Entity</c>, <c>Hash</c> for an NPC, <c>Name</c>, <c>Level</c>).</summary>
        Death,

        /// <summary>
        /// A GM command. Source: the GM (<c>Entity</c>, <c>Name</c>, <c>Level</c>), with <c>Detail</c> the command name and
        /// <c>DetailId</c> the GM level it ran at. The change lands on whoever the command acted on.
        /// </summary>
        Gm,

        /// <summary>
        /// A module operation (<c>player.stat-add</c>, <c>player.purchase</c>, ...). Source: <c>Name</c> the module id
        /// (<c>module</c> when the engine does not know it), <c>Detail</c> the operation name (empty for an action that is not
        /// a named operation).
        /// </summary>
        Module
    }

    /// <summary>
    /// Who or what was on the other side of an audited change (module API 1.17): the monster killed, the shop, the other
    /// trader, the GM. The engine fills it when the change happens, while the entity still exists, so a module needs no
    /// later lookup (a corpse, a despawned NPC or a terminal is often gone by then). <see cref="StatChangeReason"/> says
    /// which fields each reason fills; the others are empty (0, <c>default</c> or ""), and a string is never null.
    /// </summary>
    public readonly record struct StatChangeSource
    {
        readonly string? _hash;
        readonly string? _name;
        readonly string? _detail;

        /// <summary>The entity on the other side; empty when there is none.</summary>
        public EntityRef Entity { get; init; }

        /// <summary>The item template id of a terminal (a shop's vending terminal, a save terminal); 0 otherwise.</summary>
        public int TemplateId { get; init; }

        /// <summary>
        /// An NPC's template hash (the same key as <see cref="NpcSpawnEvent.NpcHash"/>), to group by monster type without
        /// relying on names; a vending terminal's vendor template hash; "" otherwise.
        /// </summary>
        public string Hash
        {
            get => _hash ?? string.Empty;
            init => _hash = string.IsNullOrEmpty(value) ? null : value;
        }

        /// <summary>The source's name when the change happened.</summary>
        public string Name
        {
            get => _name ?? string.Empty;
            init => _name = string.IsNullOrEmpty(value) ? null : value;
        }

        /// <summary>A character's level when the change happened (a door's lock difficulty for <c>Lockpick</c>); 0 otherwise.</summary>
        public int Level { get; init; }

        /// <summary>A reason-specific id (see <see cref="StatChangeReason"/>); 0 when none.</summary>
        public int DetailId { get; init; }

        /// <summary>A reason-specific text (see <see cref="StatChangeReason"/>); "" when none.</summary>
        public string Detail
        {
            get => _detail ?? string.Empty;
            init => _detail = string.IsNullOrEmpty(value) ? null : value;
        }

        /// <summary>Whether no field is set.</summary>
        public bool IsEmpty => Entity == default && TemplateId == 0 && _hash == null && _name == null && Level == 0
            && DetailId == 0 && _detail == null;
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

        /// <summary>
        /// Who or what caused it (1.17): the monster killed, the vendor, the other trader, the quest, the GM, the module.
        /// What it holds depends on <see cref="Reason"/>; empty for <see cref="StatChangeReason.Other"/>.
        /// </summary>
        public StatChangeSource Source { get; init; }
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
