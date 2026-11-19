namespace AORebirth.Modules.Events
{
    using System;
    using System.Collections.Generic;

    using AORebirth.Modules.World;

    /// <summary>
    /// Gameplay notifications. Handlers run synchronously on the playfield's tick thread, so they must be fast and must
    /// never block (no I/O, locks or waits); hand slow work to another thread. Event objects are only built when at
    /// least one handler is subscribed. A handler that throws is logged; one that keeps throwing is unsubscribed.
    /// <para>
    /// Every event carries <c>Timestamp</c>, when the engine raised it on <see cref="IZoneClock"/> (module API 1.6). An
    /// event a client message caused also carries <c>RequestTimestamp</c>, that message's
    /// <see cref="Network.MessageSample.Timestamp"/>, or 0 when no message caused it.
    /// </para>
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

        /// <summary>
        /// The engine accepted, queued or refused a player's request to use something: an item, a nano, a special attack, a
        /// perk, or the start of a fight (1.6). Raised once per request; a malformed one raises nothing. Needs
        /// <c>observe-actions</c>.
        /// </summary>
        event Action<PlayerActionEvent> PlayerActionAttempted;

        /// <summary>
        /// A Started or Queued action ended: Executed, Interrupted, Missed, Resisted, Failed or Cancelled (1.6). Raised once
        /// per such request. Needs <c>observe-actions</c>.
        /// </summary>
        event Action<PlayerActionEvent> PlayerActionResolved;

        /// <summary>A lock was set on a player (1.6). Locks restored at login or a zone change raise nothing. Needs <c>observe-actions</c>.</summary>
        event Action<CooldownEvent> CooldownStarted;

        /// <summary>
        /// A lock ran out or was cleared (1.6). For a lock with an "available" message it is raised on the tick that sends
        /// it. Needs <c>observe-actions</c>.
        /// </summary>
        event Action<CooldownEvent> CooldownEnded;

        /// <summary>The engine refused or rewrote a client position and pulled the client back (1.6).</summary>
        event Action<PlayerMovementCorrectedEvent> PlayerMovementCorrected;

        /// <summary>An item changed place: loot, pickup, trade, shop, bank, reward, creation, destruction (1.6).</summary>
        event Action<ItemMovedEvent> ItemMoved;

        /// <summary>
        /// A player spoke in chat the zone handles (1.6). Needs <c>observe-chat</c>; the text is filled only for a module
        /// that also has <c>read-chat-text</c>.
        /// </summary>
        event Action<PlayerSpokeEvent> PlayerSpoke;

        /// <summary>A hit or miss involving at least one player was resolved (1.6).</summary>
        event Action<CombatResolvedEvent> CombatResolved;

        /// <summary>
        /// A player completed a mission and its reward was committed (1.12). Raised on the player's playfield tick after
        /// the engine paid the mission's own credits, experience and items; what a module pays on top is its own.
        /// </summary>
        event Action<MissionCompletedEvent> MissionCompleted;
    }

    public sealed class PlayerPlayfieldEvent
    {
        public int CharacterId { get; init; }

        public int PlayfieldId { get; init; }

        /// <summary>When the engine raised it, on <see cref="IZoneClock"/> (1.6).</summary>
        public long Timestamp { get; init; }
    }

    public sealed class PlayerSessionEvent
    {
        public int CharacterId { get; init; }

        /// <summary>The playfield the character logged in to or out of.</summary>
        public int PlayfieldId { get; init; }

        /// <summary>When the engine raised it, on <see cref="IZoneClock"/> (1.6).</summary>
        public long Timestamp { get; init; }
    }

    public sealed class NpcSpawnEvent
    {
        public int PlayfieldId { get; init; }

        public int NpcInstance { get; init; }

        /// <summary>The NPC's template hash, or empty when it has none.</summary>
        public string NpcHash { get; init; } = string.Empty;

        /// <summary>When the engine raised it, on <see cref="IZoneClock"/> (1.6).</summary>
        public long Timestamp { get; init; }

        /// <summary>Where the NPC is (1.6).</summary>
        public WorldPosition Position { get; init; }

        public int Level { get; init; }

        /// <summary>The spawn point that owns the NPC (stable for that point across respawns), or 0 when none (1.6).</summary>
        public int SpawnerId { get; init; }
    }

    public sealed class KnubotChatOpenedEvent
    {
        public int PlayfieldId { get; init; }

        public int CharacterId { get; init; }

        public int NpcInstance { get; init; }

        /// <summary>The script's id: its path under the Knubot folder without ".json", '/'-separated.</summary>
        public string ScriptId { get; init; } = string.Empty;

        /// <summary>When the engine raised it, on <see cref="IZoneClock"/> (1.6).</summary>
        public long Timestamp { get; init; }

        /// <summary>The <see cref="Network.MessageSample.Timestamp"/> of the message that opened the chat; 0 when none (1.6).</summary>
        public long RequestTimestamp { get; init; }
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

        /// <summary>When the engine raised it, on <see cref="IZoneClock"/> (1.6).</summary>
        public long Timestamp { get; init; }
    }

    public sealed class PlayerDiedEvent
    {
        public int PlayfieldId { get; init; }

        public int PlayerInstance { get; init; }

        /// <summary>Identity instance of the killer, or 0 when unknown.</summary>
        public int KillerInstance { get; init; }

        public bool KilledByPlayer { get; init; }

        /// <summary>When the engine raised it, on <see cref="IZoneClock"/> (1.6).</summary>
        public long Timestamp { get; init; }
    }

    /// <summary>A player completed a mission (module API 1.12).</summary>
    public sealed class MissionCompletedEvent
    {
        public int CharacterId { get; init; }

        public int PlayfieldId { get; init; }

        /// <summary>The quest's id in the player's journal.</summary>
        public string QuestId { get; init; } = string.Empty;

        /// <summary>True for a generated mission (a terminal roll's offer, or a Quests.json quest with a dungeon); false for an ordinary quest.</summary>
        public bool Generated { get; init; }

        /// <summary>
        /// The GameData <c>Missions/Missions.json</c> mission it ran (its <c>Dungeon.Mission</c>: a terminal's solo or
        /// team mission, or the one a Quests.json dungeon quest names); empty for a quest without a dungeon (1.15).
        /// </summary>
        public string Mission { get; init; } = string.Empty;

        /// <summary>The mission's title as the journal showed it.</summary>
        public string Title { get; init; } = string.Empty;

        /// <summary>The mission's quality (level); 0 for a quest without one.</summary>
        public int Quality { get; init; }

        /// <summary>Of a generated mission's dungeon population, how many were killed and how many there were; 0/0 when unknown.</summary>
        public int Killed { get; init; }

        public int Total { get; init; }

        /// <summary>The credits the engine paid, after any completion split.</summary>
        public int Credits { get; init; }

        /// <summary>The experience the engine awarded, after any completion split.</summary>
        public int Experience { get; init; }

        /// <summary>Whether the mission's reward roll was won; null for a mission without one.</summary>
        public bool? RewardWon { get; init; }

        /// <summary>When the engine raised it, on <see cref="IZoneClock"/>.</summary>
        public long Timestamp { get; init; }
    }
}
