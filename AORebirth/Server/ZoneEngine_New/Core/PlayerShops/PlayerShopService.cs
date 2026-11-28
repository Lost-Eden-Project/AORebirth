namespace ZoneEngine_New.Core.PlayerShops
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;

    using AORebirth.Modules.Events;

    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages.N3Messages;

    using ZoneEngine_New.Core.Configuration;
    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Modules;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Trade;

    /// <summary>
    /// Player shops: a character rents a "Shop for Rent" terminal (<see cref="PlayerShopTerminal"/>), names it, puts
    /// tradeable items from their inventory up for sale at their own prices, and takes the credits its sales bring in.
    /// Anyone else buys from it at those prices: no Computer Literacy or vendor modifier applies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An item for sale keeps its instance id: its item_instances row moves to the shop (container 0x790, the shop id, the
    /// shop slot) and on to its buyer, and is never reminted. Every action changes memory on the shop's playfield tick and
    /// is stored afterwards in the acting character's lane (<see cref="InventoryActionService.TryCommit"/>): the item
    /// rows, the character's credits and the shop's rows in one transaction, which the database refuses unless the shop's
    /// rows are what memory says (a sale also locks the item's row exactly where the shop holds it).
    /// </para>
    /// <para>
    /// Writes of one item never cross lanes out of order: a listing whose last change is not stored yet is pending, and
    /// nothing may buy, reprice or take it back until it is (<see cref="PlayerShop"/>). The balance counts only stored
    /// sales and a withdrawal lowers it at once, so memory never offers credits storage does not hold. A write that can no
    /// longer be known to have stored (its character quarantined) locks the shop until the next start, which reads it from
    /// storage: nothing at a locked shop can be bought, taken or withdrawn.
    /// </para>
    /// </remarks>
    public sealed class PlayerShopService
    {
        /// <summary>Writes a shop may have outstanding at once; more are refused as busy (a flood of price changes).</summary>
        const int MaxPendingWrites = 16;

        /// <summary>How far from the terminal a viewer keeps receiving its updates.</summary>
        const float ViewRange = 15f;

        readonly InventoryActionService _inventory;
        readonly TradeService _trades;
        readonly IPlayerShopRepository _repository;
        readonly IItemBuilder _items;
        readonly IItemInstanceIdAllocator _ids;
        readonly GameplayPlayerShops _rules;
        readonly IZoneLogger _logger;

        /// <summary>Playfield instance → terminal instance → its shop.</summary>
        readonly ConcurrentDictionary<int, ConcurrentDictionary<int, PlayerShop>> _byPlayfield = new();

        /// <summary>Owner character → their one shop. Claimed before a rent is applied, so two rents cannot both pass.</summary>
        readonly ConcurrentDictionary<int, PlayerShop> _byOwner = new();

        readonly List<Player> _players = new();
        int _started;

        public PlayerShopService(InventoryActionService inventory, TradeService trades, IPlayerShopRepository repository,
            IItemBuilder items, IItemInstanceIdAllocator ids, GameplaySettings settings, IZoneLogger logger)
        {
            _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            _trades = trades ?? throw new ArgumentNullException(nameof(trades));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _items = items ?? throw new ArgumentNullException(nameof(items));
            _ids = ids ?? throw new ArgumentNullException(nameof(ids));
            _rules = (settings ?? throw new ArgumentNullException(nameof(settings))).PlayerShops;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public GameplayPlayerShops Rules => _rules;

        /// <summary>
        /// Reads every rented shop and what it holds, before any client connects. A shop whose rows disagree (an item
        /// without a price, a price without its item, two items in one slot, a slot out of range) is loaded locked.
        /// </summary>
        public void Start()
        {
            if (System.Threading.Interlocked.Exchange(ref _started, 1) != 0)
                return;
            PlayerShopSnapshot snapshot = _repository.LoadAll();
            var byId = new Dictionary<int, PlayerShop>();
            foreach (PlayerShopRecord record in snapshot.Shops)
            {
                var shop = new PlayerShop(record.ShopId, record.PlayfieldId, record.TerminalInstance, record.OwnerId, record.OwnerName,
                    record.Name, record.Balance);
                if (!Terminals(record.PlayfieldId).TryAdd(record.TerminalInstance, shop) || !_byOwner.TryAdd(record.OwnerId, shop))
                    throw new InvalidOperationException("Player shop rows are inconsistent: shop " + record.ShopId
                        + " shares its terminal or owner with another.");
                byId.Add(shop.ShopId, shop);
            }

            var priced = new HashSet<int>();
            foreach (ItemInstanceRecord row in snapshot.Items)
            {
                if (!byId.TryGetValue(row.ContainerInstance, out PlayerShop? shop))
                {
                    _logger.Error("Player shop item row has no shop item=" + row.InstanceId + " shop=" + row.ContainerInstance);
                    continue;
                }
                if (TryLoadListing(shop, row, snapshot))
                    priced.Add(row.InstanceId);
            }
            foreach ((int itemId, int shopId) in snapshot.ShopByItem)
            {
                if (!priced.Contains(itemId) && byId.TryGetValue(shopId, out PlayerShop? shop))
                    Lock(shop, "price row for item " + itemId + " has no item");
            }

            _logger.Info(string.Format(CultureInfo.InvariantCulture, "Player shops loaded shops={0} items={1}",
                snapshot.Shops.Count, priced.Count));
        }

        void Lock(PlayerShop shop, string reason)
        {
            shop.Locked = true;
            _logger.Error(string.Format(CultureInfo.InvariantCulture,
                "Player shop {0} locked (owner {1}, playfield {2}, terminal {3}): {4}",
                shop.ShopId, shop.OwnerId, shop.PlayfieldId, shop.TerminalInstance, reason));
        }

        /// <summary>
        /// One stored item row as a listing of <paramref name="shop"/>; a row without a usable price, slot or template
        /// locks the shop (its rows disagree) and is skipped.
        /// </summary>
        bool TryLoadListing(PlayerShop shop, ItemInstanceRecord row, PlayerShopSnapshot snapshot)
        {
            if (!snapshot.PricesByItem.TryGetValue(row.InstanceId, out int price) || price <= 0
                || !snapshot.ShopByItem.TryGetValue(row.InstanceId, out int shopId) || shopId != shop.ShopId
                || row.ContainerPlacement < 0 || row.ContainerPlacement >= _rules.MaxItems
                || shop.Listings.ContainsKey(row.ContainerPlacement)
                || !_items.TryFromInstanceRecord(row, out Item item))
            {
                Lock(shop, "item " + row.InstanceId + " has no usable price, slot or template");
                return false;
            }
            shop.AddListing(new PlayerShopListing(item, row.ContainerPlacement, price));
            return true;
        }

        /// <summary>
        /// Reads a shop whose memory can no longer be trusted back from storage, on its playfield's tick, once nothing of
        /// it is pending: a shop row restores its listings, balance and name and unlocks; no row (its rent, or its
        /// abandon, never stored) releases the terminal. A read that fails leaves the shop locked until the next start.
        /// True when the shop changed.
        /// </summary>
        bool Recover(ZoneEngine_New.Core.Playfield.Playfield playfield, PlayerShop shop, ConcurrentDictionary<int, PlayerShop> terminals)
        {
            PlayerShopSnapshot snapshot;
            try
            {
                snapshot = _repository.LoadShop(shop.ShopId);
            }
            catch (Exception exception)
            {
                shop.RecoverFromStorage = false;
                _logger.Error(exception, "Player shop " + shop.ShopId + " could not be read back from storage; it stays locked until the next start");
                return false;
            }

            PlayerShopRecord? record = snapshot.Shops.FirstOrDefault(stored => stored.ShopId == shop.ShopId);
            if (record == null)
            {
                shop.RecoverFromStorage = false;
                terminals.TryRemove(new KeyValuePair<int, PlayerShop>(shop.TerminalInstance, shop));
                _byOwner.TryRemove(new KeyValuePair<int, PlayerShop>(shop.OwnerId, shop));
                _logger.Info("Player shop " + shop.ShopId + " is not stored (owner " + shop.OwnerId + ", terminal " + shop.TerminalInstance
                    + "): released for rent");
                if (TryGetTerminal(playfield, shop.TerminalInstance, out PlayerShopTerminal terminal))
                {
                    foreach (Player viewer in shop.Viewers.ToArray())
                        SendView(viewer, terminal, null);
                    BroadcastName(terminal, string.Empty, string.Empty);
                }
                return true;
            }

            shop.ResetFromStorage(record.Name, record.Balance);
            foreach (ItemInstanceRecord row in snapshot.Items)
            {
                if (row.ContainerInstance == shop.ShopId && !TryLoadListing(shop, row, snapshot))
                    return true;
            }
            foreach ((int itemId, int shopId) in snapshot.ShopByItem)
            {
                if (shopId == shop.ShopId && !shop.Listings.Values.Any(listing => listing.Item.InstanceId == itemId))
                {
                    Lock(shop, "price row for item " + itemId + " has no item");
                    return true;
                }
            }
            _logger.Info("Player shop " + shop.ShopId + " read back from storage after a quarantined write: items=" + shop.Count
                + " balance=" + shop.Balance.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        ConcurrentDictionary<int, PlayerShop> Terminals(int playfieldId) => _byPlayfield.GetOrAdd(playfieldId, _ => new());

        public bool TryGetShop(int playfieldId, int terminalInstance, out PlayerShop shop)
        {
            shop = null!;
            return _byPlayfield.TryGetValue(playfieldId, out ConcurrentDictionary<int, PlayerShop>? terminals)
                && terminals.TryGetValue(terminalInstance, out shop!);
        }

        public bool TryGetShopOf(int ownerId, out PlayerShop shop) => _byOwner.TryGetValue(ownerId, out shop!);

        /// <summary>On the playfield tick: settles stored writes of its shops and refreshes their viewers.</summary>
        public void Tick(ZoneEngine_New.Core.Playfield.Playfield playfield)
        {
            ArgumentNullException.ThrowIfNull(playfield);
            if (!_byPlayfield.TryGetValue(playfield.Identity.Instance, out ConcurrentDictionary<int, PlayerShop>? terminals))
                return;
            foreach (PlayerShop shop in terminals.Values)
            {
                bool changed = shop.HasPendingWrites && shop.Settle();
                if (changed && shop.Locked)
                    _logger.Error("Player shop " + shop.ShopId + " locked: a write's character was quarantined before it was stored");
                if (shop.RecoverFromStorage && !shop.HasPendingWrites)
                    changed |= Recover(playfield, shop, terminals);
                if (changed && TryGetTerminal(playfield, shop.TerminalInstance, out PlayerShopTerminal terminal))
                    Refresh(terminal);
            }
        }

        /// <summary>The terminal was used: shows the shop (or the rent offer) to a client that reads player shops.</summary>
        public void Open(Player player, PlayerShopTerminal terminal)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(terminal);
            if (!player.SpeaksExtendedMessages)
            {
                RequirementFeedback.SendText(player, "Player shops need the Lost Eden client.");
                return;
            }
            if (!CanAct(player, terminal))
                return;
            if (Find(terminal) is PlayerShop shop)
            {
                shop.AddViewer(player);
                SendView(player, terminal, shop);
                return;
            }
            SendView(player, terminal, null);
        }

        /// <summary>Every rented terminal's label on the player's playfield, after the client's hello.</summary>
        public void SendNames(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);
            if (!player.SpeaksExtendedMessages || player.Session == null || player.Playfield == null
                || !_byPlayfield.TryGetValue(player.Playfield.Identity.Instance, out ConcurrentDictionary<int, PlayerShop>? terminals))
                return;
            foreach (PlayerShop shop in terminals.Values)
            {
                if (!shop.Opening)
                    player.Session.Send(NameMessage(player, shop.TerminalInstance, shop.Name, shop.OwnerName));
            }
        }

        /// <summary>A <see cref="PlayerShopActionMessage"/> from the client, on its playfield's tick.</summary>
        public void Handle(Player player, PlayerShopActionMessage message)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(message);
            if (!player.SpeaksExtendedMessages || player.Playfield == null
                || !PlayerShopTerminal.IsPlayerShopTerminalType(message.Terminal.Type)
                || !TryGetTerminal(player.Playfield, message.Terminal.Instance, out PlayerShopTerminal terminal))
                return;

            if (message.Action == PlayerShopAction.Close)
            {
                Find(terminal)?.RemoveViewer(player);
                return;
            }

            if (!CanAct(player, terminal))
            {
                Reply(player, terminal, message.Action, PlayerShopResult.CannotUse);
                return;
            }

            PlayerShop? shop = Find(terminal);
            if (shop != null)
                shop.Settle();
            PlayerShopResult result;
            try
            {
                result = message.Action switch
                {
                    PlayerShopAction.Open => PlayerShopResult.Done,
                    PlayerShopAction.Rent => Rent(player, terminal, shop, message.Price),
                    PlayerShopAction.Rename => Rename(player, terminal, shop, message.Name),
                    PlayerShopAction.Abandon => Abandon(player, terminal, shop),
                    PlayerShopAction.Stock => Stock(player, terminal, shop, message.Slot, message.Price),
                    PlayerShopAction.SetPrice => SetPrice(player, terminal, shop, message.Slot, message.ItemInstance, message.Price),
                    PlayerShopAction.Unstock => Unstock(player, terminal, shop, message.Slot, message.ItemInstance),
                    PlayerShopAction.Buy => Buy(player, terminal, shop, message.Slot, message.ItemInstance, message.Price),
                    PlayerShopAction.Withdraw => Withdraw(player, terminal, shop, message.Price),
                    _ => PlayerShopResult.CannotUse
                };
            }
            catch (Exception exception)
            {
                // A failed plan or apply: TryCommit quarantined and closed the player when memory had changed.
                _logger.Error(exception, "Player shop action failed char=" + player.Identity.Instance + " action=" + message.Action);
                if (shop != null)
                {
                    Lock(shop, "an action threw while it was applied");
                    shop.RecoverFromStorage = true;
                }
                result = PlayerShopResult.Locked;
            }

            Reply(player, terminal, message.Action, result);
            PlayerShop? now = Find(terminal);
            if (now != null && message.Action != PlayerShopAction.Abandon)
                now.AddViewer(player);
            if (result == PlayerShopResult.Done || message.Action == PlayerShopAction.Open)
            {
                if (now != null)
                    Refresh(terminal);
                else
                    SendView(player, terminal, null);
            }
            else
                SendView(player, terminal, now);
        }

        PlayerShopResult Rent(Player player, PlayerShopTerminal terminal, PlayerShop? existing, int shownCost)
        {
            if (existing != null)
                return Refuse(player, PlayerShopResult.NotAvailable, "This shop terminal is already rented.");
            if (shownCost != _rules.RentCost)
                return Refuse(player, PlayerShopResult.Changed, "The rent has changed; look again.");
            int ownerId = player.Identity.Instance;
            if (_byOwner.ContainsKey(ownerId))
                return Refuse(player, PlayerShopResult.AlreadyOwnShop, "You already own a shop; you can't own more than one shop at a time!");
            int cost = _rules.RentCost;
            if (Cash(player) < cost)
                return Refuse(player, PlayerShopResult.NotEnoughCredits, "You do not have the credits to rent this shop.");

            int playfieldId = terminal.Playfield!.Identity.Instance;
            string name = DefaultName(player);
            var shop = new PlayerShop(_ids.Allocate(), playfieldId, terminal.Identity.Instance, ownerId, player.Name ?? string.Empty, name, 0)
            {
                Opening = true
            };
            if (!_byOwner.TryAdd(ownerId, shop))
                return Refuse(player, PlayerShopResult.AlreadyOwnShop, "You already own a shop; you can't own more than one shop at a time!");
            if (!Terminals(playfieldId).TryAdd(terminal.Identity.Instance, shop))
            {
                _byOwner.TryRemove(new KeyValuePair<int, PlayerShop>(ownerId, shop));
                return Refuse(player, PlayerShopResult.NotAvailable, "This shop terminal is already rented.");
            }

            PlayerShopWriteToken token = shop.Track(player, () =>
            {
                shop.Opening = false;
                BroadcastName(terminal, shop.Name, shop.OwnerName);
            });
            bool committed = _inventory.TryCommit(player, [],
                () => Cash(player) >= cost && !player.IsDead,
                () =>
                {
                    if (cost > 0)
                        SetCash(player, Cash(player) - cost, terminal);
                },
                cost > 0 ? [new StatRecord { StatId = (int)CharacterStat.Cash, StatValue = Cash(player) - cost }] : null,
                playerShop: new PlayerShopWrite(PlayerShopWriteKind.Rent, shop.ShopId, ownerId)
                {
                    PlayfieldId = playfieldId, TerminalInstance = terminal.Identity.Instance, Name = name
                },
                stored: token.MarkStored);
            if (!committed)
            {
                shop.Untrack(token);
                if (player.IsPersistenceQuarantined)
                {
                    // Cash may have changed in memory; the claim stays until the tick reads storage back (no shop row:
                    // the terminal is released).
                    Lock(shop, "the rent's apply failed");
                    shop.RecoverFromStorage = true;
                    return PlayerShopResult.Locked;
                }
                Terminals(playfieldId).TryRemove(new KeyValuePair<int, PlayerShop>(terminal.Identity.Instance, shop));
                _byOwner.TryRemove(new KeyValuePair<int, PlayerShop>(ownerId, shop));
                return Refuse(player, PlayerShopResult.NotEnoughCredits, "You do not have the credits to rent this shop.");
            }

            player.FlushDirtyStats();
            Audit("rent", player, shop, 0, cost);
            RequirementFeedback.SendText(player, "You are now renting this shop terminal!");
            return PlayerShopResult.Done;
        }

        PlayerShopResult Rename(Player player, PlayerShopTerminal terminal, PlayerShop? shop, string? requested)
        {
            if (OwnedUsable(player, shop) is PlayerShopResult refused)
                return refused;
            string name = (requested ?? string.Empty).Trim();
            if (!IsShowableName(name, _rules.MaxNameLength))
                return Refuse(player, PlayerShopResult.BadName,
                    "A shop name is 1 to " + _rules.MaxNameLength + " letters, digits, spaces or punctuation (no < or >).");
            if (string.Equals(name, shop!.Name, StringComparison.Ordinal))
                return PlayerShopResult.Done;

            PlayerShopWriteToken token = shop.Track(player, () => { });
            bool committed = _inventory.TryCommit(player, [], () => true, () => shop.Name = name,
                playerShop: new PlayerShopWrite(PlayerShopWriteKind.Rename, shop.ShopId, shop.OwnerId) { Name = name },
                stored: token.MarkStored);
            if (!committed)
                return NotCommitted(player, shop, token, PlayerShopResult.CannotUse, "The shop could not be renamed.");
            BroadcastName(terminal, shop.Name, shop.OwnerName);
            Audit("rename", player, shop, 0, 0);
            return PlayerShopResult.Done;
        }

        PlayerShopResult Abandon(Player player, PlayerShopTerminal terminal, PlayerShop? shop)
        {
            if (OwnedUsable(player, shop) is PlayerShopResult refused)
                return refused;
            if (shop!.Count > 0 || shop.Balance != 0 || shop.PendingSales != 0 || shop.HasPendingWrites)
                return Refuse(player, PlayerShopResult.NotEmpty, "The shop must be empty of items and cash!");

            shop.Closing = true;
            int playfieldId = shop.PlayfieldId;
            PlayerShopWriteToken token = shop.Track(player, () =>
            {
                Terminals(playfieldId).TryRemove(new KeyValuePair<int, PlayerShop>(shop.TerminalInstance, shop));
                _byOwner.TryRemove(new KeyValuePair<int, PlayerShop>(shop.OwnerId, shop));
                BroadcastName(terminal, string.Empty, string.Empty);
            });
            bool committed = _inventory.TryCommit(player, [], () => true, () => { },
                playerShop: new PlayerShopWrite(PlayerShopWriteKind.Abandon, shop.ShopId, shop.OwnerId),
                stored: token.MarkStored);
            if (!committed)
            {
                shop.Closing = player.IsPersistenceQuarantined;
                return NotCommitted(player, shop, token, PlayerShopResult.CannotUse, "The shop could not be given up.");
            }
            Audit("abandon", player, shop, 0, 0);
            RequirementFeedback.SendText(player, "You have given up your shop.");
            return PlayerShopResult.Done;
        }

        PlayerShopResult Stock(Player player, PlayerShopTerminal terminal, PlayerShop? shop, int inventorySlot, int price)
        {
            if (OwnedUsable(player, shop) is PlayerShopResult refused)
                return refused;
            if (!IsPrice(price))
                return Refuse(player, PlayerShopResult.BadPrice, "A price is 1 to " + TradeRules.MaxCash.ToString("N0", CultureInfo.InvariantCulture) + " credits.");
            int slot = shop!.FirstFreeSlot(_rules.MaxItems);
            if (slot < 0)
                return Refuse(player, PlayerShopResult.ShopFull, "This is your shop (max " + _rules.MaxItems + " items)!");

            Container page = player.Inventory.Inventory;
            if (!page.Content.TryGetValue(inventorySlot, out Item? item) || !IsSellable(player, item))
                return Refuse(player, PlayerShopResult.ItemRefused, "Only tradeable items from your inventory, not bags, can be sold in your shop.");

            var listing = new PlayerShopListing(item, slot, price) { Pending = true };
            var shopContainer = new Identity { Type = IdentityType.PlayerShopInventory, Instance = shop.ShopId };
            PlayerShopWriteToken token = shop.Track(player, () => listing.Pending = false);
            bool committed = _inventory.TryCommit(player,
                [new InventoryRowChange(item, shopContainer, slot, item.StackCount)],
                () => page.Content.TryGetValue(inventorySlot, out Item? current) && ReferenceEquals(current, item)
                    && IsSellable(player, item) && shop.IsSlotFree(slot),
                () =>
                {
                    page.Content.Remove(inventorySlot);
                    shop.AddListing(listing);
                    player.Session?.Send(new CharacterActionMessage
                    {
                        Identity = player.Identity,
                        Action = CharacterActionType.DeleteItem,
                        Target = new Identity { Type = IdentityType.Inventory, Instance = inventorySlot }
                    });
                },
                playerShop: new PlayerShopWrite(PlayerShopWriteKind.Stock, shop.ShopId, shop.OwnerId)
                {
                    ItemInstanceId = item.InstanceId, Price = price, MaxItems = _rules.MaxItems
                },
                stored: token.MarkStored);
            if (!committed)
                return NotCommitted(player, shop, token, PlayerShopResult.ItemRefused, "That item can no longer be sold here.");

            ModuleHooks.ItemMoved(player, item, ModuleHooks.Place(ItemPlaceKind.Inventory), ModuleHooks.Place(ItemPlaceKind.Shop, terminal.Identity));
            Audit("stock", player, shop, item.InstanceId, price);
            return PlayerShopResult.Done;
        }

        PlayerShopResult SetPrice(Player player, PlayerShopTerminal terminal, PlayerShop? shop, int slot, int itemInstance, int price)
        {
            if (OwnedUsable(player, shop) is PlayerShopResult refused)
                return refused;
            if (!IsPrice(price))
                return Refuse(player, PlayerShopResult.BadPrice, "A price is 1 to " + TradeRules.MaxCash.ToString("N0", CultureInfo.InvariantCulture) + " credits.");
            if (!shop!.TryGetListing(slot, itemInstance, out PlayerShopListing listing) || listing.Pending)
                return Refuse(player, PlayerShopResult.Changed, "That item is still being changed; try again in a moment.");
            int old = listing.Price;
            if (old == price)
                return PlayerShopResult.Done;

            PlayerShopWriteToken token = shop.Track(player, () => listing.Pending = false);
            bool committed = _inventory.TryCommit(player, [],
                () => shop.TryGetListing(slot, itemInstance, out PlayerShopListing current) && ReferenceEquals(current, listing) && !listing.Pending,
                () =>
                {
                    listing.Price = price;
                    listing.Pending = true;
                },
                playerShop: new PlayerShopWrite(PlayerShopWriteKind.Price, shop.ShopId, shop.OwnerId)
                {
                    ItemInstanceId = itemInstance, Price = price, ExpectedPrice = old
                },
                stored: token.MarkStored);
            if (!committed)
                return NotCommitted(player, shop, token, PlayerShopResult.Changed, "That item is still being changed; try again in a moment.");
            Audit("price", player, shop, itemInstance, price);
            return PlayerShopResult.Done;
        }

        PlayerShopResult Unstock(Player player, PlayerShopTerminal terminal, PlayerShop? shop, int slot, int itemInstance)
        {
            if (OwnedUsable(player, shop) is PlayerShopResult refused)
                return refused;
            if (!shop!.TryGetListing(slot, itemInstance, out PlayerShopListing listing) || listing.Pending)
                return Refuse(player, PlayerShopResult.Changed, "That item is still being changed; try again in a moment.");
            Container page = player.Inventory.Inventory;
            int free = page.FindFreeSlot();
            if (free < 0)
                return Refuse(player, PlayerShopResult.NoRoom, "You have no room in your inventory.");
            Item item = listing.Item;
            if (TradeRules.IsUnique(item) && TradeRules.WouldDuplicateUnique(player, item.LowId, item.HighId))
                return Refuse(player, PlayerShopResult.UniqueHeld, "You already have one of those.");

            var shopContainer = new Identity { Type = IdentityType.PlayerShopInventory, Instance = shop.ShopId };
            PlayerShopWriteToken token = shop.Track(player, () => shop.ReleaseSlot(slot));
            bool committed = _inventory.TryCommit(player,
                [new InventoryRowChange(item, page.Identity, free, item.StackCount)],
                () => shop.TryGetListing(slot, itemInstance, out PlayerShopListing current) && ReferenceEquals(current, listing)
                    && !listing.Pending && !page.Content.ContainsKey(free),
                () =>
                {
                    shop.RemoveListingKeepingSlot(listing);
                    if (!page.Add(free, item))
                        throw new InvalidOperationException("Reserved inventory slot changed.");
                    SendGrant(player, item);
                },
                expectedSources: [item.ToRecord(shopContainer, slot, item.StackCount)],
                playerShop: new PlayerShopWrite(PlayerShopWriteKind.Unstock, shop.ShopId, shop.OwnerId) { ItemInstanceId = itemInstance },
                stored: token.MarkStored);
            if (!committed)
                return NotCommitted(player, shop, token, PlayerShopResult.Changed, "That item is still being changed; try again in a moment.");
            ModuleHooks.ItemMoved(player, item, ModuleHooks.Place(ItemPlaceKind.Shop, terminal.Identity), ModuleHooks.Place(ItemPlaceKind.Inventory));
            Audit("unstock", player, shop, itemInstance, listing.Price);
            return PlayerShopResult.Done;
        }

        PlayerShopResult Buy(Player player, PlayerShopTerminal terminal, PlayerShop? shop, int slot, int itemInstance, int shownPrice)
        {
            if (Usable(player, shop) is PlayerShopResult refused)
                return refused;
            if (shop!.OwnerId == player.Identity.Instance)
                return Refuse(player, PlayerShopResult.NotOwner, "You cannot buy from your own shop; take the item back instead.");
            if (!shop.TryGetListing(slot, itemInstance, out PlayerShopListing listing) || listing.Pending || listing.Price != shownPrice)
                return Refuse(player, PlayerShopResult.Changed, "The shop was altered by the owner. Trade aborted.");
            int price = listing.Price;
            if (Cash(player) < price)
                return Refuse(player, PlayerShopResult.NotEnoughCredits, "You do not have enough credits.");
            if (shop.Balance + shop.PendingSales + price > TradeRules.MaxCash)
                return Refuse(player, PlayerShopResult.CreditLimit, "This shop cannot take any more credits until its owner withdraws some.");
            Container page = player.Inventory.Inventory;
            int free = page.FindFreeSlot();
            if (free < 0)
                return Refuse(player, PlayerShopResult.NoRoom, "You have no room in your inventory.");
            Item item = listing.Item;
            if (TradeRules.IsUnique(item) && TradeRules.WouldDuplicateUnique(player, item.LowId, item.HighId))
                return Refuse(player, PlayerShopResult.UniqueHeld, "You already have one of those.");

            var shopContainer = new Identity { Type = IdentityType.PlayerShopInventory, Instance = shop.ShopId };
            PlayerShopWriteToken token = shop.Track(player, () =>
            {
                shop.PendingSales -= price;
                shop.Balance += price;
                shop.ReleaseSlot(slot);
            });
            bool committed = _inventory.TryCommit(player,
                [new InventoryRowChange(item, page.Identity, free, item.StackCount)],
                () => shop.TryGetListing(slot, itemInstance, out PlayerShopListing current) && ReferenceEquals(current, listing)
                    && !listing.Pending && listing.Price == price && Cash(player) >= price && !page.Content.ContainsKey(free),
                () =>
                {
                    shop.RemoveListingKeepingSlot(listing);
                    shop.PendingSales += price;
                    if (!page.Add(free, item))
                        throw new InvalidOperationException("Reserved inventory slot changed.");
                    SetCash(player, Cash(player) - price, terminal);
                    SendGrant(player, item);
                },
                [new StatRecord { StatId = (int)CharacterStat.Cash, StatValue = Cash(player) - price }],
                [item.ToRecord(shopContainer, slot, item.StackCount)],
                new PlayerShopWrite(PlayerShopWriteKind.Sale, shop.ShopId, shop.OwnerId)
                {
                    ItemInstanceId = itemInstance, ExpectedPrice = price, MaxBalance = TradeRules.MaxCash
                },
                token.MarkStored);
            if (!committed)
                return NotCommitted(player, shop, token, PlayerShopResult.Changed, "The shop was altered by the owner. Trade aborted.");

            player.FlushDirtyStats();
            ModuleHooks.ItemMoved(player, item, ModuleHooks.Place(ItemPlaceKind.Shop, terminal.Identity), ModuleHooks.Place(ItemPlaceKind.Inventory));
            Audit("sale", player, shop, itemInstance, price);
            return PlayerShopResult.Done;
        }

        PlayerShopResult Withdraw(Player player, PlayerShopTerminal terminal, PlayerShop? shop, int requested)
        {
            if (OwnedUsable(player, shop) is PlayerShopResult refused)
                return refused;
            if (requested < 0)
                return Refuse(player, PlayerShopResult.BadPrice, "Withdraw a positive number of credits.");
            long amount = requested == 0 ? shop!.Balance : requested;
            if (amount <= 0 || amount > shop!.Balance)
                return Refuse(player, PlayerShopResult.NotEnoughCredits,
                    "The shop balance is " + shop!.Balance.ToString("N0", CultureInfo.InvariantCulture) + " credits.");
            if (Cash(player) + amount > TradeRules.MaxCash)
                return Refuse(player, PlayerShopResult.CreditLimit, "You cannot hold that many credits.");

            int take = (int)amount;
            PlayerShopWriteToken token = shop.Track(player, () => { });
            bool committed = _inventory.TryCommit(player, [],
                () => shop.Balance >= take && (long)Cash(player) + take <= TradeRules.MaxCash,
                () =>
                {
                    shop.Balance -= take;
                    SetCash(player, Cash(player) + take, terminal);
                },
                [new StatRecord { StatId = (int)CharacterStat.Cash, StatValue = Cash(player) + take }],
                playerShop: new PlayerShopWrite(PlayerShopWriteKind.Withdraw, shop.ShopId, shop.OwnerId) { Amount = take },
                stored: token.MarkStored);
            if (!committed)
                return NotCommitted(player, shop, token, PlayerShopResult.Changed, "The shop's balance changed; look again.");
            player.FlushDirtyStats();
            Audit("withdraw", player, shop, 0, take);
            return PlayerShopResult.Done;
        }

        /// <summary>Gates every action on a rented shop: it exists, is set up, is not being given up, and is not locked.</summary>
        PlayerShopResult? Usable(Player player, PlayerShop? shop)
        {
            if (shop == null || shop.Closing)
                return Refuse(player, PlayerShopResult.NotAvailable, "Nobody rents this shop terminal.");
            if (shop.Locked)
                return Refuse(player, PlayerShopResult.Locked,
                    "This shopping booth has been locked down and can not be used until it has been unlocked or reset. Please try another booth.");
            if (shop.Opening)
                return Refuse(player, PlayerShopResult.Busy, "The shop is still being set up; try again in a moment.");
            if (shop.PendingWriteCount >= MaxPendingWrites)
                return Refuse(player, PlayerShopResult.Busy, "The shop is busy; try again in a moment.");
            return null;
        }

        PlayerShopResult? OwnedUsable(Player player, PlayerShop? shop)
        {
            if (Usable(player, shop) is PlayerShopResult refused)
                return refused;
            if (shop!.OwnerId != player.Identity.Instance)
                return Refuse(player, PlayerShopResult.NotOwner, "Only the shop's owner can do that.");
            return null;
        }

        /// <summary>
        /// A commit that did not go through. When the player was quarantined (its apply threw after memory changed), the
        /// shop's memory may no longer match storage: it locks until the next start reads it.
        /// </summary>
        PlayerShopResult NotCommitted(Player player, PlayerShop shop, PlayerShopWriteToken token, PlayerShopResult result, string text)
        {
            shop.Untrack(token);
            if (player.IsPersistenceQuarantined)
            {
                Lock(shop, "an action's apply failed for character " + player.Identity.Instance);
                return PlayerShopResult.Locked;
            }
            return Refuse(player, result, text);
        }

        /// <summary>
        /// Who may act at a terminal: in play on its playfield, within reach, meeting its use requirements, alive, with a
        /// loaded inventory that is not quarantined, and not holding a trade or shop window open.
        /// </summary>
        bool CanAct(Player player, PlayerShopTerminal terminal)
            => player.Session?.State == SessionState.InPlay && !player.IsDead && !player.IsPersistenceQuarantined
                && player.Inventory.IsHydrated && player.Identity.Instance > 0
                && terminal.CanBeginUse(player) && !_trades.TryGetSession(player, out _);

        /// <summary>
        /// An item the owner may put up for sale: a loose main-inventory item with a stored or storable instance, not
        /// NoDrop, not a bag (a container's contents would go with it), and not locked for a move, trade or use.
        /// </summary>
        static bool IsSellable(Player player, Item item)
            => item.InstanceId > 0 && item.StackCount > 0 && !item.Locked && !player.Inventory.IsLockedForTransfer(item)
                && !InventoryMoveService.IsBagItem(item) && !TradeRules.IsNoDrop(item);

        static bool IsPrice(int price) => price > 0 && price <= TradeRules.MaxCash;

        /// <summary>
        /// 1 to <paramref name="maxLength"/> printable ASCII characters, not only spaces, and no angle brackets: other
        /// players' clients show the name, and the client reads markup (&lt;font&gt;) in text it shows.
        /// </summary>
        internal static bool IsShowableName(string? name, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > maxLength)
                return false;
            foreach (char c in name)
            {
                if (c is < ' ' or > '~' or '<' or '>')
                    return false;
            }
            return true;
        }

        string DefaultName(Player player)
        {
            string name = (player.Name ?? "A") + "'s Shop";
            name = IsShowableName(name, int.MaxValue) ? name : "Player Shop";
            return name.Length <= _rules.MaxNameLength ? name : name[.._rules.MaxNameLength].TrimEnd();
        }

        PlayerShop? Find(PlayerShopTerminal terminal)
            => terminal.Playfield != null && TryGetShop(terminal.Playfield.Identity.Instance, terminal.Identity.Instance, out PlayerShop shop)
                ? shop
                : null;

        static bool TryGetTerminal(ZoneEngine_New.Core.Playfield.Playfield playfield, int instance, out PlayerShopTerminal terminal)
        {
            terminal = null!;
            return playfield.GetService<DynelRegistry>() is DynelRegistry registry
                && registry.TryGet(new Identity { Type = PlayerShopTerminal.TerminalType, Instance = instance }, out Dynel? dynel)
                && (terminal = (dynel as PlayerShopTerminal)!) != null;
        }

        static int Cash(Player player) => player.Stats.GetOrZero(CharacterStat.Cash, StatDetail.Base);

        static void SetCash(Player player, int cash, PlayerShopTerminal terminal)
        {
            using (StatChangeScope.Begin(StatChangeReason.Shop, ModuleHooks.SourceOf(terminal)))
                player.Stats.Set(CharacterStat.Cash, TradeRules.ClampCash(cash), StatDetail.Base, dirty: true);
        }

        /// <summary>The item appeared in the player's main inventory: the client places it in its first free slot.</summary>
        static void SendGrant(Player player, Item item)
            => player.Session?.Send(new AddTemplateMessage
            {
                Identity = player.Identity,
                HighId = item.HighId,
                LowId = item.LowId,
                Quality = item.Quality,
                Count = item.StackCount
            });

        static PlayerShopResult Refuse(Player player, PlayerShopResult result, string text)
        {
            RequirementFeedback.SendText(player, text);
            return result;
        }

        static void Reply(Player player, PlayerShopTerminal terminal, PlayerShopAction action, PlayerShopResult result)
            => player.Session?.Send(new PlayerShopResultMessage
            {
                Identity = player.Identity,
                Terminal = terminal.Identity,
                Action = action,
                Result = result
            });

        /// <summary>Every viewer still at the terminal gets the shop as it stands now; the others stop getting it.</summary>
        void Refresh(PlayerShopTerminal terminal)
        {
            PlayerShop? shop = Find(terminal);
            if (shop == null)
                return;
            shop.PruneViewers(viewer => viewer.Session == null || !ReferenceEquals(viewer.Playfield, terminal.Playfield)
                || !viewer.SpeaksExtendedMessages || terminal.GetEdgeDistanceTo(viewer) > ViewRange);
            foreach (Player viewer in shop.Viewers)
                SendView(viewer, terminal, shop);
        }

        void SendView(Player viewer, PlayerShopTerminal terminal, PlayerShop? shop)
        {
            if (viewer.Session == null)
                return;
            bool owner = shop != null && shop.OwnerId == viewer.Identity.Instance;
            PlayerShopState state = shop == null || shop.Closing ? PlayerShopState.ForRent
                : shop.Locked ? PlayerShopState.Locked
                : owner ? PlayerShopState.Owner
                : PlayerShopState.Customer;
            var items = new List<PlayerShopViewItem>();
            if (shop != null && state is PlayerShopState.Owner or PlayerShopState.Customer && (owner || !shop.Opening))
            {
                foreach (PlayerShopListing listing in shop.Listings.Values)
                {
                    if (!owner && listing.Pending)
                        continue;
                    Item item = listing.Item;
                    items.Add(new PlayerShopViewItem(listing.Slot, item.InstanceId, item.LowId, item.HighId, item.Quality, item.StackCount,
                        listing.Price, owner && listing.Pending ? PlayerShopViewItem.PendingFlag : 0));
                }
            }
            bool shown = state is PlayerShopState.Owner or PlayerShopState.Customer or PlayerShopState.Locked;
            viewer.Session.Send(new PlayerShopViewMessage
            {
                Identity = viewer.Identity,
                Terminal = terminal.Identity,
                State = state,
                RentCost = _rules.RentCost,
                MaxItemCount = _rules.MaxItems,
                MaxNameLength = _rules.MaxNameLength,
                Balance = owner && shop != null ? (int)Math.Min(shop.Balance, int.MaxValue) : 0,
                OwnerId = shown ? shop!.OwnerId : 0,
                ShopName = shown ? shop!.Name : string.Empty,
                OwnerName = shown ? shop!.OwnerName : string.Empty,
                Items = items
            });
        }

        /// <summary>A terminal's label changed: everyone on its playfield whose client reads it is told.</summary>
        void BroadcastName(PlayerShopTerminal terminal, string name, string ownerName)
        {
            if (terminal.Playfield?.GetService<DynelRegistry>() is not DynelRegistry registry)
                return;
            lock (_players)
            {
                _players.Clear();
                registry.CopyPlayers(_players);
                foreach (Player player in _players)
                {
                    if (player.SpeaksExtendedMessages)
                        player.Session?.Send(NameMessage(player, terminal.Identity.Instance, name, ownerName));
                }
                _players.Clear();
            }
        }

        static PlayerShopNameMessage NameMessage(Player player, int terminalInstance, string name, string ownerName) => new()
        {
            Identity = player.Identity,
            Terminal = new Identity { Type = PlayerShopTerminal.TerminalType, Instance = terminalInstance },
            ShopName = name,
            OwnerName = ownerName
        };

        void Audit(string action, Player player, PlayerShop shop, int itemInstance, long credits)
            => _logger.Info(string.Format(CultureInfo.InvariantCulture,
                "Player shop {0} char={1} shop={2} owner={3} playfield={4} terminal={5} item={6} credits={7} balance={8}",
                action, player.Identity.Instance, shop.ShopId, shop.OwnerId, shop.PlayfieldId, shop.TerminalInstance, itemInstance,
                credits, shop.Balance));
    }
}
