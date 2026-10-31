namespace ZoneEngine_New.Core.Trade
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Inventory;

    /// <summary>One purchasable line in a vending machine's generated stock.</summary>
    public readonly struct ShopStockSlot
    {
        public ShopStockSlot(int lowId, int highId, int quality, string itemHash = "", int pricePercent = 100)
        {
            LowId = lowId;
            HighId = highId;
            Quality = quality;
            ItemHash = itemHash ?? string.Empty;
            PricePercent = pricePercent > 0 ? pricePercent : 100;
        }

        /// <summary>The line's price as a percentage of the item's normal shop price (vending machine stock entry; 100 = normal).</summary>
        public int PricePercent { get; }

        public int LowId { get; }

        public int HighId { get; }

        public int Quality { get; }

        /// <summary>The exact item-pair correlation used by DAO-backed stock, when uniquely available.</summary>
        public string ItemHash { get; }
    }

    internal readonly struct ShopStockRange
    {
        public ShopStockRange(string itemHash, int lowId, int highId, int minimumQuality, int maximumQuality)
        {
            ItemHash = itemHash;
            LowId = lowId;
            HighId = highId;
            MinimumQuality = minimumQuality;
            MaximumQuality = maximumQuality;
        }

        public string ItemHash { get; }
        public int LowId { get; }
        public int HighId { get; }
        public int MinimumQuality { get; }
        public int MaximumQuality { get; }
    }

    /// <summary>
    /// A vending machine's rolled inventory. Stock is shared by every concurrent shopper, so the
    /// pane they all see stays consistent, and it is only re-rolled after the machine has been idle
    /// (no open trade) for <see cref="IdleRefreshMinutes"/>.
    /// </summary>
    public sealed class ShopStock
    {
        public const int IdleRefreshMinutes = 10;

        readonly List<ShopStockSlot> _slots = new();
        bool _sorted;
        ShopStockRange[]? _configuredRanges;
        int _openTrades;
        long _idleSinceMs = Environment.TickCount64;

        public IReadOnlyList<ShopStockSlot> Slots => _slots.AsReadOnly();

        public bool IsGenerated { get; private set; }
        internal bool IsConfiguredSnapshot { get; private set; }
        internal string? ConfigurationUnavailableReason { get; private set; }

        /// <summary>An exact accepted vendor adapter installs its entire frozen stock once.</summary>
        internal void SetConfiguredSnapshot(IReadOnlyList<ShopStockSlot> slots)
        {
            ArgumentNullException.ThrowIfNull(slots);
            if (IsGenerated || _openTrades != 0 || slots.Count == 0)
                throw new InvalidOperationException("An accepted shop snapshot must be nonempty and installed before opening.");
            var copy = new List<ShopStockSlot>(slots.Count);
            foreach (var slot in slots)
            {
                if (slot.LowId <= 0 || slot.HighId <= 0 || slot.Quality <= 0)
                    throw new InvalidOperationException("An accepted shop snapshot contains an incomplete item identity.");
                copy.Add(slot);
            }
            _slots.Clear(); _slots.AddRange(copy);
            _sorted = false;
            IsGenerated = true; IsConfiguredSnapshot = true; ConfigurationUnavailableReason = null;
        }

        /// <summary>Installs the complete DAO row set and rolls one slot per row.</summary>
        internal void SetConfiguredRanges(IReadOnlyList<ShopStockRange> ranges, Random random)
        {
            ArgumentNullException.ThrowIfNull(ranges);
            ArgumentNullException.ThrowIfNull(random);
            if (IsGenerated || _openTrades != 0 || ranges.Count == 0)
                throw new InvalidOperationException("An accepted shop definition must be nonempty and installed before opening.");

            var copy = new ShopStockRange[ranges.Count];
            for (int i = 0; i < ranges.Count; i++)
            {
                ShopStockRange range = ranges[i];
                if (range.LowId <= 0
                    || range.HighId <= 0
                    || range.MinimumQuality <= 0
                    || range.MaximumQuality < range.MinimumQuality)
                    throw new InvalidOperationException("An accepted shop definition contains an incomplete stock row.");
                copy[i] = range;
            }

            _configuredRanges = copy;
            IsConfiguredSnapshot = true;
            ConfigurationUnavailableReason = null;
            GenerateConfigured(random);
        }

        internal void SetConfigurationUnavailable(string reason)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            if (IsGenerated || _openTrades != 0)
                throw new InvalidOperationException("Shop stock availability must be set before opening or generating stock.");
            _slots.Clear();
            ConfigurationUnavailableReason = reason;
        }

        internal void EnsureConfiguredFresh(Random random)
        {
            ArgumentNullException.ThrowIfNull(random);
            if (_configuredRanges == null || (IsGenerated && !IsIdleExpired()))
                return;

            GenerateConfigured(random);
        }

        /// <summary>Shoppers currently holding this machine's trade window open.</summary>
        public int OpenTrades => _openTrades;

        /// <summary>
        /// Rolls a fresh stock list when the machine has never been opened, or when nobody has been
        /// trading with it for <see cref="IdleRefreshMinutes"/>. A machine with a live shopper keeps
        /// its list so concurrent panes cannot disagree.
        /// </summary>
        public void EnsureFresh(
            VendingMachineDefinition definition,
            HashItemMinter minter,
            Random random,
            Func<int, int, bool>? hasTemplate = null)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(minter);
            ArgumentNullException.ThrowIfNull(random);

            if (IsConfiguredSnapshot || (IsGenerated && !IsIdleExpired()))
                return;

            Generate(definition, minter, random, hasTemplate);
        }

        /// <summary>
        /// Stock from the shop item's own list (items.dat ShopInventory), as the stock engine expands it: each line's
        /// hash fans out one level (a leaf hash is its own single child), and each child is rolled
        /// <see cref="Inventory.ShopInventoryEntry.Amount"/> times. A roll picks a QL in the line's band and a leaf under
        /// the child that can be made at it, and is kept when random(0, 100) &lt; <see cref="Inventory.ShopInventoryEntry.Chance"/>;
        /// a roll identical to one already stocked adds no row. Items <paramref name="hasTemplate"/> rejects
        /// (no server template for the low/high ids) are skipped. Lines stay in list order, up to
        /// <see cref="VendingMachineDefinition.MaxSlots"/>.
        /// </summary>
        public void EnsureFreshFromShopInventory(
            IReadOnlyList<Inventory.ShopInventoryEntry> lines,
            HashItemMinter minter,
            Random random,
            Func<int, int, bool>? hasTemplate = null)
        {
            ArgumentNullException.ThrowIfNull(lines);
            ArgumentNullException.ThrowIfNull(minter);
            ArgumentNullException.ThrowIfNull(random);

            if (IsConfiguredSnapshot || (IsGenerated && !IsIdleExpired()))
                return;

            _slots.Clear();
            _sorted = false;
            List<HashInstance> leaves = new();
            List<HashInstance> usable = new();
            HashSet<(int LowId, int HighId, int Quality)> stocked = new();
            foreach (Inventory.ShopInventoryEntry line in lines)
            {
                if (_slots.Count >= VendingMachineDefinition.MaxSlots)
                    break;
                if (string.IsNullOrEmpty(line.Hash))
                    continue;

                int minQuality = Math.Max(1, Math.Min(line.MinQl, line.MaxQl));
                int maxQuality = Math.Max(1, Math.Max(line.MinQl, line.MaxQl));
                int chance = (int)Math.Min(line.Chance, 100u);
                IReadOnlyList<string> groups = minter.TryGetChildHashes(line.Hash, out IReadOnlyList<string> children)
                    ? children
                    : new[] { line.Hash };

                // Count rounds over every child, as retail lists them (all children, then all children again).
                for (int round = 0; round < line.Amount && _slots.Count < VendingMachineDefinition.MaxSlots; round++)
                {
                    for (int g = 0; g < groups.Count && _slots.Count < VendingMachineDefinition.MaxSlots; g++)
                    {
                        int quality = random.Next(minQuality, maxQuality + 1);
                        if (random.Next(100) >= chance)
                            continue;

                        leaves.Clear();
                        minter.CollectLeafInstances(groups[g], leaves);
                        usable.Clear();
                        for (int i = 0; i < leaves.Count; i++)
                        {
                            if (minter.LeafCoversQuality(leaves[i], quality))
                                usable.Add(leaves[i]);
                        }

                        if (usable.Count == 0)
                            continue;

                        // A roll matching one already stocked adds no row: retail Antonio Stacklund (GMUE, Count 5,
                        // fixed-QL items) lists each item once, while a QL band yields one row per distinct QL.
                        HashInstance leaf = usable[random.Next(usable.Count)];
                        if (minter.TryRollIdsFor(leaf, quality, out int lowId, out int highId, out int rolledQuality)
                            && (hasTemplate == null || hasTemplate(lowId, highId))
                            && stocked.Add((lowId, highId, rolledQuality)))
                            _slots.Add(new ShopStockSlot(lowId, highId, rolledQuality));
                    }
                }
            }

            IsGenerated = true;
            _idleSinceMs = Environment.TickCount64;
        }

        public bool IsIdleExpired()
        {
            if (_openTrades > 0)
                return false;

            long idleMs = Environment.TickCount64 - _idleSinceMs;
            return idleMs >= IdleRefreshMinutes * 60L * 1000L;
        }

        public void OpenTrade()
        {
            _openTrades++;
        }

        public void CloseTrade()
        {
            if (_openTrades > 0)
                _openTrades--;

            if (_openTrades == 0)
                _idleSinceMs = Environment.TickCount64;
        }

        /// <summary>Cancels every outstanding open trade count (vendor death, machine despawn).</summary>
        public void Reset()
        {
            _openTrades = 0;
            _idleSinceMs = Environment.TickCount64;
        }

        /// <summary>
        /// Orders the stock by item name, then QL (then template ids): Item 1 QL5, Item 1 QL10, Item 2 QL5.
        /// Once per generated list, and never while a shopper holds the window open: their picks are indices into
        /// this list.
        /// </summary>
        internal void EnsureSorted(Func<ShopStockSlot, string> nameOf)
        {
            ArgumentNullException.ThrowIfNull(nameOf);
            if (_sorted || _openTrades != 0)
                return;

            List<ShopStockSlot> ordered = _slots
                .OrderBy(slot => nameOf(slot) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(slot => slot.Quality)
                .ThenBy(slot => slot.LowId)
                .ThenBy(slot => slot.HighId)
                .ToList();
            _slots.Clear();
            _slots.AddRange(ordered);
            _sorted = true;
        }

        public bool TryGetSlot(int index, out ShopStockSlot slot)
        {
            if (index < 0 || index >= _slots.Count)
            {
                slot = default;
                return false;
            }

            slot = _slots[index];
            return true;
        }

        public ShopUpdateMessage BuildShopUpdate(Identity shopIdentity)
        {
            var slots = new VendingMachineSlot[_slots.Count];
            for (int i = 0; i < _slots.Count; i++)
            {
                ShopStockSlot stock = _slots[i];
                slots[i] = new VendingMachineSlot
                {
                    ItemLowId = stock.LowId,
                    ItemHighId = stock.HighId,
                    Quality = stock.Quality
                };
            }

            return new ShopUpdateMessage
            {
                Identity = shopIdentity,
                Unknown = 1,
                VendingMachineSlots = slots
            };
        }

        void Generate(VendingMachineDefinition definition, HashItemMinter minter, Random random, Func<int, int, bool>? hasTemplate)
        {
            _slots.Clear();
            _sorted = false;
            List<HashInstance> leaves = new();

            foreach (VendingMachineStockEntry entry in definition.UsableEntries())
            {
                if (_slots.Count >= VendingMachineDefinition.MaxSlots)
                    break;

                (int minQuality, int maxQuality) = VendingMachineDefinition.QualityBand(entry);

                if (entry.Expansion == StockExpansion.ExpandAll)
                {
                    leaves.Clear();
                    minter.CollectLeafInstances(entry.Hash, leaves);
                    for (int i = 0; i < leaves.Count; i++)
                    {
                        if (_slots.Count >= VendingMachineDefinition.MaxSlots)
                            break;

                        AddLeafSlot(minter, leaves[i], random.Next(minQuality, maxQuality + 1), entry.PricePercent, hasTemplate);
                    }

                    continue;
                }

                for (int repeat = 0; repeat < entry.Repeats; repeat++)
                {
                    if (_slots.Count >= VendingMachineDefinition.MaxSlots)
                        break;

                    if (entry.Chance < 100 && random.Next(100) >= entry.Chance)
                        continue;

                    int quality = random.Next(minQuality, maxQuality + 1);
                    if (minter.TryRollIds(entry.Hash, quality, out int lowId, out int highId, out int rolledQuality)
                        && (hasTemplate == null || hasTemplate(lowId, highId)))
                        _slots.Add(new ShopStockSlot(lowId, highId, rolledQuality, pricePercent: entry.PricePercent));
                }
            }

            IsGenerated = true;
            _idleSinceMs = Environment.TickCount64;
        }

        void GenerateConfigured(Random random)
        {
            ShopStockRange[] ranges = _configuredRanges
                ?? throw new InvalidOperationException("Configured shop ranges are unavailable.");
            _slots.Clear();
            _sorted = false;
            foreach (ShopStockRange range in ranges)
            {
                int quality = random.Next(range.MinimumQuality, range.MaximumQuality + 1);
                _slots.Add(new ShopStockSlot(range.LowId, range.HighId, quality, range.ItemHash));
            }
            IsGenerated = true;
            _idleSinceMs = Environment.TickCount64;
        }

        void AddLeafSlot(HashItemMinter minter, HashInstance leaf, int desiredQuality, int pricePercent, Func<int, int, bool>? hasTemplate)
        {
            if (minter.TryRollIdsFor(leaf, desiredQuality, out int lowId, out int highId, out int quality)
                && (hasTemplate == null || hasTemplate(lowId, highId)))
                _slots.Add(new ShopStockSlot(lowId, highId, quality, pricePercent: pricePercent));
        }
    }
}
