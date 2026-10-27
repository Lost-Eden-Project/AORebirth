namespace ZoneEngine_New.Core.Inventory
{
    using System;
    using System.Collections.Generic;

    using AORebirth.Enums;

    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;

    /// <summary>
    /// The one answer to "does this player already hold this Unique": every carried page and overflow, the interior of
    /// every carried bag (also one in overflow, and one never opened this session, read from its stored rows), and the
    /// player's own items on an open trade, shop or Knubot table (taken off their page but still the player's). The
    /// bank is not held: it may keep a copy, but nothing brings one from it into a carried page while another is held.
    /// <see cref="Trade.TradeRules.WouldDuplicateUnique"/> answers through this, so every grant path checks the same set.
    /// </summary>
    public static class UniqueItems
    {
        /// <summary>The template is flagged Unique.</summary>
        public static bool IsUnique(ItemTemplate? template) => template != null && (template.Flags & (int)ItemFlags.Unique) != 0;

        /// <summary>
        /// True when <paramref name="player"/> holds an item of the <paramref name="lowId"/>/<paramref name="highId"/>
        /// family. <paramref name="except"/> items do not count (an item leaving the carried pages in the same move).
        /// </summary>
        public static bool Holds(Player player, int lowId, int highId, Func<Item, bool>? except = null)
        {
            ArgumentNullException.ThrowIfNull(player);
            PlayerInventory inventory = player.Inventory;
            if (!inventory.IsHydrated)
                return false;

            List<int>? unopened = null;
            foreach (Item held in inventory.EnumerateHeldItems())
            {
                if (except != null && except(held))
                    continue;
                if (SameFamily(held.LowId, held.HighId, lowId, highId))
                    return true;
                if (InventoryMoveService.IsBagItem(held) && !inventory.IsBagHydrated(held))
                    (unopened ??= new List<int>()).Add(BagContainerId(held));
            }

            if (player.Playfield?.GetService<Trade.TradeService>() is { } trades
                && trades.TryGetSession(player, out Trade.TradeSession session))
            {
                foreach (Item offered in session.OfferFor(player).Items.Values)
                {
                    if (except != null && except(offered))
                        continue;
                    if (SameFamily(offered.LowId, offered.HighId, lowId, highId))
                        return true;
                    if (!InventoryMoveService.IsBagItem(offered))
                        continue;

                    // A bag on the table still holds its contents, and they come back with it if the trade is cancelled;
                    // without this, a unique inside it could be taken again while it sat there.
                    if (!inventory.TryGetBackpackPage(offered.Identity, out Container bag) || !bag.IsHydrated)
                    {
                        (unopened ??= new List<int>()).Add(BagContainerId(offered));
                        continue;
                    }
                    foreach (Item content in bag.Content.Values)
                    {
                        if ((except == null || !except(content)) && SameFamily(content.LowId, content.HighId, lowId, highId))
                            return true;
                    }
                }
            }

            return unopened != null && StoredRowsHold(player, unopened, lowId, highId);
        }

        /// <summary>
        /// The first Unique that <paramref name="incoming"/> (or, for a bag, its contents) would duplicate on
        /// <paramref name="player"/>; <paramref name="except"/> as in <see cref="Holds"/>.
        /// </summary>
        public static bool WouldDuplicate(Player player, Item incoming, out string name, Func<Item, bool>? except = null)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(incoming);
            name = string.Empty;
            bool Skip(Item item) => ReferenceEquals(item, incoming) || (except != null && except(item));

            if (Trade.TradeRules.IsUnique(incoming) && Holds(player, incoming.LowId, incoming.HighId, Skip))
            {
                name = incoming.Name;
                return true;
            }

            if (!InventoryMoveService.IsBagItem(incoming))
                return false;

            // A bag brings its contents: from its page when loaded, else from its stored rows.
            if (player.Inventory.TryGetBackpackPage(incoming.Identity, out Container page) && page.IsHydrated)
            {
                foreach (Item content in page.Content.Values)
                {
                    if (Trade.TradeRules.IsUnique(content) && Holds(player, content.LowId, content.HighId, Skip))
                    {
                        name = content.Name;
                        return true;
                    }
                }

                return false;
            }

            IInventoryRepository? repository = player.Playfield?.GetService<IInventoryRepository>();
            IItemTemplateCatalog? catalog = player.Playfield?.GetService<IItemTemplateCatalog>();
            int containerId = BagContainerId(incoming);
            if (repository == null || catalog == null || containerId <= 0)
                return false;
            foreach (ItemInstanceRecord row in repository.GetContainerItems(containerId))
            {
                if (catalog.TryGet(row.LowId, out ItemTemplate template) && IsUnique(template)
                    && Holds(player, row.LowId, row.HighId, Skip))
                {
                    name = template.Name;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Two of <paramref name="items"/> are the same Unique family (one grant carrying it twice).</summary>
        public static bool HasRepeatedUnique(IReadOnlyList<Item> items, out string name)
        {
            ArgumentNullException.ThrowIfNull(items);
            name = string.Empty;
            for (int i = 0; i < items.Count; i++)
            {
                if (!Trade.TradeRules.IsUnique(items[i]))
                    continue;
                for (int j = i + 1; j < items.Count; j++)
                {
                    if (SameFamily(items[i].LowId, items[i].HighId, items[j].LowId, items[j].HighId))
                    {
                        name = items[i].Name;
                        return true;
                    }
                }
            }

            return false;
        }

        static bool SameFamily(int heldLow, int heldHigh, int lowId, int highId)
            => heldLow == lowId || heldLow == highId || heldHigh == lowId || heldHigh == highId;

        static int BagContainerId(Item bag) => bag.Identity.Instance > 0 ? bag.Identity.Instance : bag.InstanceId;

        static bool StoredRowsHold(Player player, List<int> containerIds, int lowId, int highId)
        {
            IInventoryRepository? repository = player.Playfield?.GetService<IInventoryRepository>();
            if (repository == null)
                return false;
            foreach (ItemInstanceRecord row in repository.GetContainerItems(containerIds))
            {
                if (SameFamily(row.LowId, row.HighId, lowId, highId))
                    return true;
            }

            return false;
        }
    }
}
