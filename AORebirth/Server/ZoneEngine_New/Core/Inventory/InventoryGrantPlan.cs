namespace ZoneEngine_New.Core.Inventory
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using AORebirth.Modules.Events;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Modules;

    /// <summary>Why <see cref="InventoryGrantPlan.TryCreate(Player, IReadOnlyList{Item}, out InventoryGrantPlan, out GrantRefusal, bool)"/> refused.</summary>
    public enum GrantRefusal
    {
        None,
        Quarantined,
        NotHydrated,
        NoRoom,
        InvalidItem,
        UniqueRepeated,
        UniqueHeld
    }

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
            => TryCreate(player, items, out plan, out _, allowOverflow);

        /// <summary>
        /// <see cref="TryCreate(Player, IReadOnlyList{Item}, out InventoryGrantPlan, bool)"/>, also giving why it refused
        /// (<see cref="GrantRefusal.None"/> on success). Every refusal is logged with its reason.
        /// </summary>
        public static bool TryCreate(Player player, IReadOnlyList<Item> items, out InventoryGrantPlan plan, out GrantRefusal refusal,
            bool allowOverflow = false)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(items);
            plan = null!;
            if (!Monitor.IsEntered(player.PersistenceGate)) throw new InvalidOperationException("Grant planning requires the player's persistence gate.");
            refusal = Check(player, items, allowOverflow, out string detail);
            if (refusal != GrantRefusal.None)
            {
                Log.Warn("Inventory grant refused char=" + player.Identity.Instance + " reason=" + refusal
                    + (detail.Length == 0 ? string.Empty : " (" + detail + ")") + " overflow=" + allowOverflow
                    + " items=" + string.Join(", ", items.Select(item => item.Name + " " + item.LowId + "/" + item.HighId + " x" + item.StackCount)));
                return false;
            }

            Container page = player.Inventory.Inventory;
            int[] inventorySlots = Enumerable.Range(page.Offset, page.Capacity).Where(slot => !page.Content.ContainsKey(slot)).Take(items.Count).ToArray();
            int[] overflowSlots = allowOverflow ? player.Inventory.FreeOverflowSlots(items.Count - inventorySlots.Length) : [];
            if (inventorySlots.Length + overflowSlots.Length < items.Count)
            {
                refusal = GrantRefusal.NoRoom;
                Log.Warn("Inventory grant refused char=" + player.Identity.Instance + " reason=" + refusal + " (no free slot left to plan)"
                    + " overflow=" + allowOverflow + " items=" + items.Count);
                return false;
            }
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

        static GrantRefusal Check(Player player, IReadOnlyList<Item> items, bool allowOverflow, out string detail)
        {
            detail = string.Empty;
            if (player.IsPersistenceQuarantined)
                return GrantRefusal.Quarantined;
            if (!player.Inventory.IsHydrated)
                return GrantRefusal.NotHydrated;
            if (!(allowOverflow ? player.Inventory.HasRoomFor(items.Count) : player.Inventory.HasFreeInventorySlots(items.Count)))
                return GrantRefusal.NoRoom;
            if (items.FirstOrDefault(item => item.InstanceId <= 0 || item.IsPersisted || item.StackCount <= 0) is Item invalid)
            {
                detail = invalid.Name + " instance=" + invalid.InstanceId + " persisted=" + invalid.IsPersisted + " count=" + invalid.StackCount;
                return GrantRefusal.InvalidItem;
            }
            if (items.Select(item => item.InstanceId).Distinct().Count() != items.Count)
                return GrantRefusal.InvalidItem;
            // No grant may give a Unique the player holds, or the same Unique twice; callers leave those out first.
            if (UniqueItems.HasRepeatedUnique(items, out string repeated))
            {
                detail = repeated + " twice";
                return GrantRefusal.UniqueRepeated;
            }
            foreach (Item item in items)
            {
                if (UniqueItems.WouldDuplicate(player, item, out string held))
                {
                    detail = held + " already held";
                    return GrantRefusal.UniqueHeld;
                }
            }

            return GrantRefusal.None;
        }

        static readonly NLog.Logger Log = NLog.LogManager.GetLogger("InventoryGrantPlan");

        /// <summary>
        /// Puts the committed items in their planned slots. With <paramref name="notify"/> the client is told (AddTemplate
        /// for the inventory, an overflow grant for overflow) and overflow is then kept to its capacity; a caller that
        /// tells the client itself calls <see cref="OverflowGrants.Trim"/> after it has. <paramref name="from"/>: where the
        /// items came from, for module item moves; none (created) by default.
        /// </summary>
        public bool PublishAfterCommit(bool notify = true, ItemPlace? from = null)
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
                ModuleHooks.ItemMoved(_player, _items[i], from ?? ModuleHooks.Place(ItemPlaceKind.None), ModuleHooks.Place(ItemPlaceKind.Inventory));
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
