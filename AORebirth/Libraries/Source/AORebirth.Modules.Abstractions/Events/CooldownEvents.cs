namespace AORebirth.Modules.Events
{
    using System;

    /// <summary>What kind of lock (module API 1.6).</summary>
    public enum CooldownKind
    {
        /// <summary>A skill locked after use (special attacks); the id is the skill's stat id.</summary>
        SkillLock,

        /// <summary>The caster-wide nano recharge; the id is 0.</summary>
        NanoRecharge,

        /// <summary>A nano strain locked after a cast; the id is the strain.</summary>
        NanoLine,

        /// <summary>A perk action's lock; the id is the perk.</summary>
        PerkLock,

        Other
    }

    /// <summary>Why a lock ended (module API 1.6).</summary>
    public enum CooldownEndReason
    {
        Expired,

        /// <summary>Cleared early: a reset, a GM, death rules.</summary>
        Cleared
    }

    /// <summary>Which lock: the kind, and the stat id, nano strain or perk id (0 for NanoRecharge, which is caster-wide).</summary>
    public readonly record struct CooldownKey(CooldownKind Kind, int Id);

    /// <summary>A lock started or ended on a player (module API 1.6, <c>observe-actions</c>).</summary>
    public sealed class CooldownEvent
    {
        public int CharacterId { get; init; }

        public int PlayfieldId { get; init; }

        /// <summary>When the engine raised it, on <see cref="IZoneClock"/>.</summary>
        public long Timestamp { get; init; }

        public CooldownKey Key { get; init; }

        /// <summary>The lock's length on the server, after modifiers (SkillLockModifier and the like).</summary>
        public TimeSpan Duration { get; init; }

        /// <summary>When it ends (CooldownStarted) or when it was due to (CooldownEnded), on <see cref="IZoneClock"/>.</summary>
        public long Deadline { get; init; }

        /// <summary>CooldownStarted: what action caused it, when one did.</summary>
        public PlayerActionKind SourceKind { get; init; }

        /// <summary>CooldownStarted: the ability id of that action; 0 when none.</summary>
        public int SourceId { get; init; }

        /// <summary>
        /// CooldownStarted: the length the client was told, or null when it is told nothing (nano recharge). It can differ
        /// from <see cref="Duration"/>: the client is told the unmodified length of a skill lock.
        /// </summary>
        public TimeSpan? ClientDuration { get; init; }

        /// <summary>CooldownEnded: Expired, or Cleared early.</summary>
        public CooldownEndReason Reason { get; init; }

        /// <summary>CooldownEnded: whether the client was sent an "available" message for it (SpecialAvailable, PerkAvailable).</summary>
        public bool ClientNotified { get; init; }
    }
}
