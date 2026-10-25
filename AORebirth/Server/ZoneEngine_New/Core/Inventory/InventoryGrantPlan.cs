namespace ZoneEngine_New.Core.Inventory
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;

    /// <summary>
    /// Pure capacity/identity plan for a mission-owned transaction. The caller holds
    /// PersistenceGate through its existing mission DAO commit and PublishAfterCommit.
    /// This class never starts an independent transaction or allocates an identity.
    /// </summary>
    public sealed class InventoryGrantPlan
    {
        readonly Player _player;
        readonly Item[] _items;
        readonly Container[] _pages;
        readonly int[] _slots;
        bool _published;

        InventoryGrantPlan(Player player, Item[] items, Container[] pages, int[] slots)
        {
            _player = player; _items = items; _pages = pages; _slots = slots;
            Rows = items.Select((item, i) => item.ToRecord(pages[i].Identity, slots[i], item.StackCount)).ToArray();
        }

        public IReadOnlyList<ItemInstanceRecord> Rows { get; }

        /// <summary>
        /// Plans a place for each of <paramref name="items"/>: free inventory slots first, then, with
        /// <paramref name="allowOverflow"/>, free overflow slots in the order the client fills them. False when they do
        /// not all fit.
        /// </summary>
        public static bool TryCreate(Player player, IReadOnlyList<Item> items, out InventoryGrantPlan plan, bool allowOverflow = false)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(items);
            plan = null!;
            if (!Monitor.IsEntered(player.PersistenceGate)) throw new InvalidOperationException("Grant planning requires the player's persistence gate.");
            if (player.IsPersistenceQuarantined || !player.Inventory.IsHydrated
                || !(allowOverflow ? player.Inventory.HasRoomFor(items.Count) : player.Inventory.HasFreeInventorySlots(items.Count))
                || items.Any(item => item.InstanceId <= 0 || item.IsPersisted || item.StackCount <= 0)
                || items.Select(item => item.InstanceId).Distinct().Count() != items.Count) return false;
            // No grant may give a Unique the player holds, or the same Unique twice; callers leave those out first.
            if (UniqueItems.HasRepeatedUnique(items, out _)) return false;
            foreach (Item item in items)
            {
                if (UniqueItems.WouldDuplicate(player, item, out _)) return false;
            }
            Container page = player.Inventory.Inventory;
            int[] inventorySlots = Enumerable.Range(page.Offset, page.Capacity).Where(slot => !page.Content.ContainsKey(slot)).Take(items.Count).ToArray();
            int[] overflowSlots = player.Inventory.FreeOverflowSlots(items.Count - inventorySlots.Length);
            if (inventorySlots.Length + overflowSlots.Length < items.Count) return false;
            var pages = new Container[items.Count];
            var slots = new int[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                bool inInventory = i < inventorySlots.Length;
                pages[i] = inInventory ? page : player.Inventory.Overflow;
                slots[i] = inInventory ? inventorySlots[i] : overflowSlots[i - inventorySlots.Length];
            }

            plan = new InventoryGrantPlan(player, items.ToArray(), pages, slots);
            return true;
        }

        /// <summary>
        /// Puts the committed items in their planned slots. With <paramref name="notify"/> the client is told (AddTemplate
        /// for the inventory, an overflow grant for overflow) and overflow is then kept to its capacity; a caller that
        /// tells the client itself calls <see cref="OverflowGrants.Trim"/> after it has.
        /// </summary>
        public bool PublishAfterCommit(bool notify = true)
        {
            if (!Monitor.IsEntered(_player.PersistenceGate)) throw new InvalidOperationException("Grant publication requires the player's persistence gate.");
            if (_published) return false;
            for (int i = 0; i < _items.Length; i++)
                if (_pages[i].Content.ContainsKey(_slots[i]) || _items[i].InstanceId != Rows[i].InstanceId || _items[i].IsPersisted)
                    throw new InvalidOperationException("Mission inventory grant plan changed before publication.");
            _published = true;
            for (int i = 0; i < _items.Length; i++)
            {
                _pages[i].Add(_slots[i], _items[i]);
                _items[i].IsPersisted = true;
                if (ReferenceEquals(_pages[i], _player.Inventory.Overflow))
                    _player.Inventory.NoteOverflowArrival(_items[i]);
            }
            if (!notify)
                return true;

            for (int i = 0; i < _items.Length; i++)
            {
                Item item = _items[i];
                if (ReferenceEquals(_pages[i], _player.Inventory.Overflow))
                {
                    OverflowGrants.Send(_player, item);
                    continue;
                }

                _player.Session?.Send(new AddTemplateMessage
                {
                    Identity = _player.Identity, LowId = item.LowId, HighId = item.HighId,
                    Quality = item.Quality, Count = item.StackCount
                });
            }

            OverflowGrants.Trim(_player);
            return true;
        }
    }
}
