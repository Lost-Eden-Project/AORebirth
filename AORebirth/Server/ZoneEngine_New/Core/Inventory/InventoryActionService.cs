namespace ZoneEngine_New.Core.Inventory
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using AORebirth.Enums;
    using AORebirth.Modules.Events;
    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages.N3Messages;
    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Modules;
    using ZoneEngine_New.Core.Network;

    /// <summary>
    /// Inventory mutations the client requests. Rows that only this player owns (split, join, and the moves in
    /// <see cref="InventoryMoveService"/>) are applied on the tick and written by the coalesced flush: one transaction
    /// per player per window, never one per packet, so no client can turn a stream of cheap requests into a stream of
    /// database round trips on the playfield thread. Deletes, pickups, combines and anything with a durable guard
    /// (a chest row locked as last seen) are applied on the tick too and queue their own transaction in the player's
    /// write-behind lane through <see cref="TryCommit"/>; the tick never waits for the database.
    /// </summary>
    public sealed class InventoryActionService
    {
        readonly IInventoryMutationPersistence _persistence;
        readonly InventoryFlushService _flush;
        readonly IItemInstanceIdAllocator _ids;
        readonly IZoneLogger _logger;

        public InventoryActionService(IInventoryMutationPersistence persistence, InventoryFlushService flush,
            IItemInstanceIdAllocator ids, IZoneLogger logger)
        {
            _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
            _flush = flush ?? throw new ArgumentNullException(nameof(flush));
            _ids = ids ?? throw new ArgumentNullException(nameof(ids));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>Largest stack a join may produce: the client's 50000 (N3Msg_JoinItems).</summary>
        const int MaxStackCount = InventoryStacking.MaxStackCount;

        public void Handle(Player player, CharacterActionMessage message)
        {
            if (player.Session?.State != SessionState.InPlay || player.IsDead || player.IsPersistenceQuarantined)
                return;
            if (message.Action == CharacterActionType.DeleteItem)
            {
                // The client names the item's template in Parameter1/Parameter2 (live: 85366/85366); a slot holding
                // anything else is not the item the player chose, and a delete cannot be undone.
                if (!TryResolveDeleteSlot(player, message.Target, out _, out _, out Item item, out _)
                    || !IsTemplateOf(item, message.Parameter1) || !IsTemplateOf(item, message.Parameter2)
                    || !TryDelete(player, message.Target, item.InstanceId)) return;
                player.Session?.Send(new CharacterActionMessage
                {
                    Identity = player.Identity, Action = message.Action, Target = message.Target,
                    Unknown = message.Unknown, Unknown1 = message.Unknown1, Unknown2 = message.Unknown2,
                    Parameter1 = 0, Parameter2 = 0
                });
            }
            else if (message.Action == CharacterActionType.SplitItem)
            {
                // The client already split locally; an echoed 0x34 would split it a second time.
                if (TryResolveStackSlot(player, message.Target, out _, out _, out Item item))
                    TrySplit(player, message.Target, item.InstanceId, message.Parameter2);
            }
            else if (message.Action == CharacterActionType.JoinItems)
            {
                var source = new Identity { Type = (IdentityType)message.Parameter1, Instance = message.Parameter2 };
                if (!TryJoin(player, message.Target, source)) return;
                // The client merges only on this echo; same Target/source layout as its request.
                player.Session?.Send(new CharacterActionMessage
                {
                    Identity = player.Identity, Action = message.Action, Target = message.Target,
                    Unknown = message.Unknown, Unknown1 = message.Unknown1, Unknown2 = message.Unknown2,
                    Parameter1 = message.Parameter1, Parameter2 = message.Parameter2
                });
            }
        }

        static bool IsTemplateOf(Item item, int templateId) => templateId == item.LowId || templateId == item.HighId;

        /// <param name="destination">Where the item goes, for module item moves: destroyed, or dropped into the world.</param>
        /// <param name="stored">
        /// Runs on the lane writer once the retire has committed, never when it fails or the lane is discarded: anything
        /// the deleted item becomes outside this inventory (a world item, a city charge) waits for it.
        /// </param>
        public bool TryDelete(Player player, Identity slot, int expectedInstanceId,
            ItemPlaceKind destination = ItemPlaceKind.Destroyed, Action? stored = null)
        {
            Chest? chest = InventoryMoveService.FindChestSource(player, slot);
            if (chest != null)
            {
                lock (player.PersistenceGate)
                    lock (chest.LootGate)
                        return chest.CanTransferLoot(player) && TryDeleteCore(player, slot, expectedInstanceId, destination, stored);
            }
            return TryDeleteCore(player, slot, expectedInstanceId, destination, stored);
        }

        /// <summary>
        /// Uses up one of the item in <paramref name="slot"/>: a stack loses one, a single item is retired. The change is
        /// applied on the tick and stored in the player's lane; <paramref name="stored"/> runs on the lane writer once it
        /// has committed, which is where the effect the item pays for belongs (a discarded lane then grants nothing).
        /// </summary>
        public bool TryConsumeOne(Player player, Identity slot, int expectedInstanceId, Action? stored = null)
        {
            if (!TryResolveDeleteSlot(player, slot, out Container page, out int placement, out Item item, out LootableDynel? loot)
                || loot != null || item.InstanceId != expectedInstanceId)
                return false;
            if (item.StackCount <= 1)
                return TryDelete(player, slot, expectedInstanceId, ItemPlaceKind.Destroyed, stored);
            int before = item.StackCount;
            return TryCommit(player,
                [new InventoryRowChange(item, page.Identity, placement, before - 1)],
                () => IsCurrent(page, placement, item, expectedInstanceId) && item.StackCount == before,
                () => item.StackCount = before - 1,
                stored: stored);
        }

        /// <summary>
        /// EXPLOIT guard: a bag may only be deleted or dropped once its contents are known to be none. A bag never opened
        /// since login has no loaded page, and the old check passed it: the delete was queued, the database then refused
        /// to retire a container with rows and the whole lane was discarded, which a client could arrange on purpose to
        /// keep its later changes out of storage (a dropped item's row survived the drop). The page is loaded here, on the
        /// tick, as opening the bag loads it; with no way to load it the delete is refused.
        /// </summary>
        static bool IsBagKnownEmpty(Player player, Item bag, Container page, int placement)
        {
            Identity container = bag.Identity.Type == IdentityType.Container && bag.Identity.Instance != 0
                ? bag.Identity
                : new Identity { Type = IdentityType.Container, Instance = bag.InstanceId };
            if (player.Inventory.TryGetBackpackPage(container, out Container bagPage) && bagPage.Content.Count != 0)
                return false;
            if (bagPage == null || !bagPage.IsHydrated)
            {
                if (player.Playfield?.GetService<IInventoryRepository>() is not IInventoryRepository repository
                    || player.Playfield.GetService<IItemBuilder>() is not IItemBuilder items)
                    return false;
                bagPage ??= player.Inventory.GetOrCreateBackpackPage(bag, container,
                    new Identity { Type = page.Identity.Type, Instance = placement });
                player.Inventory.HydrateBackpack(bagPage, repository, items);
                if (!bagPage.IsHydrated)
                    return false;
            }
            return bagPage.Content.Count == 0;
        }

        bool TryDeleteCore(Player player, Identity slot, int expectedInstanceId, ItemPlaceKind destination, Action? stored)
        {
            if (!TryResolveDeleteSlot(player, slot, out Container page, out int placement, out Item item, out LootableDynel? loot)
                || item.InstanceId != expectedInstanceId) return false;
            if (InventoryMoveService.IsBagItem(item) && !IsBagKnownEmpty(player, item, page, placement)) return false;
            if (!BeltDeckSlots.AllowsRemoval(player, page, placement)) return false;
            var graveyard = new Identity { Type = IdentityType.None, Instance = player.Identity.Instance };
            ItemInstanceRecord[] expectedSources = loot != null && item.IsPersisted
                ? [item.ToRecord(page.Identity, placement, item.StackCount)] : [];
            bool result = TryCommit(player,
                [new InventoryRowChange(item, graveyard, item.InstanceId, item.StackCount, Retired: true)],
                () => IsCurrent(page, placement, item, expectedInstanceId)
                    && (loot is not Chest sourceChest || sourceChest.CanDistribute(item)),
                () => page.Content.Remove(placement), expectedSources: expectedSources, stored: stored);
            if (result)
            {
                loot?.NotifyLootChanged();
                ModuleHooks.ItemMoved(player, item, ItemPlaces.Of(page, loot), ModuleHooks.Place(destination));
                // A deleted or dropped bag loses its name (a dropped bag picked up again is a new item).
                if (loot == null && InventoryMoveService.IsBagItem(item) && player.BagNames.Remove(item.InstanceId)
                    && player.SpeaksExtendedMessages)
                    player.Session?.Send(new BagNameMessage { Identity = player.Identity, Bag = item.Identity, Name = string.Empty });
            }
            if (result && page.Identity.Type.IsWearPage())
            {
                player.Rebase();
                player.OnEquipmentChanged(new EquipSlot(page.Identity.Type, slot.Instance));
            }
            return result;
        }

        public bool TrySplit(Player player, Identity slot, int expectedInstanceId, int amount)
        {
            if (player.Session?.State != SessionState.InPlay || player.IsPersistenceQuarantined || player.IsDead
                || !TryResolveStackSlot(player, slot, out Container page, out int placement, out Item item)
                || item.InstanceId != expectedInstanceId || !IsStackPage(page)
                || !IsStackable(item) || item.Can(CanFlags.CantSplit)
                // Two instances of a unique item would bypass the one-per-character rule.
                || Trade.TradeRules.IsUnique(item)
                || amount <= 0 || amount >= item.StackCount) return false;
            // Same rule the client used for its local split: the page's first free slot.
            int destination = page.FindFreeSlot();
            if (destination < 0) return false;
            int originalCount = item.StackCount;
            int newId = _ids.Allocate();
            if (newId <= 0 || newId == item.InstanceId) throw new InvalidOperationException("Invalid split instance allocation.");
            var split = new Item
            {
                InstanceId = newId, Identity = item.Identity.Type == IdentityType.None ? Identity.None
                    : new Identity { Type = item.Identity.Type, Instance = newId },
                LowId = item.LowId, HighId = item.HighId, Quality = item.Quality,
                StackCount = amount, Source = item.Source, Definition = item.Definition
            };
            // Applied on the tick, stored by the coalesced flush: the remainder as a location update carrying its new
            // count, the split half as an insert. A split-join loop from a hostile client costs memory work only.
            player.InvalidateOwnedItemCache();
            item.StackCount = originalCount - amount;
            if (!page.Add(destination, split)) throw new InvalidOperationException("Reserved split slot changed.");
            player.Inventory.MarkDirty(item, page, placement);
            player.Inventory.MarkDirty(split, page, destination);
            _flush.NotifyDirty(player);
            return true;
        }

        /// <summary>
        /// Merges <paramref name="source"/> into <paramref name="target"/>. The client keeps the
        /// Target slot and drops the source slot (FUN_1002a40e), so the source row is retired.
        /// </summary>
        public bool TryJoin(Player player, Identity target, Identity source)
        {
            if (player.Session?.State != SessionState.InPlay || player.IsPersistenceQuarantined || player.IsDead
                || target.Type != source.Type || target.Instance == source.Instance
                || !TryResolveStackSlot(player, target, out Container page, out int targetSlot, out Item kept)
                || !TryResolveStackSlot(player, source, out Container sourcePage, out int sourceSlot, out Item merged)
                || !ReferenceEquals(page, sourcePage) || targetSlot == sourceSlot || ReferenceEquals(kept, merged)
                || !IsStackPage(page) || !IsStackable(kept) || !IsStackable(merged)
                || kept.LowId != merged.LowId || kept.HighId != merged.HighId || kept.Quality != merged.Quality)
                return false;
            int keptId = kept.InstanceId, mergedId = merged.InstanceId;
            int keptCount = kept.StackCount, mergedCount = merged.StackCount;
            if (keptCount <= 0 || mergedCount <= 0 || (long)keptCount + mergedCount > MaxStackCount) return false;
            int total = keptCount + mergedCount;
            var graveyard = new Identity { Type = IdentityType.None, Instance = player.Identity.Instance };
            // Applied on the tick, stored by the coalesced flush: the kept stack as a location update carrying its new
            // count, the merged row retired to the graveyard (dropped outright when it never had a row).
            player.InvalidateOwnedItemCache();
            page.Content.Remove(sourceSlot);
            kept.StackCount = total;
            player.Inventory.MarkDirty(kept, page, targetSlot);
            player.Inventory.MarkOrphaned(merged, graveyard);
            _flush.NotifyDirty(player);
            return true;
        }

        /// <summary>
        /// Durable half of a take from a shared container that the tick has already applied in memory. The row moves
        /// to <paramref name="container"/> in its own transaction that first locks it exactly as this server last saw it
        /// (<paramref name="expectedSource"/>). It runs off the tick through the player's ordered commit queue: ahead of
        /// their dirty inventory, so a later move of the same item is written after it, and ahead of every hard flush,
        /// so logout, zone change, trade and the chest's next open all see it stored. A row that no longer matches means
        /// memory and storage disagree about who holds the item: the commit throws, the player is quarantined and the
        /// next login loads storage, which the chest's next open reads as well.
        /// </summary>
        internal void QueueSharedSourceRelocation(Player player, Item item, ItemInstanceRecord expectedSource,
            Identity container, int placement)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(expectedSource);
            int characterId = player.Identity.Instance;
            var location = new ItemLocationUpdate(item.InstanceId, (int)container.Type, container.Instance, placement);
            // A later move of the item waits for this write: lane work queued ahead of it must not store that move
            // first, or this older place would overwrite it.
            var claim = new QueuedRowClaim([player.Inventory], [item.InstanceId]);
            _flush.Enqueue(player, () =>
            {
                _persistence.Persist(new InventoryMutationBatch(characterId, [], [location], [], []) { ExpectedSources = [expectedSource] });
                lock (player.PersistenceGate)
                    claim.Release();
            });
        }

        /// <summary>
        /// Stack slot for split/join: an owned page slot, or a carried bag's packed 0x6B
        /// handle/slot. Bags in the bank, loot, or mid-trade/move are not owned stack pages.
        /// </summary>
        static bool TryResolveStackSlot(Player player, Identity slot, out Container page, out int placement, out Item item)
        {
            if (slot.Type != IdentityType.Backpack)
            {
                placement = slot.Instance;
                return TryResolveOwnedSlot(player, slot, out page, out item);
            }

            page = null!; item = null!;
            placement = InventoryMoveService.DecodeBackpackSlot(slot);
            int handle = InventoryMoveService.DecodeBackpackHandle(slot);
            // Unhydrated bag pages have rows not yet loaded; a free slot there may be occupied.
            return player.Inventory.IsHydrated && handle != 0
                && player.Inventory.TryGetOwnedBackpackPageByHandle(handle, out page)
                && page.IsHydrated && page.LinkedItem is { Locked: false }
                && (page.Flags & ContainerFlags.CanRemove) != 0
                && placement >= page.Offset && placement < page.Offset + page.Capacity
                && page.Content.TryGetValue(placement, out item!) && !item.Locked && item.InstanceId > 0;
        }

        /// <summary>
        /// The slot a delete names: an owned page slot (inventory, wear pages, bank, overflow), or the packed
        /// Backpack/Container handle/slot the move path resolves (a carried bag, or loot the player has open), or a
        /// bag kept in the bank. In-memory loot items are retired without a database row.
        /// </summary>
        static bool TryResolveDeleteSlot(Player player, Identity slot, out Container page, out int placement, out Item item,
            out LootableDynel? loot)
        {
            loot = null;
            if (slot.Type != IdentityType.Backpack && slot.Type != IdentityType.Container)
            {
                placement = slot.Instance;
                return TryResolveOwnedSlot(player, slot, out page, out item);
            }

            if (player.Inventory.IsHydrated
                && InventoryMoveService.TryResolveSource(player, slot, out page, out placement, out item, out loot))
                return !item.Locked && item.InstanceId > 0;

            loot = null;
            page = null!; item = null!;
            int handle = InventoryMoveService.DecodeBackpackHandle(slot);
            placement = InventoryMoveService.DecodeBackpackSlot(slot);
            return slot.Type == IdentityType.Backpack && handle != 0 && player.Inventory.IsHydrated
                && player.Inventory.Bank.IsHydrated
                && player.Inventory.TryGetBackpackPageByHandle(handle, out page)
                && page.LinkedItem is { Locked: false } bag && player.Inventory.Bank.Content.ContainsValue(bag)
                && page.IsHydrated && (page.Flags & ContainerFlags.CanRemove) != 0
                && placement >= page.Offset && placement < page.Offset + page.Capacity
                && page.Content.TryGetValue(placement, out item!) && !item.Locked && item.InstanceId > 0;
        }

        /// <summary>Wear pages hold one item per slot; overflow items can only be deleted or moved into the inventory.</summary>
        static bool IsStackPage(Container page)
            => !page.Identity.Type.IsWearPage() && page.Identity.Type != IdentityType.OverflowWindow;

        static bool IsStackable(Item item)
            => item.Can(CanFlags.Stackable) && !InventoryMoveService.IsBagItem(item);

        public static bool TryResolveOwnedSlot(Player player, Identity slot, out Container page, out Item item)
        {
            page = null!; item = null!;
            if (!player.Inventory.IsHydrated) return false;
            page = slot.Type switch
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
            // Backpack actions have a separate packed handle/slot contract. Do not guess it here.
            return page != null && slot.Instance >= page.Offset && slot.Instance < page.Offset + page.Capacity
                && page.Content.TryGetValue(slot.Instance, out item!) && !item.Locked && item.InstanceId > 0;
        }

        static bool IsCurrent(Container page, int slot, Item expected, int instance)
            => !expected.Locked && expected.InstanceId == instance
                && page.Content.TryGetValue(slot, out Item? current) && ReferenceEquals(current, expected);

        /// <summary>
        /// Applies an inventory mutation on the tick and queues its transaction in the player's lane
        /// (<see cref="InventoryFlushService.Enqueue"/>). <paramref name="validate"/> runs first, under the player's
        /// gate; <paramref name="publish"/> then changes memory and tells the client. The queued transaction writes the
        /// rows the plan decided, folded with the dirty inventory it finds then (which is newer), in one batch with
        /// <paramref name="expectedSources"/> and <paramref name="finalStats"/>. Items the plan inserts count as persisted
        /// from here on: whatever moves or retires one later writes behind the insert in the lane. False when the action
        /// does not apply (nothing changed); a transaction that fails quarantines the player for a reload.
        /// <paramref name="playerShop"/> is a player shop change stored in the same transaction; <paramref name="stored"/>
        /// runs on the lane's writer once that transaction has committed (never when it fails or is discarded).
        /// </summary>
        internal bool TryCommit(Player player, IReadOnlyList<InventoryRowChange> changes,
            Func<bool> validate, Action publish, IReadOnlyList<StatRecord>? finalStats = null,
            IReadOnlyList<ItemInstanceRecord>? expectedSources = null, PlayerShopWrite? playerShop = null, Action? stored = null)
        {
            player.InvalidateOwnedItemCache();
            lock (player.PersistenceGate)
            {
                if (player.Session?.State != SessionState.InPlay || player.IsPersistenceQuarantined || !validate())
                    return false;
                if (changes.Select(c => c.Item.InstanceId).Distinct().Count() != changes.Count)
                    throw new InvalidOperationException("One mutation may write an instance only once.");
                var rows = new PlannedRow[changes.Count];
                for (int i = 0; i < rows.Length; i++)
                {
                    InventoryRowChange change = changes[i];
                    Item item = change.Item;
                    if (item.InstanceId <= 0 || change.FinalCount <= 0)
                        throw new InvalidOperationException("Invalid inventory mutation identity or count.");
                    rows[i] = new PlannedRow(item, change.Container, change.Placement, change.FinalCount, change.Retired,
                        HadRow: item.IsPersisted, CountBefore: item.StackCount, change.PreviousQuality, item.Quality);
                }
                int[] emptyContainersBeforeRetire = changes
                    .Where(change => change.Retired && InventoryMoveService.IsBagItem(change.Item))
                    .Select(change => change.Item.InstanceId).ToArray();
                long planSequence = PlayerInventory.DirtySequence;

                try
                {
                    publish();
                }
                catch (Exception exception)
                {
                    // Memory may be half changed with nothing queued to store it: reload the character from storage.
                    player.QuarantinePersistence();
                    player.Session?.Close();
                    _logger.Error(exception, "Inventory action publication failed; player persistence quarantined for reload.");
                    return false;
                }

                foreach (PlannedRow row in rows)
                    if (!row.Retired && !row.HadRow)
                        row.Item.IsPersisted = true; // Its insert is queued below, ahead of any later move or retire.
                // The transaction queued below decides these rows: lane work queued ahead of it leaves their dirty
                // entries for it.
                var claim = new QueuedRowClaim([player.Inventory], rows.Select(row => row.Item.InstanceId));

                var writes = new InventoryWriteSet();
                int characterId = player.Identity.Instance;
                IReadOnlyList<StatRecord> stats = finalStats ?? [];
                IReadOnlyList<ItemInstanceRecord> sources = expectedSources ?? [];
                _flush.Enqueue(player, () =>
                {
                    Persist(player, characterId, writes, rows, claim, planSequence, stats, sources, emptyContainersBeforeRetire, playerShop);
                    // Committed: a throw from here on must not reach the lane, which would retry a stored transaction.
                    try
                    {
                        stored?.Invoke();
                    }
                    catch (Exception exception)
                    {
                        _logger.Error(exception, "Inventory action stored-callback failed after commit char=" + characterId);
                    }
                });
            }
            return true;
        }

        /// <summary>
        /// The queued transaction, in the player's lane. <paramref name="writes"/> keeps what was taken across a retry
        /// of a transient failure, so dirty entries taken by a failed attempt are written by the next.
        /// </summary>
        void Persist(Player player, int characterId, InventoryWriteSet writes, PlannedRow[] rows, QueuedRowClaim claim,
            long planSequence, IReadOnlyList<StatRecord> finalStats, IReadOnlyList<ItemInstanceRecord> expectedSources,
            int[] emptyContainersBeforeRetire, PlayerShopWrite? playerShop)
        {
            lock (player.PersistenceGate)
            {
                // The plan's rows are decided with what is taken now: their dirty entries are folded in.
                claim.Release();
                PlayerInventory.InventoryDirtyFlush? taken = player.Inventory.TakeDirty();
                taken?.MarkNewlyPersisted();
                writes.Fold(taken, player.DrainDirtyUploadedNanos());
            }

            var stacks = new List<ItemStackUpdate>();
            var qualities = new List<ItemQualityUpdate>();
            foreach (PlannedRow row in rows)
            {
                if (row.Retired)
                {
                    writes.Retire(row.Item, row.HadRow, row.Container, row.Placement);
                    continue;
                }
                writes.Place(row.Item, row.HadRow, row.Container, row.Placement, row.FinalCount, planSequence);
                if (row.FinalCount != row.CountBefore)
                    stacks.Add(new ItemStackUpdate(row.Item.InstanceId, row.CountBefore, row.FinalCount));
                if (row.PreviousQuality > 0 && row.PreviousQuality != row.Quality)
                    qualities.Add(new ItemQualityUpdate(row.Item.InstanceId, row.PreviousQuality, row.Quality));
            }

            _persistence.Persist(new InventoryMutationBatch(characterId, [.. writes.Inserts], [.. writes.Locations], stacks, writes.UploadedNanos)
            {
                ExpectedSources = expectedSources,
                FinalStats = finalStats,
                Qualities = qualities,
                EmptyContainersBeforeRetire = emptyContainersBeforeRetire,
                PlayerShop = playerShop
            });
        }

        /// <summary>One row of a <see cref="TryCommit"/> plan, as the tick decided it: whether the item had a row then decides insert or update.</summary>
        sealed record PlannedRow(Item Item, Identity Container, int Placement, int FinalCount, bool Retired, bool HadRow,
            int CountBefore, int PreviousQuality, int Quality);
    }

    /// <param name="PreviousQuality">The stored QL of a persisted <paramref name="Item"/> that replaces it in place at a new QL; 0 otherwise.</param>
    internal sealed record InventoryRowChange(Item Item, Identity Container, int Placement, int FinalCount, bool Retired = false,
        int PreviousQuality = 0);
}
