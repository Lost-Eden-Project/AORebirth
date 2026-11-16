namespace ZoneEngine_New.Core.Trade
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    using AORebirth.Enums;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>The ICC Commendations shop (<see cref="IccShopCatalog"/>): items bought with stat 695 at an ICC terminal.</summary>
    public sealed partial class TradeService
    {
        /// <summary>
        /// One purchase (<c>IccPurchase</c>): at an ICC terminal within reach, an item the character could roll at its
        /// level, at a QL it could roll it at, paid in ICC Commendations. The item and the new balance are saved in one
        /// transaction, as a shop purchase paid in another currency is, then the item is granted and the stat sent.
        /// </summary>
        public IccPurchaseStatus BuyIcc(Player player, Identity terminal, int itemId, int quality, IccShopCatalog shop, out int price)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(shop);
            price = 0;

            if (player.Session == null || player.Playfield == null || player.IsDead || !player.Inventory.IsHydrated
                || player.IsPersistenceQuarantined)
                return IccPurchaseStatus.Failed;
            // A trade window open holds items and credits the purchase must not cross.
            if (TryGetSession(player, out _))
                return IccPurchaseStatus.Busy;

            if (!player.Playfield.GetRequiredService<DynelRegistry>().TryGet(terminal, out Dynel? dynel)
                || dynel is not VendingMachine machine || !shop.IsTerminal(machine.Template.Id)
                || player.GetEdgeDistanceTo(machine) > RangeCancelDistance)
                return IccPurchaseStatus.NotAtTerminal;

            if (!shop.TryGet(itemId, out IccShopItem entry))
                return IccPurchaseStatus.UnknownItem;
            int level = player.Stats.GetOrOne(CharacterStat.Level);
            if (!shop.TryOffer(entry, level, quality, out int lowId, out int highId, out price))
                return IccPurchaseStatus.QualityNotAllowed;

            int balance = player.Stats.GetOrZero(CharacterStat.Commendations);
            if (balance < price)
                return IccPurchaseStatus.NotEnoughCommendations;

            Item item = _minter.Create(lowId, highId, quality, ItemSource.Vendor);
            if (TradeRules.IsUnique(item) && TradeRules.WouldDuplicateUnique(player, item.LowId, item.HighId))
                return IccPurchaseStatus.UniqueHeld;
            item.AssignInstanceId(_ids.Allocate());

            var deliveries = new List<Delivery>();
            if (!TryPlanDeliveries([item], player, deliveries))
                return IccPurchaseStatus.NoRoom;

            int remaining = balance - price;
            int cash = player.Stats.GetOrZero(CharacterStat.Cash);
            bool durable = false;
            try
            {
                _flush.WithExclusivePlayers(player, null, () =>
                {
                    deliveries.Clear();
                    PlanDeliveries([item], player, deliveries);
                    PersistPlan(player, cash, null, 0, deliveries, [], machine.Identity, CharacterStat.Commendations, remaining);
                    durable = true;
                    ApplyDeliveries(deliveries);
                    player.Stats.Set(CharacterStat.Commendations, remaining, StatDetail.Base, dirty: true);
                    foreach (Delivery delivery in deliveries)
                        SendDelivery(delivery);
                });
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "ICC shop purchase was not saved character=" + player.Identity.Instance);
                if (durable || exception is DatabaseCommitOutcomeUnknownException or DatabaseStateConflictException)
                {
                    player.QuarantinePersistence();
                    player.Session?.Close();
                }
                else
                {
                    _flush.NotifyDirty(player);
                }
                return IccPurchaseStatus.Failed;
            }

            player.FlushDirtyStats();
            _logger.Info(string.Format(CultureInfo.InvariantCulture,
                "ICC shop purchase character={0} item={1} ({2}/{3}) ql={4} price={5} commendations={6}",
                player.Identity.Instance, entry.Name, lowId, highId, quality, price, remaining));
            return IccPurchaseStatus.Bought;
        }
    }
}
