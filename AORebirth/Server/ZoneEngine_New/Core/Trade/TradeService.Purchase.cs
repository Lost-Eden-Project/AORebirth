namespace ZoneEngine_New.Core.Trade
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    using AORebirth.Enums;

    using LostEden.Protocol.GameData;

    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>How a <see cref="TradeService.BuyForStat"/> went.</summary>
    public enum PurchaseStatus
    {
        Bought,
        NotAtAnchor,
        Insufficient,
        NoRoom,
        UniqueHeld,
        Failed,
        Busy,
    }

    /// <summary>
    /// A purchase outside the stock shop window: one item for a stat, at a terminal or NPC within reach. The shop that
    /// decides what is for sale and at what price is a module's (module API 1.12 <c>player.purchase</c>); this is the
    /// part that must not be wrong: reach, the open-window check, uniqueness, room, and the item and the new balance
    /// saved in one transaction before either is applied.
    /// </summary>
    public sealed partial class TradeService
    {
        /// <summary>
        /// Sells <paramref name="player"/> one item (<paramref name="lowId"/>/<paramref name="highId"/> at
        /// <paramref name="quality"/>) for <paramref name="price"/> of <paramref name="currency"/>, at
        /// <paramref name="anchor"/> (a dynel on the player's playfield within <see cref="RangeCancelDistance"/>). On
        /// the playfield tick.
        /// </summary>
        public PurchaseStatus BuyForStat(Player player, Identity anchor, int lowId, int highId, int quality, CharacterStat currency, int price)
        {
            ArgumentNullException.ThrowIfNull(player);
            if (price < 0)
                throw new ArgumentOutOfRangeException(nameof(price));
            if (player.Playfield is Playfield owner)
                owner.RequireTick();

            if (player.Session == null || player.Playfield == null || player.IsDead || !player.Inventory.IsHydrated
                || player.IsPersistenceQuarantined)
                return PurchaseStatus.Failed;
            // A trade or shop window open holds items and credits the purchase must not cross.
            if (TryGetSession(player, out _))
                return PurchaseStatus.Busy;

            if (!player.Playfield.GetRequiredService<DynelRegistry>().TryGet(anchor, out Dynel? dynel) || dynel == null
                || !ReferenceEquals(dynel.Playfield, player.Playfield) || player.GetEdgeDistanceTo(dynel) > RangeCancelDistance)
                return PurchaseStatus.NotAtAnchor;

            int balance = player.Stats.GetOrZero(currency);
            if (balance < price)
                return PurchaseStatus.Insufficient;

            Item item = _minter.Create(lowId, highId, quality, ItemSource.Vendor);
            if (TradeRules.IsUnique(item) && TradeRules.WouldDuplicateUnique(player, item.LowId, item.HighId))
                return PurchaseStatus.UniqueHeld;
            item.AssignInstanceId(_ids.Allocate());

            var deliveries = new List<Delivery>();
            if (!TryPlanDeliveries([item], player, deliveries))
                return PurchaseStatus.NoRoom;

            int remaining = balance - price;
            int cash = player.Stats.GetOrZero(CharacterStat.Cash);
            bool durable = false;
            try
            {
                _flush.WithExclusivePlayers(player, null, () =>
                {
                    deliveries.Clear();
                    PlanDeliveries([item], player, deliveries);
                    if (currency == CharacterStat.Cash)
                        PersistPlan(player, remaining, null, 0, deliveries, [], dynel.Identity);
                    else
                        PersistPlan(player, cash, null, 0, deliveries, [], dynel.Identity, currency, remaining);
                    durable = true;
                    ApplyDeliveries(deliveries);
                    player.Stats.Set(currency, remaining, StatDetail.Base, dirty: true);
                    foreach (Delivery delivery in deliveries)
                        SendDelivery(delivery);
                });
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Purchase was not saved character=" + player.Identity.Instance);
                if (durable || exception is DatabaseCommitOutcomeUnknownException or DatabaseStateConflictException)
                {
                    player.QuarantinePersistence();
                    player.Session?.Close();
                }
                else
                {
                    _flush.NotifyDirty(player);
                }
                return PurchaseStatus.Failed;
            }

            player.FlushDirtyStats();
            _logger.Info(string.Format(CultureInfo.InvariantCulture,
                "Purchase character={0} item={1}/{2} ql={3} price={4} stat={5} balance={6} at={7}:{8}",
                player.Identity.Instance, lowId, highId, quality, price, (int)currency, remaining, (int)anchor.Type, anchor.Instance));
            return PurchaseStatus.Bought;
        }
    }
}
