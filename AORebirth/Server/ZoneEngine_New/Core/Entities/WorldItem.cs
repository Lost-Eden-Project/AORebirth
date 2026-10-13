namespace ZoneEngine_New.Core.Entities
{
    using System;

    using AORebirth.Enums;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

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

        /// <summary>
        /// Who may pick it up besides anyone at all: None for a world spawn (Carbonrich Rock), the dropping player for a
        /// dropped item. Pickup also needs the template's Carry flag (<see cref="CanBePickedUpBy"/>).
        /// </summary>
        public Identity Owner { get; init; } = Identity.None;

        public int StackCount { get; init; } = 1;

        /// <summary>When a temporary item (a player drop) leaves the world; null for world spawns.</summary>
        public DateTime? ExpiresAtUtc { get; init; }

        public bool IsExpired(DateTime nowUtc) => ExpiresAtUtc is DateTime expires && nowUtc >= expires;

        /// <summary>Carry-flagged, and either ownerless (world spawn) or owned by <paramref name="player"/> (their drop).</summary>
        public bool CanBePickedUpBy(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);
            CanFlags can = Template.Stats.TryGetValue(CharacterStat.Can, out int value) ? (CanFlags)(uint)value : 0;
            return (can & CanFlags.Carry) != 0 && (Owner.Instance == 0 || Owner == player.Identity);
        }

        /// <summary>
        /// The owner fields must be 0:0; the codec only writes Coordinate/Heading for an ownerless item,
        /// and the client has no Dynels.dat position for a runtime instance.
        /// </summary>
        public override MessageBody BuildSpawnMessage()
        {
            var message = (SimpleItemFullUpdateMessage)base.BuildSpawnMessage();
            message.Identitytype = 0;
            message.Instance = 0;
            return message;
        }

        protected override GameTuple<CharacterStat, uint>[] BuildStats()
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

        static GameTuple<CharacterStat, uint> Stat(CharacterStat stat, int value)
            => new() { Value1 = stat, Value2 = (uint)value };
    }
}
