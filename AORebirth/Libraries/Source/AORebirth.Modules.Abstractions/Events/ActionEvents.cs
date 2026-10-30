namespace AORebirth.Modules.Events
{
    using System;

    using AORebirth.Modules.World;

    /// <summary>What a player asked to use (module API 1.6).</summary>
    public enum PlayerActionKind
    {
        ItemUse,

        /// <summary>An item used on something else: a target, another item.</summary>
        ItemUseOn,

        NanoCast,
        SpecialAttack,
        Perk,
        FightStart,
        Other
    }

    /// <summary>How a request went (module API 1.6).</summary>
    public enum PlayerActionOutcome
    {
        // PlayerActionAttempted

        /// <summary>Runs now.</summary>
        Started,

        /// <summary>Waits behind the use queue or a cast.</summary>
        Queued,

        Refused,

        // PlayerActionResolved

        Executed,

        /// <summary>A cast broken off.</summary>
        Interrupted,

        Missed,
        Resisted,

        /// <summary>Refused when it came to run: the lock or requirement checks done again at execution time.</summary>
        Failed,

        /// <summary>Replaced, or the player logged out first.</summary>
        Cancelled
    }

    /// <summary>Why a request was refused (module API 1.6).</summary>
    public enum ActionRefusal
    {
        None,

        /// <summary>A lock is running: <see cref="PlayerActionEvent.Cooldown"/> and <see cref="PlayerActionEvent.ReadyIn"/> say which and for how long.</summary>
        Cooldown,

        /// <summary>Already casting, or the same action already queued.</summary>
        Busy,

        QueueFull,

        /// <summary>Skill, profession, level, flags.</summary>
        Requirements,

        /// <summary>Nano, ammo, item count.</summary>
        Resources,

        /// <summary>A wrong, missing or dead target.</summary>
        Target,

        Range,
        LineOfSight,

        /// <summary>Stunned, dead, moving when it needs standing still.</summary>
        State,

        Other
    }

    /// <summary>
    /// A player's request to use something, raised where the engine decides (module API 1.6, <c>observe-actions</c>):
    /// what the engine did, not what the client asked.
    /// </summary>
    public sealed class PlayerActionEvent
    {
        public int CharacterId { get; init; }

        public int PlayfieldId { get; init; }

        /// <summary>When the engine raised it, on <see cref="IZoneClock"/>.</summary>
        public long Timestamp { get; init; }

        /// <summary>The <see cref="Network.MessageSample.Timestamp"/> of the client message that asked for it; 0 when none.</summary>
        public long RequestTimestamp { get; init; }

        public PlayerActionKind Kind { get; init; }

        /// <summary>What was used: the item's template id, the nano id, the special's stat id, the perk id.</summary>
        public int AbilityId { get; init; }

        /// <summary>For items: the item itself (template ids and quality). Default for everything else.</summary>
        public ItemRef Item { get; init; }

        /// <summary>The target, or default when none.</summary>
        public EntityRef Target { get; init; }

        public PlayerActionOutcome Outcome { get; init; }

        /// <summary>Why it was Refused or Failed; <see cref="ActionRefusal.None"/> otherwise.</summary>
        public ActionRefusal Refusal { get; init; }

        /// <summary>For a Cooldown refusal: the lock that refused it.</summary>
        public CooldownKey? Cooldown { get; init; }

        /// <summary>For a Cooldown refusal: how long the lock still had to run.</summary>
        public TimeSpan ReadyIn { get; init; }

        /// <summary>The engine's own reason, for logs ("nano line locked"); not a stable contract.</summary>
        public string Detail { get; init; } = string.Empty;
    }
}
