namespace ZoneEngine_New.Core.Data
{
    using System.Collections.Generic;
    using System.Linq;
    using AORebirth.Interfaces.Persistence.Characters;
    using ZoneEngine_New.Core.Logging;
    using static SharedCharacterPersistence;

    /// <summary>
    /// Reads player_shops, player_shop_items and the shops' item rows. A read that fails throws: without its shops the
    /// zone would offer rented terminals for rent again and hide what they hold, so startup stops instead.
    /// </summary>
    public sealed class MySqlPlayerShopRepository : IPlayerShopRepository
    {
        readonly ICharacterPersistenceDao _persistence;
        readonly IZoneLogger? _logger;

        public MySqlPlayerShopRepository(IZoneLogger? logger = null, ICharacterPersistenceDao? persistence = null)
        {
            _logger = logger;
            _persistence = persistence ?? Create();
        }

        public PlayerShopSnapshot LoadAll() => Run(() =>
        {
            PlayerShopRecord[] shops = _persistence.LoadPlayerShops()
                .Select(v => new PlayerShopRecord(v.ShopId, v.PlayfieldId, v.TerminalInstance, v.OwnerId, v.OwnerName ?? string.Empty,
                    v.Name ?? string.Empty, v.Balance))
                .ToArray();
            var prices = new Dictionary<int, int>();
            var shopByItem = new Dictionary<int, int>();
            foreach (PersistedPlayerShopPriceData price in _persistence.LoadPlayerShopPrices())
            {
                prices[price.ItemInstanceId] = price.Price;
                shopByItem[price.ItemInstanceId] = price.ShopId;
            }
            ItemInstanceRecord[] items = _persistence.LoadPlayerShopItems().Select(Map).ToArray();
            return new PlayerShopSnapshot(shops, items, prices, shopByItem);
        }, _logger);
    }
}
