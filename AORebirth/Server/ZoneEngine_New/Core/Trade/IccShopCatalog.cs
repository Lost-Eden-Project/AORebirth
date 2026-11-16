namespace ZoneEngine_New.Core.Trade
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;

    using ZoneEngine_New.Core.Logging;

    /// <summary>
    /// The ICC Commendations shop (GameData/Shops/IccShop.json, written by the shop catalog tool; the client reads the
    /// same file from its data patch): every item it sells, by category, with the QL steps it is made of and each step's
    /// credit value; the mission qualities each level rolls (one per difficulty slider position); the typical item value
    /// at each QL; and the shop terminals' templates.
    /// <para>A character may buy what a mission it can roll offers (<see cref="TryOffer"/>): an item at one of its level's
    /// mission qualities that the item's own QL range holds, a QL 1 item at QL 1, a nano crystal (one QL) when its QL is
    /// within <see cref="NanoQualityWindow"/> of one of them. The price (<see cref="Price"/>) starts from the typical
    /// value at that QL, nudged by the item's own value, times its category's multiplier and QL band; an item's
    /// <see cref="IccShopItem.FixedPrice"/> replaces it. The client applies the same rule and price.</para>
    /// </summary>
    public sealed class IccShopCatalog
    {
        public const string RelativePath = "Shops/IccShop.json";

        static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        public static IccShopCatalog Empty { get; } = new();

        public int Version { get; set; }

        public int[] Terminals { get; set; } = [];

        public int NanoQualityWindow { get; set; } = 10;

        /// <summary>Per level (entry 0 is level 1), the mission qualities it rolls, lowest first.</summary>
        public int[][] QualitiesByLevel { get; set; } = [];

        public double CreditsPerCommendation { get; set; }

        /// <summary>How far an item's own value may move its price from the typical one (its value over the typical).</summary>
        public IccShopNudge ValueNudge { get; set; } = new();

        /// <summary>Per QL (entry 0 is QL 1), the median credit value of the items sold at it.</summary>
        public int[] TypicalValueByQuality { get; set; } = [];

        public IccShopCategory[] Categories { get; set; } = [];

        public IccShopItem[] Items { get; set; } = [];

        Dictionary<int, IccShopItem> _byId = new();
        Dictionary<string, IccShopCategory> _categories = new(StringComparer.Ordinal);
        HashSet<int> _terminals = new();

        /// <summary>The catalog at <paramref name="path"/>; empty (no terminal sells) when it is missing or does not read.</summary>
        public static IccShopCatalog Load(string path, IZoneLogger logger)
        {
            if (!File.Exists(path))
            {
                logger.Warn("ICC shop catalog " + path + " is missing; the ICC shop sells nothing.");
                return Empty;
            }
            try
            {
                IccShopCatalog catalog = JsonSerializer.Deserialize<IccShopCatalog>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException("the catalog is null");
                if (catalog.Problem() is string problem)
                    throw new InvalidDataException(problem);
                catalog.Index();
                logger.Info("GameData ICC shop items=" + catalog.Items.Length + " terminals=" + string.Join(",", catalog.Terminals));
                return catalog;
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException)
            {
                logger.Error(exception, "ICC shop catalog " + path + " does not read; the ICC shop sells nothing.");
                return Empty;
            }
        }

        string? Problem()
        {
            if (Version != 1)
                return "Version must be 1";
            if (QualitiesByLevel is not { Length: > 0 } || QualitiesByLevel.Any(row => row is not { Length: > 0 } || row.Any(q => q < 1)))
                return "QualitiesByLevel must list at least one quality of at least 1 per level";
            if (CreditsPerCommendation <= 0 || double.IsNaN(CreditsPerCommendation))
                return "CreditsPerCommendation must be above 0";
            if (TypicalValueByQuality is not { Length: > 0 })
                return "TypicalValueByQuality must list a value per QL";
            if (ValueNudge == null || ValueNudge.Min <= 0 || ValueNudge.Max < ValueNudge.Min)
                return "ValueNudge must have 0 < Min <= Max";
            if (Categories.Any(c => c == null || string.IsNullOrEmpty(c.Key) || c.Multiplier < 0 || double.IsNaN(c.Multiplier)
                    || (c.QlScale ?? []).Any(b => b == null || b.Multiplier < 0 || double.IsNaN(b.Multiplier))))
                return "a category has no key or a negative multiplier";
            var keys = new HashSet<string>(Categories.Select(c => c.Key), StringComparer.Ordinal);
            foreach (IccShopItem item in Items)
            {
                if (item?.Steps is not { Length: > 0 } || item.Steps.Any(s => s is not { Length: 2 } || s[0] <= 0 || s[1] <= 0))
                    return "an item has no QL steps";
                if (item.Values is not { } values || values.Length != item.Steps.Length)
                    return "item " + item.Steps[0][0] + " has no value for each QL step";
                if (!keys.Contains(item.Category))
                    return "item " + item.Steps[0][0] + " names an unknown category " + item.Category;
                if (item.FixedPrice < 0)
                    return "item " + item.Steps[0][0] + " has a negative FixedPrice";
            }
            return null;
        }

        void Index()
        {
            _byId = new Dictionary<int, IccShopItem>();
            foreach (IccShopItem item in Items)
            {
                // Lowest QL first, each step keeping its value.
                int[] order = Enumerable.Range(0, item.Steps.Length)
                    .OrderBy(i => item.Steps[i][1]).ThenBy(i => item.Steps[i][0]).ToArray();
                item.Steps = order.Select(i => item.Steps[i]).ToArray();
                item.Values = order.Select(i => item.Values[i]).ToArray();
                _byId[item.Id] = item;
            }
            _categories = Categories.ToDictionary(c => c.Key, StringComparer.Ordinal);
            _terminals = new HashSet<int>(Terminals);
        }

        public bool IsTerminal(int templateId) => _terminals.Contains(templateId);

        public bool TryGet(int itemId, out IccShopItem item) => _byId.TryGetValue(itemId, out item!);

        /// <summary>The mission qualities a character of <paramref name="level"/> rolls, lowest first.</summary>
        public int[] Qualities(int level)
            => QualitiesByLevel.Length == 0 ? [] : QualitiesByLevel[Math.Clamp(level, 1, QualitiesByLevel.Length) - 1];

        /// <summary>
        /// Whether a character of <paramref name="level"/> may buy <paramref name="item"/> at <paramref name="quality"/>;
        /// when it may, the template pair the item is made with at that QL and its price.
        /// </summary>
        public bool TryOffer(IccShopItem item, int level, int quality, out int lowId, out int highId, out int price)
        {
            lowId = highId = price = 0;
            int[] rolled = Qualities(level);
            int lowest = item.Steps[0][1], highest = item.Steps[^1][1];
            bool allowed = item.Nano
                ? quality == lowest && rolled.Any(q => Math.Abs(q - lowest) <= NanoQualityWindow)
                : highest == 1 ? quality == 1
                : quality >= lowest && quality <= highest && Array.IndexOf(rolled, quality) >= 0;
            if (!allowed)
                return false;

            (int low, int high) = StepsAt(item, quality);
            lowId = item.Steps[low][0];
            highId = item.Steps[high][0];
            price = Price(item, quality);
            return true;
        }

        // The steps that hold the QL: the last at or below it and the first at or above it (indices).
        static (int Low, int High) StepsAt(IccShopItem item, int quality)
        {
            int low = 0, high = item.Steps.Length - 1;
            for (int i = 0; i < item.Steps.Length; i++)
            {
                if (item.Steps[i][1] <= quality)
                    low = i;
                if (item.Steps[i][1] >= quality)
                {
                    high = i;
                    break;
                }
            }
            return (low, high);
        }

        /// <summary>The item's credit value at <paramref name="quality"/>, linear between the steps that hold it.</summary>
        public static double ValueAt(IccShopItem item, int quality)
        {
            (int low, int high) = StepsAt(item, quality);
            int lowQl = item.Steps[low][1], highQl = item.Steps[high][1];
            if (highQl == lowQl)
                return item.Values[low];
            return item.Values[low] + (item.Values[high] - item.Values[low]) * (double)(quality - lowQl) / (highQl - lowQl);
        }

        /// <summary>
        /// The price at <paramref name="quality"/>: the item's FixedPrice when set; else the typical value there over
        /// CreditsPerCommendation, times the item's own value over the typical (kept within ValueNudge), times the
        /// category's multiplier and its QL band's (the last band whose From is at or below the QL), rounded up, at least 1.
        /// </summary>
        public int Price(IccShopItem item, int quality)
        {
            if (item.FixedPrice > 0)
                return item.FixedPrice;
            double multiplier = 1;
            if (_categories.TryGetValue(item.Category, out IccShopCategory? category))
            {
                multiplier = category.Multiplier;
                double band = 1;
                foreach (IccShopQlBand b in category.QlScale ?? [])
                    if (quality >= b.From)
                        band = b.Multiplier;
                multiplier *= band;
            }
            double typical = TypicalValueByQuality[Math.Clamp(quality, 1, TypicalValueByQuality.Length) - 1];
            double nudge = typical > 0 ? Math.Clamp(ValueAt(item, quality) / typical, ValueNudge.Min, ValueNudge.Max) : 1;
            double price = typical / CreditsPerCommendation * nudge * multiplier;
            return (int)Math.Max(1, Math.Min(int.MaxValue, Math.Ceiling(price)));
        }
    }

    public sealed class IccShopCategory
    {
        public string Key { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        public double Multiplier { get; set; } = 1;

        /// <summary>QL bands, lowest first: each band's multiplier from its QL up to the next band's.</summary>
        public IccShopQlBand[]? QlScale { get; set; }
    }

    public sealed class IccShopQlBand
    {
        public int From { get; set; }

        public double Multiplier { get; set; } = 1;
    }

    public sealed class IccShopNudge
    {
        public double Min { get; set; } = 1;

        public double Max { get; set; } = 1;
    }

    /// <summary>One shop item: its template ids by QL (<c>[id, ql]</c> pairs, lowest QL first) and each step's credit value.</summary>
    public sealed class IccShopItem
    {
        public string Name { get; set; } = string.Empty;

        public int[][] Steps { get; set; } = [];

        public int[] Values { get; set; } = [];

        public string Category { get; set; } = string.Empty;

        public bool Nano { get; set; }

        /// <summary>A price set by hand, at every QL, in place of the formula; 0: none.</summary>
        public int FixedPrice { get; set; }

        /// <summary>The item's id in the shop and in <c>IccPurchase</c>: the template id of its lowest QL step.</summary>
        public int Id => Steps[0][0];
    }
}
