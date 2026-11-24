namespace ZoneEngine_New.Core.Inventory
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    using AORebirth.Enums;

    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages.N3Messages;

    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// Authoritative ClientMoveItemToInventory, ClientContainerAddItem, and delayed equip/unequip.
    /// </summary>
    public sealed class InventoryMoveService
    {
        const int DefaultEquipDelay = 20;

        const int TemplateActionCount = 1;

        /// <summary>
        /// Server→client: the equip delay is over. The client starts its equip delay (timer kind 6) as it sends a move to
        /// or from a worn slot, from the two items' EquipDelay (211), reading a missing stat as 1234567890, and refuses every
        /// item move while it runs (Gamecode.dll N3Msg_MoveItemToInventory → FUN_1004b1f4 → FUN_1004824c). This action ends
        /// it: FUN_1005d3ff case 0x30 → FUN_10049421 removes the character's equip delay timer, then refreshes the view
        /// Target names (Target.Instance = absolute placement). Without it a delay built from an item lacking 211 never ends
        /// before a zone or relog (rubi-ka.org team request 9).
        /// </summary>
        const CharacterActionType EquipDoneAction = (CharacterActionType)0x92;

        /// <summary>The automatic placement a loot take names, and the echo the client joins a looted stack on.</summary>
        const int LootAutoPlacement = 0x6F;

        private readonly object _gate = new();
        private readonly Dictionary<int, PendingEquip> _pending = new();
        private readonly IZoneLogger _logger;
        private readonly InventoryFlushService _flush;
        private readonly InventoryActionService _actions;

        public InventoryMoveService(IZoneLogger logger, InventoryFlushService flush, InventoryActionService actions)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(flush);
            _logger = logger;
            _flush = flush;
            _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        }

        /// <summary>The engine's one inventory action service, which each playfield's services expose too.</summary>
        internal InventoryActionService Actions => _actions;

        public void CancelPending(int characterId)
        {
            PendingEquip? pending;
            lock (_gate)
            {
                if (!_pending.Remove(characterId, out pending))
                    return;
            }

            SetMoveLock(pending, false);
            SendEquipDone(pending.Player, WearSide(pending));
        }

        public bool HasPending(int characterId)
        {
            lock (_gate)
                return _pending.ContainsKey(characterId);
        }

        public void Tick(Playfield playfield, double deltaTime)
        {
            ArgumentNullException.ThrowIfNull(playfield);
            if (deltaTime <= 0)
                return;

            // Lists only when something is pending: this runs every tick on every playfield.
            List<PendingEquip>? due = null;
            List<PendingEquip>? stale = null;
            lock (_gate)
            {
                if (_pending.Count == 0)
                    return;

                List<int>? remove = null;
                foreach (KeyValuePair<int, PendingEquip> pair in _pending)
                {
                    PendingEquip pending = pair.Value;
                    if (!ReferenceEquals(pending.OriginPlayfield, playfield))
                        continue;

                    if (!ReferenceEquals(pending.Player.Playfield, playfield))
                    {
                        (stale ??= []).Add(pending);
                        (remove ??= []).Add(pair.Key);
                        continue;
                    }

                    pending.RemainingSeconds -= deltaTime;
                    if (pending.RemainingSeconds > 0)
                        continue;

                    (due ??= []).Add(pending);
                    (remove ??= []).Add(pair.Key);
                }

                if (remove != null)
                    foreach (int id in remove)
                        _pending.Remove(id);
            }

            if (stale != null)
                foreach (PendingEquip pending in stale)
                    SetMoveLock(pending, false);

            if (due != null)
                foreach (PendingEquip pending in due)
                {
                    CompletePending(pending);
                    SendEquipDone(pending.Player, WearSide(pending));
                }
        }

        public void Handle(Player player, ClientMoveItemToInventory message)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(message);
            Identity? wearSide = WearSide(player, message.SourceContainer, message.Slot);
            Chest? chest = FindChestSource(player, message.SourceContainer);
            if (chest != null)
            {
                lock (player.PersistenceGate)
                    lock (chest.LootGate) HandleMove(player, message);
            }
            else
            {
                HandleMove(player, message);
            }
            EndClientEquipDelay(player, wearSide);
        }

        /// <summary>
        /// A request that touched a worn slot started the client's equip delay. Unless it left an equip pending (whose end
        /// sends the action), it is over now: completed at once or refused, so the client's delay ends with it.
        /// </summary>
        void EndClientEquipDelay(Player player, Identity? wearSide)
        {
            if (wearSide is Identity side && !HasPending(player.Identity.Instance))
                SendEquipDone(player, side);
        }

        /// <summary>The worn placement a move request names, as its source or its target; null when it touches none.</summary>
        static Identity? WearSide(Player player, Identity source, int? targetPlacement)
        {
            if (source.Type.IsWearPage())
                return source;
            if (targetPlacement is int placement
                && player.Inventory.TryResolveTargetSlot(placement, out Container page, out int slot, out bool isWear)
                && isWear)
                return new Identity { Type = page.Identity.Type, Instance = slot };
            return null;
        }

        static Identity WearSide(PendingEquip pending)
            => pending.DestPage.Identity.Type.IsWearPage()
                ? new Identity { Type = pending.DestPage.Identity.Type, Instance = pending.DestSlot }
                : new Identity { Type = pending.SourcePage.Identity.Type, Instance = pending.SourceSlot };

        static void SendEquipDone(Player player, Identity wearSide)
        {
            player.Session?.Send(
                new CharacterActionMessage
                {
                    Identity = player.Identity,
                    Unknown = 0,
                    Action = EquipDoneAction,
                    Unknown1 = 0,
                    Target = wearSide,
                    Parameter1 = 0,
                    Parameter2 = 0,
                    Unknown2 = string.Empty
                });
        }

        void HandleMove(Player player, ClientMoveItemToInventory message)
        {
            if (!player.Inventory.IsHydrated || player.Session == null || player.Playfield == null)
                return;

            if (IsBlockedByPending(player, message))
            {
                _logger.Warn(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "ClientMoveItemToInventory rejected during pending equip char={0} source={1} target={2}",
                        player.Identity.Instance,
                        message.SourceContainer,
                        message.Slot));
                return;
            }

            if (!TryResolveSource(
                    player,
                    message.SourceContainer,
                    out Container sourcePage,
                    out int sourceSlot,
                    out Item item,
                    out LootableDynel? lootSource))
            {
                return;
            }

            // Looted to the automatic marker: stock picks a stack to join before it looks for a free slot, so a full
            // inventory still takes the join.
            if (lootSource != null && message.Slot == LootAutoPlacement && !AutoConsumedItems.IsAutoConsumed(item)
                && TryFindLootJoinTarget(player.Inventory.Inventory, item, out int joinSlot, out Item joinTarget))
            {
                if (TryJoinLoot(player, sourcePage, sourceSlot, item, lootSource, joinSlot, joinTarget))
                    SendAck(player, message.SourceContainer, player.Identity, LootAutoPlacement);
                return;
            }

            bool sourceIsWear = sourcePage.Identity.Type.IsWearPage();
            if (!player.Inventory.TryResolveTargetSlot(
                    message.Slot,
                    out Container destPage,
                    out int destSlot,
                    out bool destIsWear))
            {
                _logger.Warn(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "ClientMoveItemToInventory unresolved target char={0} target={1}",
                        player.Identity.Instance,
                        message.Slot));
                return;
            }

            // From overflow an item can only go into the inventory: no equipping, banking or bagging it straight from there.
            if (sourcePage.Identity.Type == IdentityType.OverflowWindow && !ReferenceEquals(destPage, player.Inventory.Inventory))
                return;

            destPage.Content.TryGetValue(destSlot, out Item? destOccupant);
            if (destOccupant != null && destOccupant.Locked)
                return;
            // Loot never swaps: the occupant would be put into the corpse or chest.
            if (lootSource != null && destOccupant != null)
                return;

            // Occupied wear or unequip-onto-occupied bag is a swap. Bag-to-bag stays empty-only.
            if (destOccupant != null && !destIsWear && !sourceIsWear)
                return;

            // Equipping from inside a bag swaps the worn item into that bag slot; a worn bag may not go there.
            if (WouldNestBag(sourcePage, sourceSlot, destPage, destSlot, item, destOccupant))
                return;

            bool touchesEquipment = sourceIsWear || destIsWear;
            if (touchesEquipment)
            {
                TryBeginEquipmentMove(
                    player,
                    message.SourceContainer,
                    player.Identity,
                    sourcePage,
                    sourceSlot,
                    destPage,
                    destSlot,
                    item,
                    destOccupant,
                    lootSource);
                return;
            }

            if (!ApplyMove(player, sourcePage, sourceSlot, destPage, destSlot, item, lootSource))
                return;

            // Original chest takes 1105/1106 and 1141/1142 echo the automatic bag marker (0x6F). Corpse loot that reaches
            // here found no stack to join (that path returned above), so it names the physical slot it landed in: the new
            // client places a looted item only from a real slot. Ownership uses the slot ApplyMove committed.
            int acknowledgementPlacement = lootSource is Chest && message.Slot == LootAutoPlacement
                ? message.Slot : destSlot;
            SendAck(player, message.SourceContainer, player.Identity, acknowledgementPlacement);

            // Looted auto-consumed items (Null flag + Can Consume) are used as they land.
            if (lootSource != null && ReferenceEquals(destPage, player.Inventory.Inventory))
                AutoConsumedItems.ConsumeLanded(player, destSlot, item, _actions);
        }

        public void Handle(Player player, ClientContainerAddItem message)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(message);
            HandleAdd(player, message);
            EndClientEquipDelay(player, WearSide(player, message.Source, targetPlacement: null));
        }

        void HandleAdd(Player player, ClientContainerAddItem message)
        {
            if (!player.Inventory.IsHydrated || player.Session == null || player.Playfield == null)
                return;

            if (!TryResolveAddSource(
                    player,
                    message.Source,
                    out Container sourcePage,
                    out int sourceSlot,
                    out Item item))
                return;

            if (player.Inventory.IsLockedForTransfer(item))
                return;

            // TODO: Block temporary items on ClientContainerAddItem
            if (!TryResolveAddTarget(player, message.Target, out Container destPage))
                return;

            // A bag goes into the bank but never into another bag. An open bag closes as it is put away.
            if (IsBagItem(item))
            {
                if (!ReferenceEquals(destPage, player.Inventory.Bank))
                    return;
                if (player.Inventory.TryGetBackpackPage(item.Identity, out Container bagPage) && bagPage.IsOpen)
                    Item.CloseBackpack(player, bagPage, item.Identity);
            }

            if (ReferenceEquals(sourcePage, destPage))
                return;

            if ((destPage.Flags & ContainerFlags.CanAdd) == 0)
                return;

            int destSlot = destPage.FindFreeSlot();
            if (destSlot < 0)
                return;

            if (sourcePage.Identity.Type.IsWearPage())
            {
                TryBeginEquipmentMove(
                    player,
                    message.Source,
                    message.Target,
                    sourcePage,
                    sourceSlot,
                    destPage,
                    destSlot,
                    item,
                    destOccupant: null,
                    lootSource: null);
                return;
            }

            if (!ApplyMove(player, sourcePage, sourceSlot, destPage, destSlot, item, lootSource: null))
                return;

            SendAck(player, message.Source, message.Target, destSlot);
        }

        void TryBeginEquipmentMove(
            Player player,
            Identity ackSource,
            Identity ackTarget,
            Container sourcePage,
            int sourceSlot,
            Container destPage,
            int destSlot,
            Item item,
            Item? destOccupant,
            LootableDynel? lootSource)
        {
            if (HasPending(player.Identity.Instance)
                || player.Playfield?.GetService<ItemUseService>()?.HasPending(player.Identity.Instance) == true)
                return;

            bool destIsWear = destPage.Identity.Type.IsWearPage();
            bool sourceIsWear = sourcePage.Identity.Type.IsWearPage();
            if (destIsWear && !MeetsEquipRequirements(player, item, destPage, destSlot, destOccupant))
            {
                LogEquipRefused(player, item, destPage, destSlot);
                return;
            }

            if (sourceIsWear
                && destOccupant != null
                && !MeetsEquipRequirements(player, destOccupant, sourcePage, sourceSlot, item))
            {
                LogEquipRefused(player, destOccupant, sourcePage, sourceSlot);
                return;
            }

            if (!MeetsWeaponHandPairing(player, item, destPage, destSlot, sourcePage, sourceSlot))
                return;

            if (!BeltDeckSlots.AllowsMove(player, sourcePage, sourceSlot, destPage, destSlot, destOccupant))
                return;

            // Social clothing takes the default delay going on and coming off; an item going onto another wear page
            // always takes its own.
            double delaySeconds = ResolveEquipDelaySeconds(item, IsSocialMove(sourcePage, destPage));
            if (destOccupant != null)
                delaySeconds += ResolveEquipDelaySeconds(destOccupant, IsSocialMove(destPage, sourcePage));

            var pending = new PendingEquip(
                player,
                ackSource,
                ackTarget,
                sourcePage,
                sourceSlot,
                destPage,
                destSlot,
                item,
                destOccupant,
                lootSource,
                delaySeconds,
                destSlot);

            if (delaySeconds <= 0)
            {
                CompletePending(pending);
                return;
            }

            SetMoveLock(pending, true);
            lock (_gate)
            {
                if (_pending.TryAdd(player.Identity.Instance, pending))
                    return;
            }

            SetMoveLock(pending, false);
        }

        static bool TryResolveAddSource(
            Player player,
            Identity source,
            out Container page,
            out int slot,
            out Item item)
        {
            page = null!;
            slot = -1;
            item = null!;

            if (source.Type == IdentityType.Backpack || source.Type == IdentityType.Container)
                return TryResolveOwnedBackpackSource(player, source, out page, out slot, out item);

            if (!player.Inventory.TryGetItem(source.Type, source.Instance, out item))
                return false;

            if (player.Inventory.IsLockedForTransfer(item))
                return false;

            slot = source.Instance;
            page = source.Type switch
            {
                IdentityType.Inventory => player.Inventory.Inventory,
                IdentityType.WeaponPage => player.Inventory.Equipment,
                IdentityType.ArmorPage => player.Inventory.Armor,
                IdentityType.ImplantPage => player.Inventory.Implant,
                IdentityType.SocialPage => player.Inventory.Social,
                IdentityType.BankByRef when player.Inventory.Bank.IsHydrated => player.Inventory.Bank,
                IdentityType.OverflowWindow => player.Inventory.Overflow,
                _ => null!
            };

            return page != null && (page.Flags & ContainerFlags.CanRemove) != 0;
        }

        static bool TryResolveOwnedBackpackSource(
            Player player,
            Identity source,
            out Container page,
            out int slot,
            out Item item)
        {
            page = null!;
            slot = DecodeBackpackSlot(source);
            item = null!;

            // A bag is only ever named by the handle the server gave the client for it. A packet without one is refused.
            int handle = DecodeBackpackHandle(source);
            if (handle != 0 && player.Inventory.TryGetOwnedBackpackPageByHandle(handle, out page))
                return TryReadRemovableSlot(page, slot, out item);

            return false;
        }

        static bool TryReadRemovableSlot(Container page, int slot, out Item item)
        {
            item = null!;
            if ((page.Flags & ContainerFlags.CanRemove) == 0)
                return false;

            return page.Content.TryGetValue(slot, out item!) && !item.Locked;
        }

        static bool TryResolveAddTarget(Player player, Identity target, out Container destPage)
        {
            destPage = null!;

            if (target.Type == IdentityType.Container)
                return player.Inventory.TryGetOwnedBackpackPage(target, out destPage);

            if (target.Type != IdentityType.Bank && target.Type != IdentityType.BankByRef)
                return false;

            if (target.Instance != player.Identity.Instance)
                return false;

            if (!player.Inventory.Bank.IsHydrated)
                return false;

            destPage = player.Inventory.Bank;
            return true;
        }

        /// <summary>
        /// A bag would land inside a bag: the moved item into a bag page, or the swapped-out occupant back into the
        /// bag page the moved item came from.
        /// </summary>
        static bool WouldNestBag(Container sourcePage, int sourceSlot, Container destPage, int destSlot, Item item,
            Item? destOccupant)
        {
            if (ReferenceEquals(sourcePage, destPage) && sourceSlot == destSlot)
                return false;
            return (IsBagPage(destPage) && IsBagItem(item))
                || (destOccupant != null && IsBagPage(sourcePage) && IsBagItem(destOccupant));
        }

        static bool IsBagPage(Container page) => (page.Flags & ContainerFlags.Backpack) != 0;

        public static bool IsBagItem(Item item)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.Identity.Type == IdentityType.Container)
                return true;

            return item.Definition != null && item.Definition.ItemType == (int)IdentityType.Backpack;
        }

        bool IsBlockedByPending(Player player, ClientMoveItemToInventory message)
        {
            lock (_gate)
            {
                if (!_pending.TryGetValue(player.Identity.Instance, out PendingEquip? pending))
                    return false;

                if (message.SourceContainer.Type == IdentityType.Backpack
                    || message.SourceContainer.Type == IdentityType.Container)
                {
                    int handle = DecodeBackpackHandle(message.SourceContainer);
                    int slot = DecodeBackpackSlot(message.SourceContainer);
                    if (pending.SourcePage.InventoryHandle == handle && pending.SourceSlot == slot)
                        return true;
                }
                else if (message.SourceContainer.Type == pending.SourcePage.Identity.Type
                    && message.SourceContainer.Instance == pending.SourceSlot)
                {
                    return true;
                }

                if (pending.LockedInstanceId > 0
                    && TryPeekSourceInstance(player, message.SourceContainer, out int instanceId)
                    && instanceId == pending.LockedInstanceId)
                {
                    return true;
                }

                if (player.Inventory.TryResolveTargetSlot(
                        message.Slot,
                        out Container destPage,
                        out int destSlot,
                        out _))
                {
                    if (ReferenceEquals(destPage, pending.DestPage) && destSlot == pending.DestSlot)
                        return true;
                    if (ReferenceEquals(destPage, pending.SourcePage) && destSlot == pending.SourceSlot)
                        return true;
                }

                return false;
            }
        }

        static bool TryPeekSourceInstance(Player player, Identity source, out int instanceId)
        {
            instanceId = 0;
            if (!TryResolveSource(player, source, out _, out _, out Item item, out _))
                return false;

            instanceId = item.InstanceId;
            return instanceId > 0;
        }

        internal static bool TryResolveSource(
            Player player,
            Identity source,
            out Container page,
            out int slot,
            out Item item,
            out LootableDynel? lootSource)
        {
            page = null!;
            slot = -1;
            item = null!;
            lootSource = null;

            if (source.Type == IdentityType.Backpack || source.Type == IdentityType.Container)
            {
                if (TryResolveOwnedBackpackSource(player, source, out page, out slot, out item))
                    return true;

                if (source.Type != IdentityType.Backpack)
                    return false;

                int handle = DecodeBackpackHandle(source);
                slot = DecodeBackpackSlot(source);

                LootableDynel? lootable = FindOpenLootByHandle(player, handle);
                if (lootable == null || (lootable.Loot.Flags & ContainerFlags.CanRemove) == 0)
                    return false;

                page = lootable.Loot;
                lootSource = lootable;
                return page.Content.TryGetValue(slot, out item!) && !item.Locked
                    && (lootable is not Chest chest || chest.CanDistribute(item));
            }

            if (!player.Inventory.TryGetItem(source.Type, source.Instance, out item))
                return false;

            if (player.Inventory.IsLockedForTransfer(item))
                return false;

            slot = source.Instance;
            page = source.Type switch
            {
                IdentityType.Inventory => player.Inventory.Inventory,
                IdentityType.WeaponPage => player.Inventory.Equipment,
                IdentityType.ArmorPage => player.Inventory.Armor,
                IdentityType.ImplantPage => player.Inventory.Implant,
                IdentityType.SocialPage => player.Inventory.Social,
                IdentityType.OverflowWindow => player.Inventory.Overflow,
                IdentityType.BankByRef => player.Inventory.Bank,
                _ => null!
            };

            return page != null && (page.Flags & ContainerFlags.CanRemove) != 0;
        }

        internal static Chest? FindChestSource(Player player, Identity source)
        {
            if (source.Type != IdentityType.Backpack || player.Playfield == null)
                return null;
            int handle = DecodeBackpackHandle(source);
            if (handle == 0)
                return null;
            return FindOpenLootByHandle(player, handle) as Chest;
        }

        /// <summary>
        /// The loot this player has open under <paramref name="handle"/>, a handle from their own pool (shared with their
        /// bags, so it cannot also name a bag). Loot they closed, that despawned, or that someone else now has open is null.
        /// </summary>
        internal static LootableDynel? FindOpenLootByHandle(Player player, int handle)
        {
            if (handle == 0 || player.Playfield == null
                || !player.Inventory.TryGetLootByHandle(handle, out Identity lootIdentity))
                return null;
            DynelRegistry? registry = player.Playfield.GetService<DynelRegistry>();
            if (registry == null || !registry.TryGet(lootIdentity, out Dynel? dynel)
                || dynel is not LootableDynel lootable
                || lootable.OpenerIdentity != player.Identity
                || lootable.InventoryHandle != handle)
                return null;
            return lootable;
        }

        void CompletePending(PendingEquip pending)
        {
            if (pending.LootSource is Chest chest)
            {
                lock (pending.Player.PersistenceGate)
                    lock (chest.LootGate) CompletePendingCore(pending);
                return;
            }
            CompletePendingCore(pending);
        }

        void CompletePendingCore(PendingEquip pending)
        {
            SetMoveLock(pending, false);

            Player player = pending.Player;
            if (!player.Inventory.IsHydrated
                || player.IsDead
                || player.Session == null
                || !ReferenceEquals(player.Playfield, pending.OriginPlayfield))
                return;

            if (!pending.SourcePage.Content.TryGetValue(pending.SourceSlot, out Item? current)
                || !ReferenceEquals(current, pending.Item))
            {
                _logger.Warn(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Pending equip aborted; source changed char={0} slot={1}",
                        player.Identity.Instance,
                        pending.SourceSlot));
                return;
            }

            // The occupant of the destination was requirement-checked when the move began, and an
            // empty destination carries no lock. Anything that arrived since (trade return, trade
            // delivery, shop purchase, loot) would otherwise be swapped onto a wear page unchecked.
            Item? occupant = pending.DestPage.Content.GetValueOrDefault(pending.DestSlot);
            if (!ReferenceEquals(occupant, pending.SwappedItem))
            {
                _logger.Warn(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Pending equip aborted; destination changed char={0} slot={1}",
                        player.Identity.Instance,
                        pending.DestSlot));
                return;
            }

            if (!ApplyMove(
                    player,
                    pending.SourcePage,
                    pending.SourceSlot,
                    pending.DestPage,
                    pending.DestSlot,
                    pending.Item,
                    pending.LootSource))
            {
                return;
            }

            // Equipping passed its ToWield/ToWear checks here, so a held-off item works again.
            pending.Item.PlayshiftSuppressed = false;

            // Live finish order (surgery-clinic + pants captures):
            // unequip CharacterAction/TemplateAction → ContainerAddItem → equip CharacterAction/TemplateAction.
            // TemplateAction Unequip/Equip; Placement=(WearPage:absoluteSlot); cell-announced. A changed look follows
            // as AppearanceUpdate after the item has moved (social capture 2026-10-06T18:12:50Z): sent before the
            // ContainerAddItem, the client rebuilt the look from the old slots and the meshes did not change.
            SendUnequipActions(player, pending);
            player.RebaseDeferringAppearance();
            ApplyWearCastNanos(player, pending);
            SendAck(player, pending.AckSource, pending.AckTarget, pending.AckTargetPlacement);
            SendEquipActions(player, pending);
            NotifyEquipmentChanged(player, pending);
            player.AnnounceDeferredAppearance();

            if (pending.DestPage.Identity.Type == IdentityType.ArmorPage && IsLevelingItem(pending.Item)
                && RescaleLevelingArmor(player))
                player.Rebase();
        }

        /// <summary>
        /// Weapon, armor and implant moves carry the equip / unequip CharacterAction and the wear TemplateAction. A social
        /// move carries neither: live answers it with ContainerAddItem alone, then AppearanceUpdate (capture
        /// 2026-10-06T18:12:48Z). The client applies an item's modifiers on those actions, so sending them for social
        /// clothing made its stats come and go client-side.
        /// </summary>
        static bool SendsWearActions(Container page)
            => page.Identity.Type.IsWearPage() && page.Identity.Type != IdentityType.SocialPage;

        /// <summary>
        /// A social right / left hand weapon: live sends the Equip / unequip CharacterAction with the social placement and
        /// nothing else (no TemplateAction, no AppearanceUpdate); the client mounts the weapon and its stance itself.
        /// Equipping one is preceded by StopFight (capture 2026-10-06T20:20:27Z).
        /// </summary>
        static bool IsSocialHand(Container page, int slot) => PlayerInventory.TryGetSocialHand(page, slot, out _);

        /// <summary>
        /// Social hand equip: a WeaponItemFullUpdate under a fresh weapon-instance id, StopFight (live), then the Equip
        /// action naming that instance with the social placement, so the client mounts the weapon and its stance.
        /// </summary>
        static void SendSocialWeaponEquip(Player player, Item item, Container page, int slot, bool stopFight)
        {
            if (!PlayerInventory.TryGetSocialHand(page, slot, out _))
                return;

            WeaponItemFullUpdateMessage? wifu = player.BuildSocialWeaponItemFullUpdate(item, slot);
            if (wifu != null)
                player.Session?.Send(wifu);
            if (stopFight)
                SendSocialWeaponStopFight(player);
            SendEquipCharacterAction(player, wifu?.Identity ?? ResolveEquipTarget(item), slot);
        }

        static void SendSocialWeaponStopFight(Player player)
        {
            // Live: N3 flag byte 0, Unknown1 1 (capture 2026-10-06T20:20:27Z).
            var stop = new StopFightMessage { Identity = player.Identity, Unknown = 0, Unknown1 = 1 };
            if (player.FightingTarget.Instance != 0)
            {
                player.Cell?.Announce(stop);
                player.SetFightingTarget(Identity.None);
            }
            else
            {
                player.Session?.Send(stop);
            }
        }

        static void SendUnequipActions(Player player, PendingEquip pending)
        {
            if (SendsWearActions(pending.SourcePage))
            {
                SendUnequipCharacterAction(player, pending.SourceSlot);
                AnnounceWearTemplateAction(
                    player,
                    pending.Item,
                    pending.SourcePage.Identity.Type,
                    pending.SourceSlot,
                    TemplateActionType.Remove);
            }
            else if (IsSocialHand(pending.SourcePage, pending.SourceSlot))
            {
                SendUnequipCharacterAction(player, pending.SourceSlot);
            }

            if (pending.SwappedItem != null && IsSocialHand(pending.DestPage, pending.DestSlot))
                SendUnequipCharacterAction(player, pending.DestSlot);

            if (pending.SwappedItem != null && SendsWearActions(pending.DestPage))
            {
                SendUnequipCharacterAction(player, pending.DestSlot);
                AnnounceWearTemplateAction(
                    player,
                    pending.SwappedItem,
                    pending.DestPage.Identity.Type,
                    pending.DestSlot,
                    TemplateActionType.Remove);
            }
        }

        static void SendEquipActions(Player player, PendingEquip pending)
        {
            if (IsSocialHand(pending.DestPage, pending.DestSlot))
                SendSocialWeaponEquip(player, pending.Item, pending.DestPage, pending.DestSlot, stopFight: true);

            if (pending.SwappedItem != null && IsSocialHand(pending.SourcePage, pending.SourceSlot))
                SendSocialWeaponEquip(player, pending.SwappedItem, pending.SourcePage, pending.SourceSlot, stopFight: false);

            if (SendsWearActions(pending.DestPage))
            {
                SendEquipCharacterAction(player, pending.Item, pending.DestSlot);
                AnnounceWearTemplateAction(
                    player,
                    pending.Item,
                    pending.DestPage.Identity.Type,
                    pending.DestSlot,
                    TemplateActionType.Wear);
            }

            // Wear→wear swap: the vacated source slot now holds the swapped item.
            if (pending.SwappedItem != null && SendsWearActions(pending.SourcePage))
            {
                SendEquipCharacterAction(player, pending.SwappedItem, pending.SourceSlot);
                AnnounceWearTemplateAction(
                    player,
                    pending.SwappedItem,
                    pending.SourcePage.Identity.Type,
                    pending.SourceSlot,
                    TemplateActionType.Wear);
            }
        }

        static void SendUnequipCharacterAction(Player player, int slot)
        {
            player.Session?.Send(
                new CharacterActionMessage
                {
                    Identity = player.Identity,
                    Unknown = 0,
                    Action = CharacterActionType.Unknown3,
                    Unknown1 = 0,
                    Target = Identity.None,
                    Parameter1 = 0,
                    Parameter2 = slot,
                    Unknown2 = string.Empty
                });
        }

        static void SendEquipCharacterAction(Player player, Item item, int slot)
            => SendEquipCharacterAction(player, ResolveEquipTarget(item), slot);

        static void SendEquipCharacterAction(Player player, Identity target, int slot)
        {
            player.Session?.Send(
                new CharacterActionMessage
                {
                    Identity = player.Identity,
                    Unknown = 0,
                    Action = CharacterActionType.Equip,
                    Unknown1 = 0,
                    Target = target,
                    Parameter1 = 0,
                    Parameter2 = slot,
                    Unknown2 = string.Empty
                });
        }

        static void AnnounceWearTemplateAction(
            Player player,
            Item item,
            IdentityType wearPage,
            int slot,
            TemplateActionType action)
        {
            player.Cell?.Announce(
                new TemplateActionMessage
                {
                    Identity = player.Identity,
                    Unknown = 0,
                    ItemLowId = item.LowId,
                    ItemHighId = item.HighId,
                    Quality = item.Quality,
                    Count = TemplateActionCount,
                    Action = action,
                    Placement = new Identity { Type = wearPage, Instance = slot },
                    Unknown3 = 0,
                    Unknown4 = 0
                });
        }

        /// <summary>
        /// Live Equip Target is the item wire identity (e.g. armor instance type 51022 / 0xC74E).
        /// Prefer the assigned occupancy identity; fall back to catalog type + InstanceId.
        /// </summary>
        static Identity ResolveEquipTarget(Item item)
        {
            if (item.Identity.Type != IdentityType.None && item.Identity.Instance != 0)
                return item.Identity;

            int type = item.ResolvedItemType;
            if (type != 0 && item.InstanceId > 0)
                return new Identity { Type = (IdentityType)type, Instance = item.InstanceId };

            return item.Identity;
        }

        static void ApplyWearCastNanos(Player player, PendingEquip pending)
        {
            Playfield? playfield = player.Playfield;
            if (playfield == null)
                return;

            IItemBuilder? items = playfield.GetService<IItemBuilder>();
            IInventoryRepository? inventory = playfield.GetService<IInventoryRepository>();
            if (items == null || inventory == null)
                return;

            // Social clothing is cosmetic and runs no wear effects.
            if (pending.DestPage.Identity.Type.IsWearPage() && pending.DestPage.Identity.Type != IdentityType.SocialPage)
                WearCastNano.ApplyItem(
                    player,
                    pending.Item,
                    includeWield: pending.DestPage.Identity.Type == IdentityType.WeaponPage,
                    items,
                    inventory);

            if (pending.SwappedItem != null && pending.SourcePage.Identity.Type.IsWearPage()
                && pending.SourcePage.Identity.Type != IdentityType.SocialPage)
                WearCastNano.ApplyItem(
                    player,
                    pending.SwappedItem,
                    includeWield: pending.SourcePage.Identity.Type == IdentityType.WeaponPage,
                    items,
                    inventory);
        }

        static void NotifyEquipmentChanged(Player player, PendingEquip pending)
        {
            if (pending.SourcePage.Identity.Type.IsWearPage())
                player.OnEquipmentChanged(new EquipSlot(pending.SourcePage.Identity.Type, pending.SourceSlot));

            if (pending.DestPage.Identity.Type.IsWearPage()
                && (pending.SourcePage.Identity.Type != pending.DestPage.Identity.Type
                    || pending.SourceSlot != pending.DestSlot))
                player.OnEquipmentChanged(new EquipSlot(pending.DestPage.Identity.Type, pending.DestSlot));
        }

        /// <summary>
        /// MoreFlags bit 11: a leveling item. Every item carrying it is a QL1/QL200 armor pair (Living Cyber Armor, Zenith
        /// Commando, Captain Anarchy Turbo, ...); Living Cyber Armor's description says it levels up with its wearer.
        /// </summary>
        const int LevelingItemMoreFlag = 0x800;

        internal static bool IsLevelingItem(Item item) => (item.GetStat(CharacterStat.MoreFlags) & LevelingItemMoreFlag) != 0;

        /// <summary>
        /// Brings worn leveling armor to the wearer's level, within its template's QL range (after an equip and on
        /// level-up). The instance keeps its id and slot; only its QL changes, and the client sees an unequip at the old
        /// QL followed by an equip at the new one. True when any piece changed; the caller rebases.
        /// </summary>
        public bool RescaleLevelingArmor(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);
            IItemBuilder? items = player.Playfield?.GetService<IItemBuilder>();
            if (items == null || player.Session == null || !player.Inventory.IsHydrated)
                return false;

            int level = player.Stats.GetOrOne(CharacterStat.Level);
            Container page = player.Inventory.Armor;
            bool changed = false;
            foreach (KeyValuePair<int, Item> worn in new List<KeyValuePair<int, Item>>(page.Content))
            {
                int slot = worn.Key;
                Item current = worn.Value;
                if (!IsLevelingItem(current) || current.Locked || current.InstanceId <= 0 || current.Quality == level)
                    continue;

                Item rescaled = items.Create(current.LowId, current.HighId, level, current.Source, current.StackCount,
                    current.InstanceId, current.Identity);
                if (rescaled.Quality == current.Quality)
                    continue;
                rescaled.IsPersisted = current.IsPersisted;
                rescaled.PlayshiftSuppressed = current.PlayshiftSuppressed;

                bool committed = _actions.TryCommit(player,
                    [new InventoryRowChange(rescaled, page.Identity, slot, rescaled.StackCount, PreviousQuality: current.Quality)],
                    () => !current.Locked && ReferenceEquals(page.Content.GetValueOrDefault(slot), current),
                    () =>
                    {
                        page.Content.Remove(slot);
                        if (!page.Add(slot, rescaled))
                            throw new InvalidOperationException("Leveling armor slot changed.");
                    });
                if (!committed)
                    continue;

                SendUnequipCharacterAction(player, slot);
                AnnounceWearTemplateAction(player, current, page.Identity.Type, slot, TemplateActionType.Remove);
                SendEquipCharacterAction(player, rescaled, slot);
                AnnounceWearTemplateAction(player, rescaled, page.Identity.Type, slot, TemplateActionType.Wear);
                _logger.Info(string.Format(CultureInfo.InvariantCulture,
                    "Leveling armor char={0} instance={1} {2} ql{3} -> ql{4}",
                    player.Identity.Instance, current.InstanceId, current.Name, current.Quality, rescaled.Quality));
                changed = true;
            }

            return changed;
        }

        bool ApplyMove(
            Player player,
            Container sourcePage,
            int sourceSlot,
            Container destPage,
            int destSlot,
            Item item,
            LootableDynel? lootSource)
        {
            if (lootSource is Chest chest)
            {
                lock (player.PersistenceGate)
                    lock (chest.LootGate)
                        return chest.CanTransferLoot(player) && chest.CanDistribute(item)
                            && ApplyMoveCore(player, sourcePage, sourceSlot, destPage, destSlot, item, lootSource);
            }
            return ApplyMoveCore(player, sourcePage, sourceSlot, destPage, destSlot, item, lootSource);
        }

        bool ApplyMoveCore(Player player, Container sourcePage, int sourceSlot, Container destPage, int destSlot,
            Item item, LootableDynel? lootSource)
        {
            Item? existingDest = destPage.Content.GetValueOrDefault(destSlot);
            if (lootSource != null && existingDest != null)
                return false;
            if (ReferenceEquals(sourcePage, destPage) && sourceSlot == destSlot)
                return true;
            if (WouldNestBag(sourcePage, sourceSlot, destPage, destSlot, item, existingDest))
                return false;
            player.InvalidateOwnedItemCache();
            if (sourceSlot < sourcePage.Offset || sourceSlot >= sourcePage.Offset + sourcePage.Capacity
                || destSlot < destPage.Offset || destSlot >= destPage.Offset + destPage.Capacity
                || destPage.Identity.Type == IdentityType.OverflowWindow)
                return false;
            // Overflow items only move into the inventory, never swap with what is there.
            if (sourcePage.Identity.Type == IdentityType.OverflowWindow
                && (!ReferenceEquals(destPage, player.Inventory.Inventory) || existingDest != null))
                return false;
            // A Unique (or a bag holding one) that enters the carried pages from the bank or loot, or a bank item swapped
            // out in exchange, may not duplicate one held. The item leaving the carried pages in the same move does not
            // count against what comes in.
            bool destCarried = player.Inventory.IsCarriedPage(destPage);
            bool sourceCarried = lootSource == null && player.Inventory.IsCarriedPage(sourcePage);
            if (destCarried && !sourceCarried
                && UniqueItems.WouldDuplicate(player, item, out string incomingName, except: held => ReferenceEquals(held, existingDest)))
            {
                Helpers.ClientFeedback.SendFormatted(player, Helpers.ClientFeedback.AlreadyHaveUniqueItem, incomingName);
                return false;
            }
            if (existingDest != null && sourceCarried && !destCarried
                && UniqueItems.WouldDuplicate(player, existingDest, out string swappedName, except: held => ReferenceEquals(held, item)))
            {
                Helpers.ClientFeedback.SendFormatted(player, Helpers.ClientFeedback.AlreadyHaveUniqueItem, swappedName);
                return false;
            }
            bool committed = lootSource != null && item.IsPersisted
                ? TakeSharedLoot(player, sourcePage, sourceSlot, destPage, destSlot, item, lootSource)
                : ApplyOwnedMove(player, sourcePage, sourceSlot, destPage, destSlot, item, existingDest, lootSource);
            if (committed)
            {
                lootSource?.NotifyLootChanged();
                if (lootSource is Corpse corpse)
                    player.Playfield?.GetService<Chat.VicinityChatRelay>()?.AnnounceLoot(player, corpse, item);
                if (Modules.ModuleHooks.WantsItemMoves(player.Playfield))
                {
                    ItemPlaces.Moved(player, item, ItemPlaces.Of(sourcePage, lootSource), ItemPlaces.Of(destPage));
                    if (existingDest != null)
                        ItemPlaces.Moved(player, existingDest, ItemPlaces.Of(destPage), ItemPlaces.Of(sourcePage, lootSource));
                }
            }
            return committed;
        }

        /// <summary>
        /// A move whose durable rows all belong to this player: between their own pages, bank and bags, or transient
        /// corpse loot that has no row yet. It is applied in memory on the tick and written by the coalesced background
        /// flush, the path grants, trades and loot rolls already take: one transaction per player per coalesce window
        /// rather than one database round trip per message, so a loot-all or a bag sort costs the tick microseconds.
        /// Logout, zone change and the trade and mission commits hard-flush ahead of their own writes, and the tick is
        /// the only writer of these pages, so the checks here are exact. An item without an instance id could never be
        /// written: the move is refused rather than left to resurrect in its old slot at the next login.
        /// </summary>
        bool ApplyOwnedMove(Player player, Container sourcePage, int sourceSlot, Container destPage, int destSlot,
            Item item, Item? existingDest, LootableDynel? lootSource)
        {
            if (player.Session?.State != SessionState.InPlay || player.IsPersistenceQuarantined
                || item.Locked || existingDest?.Locked == true
                || (lootSource is Chest sourceChest && !sourceChest.CanDistribute(item))
                || !sourcePage.Content.TryGetValue(sourceSlot, out Item? current) || !ReferenceEquals(current, item)
                || !ReferenceEquals(destPage.Content.GetValueOrDefault(destSlot), existingDest))
                return false;
            if (item.InstanceId <= 0 || existingDest is { InstanceId: <= 0 })
            {
                _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                    "Inventory move refused: item without an instance id char={0} source={1}/{2} dest={3}/{4}",
                    player.Identity.Instance, sourcePage.Identity, sourceSlot, destPage.Identity, destSlot));
                return false;
            }

            sourcePage.Content.Remove(sourceSlot);
            if (existingDest != null)
            {
                destPage.Content.Remove(destSlot);
                if (!sourcePage.Add(sourceSlot, existingDest))
                    throw new InvalidOperationException("Reserved inventory swap source changed.");
            }
            if (!destPage.Add(destSlot, item))
                throw new InvalidOperationException("Reserved inventory move destination changed.");

            PlayerInventory inventory = player.Inventory;
            inventory.MarkDirty(item, destPage, destSlot);
            if (existingDest != null)
                inventory.MarkDirty(existingDest, sourcePage, sourceSlot);
            _flush.NotifyDirty(player);
            return true;
        }

        /// <summary>
        /// A take from a shared durable container (a mission chest reloads its rows from storage on every new open). The
        /// chest's in-memory contents are authoritative while it is loaded and the caller holds its loot gate, so the
        /// take is applied here and acknowledged at once. Its row changes owner off the tick through the player's
        /// ordered commit queue, in a transaction that first locks the chest row exactly as this server last saw it
        /// (<see cref="InventoryActionService.QueueSharedSourceRelocation"/>), and the chest writes every queued take
        /// before it reloads its rows for a new opener, so a stale projection can never hand out an item twice. Loot
        /// never lands on an occupied slot (the caller refused that), so there is no swap here.
        /// </summary>
        bool TakeSharedLoot(Player player, Container sourcePage, int sourceSlot, Container destPage, int destSlot,
            Item item, LootableDynel lootSource)
        {
            if (player.Session?.State != SessionState.InPlay || player.IsPersistenceQuarantined || item.Locked
                || (lootSource is Chest sourceChest && !sourceChest.CanDistribute(item))
                || !sourcePage.Content.TryGetValue(sourceSlot, out Item? current) || !ReferenceEquals(current, item)
                || destPage.Content.ContainsKey(destSlot))
                return false;

            // Queued before memory changes: a refused queue leaves the chest as it was. The commit cannot run before
            // this returns, since the caller holds the player's persistence gate.
            _actions.QueueSharedSourceRelocation(player, item,
                item.ToRecord(sourcePage.Identity, sourceSlot, item.StackCount), destPage.Identity, destSlot);
            sourcePage.Content.Remove(sourceSlot);
            if (!destPage.Add(destSlot, item))
                throw new InvalidOperationException("Reserved inventory move destination changed.");
            (lootSource as Chest)?.RecordPendingTake(player);
            return true;
        }

        /// <summary>
        /// Stock's loot placement (1004ac4b): a looted item with Can Stackable (0x200) set and CantSplit (0x1000000)
        /// clear goes onto the FIRST inventory slot from 0x40 up holding the same LowId / HighId / QL. The target's own
        /// flags and count are not part of the choice; whether the join can go ahead is decided by the caller.
        /// </summary>
        static bool TryFindLootJoinTarget(Container inventory, Item looted, out int slot, out Item target)
        {
            slot = -1;
            target = null!;
            if (!looted.Can(CanFlags.Stackable) || looted.Can(CanFlags.CantSplit))
                return false;

            for (int candidate = inventory.Offset; candidate < inventory.Offset + inventory.Capacity; candidate++)
            {
                if (inventory.Content.TryGetValue(candidate, out Item? held) && InventoryStacking.SameStack(held, looted))
                {
                    slot = candidate;
                    target = held;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Adds the looted count onto the inventory stack stock chose and retires the looted item. When the join cannot
        /// go ahead (the stack would pass 50000, either item is locked or a bag) the take is refused outright: no echo,
        /// so the client, which joins on the 0x6F echo by the same rule, never shows a stack the server does not hold.
        /// Stock instead swaps the looted item in and drops the stack into the loot (1002aa2a); loot never swaps here.
        /// </summary>
        bool TryJoinLoot(Player player, Container sourcePage, int sourceSlot, Item item, LootableDynel lootSource,
            int targetSlot, Item target)
        {
            if (lootSource is Chest chest)
            {
                lock (player.PersistenceGate)
                    lock (chest.LootGate)
                        return chest.CanTransferLoot(player) && chest.CanDistribute(item)
                            && TryJoinLootCore(player, sourcePage, sourceSlot, item, lootSource, targetSlot, target);
            }
            return TryJoinLootCore(player, sourcePage, sourceSlot, item, lootSource, targetSlot, target);
        }

        bool TryJoinLootCore(Player player, Container sourcePage, int sourceSlot, Item item, LootableDynel lootSource,
            int targetSlot, Item target)
        {
            int lootedCount = item.StackCount;
            int targetCount = target.StackCount;
            if (item.Locked || target.Locked || item.InstanceId <= 0 || target.InstanceId <= 0
                || IsBagItem(item) || IsBagItem(target)
                || lootedCount <= 0 || targetCount <= 0
                || (long)targetCount + lootedCount > InventoryStacking.MaxStackCount)
                return false;

            Container inventory = player.Inventory.Inventory;
            if (UniqueItems.WouldDuplicate(player, item, out string incomingName))
            {
                Helpers.ClientFeedback.SendFormatted(player, Helpers.ClientFeedback.AlreadyHaveUniqueItem, incomingName);
                return false;
            }

            int total = targetCount + lootedCount;
            var graveyard = new Identity { Type = IdentityType.None, Instance = player.Identity.Instance };
            if (player.Session?.State != SessionState.InPlay || player.IsPersistenceQuarantined
                || (lootSource is Chest sourceChest && !sourceChest.CanDistribute(item))
                || !sourcePage.Content.TryGetValue(sourceSlot, out Item? current) || !ReferenceEquals(current, item)
                || !ReferenceEquals(inventory.Content.GetValueOrDefault(targetSlot), target))
                return false;

            // Applied on the tick, stored by the coalesced flush (see ApplyOwnedMove): the stack as a location update
            // carrying its new count. Shared durable loot retires its chest row off the tick with the chest row locked as
            // last seen (see TakeSharedLoot); transient loot never had a row and is recorded as retired so that a row
            // landing for it in flight is re-homed rather than left to duplicate the stack at the next login.
            if (item.IsPersisted)
                _actions.QueueSharedSourceRelocation(player, item,
                    item.ToRecord(sourcePage.Identity, sourceSlot, lootedCount), graveyard, item.InstanceId);
            player.InvalidateOwnedItemCache();
            sourcePage.Content.Remove(sourceSlot);
            target.StackCount = total;
            player.Inventory.MarkDirty(target, inventory, targetSlot);
            if (item.IsPersisted)
                (lootSource as Chest)?.RecordPendingTake(player);
            else
                player.Inventory.MarkOrphaned(item, graveyard);
            _flush.NotifyDirty(player);

            lootSource.NotifyLootChanged();
            if (lootSource is Corpse corpse)
                player.Playfield?.GetService<Chat.VicinityChatRelay>()?.AnnounceLoot(player, corpse, item);
            if (Modules.ModuleHooks.WantsItemMoves(player.Playfield))
                ItemPlaces.Moved(player, item, ItemPlaces.Of(sourcePage, lootSource), ItemPlaces.Of(inventory));
            return true;
        }

        static void SetMoveLock(PendingEquip pending, bool locked)
        {
            pending.Item.Locked = locked;
            if (pending.SwappedItem != null)
                pending.SwappedItem.Locked = locked;
        }

        static void SendAck(Player player, Identity sourceContainer, Identity target, int targetPlacement)
        {
            player.Session?.Send(
                new ContainerAddItem
                {
                    Identity = player.Identity,
                    Source = sourceContainer,
                    Target = target,
                    Slot = targetPlacement,
                    Unknown = 0
                });
        }

        /// <summary>
        /// Page, slot and the item's wear or wield requirements. Worn / wielded item requirements (one-only sets such as
        /// 231234's HasNotWornItem list or 85908's HasNotWieldedItem pair) leave out <paramref name="item"/> and
        /// <paramref name="swappedWith"/>, which are the items changing place.
        /// </summary>
        static bool MeetsEquipRequirements(Player player, Item item, Container wearPage, int destSlot, Item? swappedWith)
        {
            // Slot bits are page-local. ItemClass must match the wear page first so a crafted
            // ClientMoveItemToInventory cannot land a weapon on armor/implant/social slots.
            // Can.Wear is not required; some legal weapons (e.g. 121564) omit it.
            if (!FitsWearPage(item, wearPage, destSlot))
                return false;
            if (!FitsWearSlot(item, wearPage, destSlot))
                return false;

            // Social clothing is cosmetic: it only changes meshes and textures, so the slot is its only requirement.
            if (wearPage.Identity.Type == IdentityType.SocialPage)
                return true;

            ActionType needed = wearPage.Identity.Type == IdentityType.WeaponPage
                ? ActionType.ToWield
                : ActionType.ToWear;

            PlayerInventory inventory = player.Inventory;
            bool? Resolve(ItemRequirement requirement) => (Operator)requirement.Operator switch
            {
                Operator.HasWornItem => inventory.IsEquipped(requirement.Value, wieldedOnly: false, item, swappedWith),
                Operator.HasNotWornItem => !inventory.IsEquipped(requirement.Value, wieldedOnly: false, item, swappedWith),
                Operator.HasWieldedItem => inventory.IsEquipped(requirement.Value, wieldedOnly: true, item, swappedWith),
                Operator.HasNotWieldedItem => !inventory.IsEquipped(requirement.Value, wieldedOnly: true, item, swappedWith),
                _ => player.ResolvePerkRequirement(requirement)
            };

            return item.Definition.MeetsActionRequirements(stat => player.Stats.Get(stat), needed, Resolve);
        }

        void LogEquipRefused(Player player, Item item, Container page, int slot)
            => _logger.Info(string.Format(CultureInfo.InvariantCulture,
                "Equip refused char={0} item={1} ({2}) class={3} slotMask=0x{4:X} page={5} slot=0x{6:X} relative={7} pageFit={8} slotFit={9}",
                player.Identity.Instance, item.LowId, item.Name, (ItemClass)item.GetStat(CharacterStat.ItemClass),
                unchecked((uint)item.GetStat(CharacterStat.Slot)), page.Identity.Type, slot, slot - page.Offset + 1,
                FitsWearPage(item, page, slot), FitsWearSlot(item, page, slot)));

        static bool FitsWearPage(Item item, Container wearPage, int destSlot)
        {
            var itemClass = (ItemClass)item.GetStat(CharacterStat.ItemClass);
            return wearPage.Identity.Type switch
            {
                IdentityType.WeaponPage => itemClass is ItemClass.Weapon or ItemClass.Utility,
                IdentityType.ArmorPage => itemClass == ItemClass.Armor,
                IdentityType.ImplantPage => itemClass is ItemClass.Implant or ItemClass.Spirit,
                // Social clothing, plus a weapon in the social right or left hand (cosmetic: it is shown in the hand
                // while social is, and is never armed).
                IdentityType.SocialPage => itemClass == ItemClass.Armor
                    || (itemClass == ItemClass.Weapon && PlayerInventory.TryGetSocialHand(wearPage, destSlot, out _)),
                _ => false
            };
        }

        static bool MeetsWeaponHandPairing(
            Player player,
            Item incoming,
            Container destPage,
            int destSlot,
            Container sourcePage,
            int sourceSlot)
        {
            if (!IsWeaponHand(destPage, destSlot) && !IsWeaponHand(sourcePage, sourceSlot))
                return true;

            Item? right = ResolveHandAfterMove(
                player,
                (int)WeaponSlots.Righthand,
                incoming,
                destPage,
                destSlot,
                sourcePage,
                sourceSlot);
            Item? left = ResolveHandAfterMove(
                player,
                (int)WeaponSlots.LeftHand,
                incoming,
                destPage,
                destSlot,
                sourcePage,
                sourceSlot);
            return AreHandsCompatible(right, left);
        }

        static bool IsWeaponHand(Container page, int slot)
            => page.Identity.Type == IdentityType.WeaponPage
                && (slot == (int)WeaponSlots.Righthand || slot == (int)WeaponSlots.LeftHand);

        static Item? ResolveHandAfterMove(
            Player player,
            int handSlot,
            Item incoming,
            Container destPage,
            int destSlot,
            Container sourcePage,
            int sourceSlot)
        {
            if (destPage.Identity.Type == IdentityType.WeaponPage && destSlot == handSlot)
                return incoming;

            if (sourcePage.Identity.Type == IdentityType.WeaponPage && sourceSlot == handSlot)
            {
                if (destPage.Content.TryGetValue(destSlot, out Item? swapped))
                    return swapped;
                return null;
            }

            return player.Inventory.Equipment.Content.GetValueOrDefault(handSlot);
        }

        static bool AreHandsCompatible(Item? right, Item? left)
        {
            if (right == null || left == null)
                return true;

            WeaponFlags rightFlags = right.GetWeaponFlags();
            WeaponFlags leftFlags = left.GetWeaponFlags();
            const WeaponFlags styleMask = WeaponFlags.Melee | WeaponFlags.Ranged | WeaponFlags.Unarmed;
            if ((rightFlags & styleMask) == 0 || (leftFlags & styleMask) == 0)
                return true;

            bool rightTwoHanded = (rightFlags & WeaponFlags.TwoHanded) != 0;
            bool leftTwoHanded = (leftFlags & WeaponFlags.TwoHanded) != 0;
            bool rightOneHanded = (rightFlags & WeaponFlags.OneHanded) != 0;
            bool leftOneHanded = (leftFlags & WeaponFlags.OneHanded) != 0;
            if (rightTwoHanded || leftTwoHanded)
            {
                if (rightTwoHanded && leftTwoHanded)
                    return false;
                if (rightTwoHanded && leftOneHanded)
                    return false;
                if (leftTwoHanded && rightOneHanded)
                    return false;
            }

            bool rightRanged = (rightFlags & WeaponFlags.Ranged) != 0;
            bool leftRanged = (leftFlags & WeaponFlags.Ranged) != 0;
            bool rightMelee = (rightFlags & (WeaponFlags.Melee | WeaponFlags.Unarmed)) != 0;
            bool leftMelee = (leftFlags & (WeaponFlags.Melee | WeaponFlags.Unarmed)) != 0;
            if ((rightRanged && leftMelee) || (leftRanged && rightMelee))
                return false;

            return true;
        }

        /// <summary>
        /// AO Placement/Slot (298) bitfield: allowed when (slotMask &amp; (1 &lt;&lt; relativeSlot)) != 0.
        /// Relative slot is page-local (WeaponSlots / ArmorSlots / ImplantSlots), 1-based.
        /// </summary>
        static bool FitsWearSlot(Item item, Container wearPage, int destSlot)
        {
            int relativeSlot = destSlot - wearPage.Offset + 1;
            // A weapon in a social hand fits by its weapon-page hand bit (Righthand 6 / LeftHand 8).
            if ((ItemClass)item.GetStat(CharacterStat.ItemClass) == ItemClass.Weapon
                && PlayerInventory.TryGetSocialHand(wearPage, destSlot, out WeaponSlots hand))
                relativeSlot = (int)hand;
            if (relativeSlot < 1 || relativeSlot > wearPage.Capacity)
                return false;

            // Unsigned: some masks carry bit 31 (Morphing Memory 290619 = 0x80000201), which is negative as int.
            uint slotMask = unchecked((uint)item.GetStat(CharacterStat.Slot));
            if (slotMask == 0)
                return false;

            return (slotMask & (1u << relativeSlot)) != 0;
        }

        /// <summary>An item moving from <paramref name="from"/> to <paramref name="to"/> is put on or taken off as social clothing.</summary>
        static bool IsSocialMove(Container from, Container to)
            => to.Identity.Type == IdentityType.SocialPage
                || (from.Identity.Type == IdentityType.SocialPage && !to.Identity.Type.IsWearPage());

        static double ResolveEquipDelaySeconds(Item item, bool isSocial)
        {
            // Social clothing always takes the default delay: it keeps cosmetic swaps from being spammed (each one is
            // announced to observers and saved). An item leaving the social page for the armor page is timed by the
            // armor page's own call, so this cannot shortcut real equip delays.
            if (isSocial)
                return DefaultEquipDelay * 0.01;

            int delay = StatCollection.Normalize(item.GetStat(CharacterStat.EquipDelay));
            if (delay <= 0)
                delay = DefaultEquipDelay;

            return delay * 0.01;
        }

        internal static int DecodeBackpackHandle(Identity sourceContainer)
            => (int)(((uint)sourceContainer.Instance >> 16) & 0xffff);

        internal static int DecodeBackpackSlot(Identity sourceContainer)
            => (int)((uint)sourceContainer.Instance & 0xffff);

        sealed class PendingEquip
        {
            public PendingEquip(
                Player player,
                Identity ackSource,
                Identity ackTarget,
                Container sourcePage,
                int sourceSlot,
                Container destPage,
                int destSlot,
                Item item,
                Item? swappedItem,
                LootableDynel? lootSource,
                double remainingSeconds,
                int ackTargetPlacement)
            {
                Player = player;
                AckSource = ackSource;
                AckTarget = ackTarget;
                SourcePage = sourcePage;
                SourceSlot = sourceSlot;
                DestPage = destPage;
                DestSlot = destSlot;
                Item = item;
                SwappedItem = swappedItem;
                LootSource = lootSource;
                RemainingSeconds = remainingSeconds;
                AckTargetPlacement = ackTargetPlacement;
                LockedInstanceId = item.InstanceId;
                OriginPlayfield = player.Playfield!;
            }

            public Player Player { get; }

            public Identity AckSource { get; }

            public Identity AckTarget { get; }

            public Container SourcePage { get; }

            public int SourceSlot { get; }

            public Container DestPage { get; }

            public int DestSlot { get; }

            public Item Item { get; }

            public Item? SwappedItem { get; }

            public LootableDynel? LootSource { get; }

            public double RemainingSeconds { get; set; }

            public int AckTargetPlacement { get; }

            public int LockedInstanceId { get; }

            public Playfield OriginPlayfield { get; }
        }
    }
}
