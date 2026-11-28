namespace ZoneEngine_New.Core.Trade
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;

    using AORebirth.Enums;
    using AORebirth.Modules.Events;

    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages.N3Messages;

    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Helpers;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Modules;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// Owns every live trade window. One session per player at a time; a vending machine may back
    /// many sessions at once. All mutation runs on the owning playfield's tick thread — the registry
    /// lock only protects lookups made from another playfield (transfer, shutdown).
    /// </summary>
    public sealed partial class TradeService
    {
        /// <summary>Walking further than this from the trade anchor cancels the window.</summary>
        public const double RangeCancelDistance = LootableDynel.OpenRange;

        readonly object _gate = new();
        readonly Dictionary<int, TradeSession> _byPlayer = new();
        readonly IZoneLogger _logger;
        readonly IGameData _gameData;
        readonly IItemTemplateCatalog _catalog;
        readonly HashItemMinter _minter;
        readonly IItemInstanceIdAllocator _ids;
        readonly InventoryFlushService _flush;
        readonly ITradePersistence _persistence;

        public TradeService(
            IZoneLogger logger,
            IGameData gameData,
            IItemTemplateCatalog catalog,
            HashItemMinter minter,
            IItemInstanceIdAllocator ids,
            InventoryFlushService flush,
            ITradePersistence persistence)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(gameData);
            ArgumentNullException.ThrowIfNull(catalog);
            ArgumentNullException.ThrowIfNull(minter);
            ArgumentNullException.ThrowIfNull(ids);
            ArgumentNullException.ThrowIfNull(flush);
            ArgumentNullException.ThrowIfNull(persistence);

            _logger = logger;
            _gameData = gameData;
            _catalog = catalog;
            _minter = minter;
            _ids = ids;
            _flush = flush;
            _persistence = persistence;
        }

        public bool TryGetSession(Player player, out TradeSession session)
        {
            ArgumentNullException.ThrowIfNull(player);
            lock (_gate)
                return _byPlayer.TryGetValue(player.Identity.Instance, out session!);
        }

        #region Opening

        /// <summary>Entry point for <see cref="VendingMachine.OnUse"/> and NPC vendor use.</summary>
        public bool TryOpenShop(Player player, VendingMachine machine)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(machine);

            if (player.Session == null || player.Playfield == null || !player.Inventory.IsHydrated)
                return false;

            if (player.IsDead || machine.Playfield == null)
                return false;

            NpcCharacter? owner = machine.OwnerNpc;
            if (owner != null && (owner.IsDead || owner.Playfield == null))
                return false;

            // Same range the tick enforces: a crafted Use from across the zone would otherwise open
            // a window that is cancelled again on the next tick.
            Dynel anchor = owner != null ? owner : machine;
            if (player.GetEdgeDistanceTo(anchor) > RangeCancelDistance)
            {
                ClientFeedback.Send(player, "Feedback_TooFarAway");
                return false;
            }

            // Using the shop that is already open closes it, as a second click on the vendor does live; a different
            // trade gives way to the new shop.
            if (TryGetSession(player, out TradeSession open))
            {
                if (open.Kind == TradeKind.Shop && ReferenceEquals(open.Machine, machine))
                {
                    Close(open, "shop used again");
                    return true;
                }

                Cancel(player, "opening another trade");
            }

            int templateId = machine.Template.Id;
            if (!string.IsNullOrEmpty(machine.Stock.ConfigurationUnavailableReason))
            {
                Tell(player, "This shop has unresolved stock data.");
                _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                    "Vending machine {0} stock unavailable: {1}",
                    machine.ShopIdentity.Instance,
                    machine.Stock.ConfigurationUnavailableReason));
                return false;
            }
            if (machine.Stock.IsConfiguredSnapshot)
            {
                // Configured stock belongs only to its exact live accepted vendor, never to
                // a matching template, nearby actor, or stale replacement identity.
                if (!IsCurrentAcceptedShop(player, machine))
                    return false;
                machine.Stock.EnsureConfiguredFresh(Random.Shared);
            }
            else if (machine.Template.ShopInventory.Count > 0)
            {
                // The shop's own stock list (items.dat ShopInventory): the primary source. A database vendor row
                // overrides it only while Gameplay.json shops.preferDatabaseStock holds (NpcContentActivationService).
                machine.Stock.EnsureFreshFromShopInventory(machine.Template.ShopInventory, _minter, Random.Shared, HasTemplate);
            }
            else
            {
                if (!_gameData.TryGetVendingMachine(templateId, out VendingMachineDefinition definition))
                {
                    Tell(player, "This shop has no stock list yet.");
                    _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                        "Vending machine {0} has no ShopInventory and no VendingMachines.json entry", templateId));
                    return false;
                }
                machine.Stock.EnsureFresh(definition, _minter, Random.Shared, HasTemplate);
            }

            machine.Stock.EnsureSorted(slot => _catalog.TryGet(slot.LowId, out ItemTemplate template) ? template.Name : string.Empty);

            Identity bag = player.Playfield.GetRequiredService<DynelRegistry>().AllocateTempBagIdentity();
            var session = new TradeSession(bag, TradeKind.Shop, player, partner: null, machine);
            Register(player, session);
            machine.Stock.OpenTrade();

            Identity shopIdentity = machine.ShopIdentity;
            player.Session.Send(machine.Stock.BuildShopUpdate(shopIdentity));
            player.Session.Send(OpenFrame(player.Identity, shopIdentity, bag));
            player.Session.Send(OpenFrame(shopIdentity, player.Identity, bag));

            _logger.Info(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Shop opened player={0} machine={1} template={2} stock={3} source={4}",
                    player.Identity.Instance,
                    shopIdentity.Instance,
                    templateId,
                    machine.Stock.Slots.Count,
                    machine.Stock.IsConfiguredSnapshot ? "configured" : machine.Template.ShopInventory.Count > 0 ? "template" : "json"));

            return true;
        }

        /// <summary>Stock generation skips items the server has no template for: they could be neither priced nor delivered.</summary>
        bool HasTemplate(int lowId, int highId) => _catalog.TryGet(lowId, out _) && _catalog.TryGet(highId, out _);

        bool TryOpenPlayerTrade(Player initiator, Identity targetIdentity)
        {
            Playfield? playfield = initiator.Playfield;
            if (playfield == null || initiator.Session == null)
                return false;

            if (!playfield.GetRequiredService<DynelRegistry>().TryGet(targetIdentity, out Dynel? dynel)
                || dynel is not Player partner
                || ReferenceEquals(partner, initiator))
                return false;

            if (partner.Session == null || partner.IsDead || initiator.IsDead)
                return false;

            if (!partner.Inventory.IsHydrated || !initiator.Inventory.IsHydrated)
                return false;

            if (initiator.GetEdgeDistanceTo(partner) > RangeCancelDistance)
            {
                ClientFeedback.Send(initiator, "Feedback_TargetOutsideRangeForTrade");
                return false;
            }

            // A repeated Open against the same partner just re-sends the window (client reconnect,
            // double click). Any other existing session on either side is cancelled first.
            if (TryGetSession(initiator, out TradeSession existing)
                && existing.Kind == TradeKind.Player
                && existing.Involves(partner))
            {
                SendPlayerOpen(initiator, partner);
                SendPlayerOpen(partner, initiator);
                return true;
            }

            if (HasSession(partner))
            {
                ClientFeedback.Send(initiator, "Feedback_TargetIsAlreadyInATrade");
                return false;
            }

            if (HasSession(initiator))
                Cancel(initiator, "opening another trade");

            Identity bag = playfield.GetRequiredService<DynelRegistry>().AllocateTempBagIdentity();
            var session = new TradeSession(bag, TradeKind.Player, initiator, partner, machine: null);
            Register(initiator, session);
            Register(partner, session);

            SendPlayerOpen(initiator, partner);
            SendPlayerOpen(partner, initiator);

            _logger.Info(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Player trade opened {0} <-> {1}",
                    initiator.Identity.Instance,
                    partner.Identity.Instance));

            return true;
        }

        #endregion

        #region Message dispatch

        public void Handle(Player player, TradeMessage message)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(message);

            if (player.Session == null || player.Playfield == null || !player.Inventory.IsHydrated)
                return;

            if (message.Action == TradeAction.Open)
            {
                TryOpenPlayerTrade(player, message.Target);
                return;
            }

            if (!TryGetSession(player, out TradeSession session))
            {
                Tell(player, "No active trade session.");
                return;
            }

            // A Knubot window is driven by the KnuBot trade messages only.
            if (session.Kind == TradeKind.Knubot)
                return;

            if (session.Machine?.Stock.IsConfiguredSnapshot == true
                && (session.AcceptedShopTransport == null || !IsCurrentAcceptedShop(player, session.Machine, session.AcceptedShopTransport)))
            {
                Cancel(player, "accepted vendor ownership changed");
                return;
            }

            switch (message.Action)
            {
                case TradeAction.AddItem:
                    HandleAddItem(player, session, message);
                    break;
                case TradeAction.RemoveItem:
                    HandleRemoveItem(player, session, message);
                    break;
                case TradeAction.UpdateCredits:
                    HandleUpdateCredits(player, session, message);
                    break;
                case TradeAction.Confirm:
                case TradeAction.Accept:
                    HandleAcceptOrConfirm(player, session, message.Action);
                    break;
                case TradeAction.Decline:
                    Decline(player, session);
                    break;
                default:
                    break;
            }
        }

        static bool IsCurrentAcceptedShop(Player player, VendingMachine machine,
            ZoneEngine_New.Core.Network.IZoneSession? expectedTransport = null)
        {
            var transport = player.Session;
            var field = player.Playfield;
            var owner = machine.OwnerNpc;
            return transport != null && transport.State == ZoneEngine_New.Core.Network.SessionState.InPlay
                && ReferenceEquals(transport.Player, player) && (expectedTransport == null || ReferenceEquals(transport, expectedTransport))
                && field != null && !field.IsDisposed && !player.IsDead && !player.IsPersistenceQuarantined && player.Inventory.IsHydrated
                && (owner == null || (!owner.IsDead && ReferenceEquals(owner.Shop, machine) && ReferenceEquals(owner.Playfield, field)))
                && ReferenceEquals(machine.Playfield, field)
                && player.GetEdgeDistanceTo(owner != null ? owner : machine) <= RangeCancelDistance
                && field.GetRequiredService<DynelRegistry>().TryGet(player.Identity, out var currentPlayer)
                && ReferenceEquals(currentPlayer, player)
                && field.GetRequiredService<ZoneEngine_New.Core.Mobs.NpcContentActivationService>()
                    .TryGetShopBinding(machine, out _);
        }

        void HandleAddItem(Player player, TradeSession session, TradeMessage message)
        {
            if (session.Committing)
                return;

            // Any change to the offers invalidates the other side's agreement.
            session.ClearAcceptances();

            if (session.Kind == TradeKind.Shop && IsShopSide(session, message.Target))
            {
                AddShopPick(player, session, message);
                return;
            }

            AddOfferedItem(player, session, message);
        }

        void AddShopPick(Player player, TradeSession session, TradeMessage message)
        {
            VendingMachine machine = session.Machine!;
            int stockIndex = message.Container.Instance;
            if (!machine.Stock.TryGetSlot(stockIndex, out ShopStockSlot picked))
                return;

            if (_catalog.TryGet(picked.LowId, out ItemTemplate pickedTemplate) && (pickedTemplate.Flags & (int)ItemFlags.Unique) != 0
                && (TradeRules.WouldDuplicateUnique(player, picked.LowId, picked.HighId) || session.ShopPicks.Any(index =>
                    machine.Stock.TryGetSlot(index, out ShopStockSlot other) && (other.LowId == picked.LowId || other.HighId == picked.HighId))))
            {
                ClientFeedback.SendFormatted(player, ClientFeedback.AlreadyHaveUniqueItem, pickedTemplate.Name);
                LogShopPick(player, session, message, "refused unique");
                return;
            }

            // Ammo and other stackables: every pick of the same line lands in one stack, so the line is one item of the
            // pane's twenty however many times it is picked (2,000 rounds was the most a 100-round line could reach).
            bool stackable = InventoryStacking.IsStackable(_minter.Create(picked.LowId, picked.HighId, picked.Quality, ItemSource.Vendor));
            if (session.AddShopPick(stockIndex, stackable) < 0)
            {
                ClientFeedback.Send(player, "Feedback_CantTradeMoreItems");
                return;
            }

            LogShopPick(player, session, message, "added");
            Acknowledge(player, message);
        }

        /// <summary>Shop buy-queue diagnostics: what the client sent and the queue (stock indexes) afterwards.</summary>
        static void LogShopPick(Player player, TradeSession session, TradeMessage message, string result)
        {
            var stock = session.Machine?.Stock;
            // The queue is bounded (TradeOffer.Capacity lines, MaxPicksPerStackableLine each), but a full one is long: log
            // the first few picks and the count, not every pick on every pick.
            const int LoggedPicks = 24;
            string picks = string.Join(",", session.ShopPicks.Take(LoggedPicks).Select(index =>
                stock != null && stock.TryGetSlot(index, out ShopStockSlot slot)
                    ? index.ToString(CultureInfo.InvariantCulture) + ":" + slot.LowId.ToString(CultureInfo.InvariantCulture)
                    : index.ToString(CultureInfo.InvariantCulture)));
            if (session.ShopPicks.Count > LoggedPicks)
                picks += ",... (" + session.ShopPicks.Count.ToString(CultureInfo.InvariantCulture) + " picks)";
            player.Logger.Info(string.Format(CultureInfo.InvariantCulture,
                "Shop pick {0} action={1} target={2}:{3} container={4}:{5} params={6},{7},{8},{9} queue=[{10}]",
                result, message.Action, (int)message.Target.Type, message.Target.Instance,
                (int)message.Container.Type, message.Container.Instance,
                message.Param1, message.Param2, message.Param3, message.Param4, picks));
        }

        void AddOfferedItem(Player player, TradeSession session, TradeMessage message)
        {
            // The client may only put items from its own pages on the table.
            if (message.Target.Instance != player.Identity.Instance)
                return;

            // Only loose carried items can be put on the table. Worn gear has to be unequipped first
            // (that path has a timed move), the bank is not reachable from a trade window, and overflow items can
            // only be deleted or moved into the inventory.
            Identity source = message.Container;
            Container page = source.Type switch
            {
                IdentityType.Inventory => player.Inventory.Inventory,
                _ => null!
            };
            if (page == null)
                return;

            if (!page.Content.TryGetValue(source.Instance, out Item? item) || player.Inventory.IsLockedForTransfer(item))
                return;

            if (InventoryMoveService.IsBagItem(item))
            {
                // A bag changes hands whole, and only between players: its contents' rows hang off the bag's own row, so
                // they follow it. Like the client, a bag holding a NoDrop item stays.
                Container? contents = session.Kind == TradeKind.Player ? LoadBagContents(player, item, source) : null;
                if (contents == null)
                {
                    ClientFeedback.Send(player, "Feedback_ItemCantBeTraded");
                    return;
                }

                if (contents.Content.Values.Any(TradeRules.IsNoDrop))
                {
                    ClientFeedback.Send(player, "Feedback_ChestContainsNoDrop");
                    return;
                }

                if (contents.IsOpen)
                    Item.CloseBackpack(player, contents, item.Identity);
            }

            if (TradeRules.IsNoDrop(item))
            {
                ClientFeedback.Send(player, "Feedback_NoDropItemCantBeTraded");
                return;
            }

            if (TradeRules.IsUnique(item) && session.Kind == TradeKind.Player
                && session.Other(player) is Player partner && TradeRules.WouldDuplicateUnique(partner, item.LowId, item.HighId))
            {
                ClientFeedback.SendFormatted(player, ClientFeedback.TradePartnerHasUniqueItem, item.Name);
                return;
            }

            if (page.Remove(source.Instance) == null)
                return;

            TradeOffer offer = session.OfferFor(player);
            int tradeSlot = offer.Add(item);
            if (tradeSlot < 0)
            {
                page.Add(source.Instance, item);
                ClientFeedback.Send(player, "Feedback_CantTradeMoreItems");
                return;
            }

            // The slot it left is the player's to use while it is on the table, so its row leaves the slot too.
            player.Inventory.ParkOffered(item, player.Identity.Instance);
            _flush.NotifyDirty(player);
            if (TablePlace(session, player) is ItemPlace table)
                ModuleHooks.ItemMoved(player, item, ModuleHooks.Place(ItemPlaceKind.Inventory), table);

            Acknowledge(player, message);

            Player? other = session.Other(player);
            if (other?.Session != null)
                SendItemRender(other, item, source);
        }

        void HandleRemoveItem(Player player, TradeSession session, TradeMessage message)
        {
            if (session.Committing)
                return;

            session.ClearAcceptances();

            if (session.Kind == TradeKind.Shop && IsShopSide(session, message.Target))
            {
                bool removed = session.RemoveShopPick(message.Container.Instance);
                LogShopPick(player, session, message, removed ? "removed" : "remove-rejected");
                if (!removed)
                    return;

                // One frame per side. An extra echo of the request (also under the player's identity) made the client
                // take a second copy out of the cart.
                player.Session!.Send(RemoveAck(player.Identity, message));
                player.Session.Send(RemoveAck(session.Machine!.ShopIdentity, message));
                return;
            }

            if (message.Target.Instance != player.Identity.Instance)
                return;

            TradeOffer offer = session.OfferFor(player);
            int tradeSlot = message.Container.Instance;
            Item? item = offer.Remove(tradeSlot);
            if (item == null)
                return;

            // Taken off the table by the player, it goes back where the client puts it: the first free inventory slot.
            if (!TryPlaceMain(player, item, out Container page, out int slot))
            {
                offer.TryRestore(tradeSlot, item);
                ClientFeedback.Send(player, "Feedback_InventoryFull");
                return;
            }

            player.Inventory.MarkDirty(item, page, slot);
            _flush.NotifyDirty(player);
            if (TablePlace(session, player) is ItemPlace table)
                ModuleHooks.ItemMoved(player, item, table, ModuleHooks.Place(ItemPlaceKind.Inventory));

            SendReturnToInventory(player, tradeSlot);
            Acknowledge(player, message);

            Player? other = session.Other(player);
            if (other?.Session != null)
                other.Session.Send(
                    ActionFrame(player.Identity, TradeAction.RemoveItem, player.Identity, message.Container));
        }

        void HandleUpdateCredits(Player player, TradeSession session, TradeMessage message)
        {
            if (session.Committing || session.Kind != TradeKind.Player)
                return;

            session.ClearAcceptances();

            int credits = Math.Max(0, message.Param2);
            TradeOffer offer = session.OfferFor(player);
            offer.Credits = credits;

            player.Session!.Send(CreditsFrame(player.Identity, credits));
            Player? other = session.Other(player);
            other?.Session?.Send(CreditsFrame(player.Identity, credits));
        }

        void HandleAcceptOrConfirm(Player player, TradeSession session, TradeAction action)
        {
            if (session.Committing)
                return;

            if (session.Kind == TradeKind.Shop)
            {
                CommitShop(player, session);
                return;
            }

            Player? other = session.Other(player);
            if (other == null)
            {
                Cancel(player, "trade partner is gone");
                return;
            }

            TradeOffer offer = session.OfferFor(player);

            if (action == TradeAction.Confirm)
            {
                offer.Accepted = true;
                if (!session.BothAccepted)
                {
                    other.Session?.Send(StatusFrame(other.Identity, TradeAction.Accept));
                    Tell(player, "Trade accepted. Waiting for other player.");
                    Tell(other, player.Name + " accepted the trade.");
                    return;
                }

                string? failure = ValidatePlayerTrade(session);
                if (failure != null)
                {
                    session.ClearAcceptances();
                    TellTradeFailure(session.Initiator, failure);
                    TellTradeFailure(session.Partner!, failure);
                    return;
                }

                session.Initiator.Session?.Send(StatusFrame(session.Initiator.Identity, TradeAction.Confirm));
                session.Partner!.Session?.Send(StatusFrame(session.Partner.Identity, TradeAction.Confirm));
                return;
            }

            if (!session.BothAccepted)
                return;

            offer.Ended = true;
            if (!session.BothEnded)
            {
                other.Session?.Send(EndPromptFrame(other.Identity, player.Identity));
                return;
            }

            CommitPlayerTrade(session);
        }

        #endregion

        #region Commit

        string? ValidatePlayerTrade(TradeSession session)
        {
            Player initiator = session.Initiator;
            Player partner = session.Partner!;

            string? failure = ValidateSide(initiator, session.InitiatorOffer, partner, session.PartnerOffer);
            if (failure != null)
                return failure;

            return ValidateSide(partner, session.PartnerOffer, initiator, session.InitiatorOffer);
        }

        /// <summary>
        /// Checks that <paramref name="giver"/>'s offer can legally land on <paramref name="receiver"/>.
        /// Trade items are durable rows, so they must fit in real inventory: overflow is not allowed
        /// to hold persisted items.
        /// </summary>
        static string? ValidateSide(Player giver, TradeOffer given, Player receiver, TradeOffer received)
        {
            // What does not fit in the inventory goes to overflow.
            if (!receiver.Inventory.HasRoomFor(given.Count))
                return receiver.Name + " does not have enough free inventory slots.";

            if (UniqueItems.HasRepeatedUnique(given.Items.Values.ToList(), out string repeated))
                return receiver.Name + " can only receive one " + repeated + ".";

            foreach (Item item in given.Items.Values)
            {
                if (TradeRules.IsNoDrop(item))
                    return item.Name + " cannot be traded.";

                // A traded bag's contents come from the giver's page, loaded when it was offered.
                if (InventoryMoveService.IsBagItem(item))
                {
                    if (!giver.Inventory.TryGetBackpackPage(item.Identity, out Container bag) || !bag.IsHydrated)
                        return item.Name + " cannot be traded.";
                    foreach (Item content in bag.Content.Values)
                    {
                        if (TradeRules.IsNoDrop(content))
                            return item.Name + " cannot be traded.";
                        // Locked after the bag was offered (an equip from inside it is pending, or a use is waiting on
                        // its delay): the lock's owner finishes first, or the row would be written under another player's bag.
                        if (content.Locked)
                            return item.Name + " cannot be traded right now.";
                        if (TradeRules.IsUnique(content) && TradeRules.WouldDuplicateUnique(receiver, content.LowId, content.HighId))
                            return receiver.Name + " already has " + content.Name + ".";
                    }
                }

                if (UniqueItems.WouldDuplicate(receiver, item, out string bagUnique, except: held => given.Items.Values.Contains(held)))
                    return receiver.Name + " already has " + bagUnique + ".";

                if (TradeRules.IsUnique(item) && TradeRules.WouldDuplicateUnique(receiver, item.LowId, item.HighId))
                    return receiver.Name + " already has " + item.Name + ".";
            }

            if (given.Credits > 0 && giver.Stats.GetOrZero(CharacterStat.Cash) < given.Credits)
            {
                return giver.Name
                    + " offered "
                    + given.Credits.ToString(CultureInfo.InvariantCulture)
                    + " credits but only has "
                    + giver.Stats.GetOrZero(CharacterStat.Cash).ToString(CultureInfo.InvariantCulture)
                    + ".";
            }

            long receiverCash = (long)receiver.Stats.GetOrZero(CharacterStat.Cash) + given.Credits - received.Credits;
            if (receiverCash < 0 || receiverCash > TradeRules.MaxCash)
                return receiver.Name + " cannot hold that many credits.";

            return null;
        }

        void CommitPlayerTrade(TradeSession session)
        {
            if (session.Committing)
                return;
            Player initiator = session.Initiator;
            Player partner = session.Partner!;

            string? failure = ValidatePlayerTrade(session);
            if (failure != null)
            {
                session.ClearAcceptances();
                TellTradeFailure(initiator, failure);
                TellTradeFailure(partner, failure);
                return;
            }

            session.Committing = true;

            int initiatorCredits = session.InitiatorOffer.Credits;
            int partnerCredits = session.PartnerOffer.Credits;
            bool applied = false;
            var deliveries = new List<Delivery>();
            var tradedBags = new List<Identity>();
            try
            {
                WithGates(initiator, partner, () =>
                {
                    PlanDeliveries(session.InitiatorOffer.Items.Values, partner, deliveries);
                    PlanDeliveries(session.PartnerOffer.Items.Values, initiator, deliveries);
                    int initiatorCash = checked(initiator.Stats.GetOrZero(CharacterStat.Cash) - initiatorCredits + partnerCredits);
                    int partnerCash = checked(partner.Stats.GetOrZero(CharacterStat.Cash) - partnerCredits + initiatorCredits);
                    TradePlan plan = PlanRows(initiator, initiatorCash, partner, partnerCash, deliveries, [], Identity.None);
                    applied = true;
                    session.InitiatorOffer.DrainAll();
                    session.PartnerOffer.DrainAll();
                    ApplyDeliveries(deliveries);
                    // One transaction for both, off the tick, in both lanes: after everything either already holds.
                    _flush.EnqueueJoint(initiator, partner, () => PersistPlan(plan));
                    // Neither side keeps a page for a bag that changed hands; its holder loads the contents on first use.
                    // A traded bag loses its name: the giver's is dropped and the receiver starts with none.
                    foreach (Delivery delivery in deliveries)
                    {
                        if (!InventoryMoveService.IsBagItem(delivery.Item))
                            continue;
                        initiator.Inventory.ForgetBackpackPage(delivery.Item.Identity);
                        partner.Inventory.ForgetBackpackPage(delivery.Item.Identity);
                        initiator.BagNames.Remove(delivery.Item.InstanceId);
                        partner.BagNames.Remove(delivery.Item.InstanceId);
                        tradedBags.Add(delivery.Item.Identity);
                    }
                    // Held back until after the Complete frames: see SendTradeCash.
                    SetCashQuietly(initiator, initiatorCash, ModuleHooks.SourceOf(partner));
                    SetCashQuietly(partner, partnerCash, ModuleHooks.SourceOf(initiator));
                });
            }
            catch (Exception exception)
            {
                FailedCommit(session, exception, applied);
                return;
            }

            // Each side receives the other's table (raised before overflow trimming, which may destroy a delivery).
            foreach (Delivery delivery in deliveries)
                ModuleHooks.ItemMoved(delivery.Receiver, delivery.Item,
                    ModuleHooks.Place(ItemPlaceKind.Trade, (ReferenceEquals(delivery.Receiver, initiator) ? partner : initiator).Identity),
                    ModuleHooks.Place(ItemPlaceKind.Inventory));

            // Both clients forget the traded bags' names (the Lost Eden client keeps its own copy per bag).
            foreach (Identity bag in tradedBags)
            {
                if (initiator.SpeaksExtendedMessages)
                    initiator.Session?.Send(new BagNameMessage { Identity = initiator.Identity, Bag = bag, Name = string.Empty });
                if (partner.SpeaksExtendedMessages)
                    partner.Session?.Send(new BagNameMessage { Identity = partner.Identity, Bag = bag, Name = string.Empty });
            }
            if (tradedBags.Count > 0)
            {
                _flush.NotifyDirty(initiator);
                _flush.NotifyDirty(partner);
            }

            Unregister(initiator);
            Unregister(partner);
            // Deliveries that went to overflow are sent; keep each window to its capacity.
            OverflowGrants.Trim(initiator);
            OverflowGrants.Trim(partner);

            SendCompleteClose(initiator, partner);
            SendCompleteClose(partner, initiator);
            SendTradeCash(initiator);
            SendTradeCash(partner);
            // After the Complete frames: the client moves the other side's items in on them (see SendTradeDelivery).
            foreach (Delivery delivery in deliveries)
                SendTradeDelivery(delivery);
            SendSocialStatus(initiator, 0);
            SendSocialStatus(partner, 0);

            _logger.Info(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Player trade completed {0} <-> {1} items={2}/{3} credits={4}/{5}",
                    initiator.Identity.Instance,
                    partner.Identity.Instance,
                    session.InitiatorOffer.Count,
                    session.PartnerOffer.Count,
                    initiatorCredits,
                    partnerCredits));
        }

        /// <summary>The bag's page with its contents, loaded from its rows if it was not opened this session.</summary>
        static Container? LoadBagContents(Player player, Item bag, Identity slot)
        {
            if (player.Inventory.TryGetBackpackPage(bag.Identity, out Container page) && page.IsHydrated)
                return page;
            if (player.Playfield?.GetService<IInventoryRepository>() is not IInventoryRepository repository
                || player.Playfield.GetService<IItemBuilder>() is not IItemBuilder items)
                return null;
            page = player.Inventory.GetOrCreateBackpackPage(bag, bag.Identity, slot);
            player.Inventory.HydrateBackpack(page, repository, items);
            return page.IsHydrated ? page : null;
        }

        // Reserve only durable main-inventory locations. Offers remain untouched until COMMIT.
        static void PlanDeliveries(IEnumerable<Item> items, Player receiver, List<Delivery> deliveries)
        {
            if (!TryPlanDeliveries(items, receiver, deliveries))
                throw new InvalidOperationException("Inventory has no durable location for the trade.");
        }

        /// <summary>
        /// Plans each item in send order, as the client will place them: a stackable item that fits whole onto an
        /// existing stack merges into it (the client parks the AddTemplate copy in its first free slot and folds it
        /// in on the JoinItems echo, freeing that slot again); anything else takes the first free slot.
        /// </summary>
        static bool TryPlanDeliveries(IEnumerable<Item> items, Player receiver, List<Delivery> deliveries)
        {
            Container page = receiver.Inventory.Inventory;
            Container overflow = receiver.Inventory.Overflow;
            var occupied = new HashSet<int>(page.Content.Keys);
            var overflowOccupied = new HashSet<int>(overflow.Content.Keys);
            var planned = new Dictionary<Item, int>(ReferenceEqualityComparer.Instance);
            foreach (Delivery delivery in deliveries)
            {
                if (!ReferenceEquals(delivery.Receiver, receiver))
                    continue;
                if (ReferenceEquals(delivery.Page, overflow))
                    overflowOccupied.Add(delivery.Slot);
                else if (delivery.MergeTarget == null)
                    occupied.Add(delivery.Slot);
                else
                    planned[delivery.MergeTarget] = planned.GetValueOrDefault(delivery.MergeTarget) + delivery.Item.StackCount;
            }

            foreach (Item item in items)
            {
                int slot = page.Offset;
                while (slot < page.Offset + page.Capacity && occupied.Contains(slot)) slot++;
                if (slot == page.Offset + page.Capacity)
                {
                    // No inventory room: the next free overflow slot, as the client fills it (trimmed after the commit).
                    int spare = overflow.Offset;
                    while (spare < overflow.Offset + overflow.Capacity && overflowOccupied.Contains(spare)) spare++;
                    if (spare == overflow.Offset + overflow.Capacity)
                        return false;

                    overflowOccupied.Add(spare);
                    deliveries.Add(new Delivery(item, receiver, overflow, spare));
                    continue;
                }

                if (InventoryStacking.TryFindStackTarget(page, item, planned, out int targetSlot, out Item target))
                {
                    planned[target] = planned.GetValueOrDefault(target) + item.StackCount;
                    deliveries.Add(new Delivery(item, receiver, page, slot, target, targetSlot));
                    continue;
                }

                occupied.Add(slot);
                deliveries.Add(new Delivery(item, receiver, page, slot));
            }

            return true;
        }

        static void ApplyDeliveries(List<Delivery> deliveries)
        {
            foreach (Delivery delivery in deliveries)
            {
                if (delivery.MergeTarget != null)
                {
                    if (!delivery.Page.Content.TryGetValue(delivery.MergeSlot, out Item? current)
                        || !ReferenceEquals(current, delivery.MergeTarget))
                        throw new InvalidOperationException("Planned trade stack changed after durable commit.");
                    delivery.MergeTarget.StackCount += delivery.Item.StackCount;
                    continue;
                }

                if (!delivery.Page.Add(delivery.Slot, delivery.Item))
                    throw new InvalidOperationException("Reserved trade location changed after durable commit.");
                delivery.Item.IsPersisted = true;
                if (delivery.Page.Identity.Type == IdentityType.OverflowWindow)
                    delivery.Receiver.Inventory.NoteOverflowArrival(delivery.Item);
            }
        }

        /// <summary>
        /// A player trade's delivery. Live sends no grant: on the Complete frames the receiving client moves the items the
        /// other side rendered into its pane (TradeRender) into its own first free slots itself (capture
        /// 2026-10-21T20:04:28Z and 20:04:35Z: Complete pair, then only the social status, no AddTemplate). An AddTemplate
        /// on top made the client add the item from it and keep the rendered copy in the partner's trade pane, so the next
        /// trade with that player showed it again. A stack merge still needs its JoinItems to fold the copy the client
        /// placed. An overflow arrival gets no grant either: the overflow window sent by OverflowGrants.Trim already
        /// holds it, and a TemplateAction Overflow on top showed a second, unmovable copy.
        /// </summary>
        static void SendTradeDelivery(Delivery delivery)
        {
            if (delivery.MergeTarget != null)
                InventoryStacking.SendJoin(delivery.Receiver, delivery.Page, delivery.MergeSlot, delivery.Slot);
        }

        static void SendDelivery(Delivery delivery)
        {
            if (delivery.MergeTarget != null)
                InventoryStacking.SendGrantAndJoin(delivery.Receiver, delivery.Item, delivery.Page, delivery.MergeSlot, delivery.Slot);
            else
                SendGrant(delivery.Receiver, delivery.Item, delivery.Page);
        }

        /// <summary>
        /// One item reaching a receiver. Slot is its own main-inventory slot, or for a merge (MergeTarget set) the
        /// free slot the client parks the copy in before folding it into the stack at MergeSlot.
        /// </summary>
        sealed record Delivery(Item Item, Player Receiver, Container Page, int Slot, Item? MergeTarget = null, int MergeSlot = 0);

        /// <summary>Takes the participants' persistence gates in id order around a tick-side plan and apply; no I/O inside.</summary>
        internal static void WithGates(Player first, Player? second, Action action)
        {
            ArgumentNullException.ThrowIfNull(first);
            ArgumentNullException.ThrowIfNull(action);
            Player low = second != null && second.Identity.Instance < first.Identity.Instance ? second : first;
            Player? high = second == null ? null : ReferenceEquals(low, first) ? second : first;
            lock (low.PersistenceGate)
            {
                if (high == null)
                    action();
                else
                    lock (high.PersistenceGate) action();
            }
        }

        enum TradeRowKind
        {
            /// <summary>The item goes to a place (an insert for an item without a row, a move for one with).</summary>
            Place,

            /// <summary>The item's row moves to a graveyard.</summary>
            Retire,

            /// <summary>The item never gets a row (a never-stored item merged away).</summary>
            Drop
        }

        /// <summary>One row of a trade plan; <paramref name="Owner"/>'s lane writes it.</summary>
        sealed record PlannedTradeRow(TradeRowKind Kind, Item Item, Player Owner, bool HadRow, Identity Container, int Placement, int Count);

        /// <summary>
        /// What a trade commit writes, decided on the tick from memory as it stands then (<see cref="PlanRows"/>) and
        /// stored off the tick (<see cref="PersistPlan"/>). Each item is recorded with whether it had a row at the plan,
        /// and an item the plan inserts counts as persisted from then on: its insert runs ahead of anything that moves or
        /// retires it later in its receiver's lane. Kept across retries of a transient failure.
        /// </summary>
        sealed class TradePlan
        {
            public TradePlan(Player first, Player? second, List<TradeCharacterWrite> characters, long sequence)
            {
                First = first;
                Second = second;
                Characters = characters;
                Sequence = sequence;
                SecondWrites = second == null ? null : new InventoryWriteSet();
            }

            public Player First { get; }
            public Player? Second { get; }
            public List<PlannedTradeRow> Rows { get; } = new();
            public List<TradeCharacterWrite> Characters { get; }
            public long Sequence { get; }
            public QueuedRowClaim Claim { get; set; } = null!;
            public InventoryWriteSet FirstWrites { get; } = new();
            public InventoryWriteSet? SecondWrites { get; }

            public InventoryWriteSet WritesFor(Player owner) => ReferenceEquals(owner, First) ? FirstWrites : SecondWrites!;

            public InventoryWriteSet? OtherWritesFor(Player owner) => ReferenceEquals(owner, First) ? SecondWrites : FirstWrites;
        }

        /// <summary>
        /// Decides the trade's rows on the tick, before the deliveries are applied in memory: which items are inserted
        /// (no row yet; they count as persisted from here on), moved, retired to a graveyard or never written. Nothing
        /// changes until every check has passed; then the plan claims its rows (<see cref="QueuedRowClaim"/>) in both
        /// participants, so lane work queued ahead of its transaction leaves their dirty entries for it.
        /// </summary>
        static TradePlan PlanRows(Player first, int firstCash, Player? second, int secondCash,
            List<Delivery> deliveries, IReadOnlyList<Item> retired, Identity graveyard,
            CharacterStat firstCurrency = CharacterStat.Cash, int firstCurrencyValue = 0)
        {
            var characters = new List<TradeCharacterWrite>
            {
                firstCurrency != CharacterStat.Cash
                    ? new(first.Identity.Instance, firstCash, [], (int)firstCurrency, firstCurrencyValue)
                    : new(first.Identity.Instance, firstCash, [])
            };
            if (second != null) characters.Add(new(second.Identity.Instance, secondCash, []));
            var plan = new TradePlan(first, second, characters, PlayerInventory.DirtySequence);

            var stackTotals = new Dictionary<Item, int>(ReferenceEqualityComparer.Instance);
            foreach (Delivery delivery in deliveries)
            {
                Item item = delivery.Item;
                if (item.InstanceId <= 0) throw new InvalidOperationException("Trade item has no durable instance id.");
                if (delivery.MergeTarget == null)
                {
                    plan.Rows.Add(new(TradeRowKind.Place, item, delivery.Receiver, item.IsPersisted, delivery.Page.Identity, delivery.Slot, item.StackCount));
                    continue;
                }

                // A merged item gives up its own row (a traded stack is retired, a minted one never written)
                // and its count lands on the stack's row.
                plan.Rows.Add(item.IsPersisted
                    ? new(TradeRowKind.Retire, item, delivery.Receiver, true,
                        new Identity { Type = IdentityType.None, Instance = delivery.Receiver.Identity.Instance }, item.InstanceId, 0)
                    : new(TradeRowKind.Drop, item, delivery.Receiver, false, Identity.None, 0, 0));
                stackTotals[delivery.MergeTarget] = stackTotals.GetValueOrDefault(delivery.MergeTarget, delivery.MergeTarget.StackCount)
                    + item.StackCount;
            }

            foreach (Delivery delivery in deliveries)
            {
                if (delivery.MergeTarget is not Item target || !stackTotals.Remove(target, out int total))
                    continue;
                if (target.InstanceId <= 0 || !target.IsPersisted || total > InventoryStacking.MaxStackCount)
                    throw new InvalidOperationException("Invalid trade stack merge.");
                plan.Rows.Add(new(TradeRowKind.Place, target, delivery.Receiver, true, delivery.Page.Identity, delivery.MergeSlot, total));
            }

            foreach (Item item in retired)
            {
                if (item.InstanceId <= 0) throw new InvalidOperationException("Trade item has no durable instance id.");
                plan.Rows.Add(item.IsPersisted
                    ? new(TradeRowKind.Retire, item, first, true, graveyard, item.InstanceId, 0)
                    : new(TradeRowKind.Drop, item, first, false, Identity.None, 0, 0));
            }

            // Inserted by this plan's transaction: persisted to every later move or retire, whose write follows it.
            foreach (PlannedTradeRow row in plan.Rows)
                if (row.Kind == TradeRowKind.Place && !row.HadRow)
                    row.Item.IsPersisted = true;
            plan.Claim = new QueuedRowClaim(second == null ? [first.Inventory] : [first.Inventory, second.Inventory],
                plan.Rows.Select(row => row.Item.InstanceId));
            return plan;
        }

        /// <summary>
        /// The queued trade transaction, in the participants' lanes: the plan's rows folded with each participant's
        /// dirty inventory taken now (an entry made since the plan wins; one made before it, or one a giver still holds
        /// for an item that changed hands, is overridden), their uploaded nanos and final cash, in one transaction.
        /// </summary>
        void PersistPlan(TradePlan plan)
        {
            Player[] players = plan.Second == null ? [plan.First] : [plan.First, plan.Second];
            WithGates(plan.First, plan.Second, () =>
            {
                // The plan's rows are decided with what is taken now: their dirty entries are folded in.
                plan.Claim.Release();
                foreach (Player player in players)
                {
                    PlayerInventory.InventoryDirtyFlush? taken = player.Inventory.TakeDirty();
                    taken?.MarkNewlyPersisted();
                    plan.WritesFor(player).Fold(taken, player.DrainDirtyUploadedNanos());
                }
            });

            // An item that changed hands leaves nothing behind in its giver's writes.
            foreach (PlannedTradeRow row in plan.Rows)
                plan.OtherWritesFor(row.Owner)?.Drop(row.Item.InstanceId);
            foreach (PlannedTradeRow row in plan.Rows)
            {
                InventoryWriteSet writes = plan.WritesFor(row.Owner);
                switch (row.Kind)
                {
                    case TradeRowKind.Place:
                        writes.Place(row.Item, row.HadRow, row.Container, row.Placement, row.Count, plan.Sequence);
                        break;
                    case TradeRowKind.Retire:
                        writes.Retire(row.Item, row.HadRow, row.Container, row.Placement);
                        break;
                    default:
                        writes.Drop(row.Item.InstanceId);
                        break;
                }
            }

            var inserts = new List<ItemInstanceRecord>(plan.FirstWrites.Inserts);
            var updates = new List<ItemLocationUpdate>(plan.FirstWrites.Locations);
            var characters = new List<TradeCharacterWrite>
            {
                plan.Characters[0] with { UploadedNanoIds = plan.FirstWrites.UploadedNanos }
            };
            if (plan.SecondWrites != null)
            {
                inserts.AddRange(plan.SecondWrites.Inserts);
                updates.AddRange(plan.SecondWrites.Locations);
                characters.Add(plan.Characters[1] with { UploadedNanoIds = plan.SecondWrites.UploadedNanos });
            }
            _persistence.Persist(new TradePersistenceBatch(inserts, updates, characters));
        }

        /// <summary>
        /// The tick-side plan or apply threw. Once memory changed (<paramref name="applied"/>) both aggregates must be
        /// reloaded from storage; before that nothing happened, and the window stays open to try again.
        /// </summary>
        void FailedCommit(TradeSession session, Exception exception, bool applied)
        {
            _logger.Error(exception, "Trade commit failed; completion was not acknowledged.");
            if (applied || session.Initiator.IsPersistenceQuarantined || session.Partner?.IsPersistenceQuarantined == true)
            {
                session.Initiator.QuarantinePersistence();
                session.Partner?.QuarantinePersistence();
                Unregister(session.Initiator);
                if (session.Partner != null) Unregister(session.Partner);
                session.Machine?.Stock.CloseTrade();
                session.Initiator.Session?.Close();
                session.Partner?.Session?.Close();
                return;
            }
            session.Committing = false;
            session.ClearAcceptances();
            Tell(session.Initiator, "Trade failed: it could not be planned. Please try again.");
            if (session.Partner != null) Tell(session.Partner, "Trade failed: it could not be planned. Please try again.");
        }

        void CommitShop(Player player, TradeSession session)
        {
            if (session.Committing)
                return;
            VendingMachine machine = session.Machine!;
            if (machine.Stock.IsConfiguredSnapshot && (!ReferenceEquals(player, session.Initiator)
                || session.AcceptedShopTransport == null || !IsCurrentAcceptedShop(player, machine, session.AcceptedShopTransport)))
            {
                Cancel(player, "accepted vendor ownership changed");
                return;
            }
            NpcCharacter? owner = machine.OwnerNpc;
            if (machine.Playfield == null || (owner != null && (owner.IsDead || owner.Playfield == null)))
            {
                Cancel(player, "the shop closed");
                return;
            }

            TradeOffer offer = session.InitiatorOffer;
            int skillSteps = TradeRules.PricingSkillSteps(player, machine.PricingSkill);

            // Purchases are paid in the shop's currency (a VP shop: Victory Points); what the shop buys from the player
            // is paid in credits either way. The pricing skill (Computer Literacy) only discounts credit prices.
            CharacterStat currency = machine.CurrencyStat;
            bool payInCash = currency == CharacterStat.Cash;
            int buySkillSteps = payInCash ? skillSteps : 0;

            // Price the purchase off the live stock list: a slot may have been re-rolled since the
            // client picked it, and the shopper must pay for what they will actually receive.
            var purchases = new List<Purchase>(session.ShopPicks.Count);
            long buyTotal = 0;
            foreach (int stockIndex in session.ShopPicks)
            {
                if (!machine.Stock.TryGetSlot(stockIndex, out ShopStockSlot stock))
                    continue;

                int price = TradeRules.BuyPrice(
                    TradeRules.ItemValue(_catalog, stock.LowId, stock.HighId, stock.Quality),
                    machine.SellModifier,
                    buySkillSteps);
                // A stock line's own price percentage (vending machine stock entry; 100 = normal).
                price = (int)Math.Min(int.MaxValue, Math.Max(0, (long)price * stock.PricePercent / 100));
                purchases.Add(new Purchase(stock, price));
                buyTotal += price;
            }

            // The client's own sell gates (Gamecode.dll 0x1006701d), enforced here so a modified client cannot get
            // around them: only a credit shop buys, never a NoDrop item, and never one worth more than its TradeLimit.
            if (offer.Count > 0 && !machine.BuysItems)
            {
                Tell(player, "This shop does not buy items.");
                return;
            }

            long sellTotal = 0;
            foreach (Item item in offer.Items.Values)
            {
                if (TradeRules.IsNoDrop(item))
                {
                    ClientFeedback.Send(player, "Feedback_NoDropItemCantBeTraded");
                    return;
                }

                int sellPrice = TradeRules.SellPrice(
                    TradeRules.StackValue(item, TradeRules.ItemValue(_catalog, item.LowId, item.HighId, item.Quality)),
                    machine.BuyModifier,
                    skillSteps);
                if (sellPrice > machine.TradeLimit)
                {
                    Tell(player, "This shop will not pay that much for an item.");
                    return;
                }

                sellTotal += sellPrice;
            }

            long finalCash = (long)player.Stats.GetOrZero(CharacterStat.Cash) + sellTotal - (payInCash ? buyTotal : 0);
            // The base value is the stored balance the purchase sets; a bonus on the currency is never spendable.
            long finalCurrency = payInCash ? 0 : (long)player.Stats.GetOrZero(currency, StatDetail.Base) - buyTotal;
            if (finalCash < 0 || finalCurrency < 0)
            {
                if (payInCash)
                    ClientFeedback.Send(player, "Feedback_NotEnoughCredits");
                else
                    Tell(player, string.Format(CultureInfo.InvariantCulture,
                        "You can not afford this. {0} extra {1} is needed.", -finalCurrency, currency));
                return;
            }

            if (finalCash > TradeRules.MaxCash)
            {
                Tell(player, "Trade failed: that would exceed the credit cap.");
                return;
            }

            // Mint first so unique duplication is checked against real items, and so a rejected
            // purchase costs the player nothing.
            var minted = new List<MintedPurchase>(purchases.Count);
            foreach (Purchase purchase in purchases)
            {
                ShopStockSlot stock = purchase.Stock;
                Item item = _minter.Create(stock.LowId, stock.HighId, stock.Quality, ItemSource.Vendor);
                if (TradeRules.IsUnique(item)
                    && (TradeRules.WouldDuplicateUnique(player, item.LowId, item.HighId)
                        || ContainsTemplate(minted, item)))
                {
                    ClientFeedback.Send(player, "Feedback_AlreadyGotUniqueItem");
                    return;
                }

                minted.Add(new MintedPurchase(item, purchase.Price));
            }

            // The same stackable line picked several times arrives as one stack, a new one past 50000. Ids are
            // allocated after that because a bag only gets its container identity once it has an instance id.
            List<Item> bought = ConsolidateStacks(minted.Select(p => p.Item).ToList(), template => _minter.Create(
                template.LowId, template.HighId, template.Quality, ItemSource.Vendor));
            foreach (Item item in bought)
                item.AssignInstanceId(_ids.Allocate());

            // What does not fit in the inventory (or join a stack) goes to overflow, which is saved; only both full refuse.
            if (!TryPlanDeliveries(bought, player, new List<Delivery>()))
            {
                ClientFeedback.Send(player, "Feedback_NoRoomInInventory");
                return;
            }

            session.Committing = true;
            bool applied = false;
            int soldCount = offer.Count;
            Item[] sold = ModuleHooks.WantsItemMoves(player.Playfield) ? offer.Items.Values.ToArray() : [];
            var deliveries = new List<Delivery>();
            try
            {
                WithGates(player, null, () =>
                {
                    PlanDeliveries(bought, player, deliveries);
                    TradePlan plan = PlanRows(player, checked((int)finalCash), null, 0, deliveries, offer.Items.Values.ToArray(), machine.Identity,
                        currency, payInCash ? 0 : checked((int)finalCurrency));
                    applied = true;
                    offer.DrainAll();
                    ApplyDeliveries(deliveries);
                    if (!payInCash)
                        player.Stats.Set(currency, (int)finalCurrency, StatDetail.Base, dirty: true);
                    SetCash(player, finalCash, AORebirth.Modules.Events.StatChangeReason.Shop, ModuleHooks.ShopSource(machine));
                    foreach (Delivery delivery in deliveries) SendDelivery(delivery);
                    _flush.Enqueue(player, () => PersistPlan(plan));
                });
            }
            catch (Exception exception)
            {
                FailedCommit(session, exception, applied);
                return;
            }

            if (ModuleHooks.WantsItemMoves(player.Playfield))
            {
                ItemPlace shop = ModuleHooks.Place(ItemPlaceKind.Shop, owner?.Identity ?? machine.Identity);
                AORebirth.Modules.Events.StatChangeSource vendor = ModuleHooks.ShopSource(machine);
                foreach (Item item in sold)
                    ModuleHooks.ItemMoved(player, item, ModuleHooks.Place(ItemPlaceKind.Inventory), shop, source: vendor);
                foreach (Delivery delivery in deliveries)
                    ModuleHooks.ItemMoved(player, delivery.Item, shop, ModuleHooks.Place(ItemPlaceKind.Inventory), source: vendor);
            }

            Unregister(player);
            machine.Stock.CloseTrade();
            OverflowGrants.Trim(player);

            Identity shopIdentity = machine.ShopIdentity;
            player.Session!.Send(EndFrame(player.Identity, TradeAction.Complete, shopIdentity, shopIdentity));

            _logger.Info(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Shop trade completed player={0} machine={1} bought={2} sold={3} charged={4} {6} sellTotal={5}",
                    player.Identity.Instance,
                    shopIdentity.Instance,
                    minted.Count,
                    soldCount,
                    buyTotal,
                    sellTotal,
                    currency));
        }

        /// <summary>
        /// Folds stackable items of the same low/high/QL into as few stacks as the 50000 cap allows, in order of
        /// first appearance. The total count is unchanged; <paramref name="mint"/> supplies an extra item only when
        /// a group's total needs more stacks than it has items.
        /// </summary>
        static List<Item> ConsolidateStacks(List<Item> items, Func<Item, Item> mint)
        {
            var result = new List<Item>(items.Count);
            var groups = new Dictionary<(int, int, int), List<Item>>();
            var order = new List<(int, int, int)>();
            foreach (Item item in items)
            {
                if (!InventoryStacking.IsStackable(item))
                {
                    result.Add(item);
                    continue;
                }

                var key = (item.LowId, item.HighId, item.Quality);
                if (!groups.TryGetValue(key, out List<Item>? group))
                {
                    groups[key] = group = [];
                    order.Add(key);
                }

                group.Add(item);
            }

            foreach (var key in order)
            {
                List<Item> group = groups[key];
                long remaining = 0;
                foreach (Item item in group)
                    remaining += item.StackCount;

                int index = 0;
                while (remaining > 0)
                {
                    Item stack = index < group.Count ? group[index] : mint(group[0]);
                    index++;
                    int count = (int)Math.Min(remaining, InventoryStacking.MaxStackCount);
                    stack.StackCount = count;
                    remaining -= count;
                    result.Add(stack);
                }
            }

            return result;
        }

        static bool ContainsTemplate(List<MintedPurchase> items, Item candidate)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Item.LowId == candidate.LowId && items[i].Item.HighId == candidate.HighId)
                    return true;
            }

            return false;
        }

        /// <summary>A stock line the shopper picked, priced at the moment of commit.</summary>
        readonly struct Purchase
        {
            public Purchase(ShopStockSlot stock, int price)
            {
                Stock = stock;
                Price = price;
            }

            public ShopStockSlot Stock { get; }

            public int Price { get; }
        }

        readonly struct MintedPurchase
        {
            public MintedPurchase(Item item, int price)
            {
                Item = item;
                Price = price;
            }

            public Item Item { get; }

            public int Price { get; }
        }

        #endregion

        #region Knubot

        /// <summary>
        /// Opens the "Give Item" window for <paramref name="npc"/>. The client moves each item into its own
        /// KnuBot trade container as it sends it (Gamecode.dll N3Msg_NPCChatAddTradeItem), so the server mirrors
        /// that container: items leave the player's page and sit locked in <see cref="TradeSession.InitiatorOffer"/>
        /// at the index the client used, the first free one. Nothing is written until the trade commits, so an
        /// interrupted trade leaves every item at its stored location.
        /// </summary>
        /// <param name="slots">The window's item slots: the script's largest trade (<see cref="Knubot.KnubotScript.TradeSlots"/>).</param>
        public bool TryOpenKnubot(Player player, NpcCharacter npc, string message, int slots = TradeOffer.Capacity)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(npc);

            if (player.Session == null || player.Playfield == null || !player.Inventory.IsHydrated || player.IsDead
                || npc.IsDead || !ReferenceEquals(npc.Playfield, player.Playfield))
                return false;

            if (player.GetEdgeDistanceTo(npc) > RangeCancelDistance)
            {
                ClientFeedback.Send(player, "Feedback_TooFarAway");
                return false;
            }

            if (TryGetSession(player, out TradeSession existing))
            {
                if (existing.Kind == TradeKind.Knubot && ReferenceEquals(existing.Npc, npc))
                    return true;

                Close(existing, "opening another trade");
            }

            var session = new TradeSession(Identity.None, TradeKind.Knubot, player, partner: null, machine: null, npc,
                initiatorSlots: slots);
            Register(player, session);

            // The client rejects an empty text (length must be 1..10000).
            player.Session.Send(new KnuBotStartTradeMessage
            {
                Identity = player.Identity,
                Unknown1 = 2,
                Target = npc.Identity,
                NumberOfItemSlotsInTradeWindow = session.InitiatorOffer.Slots,
                Message = string.IsNullOrWhiteSpace(message) ? "Trade" : message
            });

            _logger.Info(string.Format(CultureInfo.InvariantCulture, "Knubot trade opened player={0} npc={1}",
                player.Identity.Instance, npc.Identity.Instance));
            return true;
        }

        /// <summary>The Knubot trade <paramref name="player"/> has open with <paramref name="npc"/>, if any.</summary>
        public bool TryGetKnubot(Player player, NpcCharacter npc, out TradeSession session)
            => TryGetSession(player, out session) && session.Kind == TradeKind.Knubot && ReferenceEquals(session.Npc, npc)
                && !session.Committing;

        /// <summary>An item put into or taken out of the Give Item window.</summary>
        public void HandleKnubot(Player player, KnuBotTradeMessage message)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(message);

            if (player.Session == null || player.Playfield == null || !player.Inventory.IsHydrated
                || !TryGetSession(player, out TradeSession session) || session.Kind != TradeKind.Knubot || session.Committing
                || session.Npc == null || message.Target != session.Npc.Identity
                || player.GetEdgeDistanceTo(session.Npc) > RangeCancelDistance)
                return;

            if ((KnuBotTradeAction)message.Unknown2 == KnuBotTradeAction.Add)
                AddKnubotItem(player, session, session.InitiatorOffer, message.Container);
            else if ((KnuBotTradeAction)message.Unknown2 == KnuBotTradeAction.Remove)
                RemoveKnubotItem(player, session, session.InitiatorOffer, message.Container.Instance);
        }

        /// <summary>
        /// The client moves the item into its trade table before it sends the add (N3Msg_NPCChatAddTradeItem), from
        /// the main inventory, an open bag or overflow; it refuses worn and bank items itself. Every outcome here
        /// leaves the client holding the item where the server holds it.
        /// </summary>
        void AddKnubotItem(Player player, TradeSession session, TradeOffer offer, Identity source)
        {
            // The client has already moved the item into the first free index of its trade container.
            int clientSlot = FirstFreeOfferSlot(offer);
            if (!TryResolveKnubotSource(player, source, out Container page, out int slot, out Item item))
            {
                // Nothing at that slot on the server: the client's view was already wrong. The best the server can do is
                // put the client's copy where the client will put it (its first free main-inventory slot).
                if (clientSlot >= 0)
                    SendReturnToInventory(player, clientSlot);
                _logger.Info(string.Format(CultureInfo.InvariantCulture,
                    "Knubot add names an empty slot player={0} source={1}/{2}", player.Identity.Instance, source.Type, source.Instance));
                return;
            }

            if (player.Inventory.IsLockedForTransfer(item) || page.LinkedItem is { Locked: true })
            {
                // Mid-transfer: the server may not move it, so the client's copy goes back to the server's slot.
                if (clientSlot >= 0)
                    ReturnLockedKnubotAdd(player, page, slot, clientSlot);
                return;
            }

            // No-drop items are allowed: the NPC only consumes them, they never reach another player. Overflow items can
            // only be deleted or moved into the inventory, which is where the undo puts them.
            string? refusal = InventoryMoveService.IsBagItem(item) || ReferenceEquals(page, player.Inventory.Overflow)
                    ? "Feedback_ItemCantBeTraded"
                : clientSlot < 0 ? "Feedback_CantTradeMoreItems"
                : null;
            if (refusal != null)
            {
                ClientFeedback.Send(player, refusal);
                if (clientSlot >= 0)
                    UndoKnubotAdd(player, page, slot, item, clientSlot);
                return;
            }

            if (page.Remove(slot) == null)
                return;

            offer.Add(item);
            // The slot it left is the player's to use while it is in the window, so its row leaves the slot too.
            player.Inventory.ParkOffered(item, player.Identity.Instance);
            _flush.NotifyDirty(player);
            if (TablePlace(session, player) is ItemPlace table)
                ModuleHooks.ItemMoved(player, item, PagePlace(player, page), table);
        }

        /// <summary>
        /// The page and slot a Knubot add names: a main-inventory or overflow slot, or a slot of a carried bag the client
        /// knows by the handle the server gave it (high 16 bits), as <see cref="InventoryMoveService"/> reads them.
        /// </summary>
        static bool TryResolveKnubotSource(Player player, Identity source, out Container page, out int slot, out Item item)
        {
            page = null!;
            slot = -1;
            item = null!;
            switch (source.Type)
            {
                case IdentityType.Inventory:
                    page = player.Inventory.Inventory;
                    slot = source.Instance;
                    break;
                case IdentityType.OverflowWindow:
                    page = player.Inventory.Overflow;
                    slot = source.Instance;
                    break;
                case IdentityType.Backpack:
                    int handle = InventoryMoveService.DecodeBackpackHandle(source);
                    slot = InventoryMoveService.DecodeBackpackSlot(source);
                    if (handle == 0 || !player.Inventory.TryGetOwnedBackpackPageByHandle(handle, out page) || !page.IsHydrated)
                        return false;
                    break;
                default:
                    return false;
            }

            return page.Content.TryGetValue(slot, out item!);
        }

        static ItemPlace PagePlace(Player player, Container page)
            => ReferenceEquals(page, player.Inventory.Inventory) || ReferenceEquals(page, player.Inventory.Overflow)
                ? ModuleHooks.Place(ItemPlaceKind.Inventory)
                : ModuleHooks.Place(ItemPlaceKind.Bag, page.Identity);

        /// <summary>
        /// The client sends the item back to the first free main-inventory slot; the server re-homes it the same way
        /// so both agree where it is.
        /// </summary>
        void UndoKnubotAdd(Player player, Container page, int slot, Item item, int clientSlot)
        {
            if (TryPlaceMain(player, item, out Container target, out int targetSlot, removeFrom: page, removeSlot: slot))
            {
                player.Inventory.MarkDirty(item, target, targetSlot);
                _flush.NotifyDirty(player);
            }

            SendReturnToInventory(player, clientSlot);
        }

        /// <summary>
        /// The server keeps a locked item where it is, so the client's copy is walked back there: the return puts it in
        /// the client's first free main-inventory slot (the client ignores Slot for a return from its trade table and
        /// searches from 0x40), then an ordinary move acknowledgement moves it from that slot to the server's. The slot
        /// the client took it from is free on the client, so for a main-inventory item its first free slot is the lower
        /// of the server's first free slot and that one. A move into a bag lands in the bag's first free slot on the
        /// client, which is the item's slot only when the bag has no gap below it, so a bag item is instead deleted from
        /// the slot it landed in and the open bag's contents are sent again, which the client shows as they are (how a
        /// mission key retired from an open bag leaves it). With no free main-inventory slot the client leaves its copy
        /// in the trade table; the window's end lists what is really returned and the client drops the copy then.
        /// </summary>
        static void ReturnLockedKnubotAdd(Player player, Container page, int slot, int clientSlot)
        {
            SendReturnToInventory(player, clientSlot);

            Container main = player.Inventory.Inventory;
            int landed = main.FindFreeSlot();
            if (ReferenceEquals(page, main))
            {
                if (landed < 0 || landed > slot)
                    landed = slot;
                if (landed == slot)
                    return;
                SendMove(player, new Identity { Type = IdentityType.Inventory, Instance = landed }, player.Identity, slot);
                return;
            }

            if (landed < 0)
                return;
            var landedSlot = new Identity { Type = IdentityType.Inventory, Instance = landed };
            if (page.Identity.Type != IdentityType.Container || !page.IsOpen || page.InventoryHandle <= 0)
            {
                SendMove(player, landedSlot, page.Identity, slot);
                return;
            }

            player.Session?.Send(new CharacterActionMessage
            {
                Identity = player.Identity,
                Unknown = 0,
                Action = CharacterActionType.DeleteItem,
                Unknown1 = 0,
                Target = landedSlot,
                Parameter1 = 0,
                Parameter2 = 0,
                Unknown2 = string.Empty
            });
            player.Session?.Send(page.BuildInventoryUpdateMessage(player.Identity, page.Identity, page.InventoryHandle, unknown1: 3, unknown2: 1));
        }

        /// <summary>The move acknowledgement the client applies to its own containers (the shape of every accepted move).</summary>
        static void SendMove(Player player, Identity source, Identity target, int slot)
            => player.Session?.Send(
                new ContainerAddItem
                {
                    Identity = player.Identity,
                    Unknown = 0,
                    Source = source,
                    Target = target,
                    Slot = slot
                });

        void RemoveKnubotItem(Player player, TradeSession session, TradeOffer offer, int tradeSlot)
        {
            Item? item = offer.Remove(tradeSlot);
            if (item == null)
                return;

            // The client only takes an item back into main inventory; with no room it leaves it in the window.
            if (!TryPlaceMain(player, item, out Container page, out int slot))
            {
                offer.TryRestore(tradeSlot, item);
                ClientFeedback.Send(player, "Feedback_InventoryFull");
                return;
            }

            player.Inventory.MarkDirty(item, page, slot);
            _flush.NotifyDirty(player);
            if (TablePlace(session, player) is ItemPlace table)
                ModuleHooks.ItemMoved(player, item, table, ModuleHooks.Place(ItemPlaceKind.Inventory));
        }

        /// <summary>
        /// The NPC keeps everything in the window and takes <paramref name="credits"/>, which the client already took
        /// off its own Cash when it accepted. Returns false when nothing changed hands; the caller then ends the
        /// window with <see cref="EndKnubot(Player, NpcCharacter, int, string)"/>.
        /// </summary>
        public bool TryCommitKnubot(Player player, NpcCharacter npc, int credits)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(npc);

            if (!TryGetKnubot(player, npc, out TradeSession session) || credits < 0
                || player.Stats.GetOrZero(CharacterStat.Cash) < credits)
                return false;

            TradeOffer offer = session.InitiatorOffer;
            Item[] given = offer.Items.OrderBy(x => x.Key).Select(x => x.Value).ToArray();
            int finalCash = player.Stats.GetOrZero(CharacterStat.Cash) - credits;

            session.Committing = true;
            bool applied = false;
            try
            {
                WithGates(player, null, () =>
                {
                    TradePlan plan = PlanRows(player, finalCash, null, 0, [], given, npc.Identity);
                    applied = true;
                    offer.DrainAll();
                    SetCash(player, finalCash, AORebirth.Modules.Events.StatChangeReason.Dialogue, ModuleHooks.SourceOf(npc));
                    _flush.Enqueue(player, () => PersistPlan(plan));
                });
            }
            catch (Exception exception)
            {
                FailedCommit(session, exception, applied);
                return false;
            }

            ItemPlace npcTable = ModuleHooks.Place(ItemPlaceKind.Trade, npc.Identity);
            foreach (Item item in given)
                ModuleHooks.ItemMoved(player, item, npcTable, ModuleHooks.Place(ItemPlaceKind.Destroyed));

            Unregister(player);

            // An empty list removes every item still in the client's window and closes it.
            player.Session?.Send(RejectedItemsFrame(player, npc, [], 0));

            _logger.Info(string.Format(CultureInfo.InvariantCulture, "Knubot trade completed player={0} npc={1} items={2} credits={3}",
                player.Identity.Instance, npc.Identity.Instance, given.Length, credits));
            return true;
        }

        /// <summary>Ends <paramref name="player"/>'s Knubot trade with <paramref name="npc"/>, handing everything back.</summary>
        public void EndKnubot(Player player, NpcCharacter npc, int refundCredits, string reason)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(npc);

            if (TryGetKnubot(player, npc, out TradeSession session))
                EndKnubot(session, refundCredits, reason);
        }

        /// <summary>
        /// Hands every item back in window order, as the client does: each goes to the first free main-inventory
        /// slot and is listed in KnuBotRejectedItems; one that no longer fits is left off the list (the client drops
        /// its copy) and goes to overflow if it may. <paramref name="refundCredits"/> gives back what the client took
        /// off its Cash when it accepted.
        /// </summary>
        void EndKnubot(TradeSession session, int refundCredits, string reason)
        {
            if (session.Committing)
                return;

            session.Committing = true;
            Player player = session.Initiator;
            TradeOffer offer = session.InitiatorOffer;
            var returned = new List<KnuBotRejectedItem>(offer.Count);
            var overflowed = new List<(Item Item, Container Page)>();
            ItemPlace table = ModuleHooks.Place(ItemPlaceKind.Trade, session.Npc?.Identity ?? Identity.None);
            foreach (int tradeSlot in offer.Items.Keys.OrderBy(x => x).ToArray())
            {
                Item item = offer.Remove(tradeSlot)!;
                if (TryPlaceMain(player, item, out Container page, out int slot))
                {
                    player.Inventory.MarkDirty(item, page, slot);
                    returned.Add(RejectedItem(item));
                    ModuleHooks.ItemMoved(player, item, table, ModuleHooks.Place(ItemPlaceKind.Inventory));
                }
                else if (player.Inventory.TryPlace(item, out page, out slot))
                {
                    player.Inventory.MarkDirty(item, page, slot);
                    overflowed.Add((item, page));
                    ModuleHooks.ItemMoved(player, item, table, ModuleHooks.Place(ItemPlaceKind.Inventory));
                }
                else
                {
                    // Overflow is full too; the item's row stays parked, so it is in overflow at the next login.
                    _logger.Error(string.Format(CultureInfo.InvariantCulture,
                        "Knubot trade return failed instance={0} owner={1}; no free slot", item.InstanceId, player.Identity.Instance));
                    ClientFeedback.Send(player, "Feedback_NoRoomInInventory");
                }
            }

            _flush.NotifyDirty(player);
            Unregister(player);

            if (session.Npc != null)
                player.Session?.Send(RejectedItemsFrame(player, session.Npc, returned.ToArray(), Math.Max(0, refundCredits)));
            foreach ((Item item, Container page) in overflowed)
                SendGrant(player, item, page);
            OverflowGrants.Trim(player);

            _logger.Info(string.Format(CultureInfo.InvariantCulture, "Knubot trade closed player={0} returned={1} overflow={2} reason={3}",
                player.Identity.Instance, returned.Count, overflowed.Count, reason));
        }

        /// <summary>
        /// Where <paramref name="player"/>'s offered items sit, for module item moves: the trade table, owned by the other
        /// side (the partner, or the NPC). Null for a shop, whose sales are raised on commit as Inventory to Shop.
        /// </summary>
        static ItemPlace? TablePlace(TradeSession session, Player player) => session.Kind switch
        {
            TradeKind.Player => ModuleHooks.Place(ItemPlaceKind.Trade, session.Other(player)?.Identity ?? Identity.None),
            TradeKind.Knubot => ModuleHooks.Place(ItemPlaceKind.Trade, session.Npc?.Identity ?? Identity.None),
            _ => null
        };

        static int FirstFreeOfferSlot(TradeOffer offer)
        {
            for (int slot = 0; slot < offer.Slots; slot++)
            {
                if (!offer.Items.ContainsKey(slot))
                    return slot;
            }

            return -1;
        }

        /// <summary>
        /// Places <paramref name="item"/> in the first free main-inventory slot, the slot the client picks for an item
        /// coming out of its trade window. <paramref name="removeFrom"/> frees the item's current slot first.
        /// </summary>
        static bool TryPlaceMain(Player player, Item item, out Container page, out int slot,
            Container? removeFrom = null, int removeSlot = 0)
        {
            page = player.Inventory.Inventory;
            removeFrom?.Remove(removeSlot);
            slot = page.FindFreeSlot();
            if (slot >= 0 && page.Add(slot, item))
                return true;

            removeFrom?.Add(removeSlot, item);
            return false;
        }

        /// <summary>
        /// How the client finds the item in its window: by template ids and QL, with the unset-stat sentinel last, for
        /// every item. Live returned a refused item that way (Professor Dedlock refusing item 300674 QL 200, capture
        /// 2026-10-30T02:53:14Z). The identity form (LowId = type, HighId = instance, QL -1) left a refused Dedlock book
        /// unmatched: the client dropped its copy until the next zone.
        /// </summary>
        static KnuBotRejectedItem RejectedItem(Item item)
            => new() { LowId = item.LowId, HighId = item.HighId, Quality = item.Quality, Unknown = (int)CharacterStat.Unset };

        static KnuBotRejectedItemsMessage RejectedItemsFrame(Player player, NpcCharacter npc, KnuBotRejectedItem[] items, int credits)
            => new()
            {
                Identity = player.Identity,
                Unknown1 = 2,
                Target = npc.Identity,
                Items = items,
                Unknown2 = credits
            };

        #endregion

        #region Cancellation

        public void Decline(Player player, TradeSession session)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(session);
            Close(session, "declined");
        }

        /// <summary>Cancels the session <paramref name="player"/> is in, if any.</summary>
        public void Cancel(Player player, string reason)
        {
            ArgumentNullException.ThrowIfNull(player);

            if (!TryGetSession(player, out TradeSession session))
                return;

            Close(session, reason);
        }

        /// <summary>Cancels every session against <paramref name="machine"/> (vendor death, despawn).</summary>
        public void CloseMachine(VendingMachine machine, string reason)
        {
            ArgumentNullException.ThrowIfNull(machine);

            List<TradeSession>? affected = null;
            lock (_gate)
            {
                foreach (TradeSession session in _byPlayer.Values)
                {
                    if (!ReferenceEquals(session.Machine, machine))
                        continue;

                    affected ??= [];
                    if (!affected.Contains(session))
                        affected.Add(session);
                }
            }

            if (affected != null)
            {
                foreach (TradeSession session in affected)
                    Close(session, reason);
            }

            machine.Stock.Reset();
        }

        void Close(TradeSession session, string reason)
        {
            if (session.Committing)
                return;

            if (session.Kind == TradeKind.Knubot)
            {
                EndKnubot(session, 0, reason);
                return;
            }

            session.Committing = true;

            // Live player-trade decline sends only the Decline frame; the client puts its own offered
            // items back. Granting them again here showed a client-side duplicate. A shop window closes
            // with the same Decline frame and the client restores its offer the same way (2026-10-13: a
            // declined shop trade showed the offered item twice).
            bool clientRestores = session.Kind is TradeKind.Player or TradeKind.Shop;

            ReturnOffer(session.Initiator, session.InitiatorOffer, clientRestores, TablePlace(session, session.Initiator));
            Unregister(session.Initiator);

            Player? partner = session.Partner;
            if (partner != null)
            {
                ReturnOffer(partner, session.PartnerOffer, clientRestores, TablePlace(session, partner));
                Unregister(partner);
            }

            if (session.Kind == TradeKind.Shop)
            {
                session.Machine?.Stock.CloseTrade();
                session.Initiator.Session?.Send(
                    EndFrame(session.Initiator.Identity, TradeAction.Decline, Identity.None, Identity.None));
                SendSocialStatus(session.Initiator, 4);
            }
            else
            {
                SendDeclineClose(session.Initiator);
                SendDeclineClose(partner);
            }

            _logger.Info(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Trade closed kind={0} initiator={1} reason={2}",
                    session.Kind,
                    session.Initiator.Identity.Instance,
                    reason));
        }

        /// <summary>
        /// Puts a cancelled offer back: the first free inventory slot, or overflow when the inventory is full (the
        /// player may have used the slots the offer left). The client restores an inventory return itself when
        /// <paramref name="clientRestores"/>; an overflow return is always sent, then overflow is kept to its capacity.
        /// <paramref name="table"/>: where the offer was, for module item moves (null for a shop's sell offer).
        /// </summary>
        void ReturnOffer(Player owner, TradeOffer offer, bool clientRestores, ItemPlace? table)
        {
            var tradeSlots = new Dictionary<Item, int>(ReferenceEqualityComparer.Instance);
            foreach (KeyValuePair<int, Item> entry in offer.Items)
                tradeSlots[entry.Value] = entry.Key;

            foreach (Item item in offer.DrainAll())
            {
                if (owner.Inventory.TryPlace(item, out Container page, out int slot))
                {
                    owner.Inventory.MarkDirty(item, page, slot);
                    if (table is ItemPlace offeredAt)
                        ModuleHooks.ItemMoved(owner, item, offeredAt, ModuleHooks.Place(ItemPlaceKind.Inventory));
                    if (page.Identity.Type == IdentityType.OverflowWindow)
                    {
                        // Retail: ContainerAddItem from the trade window into overflow; the window follows (Trim).
                        if (clientRestores)
                            OverflowGrants.SendFromTrade(owner, tradeSlots.GetValueOrDefault(item), slot);
                        else
                            SendGrant(owner, item, page);
                    }
                    else if (!clientRestores)
                        SendGrant(owner, item, page);
                    continue;
                }

                _logger.Error(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Trade return failed instance={0} owner={1}; no free slot",
                        item.InstanceId,
                        owner.Identity.Instance));
                ClientFeedback.Send(owner, "Feedback_NoRoomInInventory");
            }

            // (An item with no room even in overflow keeps its parked row and is in overflow at the next login.)
            offer.Credits = 0;
            _flush.NotifyDirty(owner);
            OverflowGrants.Trim(owner);
        }

        #endregion

        #region Tick

        /// <summary>
        /// Per-playfield liveness pass. Cancels windows whose anchor died, despawned, zoned, or
        /// walked out of range.
        /// </summary>
        public void Tick(Playfield playfield, double deltaTime)
        {
            ArgumentNullException.ThrowIfNull(playfield);
            _ = deltaTime;

            // Runs on every playfield tick, so the common "nobody is trading" case must not allocate.
            List<TradeSession>? stale = null;
            lock (_gate)
            {
                if (_byPlayer.Count == 0)
                    return;

                foreach (TradeSession session in _byPlayer.Values)
                {
                    if (!ReferenceEquals(session.Initiator.Playfield, playfield))
                        continue;

                    if (!IsStale(session, playfield))
                        continue;

                    stale ??= [];
                    if (!stale.Contains(session))
                        stale.Add(session);
                }
            }

            if (stale == null)
                return;

            foreach (TradeSession session in stale)
                Close(session, "out of range or partner gone");
        }

        static bool IsStale(TradeSession session, Playfield playfield)
        {
            Player initiator = session.Initiator;
            if (initiator.Session == null || initiator.IsDead)
                return true;

            if (session.Kind == TradeKind.Shop)
            {
                VendingMachine? machine = session.Machine;
                if (machine == null || !ReferenceEquals(machine.Playfield, playfield))
                    return true;
                if (machine.Stock.IsConfiguredSnapshot && (session.AcceptedShopTransport == null
                    || !IsCurrentAcceptedShop(initiator, machine, session.AcceptedShopTransport)))
                    return true;

                NpcCharacter? owner = machine.OwnerNpc;
                if (owner != null && (owner.IsDead || !ReferenceEquals(owner.Playfield, playfield)))
                    return true;

                Dynel anchor = owner != null ? owner : machine;
                return initiator.GetEdgeDistanceTo(anchor) > RangeCancelDistance;
            }

            if (session.Kind == TradeKind.Knubot)
            {
                NpcCharacter? npc = session.Npc;
                return npc == null || npc.IsDead || !ReferenceEquals(npc.Playfield, playfield)
                    || initiator.GetEdgeDistanceTo(npc) > RangeCancelDistance;
            }

            Player? partner = session.Partner;
            if (partner == null
                || partner.Session == null
                || partner.IsDead
                || !ReferenceEquals(partner.Playfield, playfield))
                return true;

            return initiator.GetEdgeDistanceTo(partner) > RangeCancelDistance;
        }

        #endregion

        #region Registry

        bool HasSession(Player player)
        {
            lock (_gate)
                return _byPlayer.ContainsKey(player.Identity.Instance);
        }

        void Register(Player player, TradeSession session)
        {
            lock (_gate)
                _byPlayer[player.Identity.Instance] = session;
        }

        void Unregister(Player player)
        {
            lock (_gate)
                _byPlayer.Remove(player.Identity.Instance);
        }

        /// <summary>
        /// Whether a client frame addresses the machine pane rather than the player's own. The owning
        /// NPC counts too, since that is the identity the player used to open the shop. Compares whole
        /// identities: a Dynels.dat machine instance can collide numerically with a character id, and
        /// matching on instance alone would let one pane answer for the other.
        /// </summary>
        static bool IsShopSide(TradeSession session, Identity target)
        {
            VendingMachine? machine = session.Machine;
            if (machine == null)
                return false;

            if (target == machine.Identity)
                return true;

            NpcCharacter? owner = machine.OwnerNpc;
            return owner != null && target == owner.Identity;
        }

        #endregion

        #region Packets

        void SendPlayerOpen(Player viewer, Player partner)
            => viewer.Session?.Send(OpenFrame(viewer.Identity, partner.Identity, Identity.None));

        static TradeMessage OpenFrame(Identity identity, Identity target, Identity container)
            => new()
            {
                Identity = identity,
                Unknown = 0,
                Unknown1 = 2,
                Action = TradeAction.Open,
                Target = target,
                Container = container
            };

        static TradeMessage ActionFrame(
            Identity identity,
            TradeAction action,
            Identity target,
            Identity container)
            => new()
            {
                Identity = identity,
                Unknown = 0,
                Unknown1 = 2,
                Action = action,
                Target = target,
                Container = container
            };

        static TradeMessage EndFrame(
            Identity identity,
            TradeAction action,
            Identity target,
            Identity container)
            => ActionFrame(identity, action, target, container);

        static TradeMessage StatusFrame(Identity subject, TradeAction action)
            => ActionFrame(subject, action, subject, subject);

        static TradeMessage EndPromptFrame(Identity viewer, Identity partner)
            => ActionFrame(viewer, TradeAction.Accept, partner, partner);

        static TradeMessage CreditsFrame(Identity offerOwner, int credits)
            => new()
            {
                Identity = offerOwner,
                Unknown = 0,
                Unknown1 = 2,
                Action = TradeAction.UpdateCredits,
                Param1 = 0,
                Param2 = credits,
                Param3 = 0,
                Param4 = 0
            };

        static TradeMessage RemoveAck(Identity identity, TradeMessage message)
            => new()
            {
                Identity = identity,
                Unknown = 0,
                Unknown1 = message.Unknown1,
                Action = message.Action,
                Target = message.Target,
                Container = message.Container
            };

        static void Acknowledge(Player player, TradeMessage message)
            => player.Session?.Send(
                new TradeMessage
                {
                    Identity = message.Identity,
                    Unknown = message.Unknown,
                    Unknown1 = message.Unknown1,
                    Action = message.Action,
                    Target = message.Target,
                    Container = message.Container
                });

        void SendCompleteClose(Player viewer, Player partner)
        {
            // Live sends a pair of action-4 frames, one per pane, to clear both sides.
            viewer.Session?.Send(
                ActionFrame(viewer.Identity, TradeAction.Complete, partner.Identity, partner.Identity));
            viewer.Session?.Send(
                ActionFrame(partner.Identity, TradeAction.Complete, viewer.Identity, viewer.Identity));
        }

        void SendDeclineClose(Player? viewer)
        {
            if (viewer?.Session == null)
                return;

            viewer.Session.Send(EndFrame(viewer.Identity, TradeAction.Decline, Identity.None, Identity.None));
            SendSocialStatus(viewer, 0);
        }

        /// <summary>Placement marker meaning "next free slot"; the client picks the real slot.</summary>
        const int NextFreeSlotMarker = 0x6F;

        /// <summary>
        /// Renders an offered item into the partner's pane with its full stack Count: the receiving client keeps
        /// this rendered copy when the trade completes, so a 1 here shows a received stack as 1 until rezone
        /// (capture 2026-11-06T13:42:12Z: 49990-stack render carries 0xC346).
        /// </summary>
        static void SendItemRender(Player viewer, Item item, Identity source)
            => viewer.Session?.Send(
                new TemplateActionMessage
                {
                    Identity = viewer.Identity,
                    Unknown = 0,
                    ItemLowId = item.LowId,
                    ItemHighId = item.HighId,
                    Quality = item.Quality,
                    Count = item.StackCount,
                    Action = TemplateActionType.TradeRender,
                    Placement = source,
                    Unknown3 = 0,
                    Unknown4 = 0
                });

        static void SendReturnToInventory(Player player, int tradeSlot)
            => player.Session?.Send(
                new ContainerAddItem
                {
                    Identity = player.Identity,
                    Unknown = 0,
                    Source = new Identity
                    {
                        Type = IdentityType.KnuBotTradeWindow,
                        Instance = tradeSlot
                    },
                    Target = new Identity
                    {
                        Type = IdentityType.Inventory,
                        Instance = player.Identity.Instance
                    },
                    Slot = NextFreeSlotMarker
                });

        /// <summary>
        /// Tells the client about an item that just appeared. Main-inventory grants use a plain
        /// AddTemplate; an item that fell through to overflow needs the capture-backed
        /// TemplateAction + ContainerAddItem pair (capture 20260806-rabbit) so the overflow window
        /// opens and renders it.
        /// </summary>
        static void SendGrant(Player player, Item item, Container page)
        {
            if (player.Session == null)
                return;

            if (page.Identity.Type != IdentityType.OverflowWindow)
            {
                player.Session.Send(
                    new AddTemplateMessage
                    {
                        Identity = player.Identity,
                        HighId = item.HighId,
                        LowId = item.LowId,
                        Quality = item.Quality,
                        Count = item.StackCount
                    });
                return;
            }

            // From the overflow placement the item holds, never placement 0 (team request 10).
            OverflowGrants.Send(player, item);
        }

        static void SendSocialStatus(Player player, int value)
            => player.Session?.Send(
                new StatMessage
                {
                    Identity = player.Identity,
                    Stats = { new((Stat)CharacterStat.SocialStatus, (uint)value) }
                });

        void SetCash(Player player, long cash, AORebirth.Modules.Events.StatChangeReason reason,
            AORebirth.Modules.Events.StatChangeSource source = default)
        {
            using (Modules.StatChangeScope.Begin(reason, source))
                player.Stats.Set(
                    CharacterStat.Cash,
                    TradeRules.ClampCash(cash),
                    StatDetail.Base,
                    dirty: true);
            player.FlushDirtyStats();
        }

        /// <summary>Sets Cash without queuing it for the next stat flush; <see cref="SendTradeCash"/> sends it.</summary>
        static void SetCashQuietly(Player player, long cash, AORebirth.Modules.Events.StatChangeSource otherTrader)
        {
            using (Modules.StatChangeScope.Begin(AORebirth.Modules.Events.StatChangeReason.Trade, otherTrader))
                player.Stats.Set(CharacterStat.Cash, TradeRules.ClampCash(cash), StatDetail.Base, dirty: false);
        }

        /// <summary>
        /// A player trade's final Cash, sent after the Complete frames. The client moves the traded credits itself on
        /// Complete, so a Cash stat sent before it was counted twice (500k traded showed as 1m); sent after, it sets the
        /// value the server holds.
        /// </summary>
        static void SendTradeCash(Player player)
            => player.Session?.Send(
                new StatMessage
                {
                    Identity = player.Identity,
                    Stats = { new((Stat)CharacterStat.Cash, (uint)player.Stats.GetOrZero(CharacterStat.Cash)) }
                });

        static void TellTradeFailure(Player player, string failure)
        {
            if (failure.EndsWith("cannot be traded.", StringComparison.Ordinal))
                ClientFeedback.Send(player, "Feedback_NoDropItemCantBeTraded");
            else if (failure.EndsWith("does not have enough free inventory slots.", StringComparison.Ordinal))
                ClientFeedback.Send(player, "Feedback_NoRoomInInventory");
            else if (failure.Contains(" already has ", StringComparison.Ordinal))
                ClientFeedback.Send(player, "Feedback_AlreadyGotUniqueItem");
            else
                Tell(player, "Trade failed: " + failure);
        }

        static void Tell(Player player, string text)
            => player.Session?.Send(
                new ChatTextMessage
                {
                    Identity = player.Identity,
                    Text = text,
                    Unknown1 = 0,
                    Unknown2 = 0
                });

        #endregion
    }
}
