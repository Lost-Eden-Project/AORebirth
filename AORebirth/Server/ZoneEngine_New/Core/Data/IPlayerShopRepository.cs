namespace ZoneEngine_New.Core.Data
{
    using System.Collections.Generic;

    /// <summary>What a <see cref="PlayerShopWrite"/> does to a player shop's rows (player_shops, player_shop_items).</summary>
    public enum PlayerShopWriteKind
    {
        Rent = 1,
        Rename = 2,
        Abandon = 3,
        Stock = 4,
        Price = 5,
        Unstock = 6,
        Sale = 7,
        Withdraw = 8
    }

    /// <summary>
    /// One player shop change, stored in the same transaction as the acting character's item rows and stats
    /// (<see cref="InventoryMutationBatch.PlayerShop"/>). The database refuses it, rolling the whole transaction back,
    /// when the shop's rows are not what the server's memory says they are.
    /// </summary>
    public sealed record PlayerShopWrite(PlayerShopWriteKind Kind, int ShopId, int OwnerId)
    {
        public int PlayfieldId { get; init; }
        public int TerminalInstance { get; init; }
        public string Name { get; init; } = string.Empty;
        public int ItemInstanceId { get; init; }
        public int Price { get; init; }
        public int ExpectedPrice { get; init; }
        public long Amount { get; init; }
        public int MaxItems { get; init; }
        public long MaxBalance { get; init; }
    }

    /// <summary>A rented shop as stored.</summary>
    public sealed record PlayerShopRecord(int ShopId, int PlayfieldId, int TerminalInstance, int OwnerId, string OwnerName,
        string Name, long Balance);

    /// <summary>Every rented shop, its items' rows (item_instances, container 0x790) and their asking prices.</summary>
    public sealed record PlayerShopSnapshot(IReadOnlyList<PlayerShopRecord> Shops, IReadOnlyList<ItemInstanceRecord> Items,
        IReadOnlyDictionary<int, int> PricesByItem, IReadOnlyDictionary<int, int> ShopByItem);

    /// <summary>Reads every player shop at startup. Changes go through the acting character's lane, never through here.</summary>
    public interface IPlayerShopRepository
    {
        PlayerShopSnapshot LoadAll();
    }
}
