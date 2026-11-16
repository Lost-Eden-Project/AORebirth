namespace SmokeLounge.AOtomation.Messaging.Messages.N3Messages
{
    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Serialization.MappingAttributes;

    /// <summary>
    /// Lost Eden's own, not stock's: the client buys an item from the ICC Commendations shop. The identity is the
    /// client's character; <see cref="Terminal"/> the shop terminal it bought at; <see cref="ItemId"/> the shop item (the
    /// first template id of its QL steps in GameData/Shops/IccShop.json) and <see cref="Quality"/> the QL asked for.
    /// </summary>
    [AoContract((int)N3MessageType.IccPurchase)]
    public class IccPurchaseMessage : N3Message
    {
        public IccPurchaseMessage()
        {
            this.N3MessageType = N3MessageType.IccPurchase;
        }

        [AoMember(0)]
        public Identity Terminal { get; set; }

        [AoMember(1)]
        public int ItemId { get; set; }

        [AoMember(2)]
        public int Quality { get; set; }
    }

    /// <summary>How an ICC Commendations purchase went (<see cref="IccPurchaseResultMessage.Status"/>).</summary>
    public enum IccPurchaseStatus
    {
        Bought = 0,
        NotAtTerminal = 1,
        UnknownItem = 2,
        QualityNotAllowed = 3,
        NotEnoughCommendations = 4,
        NoRoom = 5,
        UniqueHeld = 6,
        Failed = 7,
        Busy = 8,
    }

    /// <summary>
    /// Lost Eden's own, not stock's: the answer to an <see cref="IccPurchaseMessage"/>, sent to the buyer. A bought item
    /// arrives in the inventory and the new balance as stat 695, as any other; this tells the shop window how it went.
    /// </summary>
    [AoContract((int)N3MessageType.IccPurchaseResult)]
    public class IccPurchaseResultMessage : N3Message
    {
        public IccPurchaseResultMessage()
        {
            this.N3MessageType = N3MessageType.IccPurchaseResult;
        }

        [AoMember(0)]
        public int ItemId { get; set; }

        [AoMember(1)]
        public int Quality { get; set; }

        /// <summary>An <see cref="IccPurchaseStatus"/>.</summary>
        [AoMember(2)]
        public int Status { get; set; }

        [AoMember(3)]
        public int Price { get; set; }
    }
}
