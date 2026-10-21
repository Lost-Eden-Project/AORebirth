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

        /// <summary>A player entered a playfield on this instance: login, zoning in, or arriving from another instance (1.2).</summary>
        event Action<PlayerPlayfieldEvent> PlayerEnteredPlayfield;

        /// <summary>A player left a playfield on this instance: logout, zoning out, or moving to another instance (1.2).</summary>
        event Action<PlayerPlayfieldEvent> PlayerLeftPlayfield;

        /// <summary>A character came into play on this instance from a login or reconnect, not a zone change (1.2).</summary>
        event Action<PlayerSessionEvent> PlayerLoggedIn;

        /// <summary>A character left play on this instance by logging out or a link-dead timeout, not a zone change (1.2).</summary>
        event Action<PlayerSessionEvent> PlayerLoggedOut;

        /// <summary>An NPC was spawned into a playfield (1.2).</summary>
        event Action<NpcSpawnEvent> NpcSpawned;

        /// <summary>An NPC left a playfield (1.2).</summary>
        event Action<NpcSpawnEvent> NpcDespawned;

        /// <summary>A player opened a Knubot chat with an NPC (1.2).</summary>
        event Action<KnubotChatOpenedEvent> KnubotChatOpened;
    }

    public sealed class PlayerPlayfieldEvent
    {
        public int CharacterId { get; init; }

        public int PlayfieldId { get; init; }
    }

    public sealed class PlayerSessionEvent
    {
        public int CharacterId { get; init; }

        /// <summary>The playfield the character logged in to or out of.</summary>
        public int PlayfieldId { get; init; }
    }

    public sealed class NpcSpawnEvent
    {
        public int PlayfieldId { get; init; }

        public int NpcInstance { get; init; }

        /// <summary>The NPC's template hash, or empty when it has none.</summary>
        public string NpcHash { get; init; } = string.Empty;
    }

    public sealed class KnubotChatOpenedEvent
    {
        public int PlayfieldId { get; init; }

        public int CharacterId { get; init; }

        public int NpcInstance { get; init; }

        /// <summary>The script's id: its path under the Knubot folder without ".json", '/'-separated.</summary>
        public string ScriptId { get; init; } = string.Empty;
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
