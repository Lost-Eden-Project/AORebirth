namespace SmokeLounge.AOtomation.Messaging.Messages.N3Messages
{
    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Serialization.MappingAttributes;

    /// <summary>
    /// Client request to drop an inventory item into the world (Gamecode.dll n3EngineClientAnarchy_t::N3Msg_DropItem
    /// (Identity_t item, Vector3_t position)). Captured 2026-10-12: Identity = the player, Unknown 0,
    /// Item = Inventory(0x68):73, Position = (671.0, 72.8, 496.5).
    /// </summary>
    [AoContract((int)N3MessageType.DropTemplate)]
    public class DropTemplateMessage : N3Message
    {
        public DropTemplateMessage()
        {
            this.N3MessageType = N3MessageType.DropTemplate;
        }

        /// <summary>The inventory slot being dropped (page type, placement).</summary>
        public Identity Item { get; set; }

        /// <summary>Where the client wants the item to land.</summary>
        public Vector3 Position { get; set; }
    }
}
