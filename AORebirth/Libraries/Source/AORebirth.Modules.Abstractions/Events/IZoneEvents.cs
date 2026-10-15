namespace AORebirth.Modules.Events
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Gameplay notifications. Handlers run synchronously on the playfield's tick thread, so they must be fast and must
    /// never block (no I/O, locks or waits); hand slow work to another thread. Event objects are only built when at
    /// least one handler is subscribed. A handler that throws is logged; one that keeps throwing is unsubscribed.
    /// </summary>
    public interface IZoneEvents
    {
        /// <summary>An NPC died. Raised after kill XP and quest credit were awarded.</summary>
        event Action<NpcKilledEvent> NpcKilled;

        /// <summary>A player died.</summary>
        event Action<PlayerDiedEvent> PlayerDied;
    }

    public sealed class NpcKilledEvent
    {
        public int PlayfieldId { get; init; }

        public int NpcInstance { get; init; }

        /// <summary>The NPC's template hash, or empty when it has none.</summary>
        public string NpcHash { get; init; } = string.Empty;

        public string NpcName { get; init; } = string.Empty;

        public int NpcLevel { get; init; }

        /// <summary>Character id of whoever landed the killing blow, or 0 when unknown.</summary>
        public int KillerInstance { get; init; }

        /// <summary>Character ids of the players present with kill credit.</summary>
        public IReadOnlyList<int> CreditedPlayers { get; init; } = Array.Empty<int>();
    }

    public sealed class PlayerDiedEvent
    {
        public int PlayfieldId { get; init; }

        public int PlayerInstance { get; init; }

        /// <summary>Identity instance of the killer, or 0 when unknown.</summary>
        public int KillerInstance { get; init; }

        public bool KilledByPlayer { get; init; }
    }
}
