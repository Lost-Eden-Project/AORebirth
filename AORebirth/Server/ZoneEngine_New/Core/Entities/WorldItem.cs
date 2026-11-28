namespace ZoneEngine_New.Core.Entities
{
    using System;
    using System.Threading;

    using AORebirth.Enums;

    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages;
    using LostEden.Protocol.Messages.N3Messages;

    using ZoneEngine_New.Core.Inventory;

    /// <summary>
    /// Item template spawned into the world from a hash. Sends the world-item stat set seen in retail
    /// SimpleItemFullUpdate captures instead of the full inventory template stats.
    /// </summary>
    public class WorldItem : StaticDynel
    {
        const int WorldItemFlags = (int)(ItemFlags.Visible | ItemFlags.TellCollision | ItemFlags.DisableStatelCollision);

        public WorldItem(Identity identity, ItemTemplate template, int lowId, int highId, int quality)
            : base(identity, template)
        {
            LowId = lowId;
            HighId = highId;
            Quality = quality;
        }

        public int LowId { get; }

        public int HighId { get; }

        public int Quality { get; }

        public override int ItemTemplateLowId => LowId;

        public override int ItemQuality => Quality;

        /// <summary>
        /// Who may pick it up besides anyone at all: None for a world spawn (Carbonrich Rock), the dropping player for a
        /// dropped item. Pickup also needs the template's Carry flag (<see cref="CanBePickedUpBy"/>).
        /// </summary>
        public Identity Owner { get; init; } = Identity.None;

        public int StackCount { get; init; } = 1;

        /// <summary>When a temporary item (a player drop) leaves the world; null for world spawns.</summary>
        public DateTime? ExpiresAtUtc { get; init; }

        /// <summary>
        /// For a dropped item: the drop's inventory change, marked once it is stored. Until then nobody picks the item
        /// up (<see cref="CanBePickedUpBy"/>): a drop whose retire never stores (its owner's lane failed and was
        /// discarded) must never be picked up as a second copy of the row storage still holds. Null for world spawns.
        /// </summary>
        public DropSettlement? Settlement { get; init; }

        public bool IsExpired(DateTime nowUtc) => ExpiresAtUtc is DateTime expires && nowUtc >= expires;

        /// <summary>
        /// Carry-flagged, either ownerless (world spawn) or owned by <paramref name="player"/> (their drop), and, for a
        /// drop, with the drop itself stored.
        /// </summary>
        public bool CanBePickedUpBy(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);
            if (Settlement is { IsStored: false })
                return false;
            CanFlags can = Template.Stats.TryGetValue(CharacterStat.Can, out int value) ? (CanFlags)(uint)value : 0;
            return (can & CanFlags.Carry) != 0 && (Owner.Instance == 0 || Owner == player.Identity);
        }

        protected override GameTuple<Stat, int>[] BuildStats()
        {
            int flags = Template.Stats.TryGetValue(CharacterStat.Flags, out int templateFlags) ? templateFlags : 0;
            return
            [
                Stat(CharacterStat.Flags, flags | WorldItemFlags),
                Stat(CharacterStat.StaticInstance, LowId),
                Stat(CharacterStat.ACGItemLevel, Quality),
                Stat(CharacterStat.ACGItemTemplateID, LowId),
                Stat(CharacterStat.ACGItemTemplateID2, HighId),
                Stat(CharacterStat.MultipleCount, Math.Max(1, StackCount))
            ];
        }

        static GameTuple<Stat, int> Stat(CharacterStat stat, int value) => new((Stat)stat, value);
    }

    /// <summary>
    /// Whether a dropped item's inventory change (the retire of its row) has been stored. Marked on the lane writer
    /// once the transaction commits; read on the playfield tick by pickup. Never marked when the lane fails or is
    /// discarded, so the drop stays on the ground untaken until it expires.
    /// </summary>
    public sealed class DropSettlement
    {
        int _stored;

        public bool IsStored => Volatile.Read(ref _stored) != 0;

        public void MarkStored() => Volatile.Write(ref _stored, 1);
    }
}
