namespace ZoneEngine_New.Core.Inventory
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;

    using AORebirth.Enums;

    using ZoneEngine_New.Core.GameData;

    /// <summary>
    /// Shared hash-to-item mint for loot, vendors, and other hash catalogs.
    /// </summary>
    public sealed class HashItemMinter
    {
        readonly IGameData _gameData;
        readonly IItemTemplateCatalog _catalog;
        readonly IItemBuilder _items;
        readonly ConcurrentDictionary<string, QualitySpan[]> _qualitySpans = new(StringComparer.Ordinal);
        readonly ConcurrentDictionary<string, LeafRange[]> _leafRanges = new(StringComparer.Ordinal);
        readonly ConcurrentDictionary<HashInstance, FamilySpan> _familySpans = new(ReferenceEqualityComparer.Instance);

        public HashItemMinter(IGameData gameData, IItemTemplateCatalog catalog, IItemBuilder items)
        {
            ArgumentNullException.ThrowIfNull(gameData);
            ArgumentNullException.ThrowIfNull(catalog);
            ArgumentNullException.ThrowIfNull(items);
            _gameData = gameData;
            _catalog = catalog;
            _items = items;
        }

        /// <summary>
        /// Rolls <paramref name="hash"/> into an item carrying a freshly allocated instance id.
        /// </summary>
        public bool TryMint(string hash, int desiredQuality, ItemSource source, out Item item)
        {
            item = null!;
            if (!TryRollIds(hash, desiredQuality, out int lowId, out int highId, out int quality))
                return false;

            item = _items.CreateWithNewInstance(lowId, highId, quality, source);
            return true;
        }

        /// <summary>
        /// SpawnItem's mint: of every leaf family reachable from <paramref name="hash"/>, the one that reaches the
        /// highest QL at or below <paramref name="desiredQuality"/>, random among families tied at that QL. A QL-range
        /// family reaches the request itself when it spans it, else its top QL. False when every family starts above
        /// the request.
        /// </summary>
        public bool TryMintHighestUpTo(string hash, int desiredQuality, ItemSource source, out Item item)
        {
            item = null!;
            if (string.IsNullOrEmpty(hash))
                return false;

            int wanted = desiredQuality < 1 ? 1 : desiredQuality;
            LeafRange[] leaves = _leafRanges.GetOrAdd(hash, BuildLeafRanges);
            int best = 0;
            int ties = 0;
            HashInstance? picked = null;
            for (int i = 0; i < leaves.Length; i++)
            {
                LeafRange leaf = leaves[i];
                if (leaf.Min > wanted)
                    continue;

                int reached = Math.Min(wanted, leaf.Max);
                if (reached > best)
                {
                    best = reached;
                    ties = 1;
                    picked = leaf.Instance;
                }
                else if (reached == best && Random.Shared.Next(++ties) == 0)
                {
                    picked = leaf.Instance;
                }
            }

            if (picked == null
                || !HashItemCatalog.TrySelectIds(picked, best, CatalogQuality, out int lowId, out int highId))
            {
                return false;
            }

            item = _items.CreateWithNewInstance(lowId, highId, best, source);
            return true;
        }

        LeafRange[] BuildLeafRanges(string hash)
        {
            var instances = new List<HashInstance>();
            _gameData.CollectHashLeafInstances(hash, instances);
            var ranges = new List<LeafRange>(instances.Count);
            foreach (HashInstance instance in instances)
            {
                int min = int.MaxValue;
                int max = int.MinValue;
                foreach (int id in instance.TemplateIds)
                {
                    if (id <= 0)
                        continue;

                    int ql = CatalogQuality(id);
                    min = Math.Min(min, ql);
                    max = Math.Max(max, ql);
                }

                if (min <= max)
                    ranges.Add(new LeafRange(instance, min, max));
            }

            return ranges.ToArray();
        }

        /// <summary>
        /// Resolves a hash to a low/high template pair and the clamped quality without creating an
        /// <see cref="Item"/>. Shops stock unminted ids and only build items at trade close.
        /// </summary>
        public bool TryRollIds(string hash, int desiredQuality, out int lowId, out int highId, out int quality)
        {
            lowId = 0;
            highId = 0;
            quality = 0;
            if (!_gameData.TryResolveHashInstance(hash, out HashInstance instance))
                return false;

            return TryRollIdsFor(instance, desiredQuality, out lowId, out highId, out quality);
        }

        /// <summary>
        /// Same as <see cref="TryRollIds"/> for an already-resolved leaf, skipping the category descent.
        /// </summary>
        public bool TryRollIdsFor(
            HashInstance instance,
            int desiredQuality,
            out int lowId,
            out int highId,
            out int quality)
        {
            ArgumentNullException.ThrowIfNull(instance);

            quality = HashItemCatalog.ClampQuality(instance, desiredQuality);
            return HashItemCatalog.TrySelectIds(instance, quality, CatalogQuality, out lowId, out highId);
        }

        /// <summary>
        /// Mints every item one loot roll or world spawn of <paramref name="hash"/> should create.
        /// Appends to <paramref name="into"/>. A SpawnAll parent mints every branch; any other hash mints one.
        /// </summary>
        public void MintSpawns(string hash, int desiredQuality, ItemSource source, List<Item> into)
        {
            ArgumentNullException.ThrowIfNull(into);
            var rolled = new List<(int LowId, int HighId, int Quality)>();
            RollSpawnIds(hash, desiredQuality, rolled);
            foreach ((int lowId, int highId, int quality) in rolled)
                into.Add(_items.CreateWithNewInstance(lowId, highId, quality, source));
        }

        /// <summary>
        /// The template ids and quality of every item one roll of <paramref name="hash"/> spawns, without building
        /// items (see <see cref="MintSpawns"/>). Appends to <paramref name="into"/>.
        /// </summary>
        public void RollSpawnIds(string hash, int desiredQuality, List<(int LowId, int HighId, int Quality)> into)
            => RollSpawnIds(hash, desiredQuality, desiredQuality, desiredQuality, into);

        /// <summary>
        /// One loot roll of <paramref name="hash"/> for the QL window <paramref name="minQuality"/>..<paramref name="maxQuality"/>
        /// (the mob's level ± the table row's LevelMod), rolled at <paramref name="rolledQuality"/>. An ordinary family drops
        /// only when its QL range overlaps the window, at the roll clamped into its range. A Special family
        /// (<see cref="ItemFlags.Special"/>) ignores the window: below its range it drops at its lowest QL, above at its
        /// highest. Appends to <paramref name="into"/>.
        /// </summary>
        public void RollSpawnIds(
            string hash,
            int minQuality,
            int maxQuality,
            int rolledQuality,
            List<(int LowId, int HighId, int Quality)> into)
        {
            ArgumentNullException.ThrowIfNull(into);
            if (string.IsNullOrEmpty(hash))
                return;

            int min = Math.Max(1, minQuality);
            int max = Math.Max(min, maxQuality);
            int roll = Math.Clamp(rolledQuality, min, max);

            // A random category pick only lands on families the window can drop.
            var instances = new List<HashInstance>();
            _gameData.CollectHashSpawns(hash, instances, child => CoversWindow(child, min, max));
            for (int i = 0; i < instances.Count; i++)
            {
                // A leaf listed directly is not filtered by the pick above.
                FamilySpan span = GetFamilySpan(instances[i]);
                if (span.Min > span.Max || (!span.Special && (span.Max < min || span.Min > max)))
                    continue;

                if (TryRollIdsFor(instances[i], Math.Clamp(roll, span.Min, span.Max), out int lowId, out int highId, out int quality))
                    into.Add((lowId, highId, quality));
            }
        }

        /// <summary>A leaf family's QL range, and whether any of its templates is Special.</summary>
        FamilySpan GetFamilySpan(HashInstance instance)
            => _familySpans.GetOrAdd(instance, BuildFamilySpan);

        FamilySpan BuildFamilySpan(HashInstance instance)
        {
            int min = int.MaxValue;
            int max = int.MinValue;
            bool special = false;
            int[] ids = instance.TemplateIds;
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i] <= 0)
                    continue;

                int ql = CatalogQuality(ids[i]);
                min = Math.Min(min, ql);
                max = Math.Max(max, ql);
                special |= _catalog.TryGet(ids[i], out ItemTemplate template)
                    && ((ItemFlags)template.Flags & ItemFlags.Special) != 0;
            }

            return new FamilySpan(min, max, special);
        }

        /// <summary>Every leaf item family reachable from <paramref name="hash"/>.</summary>
        public void CollectLeafInstances(string hash, List<HashInstance> into)
            => _gameData.CollectHashLeafInstances(hash, into);

        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source)
            => _items.CreateWithNewInstance(lowId, highId, quality, source);

        public Item Create(int lowId, int highId, int quality, ItemSource source)
            => _items.Create(lowId, highId, quality, source);

        /// <summary>
        /// True when some leaf family reachable from <paramref name="hash"/> can drop for the QL window
        /// <paramref name="min"/>..<paramref name="max"/>: its QL range overlaps the window, or it is Special.
        /// </summary>
        bool CoversWindow(string hash, int min, int max)
        {
            if (!_qualitySpans.TryGetValue(hash, out QualitySpan[]? spans))
                spans = GetQualitySpans(hash, new HashSet<string>(StringComparer.Ordinal));

            for (int i = 0; i < spans.Length; i++)
            {
                if (spans[i].Min <= max && spans[i].Max >= min)
                    return true;
            }

            return false;
        }

        QualitySpan[] GetQualitySpans(string hash, HashSet<string> visiting)
        {
            if (_qualitySpans.TryGetValue(hash, out QualitySpan[]? cached))
                return cached;
            if (!visiting.Add(hash))
                return Array.Empty<QualitySpan>();

            QualitySpan[] spans;
            if (_gameData.TryGetHashInstance(hash, out HashInstance instance))
            {
                FamilySpan family = GetFamilySpan(instance);
                spans = family.Min > family.Max
                    ? Array.Empty<QualitySpan>()
                    : new[] { family.Special ? new QualitySpan(int.MinValue, int.MaxValue) : new QualitySpan(family.Min, family.Max) };
            }
            else if (_gameData.TryGetHashTemplate(hash, out IReadOnlyList<string> children))
            {
                var all = new List<QualitySpan>();
                for (int i = 0; i < children.Count; i++)
                {
                    if (!string.IsNullOrEmpty(children[i]))
                        all.AddRange(GetQualitySpans(children[i], visiting));
                }

                spans = Merge(all);
            }
            else
            {
                spans = Array.Empty<QualitySpan>();
            }

            visiting.Remove(hash);
            _qualitySpans.TryAdd(hash, spans);
            return spans;
        }

        static QualitySpan[] Merge(List<QualitySpan> spans)
        {
            if (spans.Count == 0)
                return Array.Empty<QualitySpan>();

            spans.Sort((a, b) => a.Min.CompareTo(b.Min));
            var merged = new List<QualitySpan> { spans[0] };
            for (int i = 1; i < spans.Count; i++)
            {
                QualitySpan last = merged[^1];
                if (spans[i].Min <= last.Max || (last.Max != int.MaxValue && spans[i].Min == last.Max + 1))
                    merged[^1] = new QualitySpan(last.Min, Math.Max(last.Max, spans[i].Max));
                else
                    merged.Add(spans[i]);
            }

            return merged.ToArray();
        }

        readonly record struct QualitySpan(int Min, int Max);

        readonly record struct FamilySpan(int Min, int Max, bool Special);

        readonly record struct LeafRange(HashInstance Instance, int Min, int Max);

        int CatalogQuality(int aoid)
        {
            if (_catalog.TryGet(aoid, out ItemTemplate template) && template.Quality > 0)
                return template.Quality;

            return 1;
        }
    }
}
