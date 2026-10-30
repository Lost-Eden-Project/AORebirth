namespace AORebirth.Modules.Events
{
    using AORebirth.Modules.World;

    /// <summary>Why the engine pulled a client back (module API 1.6).</summary>
    public enum MovementCorrection
    {
        Rooted,
        Jump,
        Speed,
        Collision,
        Bounds,
        Other
    }

    /// <summary>The engine refused or rewrote a client's position and pulled it back (module API 1.6).</summary>
    public sealed class PlayerMovementCorrectedEvent
    {
        public int CharacterId { get; init; }

        public int PlayfieldId { get; init; }

        /// <summary>When the engine raised it, on <see cref="IZoneClock"/>.</summary>
        public long Timestamp { get; init; }

        /// <summary>The <see cref="Network.MessageSample.Timestamp"/> of the movement message corrected; 0 when none.</summary>
        public long RequestTimestamp { get; init; }

        /// <summary>Where the client said it was.</summary>
        public WorldPosition Reported { get; init; }

        /// <summary>Where the server put it.</summary>
        public WorldPosition Kept { get; init; }

        public MovementCorrection Reason { get; init; }
    }

    /// <summary>Where an item can be (module API 1.6).</summary>
    public enum ItemPlaceKind
    {
        /// <summary>Nowhere: as <see cref="ItemMovedEvent.From"/>, the item was created.</summary>
        None,

        Inventory,
        Equipment,
        Bank,
        Bag,
        Corpse,
        World,
        Trade,
        Shop,
        Mail,
        Reward,

        /// <summary>As <see cref="ItemMovedEvent.To"/>: deleted or consumed.</summary>
        Destroyed
    }

    /// <summary>Where an item is: the kind, and its owner or container (the corpse, the shop NPC, the other trader).</summary>
    public readonly record struct ItemPlace(ItemPlaceKind Kind, EntityRef Owner);

    /// <summary>
    /// An item changed place (module API 1.6): loot, pickups, trades, shops, the bank and rewards alike. Rearranging slots
    /// within one character's inventory is not raised.
    /// </summary>
    public sealed class ItemMovedEvent
    {
        /// <summary>The player it happened to or for.</summary>
        public int CharacterId { get; init; }

        public int PlayfieldId { get; init; }

        /// <summary>When the engine raised it, on <see cref="IZoneClock"/>.</summary>
        public long Timestamp { get; init; }

        /// <summary>The <see cref="Network.MessageSample.Timestamp"/> of the client message that caused it; 0 when none.</summary>
        public long RequestTimestamp { get; init; }

        public ItemRef Item { get; init; }

        /// <summary>The stack count moved; 1 for an unstacked item.</summary>
        public int Count { get; init; }

        public ItemPlace From { get; init; }

        public ItemPlace To { get; init; }
    }

    /// <summary>A chat channel the zone handles (module API 1.6).</summary>
    public enum ChatChannelKind
    {
        Vicinity,
        Say,
        Shout,
        Whisper,
        Emote,
        Team,
        Other
    }

    /// <summary>
    /// A player spoke in chat the zone handles (module API 1.6, <c>observe-chat</c>). Tells and channels go through the
    /// chat server and are not seen here.
    /// </summary>
    public sealed class PlayerSpokeEvent
    {
        public int CharacterId { get; init; }

        public int PlayfieldId { get; init; }

        /// <summary>When the engine raised it, on <see cref="IZoneClock"/>.</summary>
        public long Timestamp { get; init; }

        /// <summary>The <see cref="Network.MessageSample.Timestamp"/> of the chat message; 0 when none.</summary>
        public long RequestTimestamp { get; init; }

        public ChatChannelKind Channel { get; init; }

        /// <summary>The text's length in characters.</summary>
        public int Length { get; init; }

        /// <summary>The text, only for a module granted <c>read-chat-text</c>; empty otherwise.</summary>
        public string Text { get; init; } = string.Empty;
    }

    /// <summary>What dealt the hit (module API 1.6).</summary>
    public enum CombatSource
    {
        AutoAttack,
        Special,
        Nano,
        Item,
        Perk,
        Pet,
        Environment
    }

    /// <summary>How the hit landed (module API 1.6).</summary>
    public enum CombatResult
    {
        Hit,
        Critical,
        Miss,
        Evaded,
        Resisted,
        Blocked
    }

    /// <summary>
    /// A resolved hit or miss involving at least one player, as attacker or target (module API 1.6). NPC-against-NPC fights
    /// are left out.
    /// </summary>
    public sealed class CombatResolvedEvent
    {
        public int PlayfieldId { get; init; }

        /// <summary>When the engine raised it, on <see cref="IZoneClock"/>.</summary>
        public long Timestamp { get; init; }

        public EntityRef Attacker { get; init; }

        public EntityRef Target { get; init; }

        public CombatSource Source { get; init; }

        /// <summary>The special's stat id, the nano id, the item's template id or the perk id; 0 for auto-attacks.</summary>
        public int SourceId { get; init; }

        public CombatResult Result { get; init; }

        /// <summary>Damage dealt (after absorbs); 0 for a miss.</summary>
        public int Amount { get; init; }

        /// <summary>The damage type stat (AC stat 90-97), or 0 when none.</summary>
        public int DamageType { get; init; }
    }
}
