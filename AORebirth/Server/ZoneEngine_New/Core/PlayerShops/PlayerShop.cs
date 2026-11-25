namespace ZoneEngine_New.Core.PlayerShops
{
    using System;
    using System.Collections.Generic;
    using System.Threading;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;

    /// <summary>
    /// One rented player shop, in memory. Only its playfield's tick reads or changes it; a lane writer only marks a
    /// <see cref="PlayerShopWriteToken"/> stored, which the tick then applies (<see cref="PlayerShopService.Tick"/>).
    /// </summary>
    /// <remarks>
    /// Every change is applied here on the tick and stored afterwards in the acting character's lane. A listing whose last
    /// change is not stored yet is <see cref="PlayerShopListing.Pending"/>: nobody may buy, reprice or take it back until
    /// its write is stored. That orders every write of an item across lanes: the owner's stock is stored before any
    /// buyer's sale of it can be queued, and a sale before anything else can touch it. <see cref="Balance"/> counts only
    /// stored sales (and is lowered at once by a withdrawal), so it never exceeds what the database holds.
    /// </remarks>
    public sealed class PlayerShop
    {
        readonly SortedDictionary<int, PlayerShopListing> _listings = new();
        readonly List<PlayerShopWriteToken> _pending = new();
        readonly List<Player> _viewers = new();

        /// <summary>
        /// Slots a sale or take-back emptied whose write is not stored yet: the sold item's row still holds the slot in
        /// storage until then, so the slot is not stocked again (another lane's write could land first) and still counts
        /// toward the shop's limit.
        /// </summary>
        readonly HashSet<int> _reserved = new();

        public PlayerShop(int shopId, int playfieldId, int terminalInstance, int ownerId, string ownerName, string name, long balance)
        {
            if (shopId <= 0)
                throw new ArgumentOutOfRangeException(nameof(shopId));
            if (ownerId <= 0)
                throw new ArgumentOutOfRangeException(nameof(ownerId));
            if (balance < 0)
                throw new ArgumentOutOfRangeException(nameof(balance));
            ShopId = shopId;
            PlayfieldId = playfieldId;
            TerminalInstance = terminalInstance;
            OwnerId = ownerId;
            OwnerName = ownerName ?? string.Empty;
            Name = name ?? string.Empty;
            Balance = balance;
        }

        public int ShopId { get; }

        public int PlayfieldId { get; }

        public int TerminalInstance { get; }

        public int OwnerId { get; }

        public string OwnerName { get; }

        public string Name { get; internal set; }

        /// <summary>Credits the owner may withdraw: stored sales, less withdrawals already taken.</summary>
        public long Balance { get; internal set; }

        /// <summary>Credits of sales applied but not stored yet; they reach <see cref="Balance"/> once stored.</summary>
        public long PendingSales { get; internal set; }

        /// <summary>The rent is not stored yet: only the owner sees the shop, and nothing can be done at it.</summary>
        public bool Opening { get; internal set; }

        /// <summary>The owner gave it up and that is being stored: nothing can be done at it.</summary>
        public bool Closing { get; internal set; }

        /// <summary>A write of this shop could not be stored: it stays shut until the server is restarted.</summary>
        public bool Locked { get; internal set; }

        public IReadOnlyDictionary<int, PlayerShopListing> Listings => _listings;

        public int Count => _listings.Count;

        /// <summary>Writes applied in memory that are not yet known to be stored.</summary>
        public bool HasPendingWrites => _pending.Count > 0;

        /// <summary>How many writes are applied in memory and not yet known to be stored.</summary>
        public int PendingWriteCount => _pending.Count;

        internal IReadOnlyList<Player> Viewers => _viewers;

        internal bool TryGetListing(int slot, int itemInstance, out PlayerShopListing listing)
            => _listings.TryGetValue(slot, out listing!) && listing.Item.InstanceId == itemInstance;

        /// <summary>The first slot from 0 up that holds nothing and is not reserved, below <paramref name="maxItems"/>; -1 when full.</summary>
        internal int FirstFreeSlot(int maxItems)
        {
            for (int slot = 0; slot < maxItems; slot++)
            {
                if (IsSlotFree(slot))
                    return slot;
            }
            return -1;
        }

        internal bool IsSlotFree(int slot) => !_listings.ContainsKey(slot) && !_reserved.Contains(slot);

        /// <summary>Removes the listing and holds its slot until <see cref="ReleaseSlot"/> (its write is stored).</summary>
        internal void RemoveListingKeepingSlot(PlayerShopListing listing)
        {
            RemoveListing(listing);
            _reserved.Add(listing.Slot);
        }

        internal void ReleaseSlot(int slot) => _reserved.Remove(slot);

        internal void AddListing(PlayerShopListing listing)
        {
            if (!_listings.TryAdd(listing.Slot, listing))
                throw new InvalidOperationException("Player shop slot is already taken.");
        }

        internal void RemoveListing(PlayerShopListing listing)
        {
            if (!_listings.TryGetValue(listing.Slot, out PlayerShopListing? current) || !ReferenceEquals(current, listing))
                throw new InvalidOperationException("Player shop listing changed before it was removed.");
            _listings.Remove(listing.Slot);
        }

        internal PlayerShopWriteToken Track(Player actor, Action onStored)
        {
            var token = new PlayerShopWriteToken(actor, onStored);
            _pending.Add(token);
            return token;
        }

        internal void Untrack(PlayerShopWriteToken token) => _pending.Remove(token);

        /// <summary>
        /// On the tick: applies every write a lane has stored since. A write still outstanding whose character was
        /// quarantined will never be known to be stored (it may or may not have committed): the shop locks, and the next
        /// start reads it from storage. True when anything changed.
        /// </summary>
        internal bool Settle()
        {
            bool changed = false;
            for (int i = 0; i < _pending.Count; i++)
            {
                PlayerShopWriteToken token = _pending[i];
                if (token.IsStored)
                {
                    _pending.RemoveAt(i--);
                    token.OnStored();
                    changed = true;
                }
                else if (token.Actor.IsPersistenceQuarantined && !Locked)
                {
                    Locked = true;
                    changed = true;
                }
            }
            return changed;
        }

        internal void AddViewer(Player player)
        {
            if (!_viewers.Contains(player))
                _viewers.Add(player);
        }

        internal void RemoveViewer(Player player) => _viewers.Remove(player);

        internal void PruneViewers(Predicate<Player> gone) => _viewers.RemoveAll(gone);
    }

    /// <summary>One item for sale: the item itself (its instance id unchanged), its shop slot and asking price.</summary>
    public sealed class PlayerShopListing
    {
        public PlayerShopListing(Item item, int slot, int price)
        {
            Item = item ?? throw new ArgumentNullException(nameof(item));
            Slot = slot;
            Price = price;
        }

        public Item Item { get; }

        public int Slot { get; }

        public int Price { get; internal set; }

        /// <summary>Its last change (stocked, repriced) is not stored yet: it cannot be bought, repriced or taken back.</summary>
        public bool Pending { get; internal set; }
    }

    /// <summary>
    /// A shop write queued in a character's lane. The lane writer sets <see cref="IsStored"/> after the transaction
    /// commits; nothing else crosses threads. <see cref="OnStored"/> runs later on the shop's tick.
    /// </summary>
    public sealed class PlayerShopWriteToken
    {
        int _stored;

        internal PlayerShopWriteToken(Player actor, Action onStored)
        {
            Actor = actor ?? throw new ArgumentNullException(nameof(actor));
            OnStored = onStored ?? throw new ArgumentNullException(nameof(onStored));
        }

        public Player Actor { get; }

        internal Action OnStored { get; }

        public bool IsStored => Volatile.Read(ref _stored) != 0;

        /// <summary>Called on the lane writer once the transaction has committed.</summary>
        internal void MarkStored() => Volatile.Write(ref _stored, 1);
    }
}
