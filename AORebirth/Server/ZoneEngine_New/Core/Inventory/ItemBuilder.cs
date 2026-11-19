namespace ZoneEngine_New.Core.Inventory
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    using AORebirth.Enums;

    using LostEden.Protocol.GameData;

    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Logging;

    public sealed class ItemBuilder : IItemBuilder
    {
        private readonly IItemTemplateCatalog _catalog;
        private readonly IItemInstanceIdAllocator? _ids;
        private readonly IZoneLogger _logger;

        public ItemBuilder(IItemTemplateCatalog catalog, IZoneLogger logger)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            ArgumentNullException.ThrowIfNull(logger);
            _catalog = catalog;
            _logger = logger;
        }

        public ItemBuilder(IItemTemplateCatalog catalog, IItemInstanceIdAllocator ids, IZoneLogger logger)
            : this(catalog, logger)
        {
            _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        }

        public Item Create(
            int lowId,
            int highId,
            int quality,
            ItemSource source,
            int stackCount = 1,
            int instanceId = 0,
            Identity? identity = null,
            byte[]? statsBlob = null)
        {
            ItemTemplate definition = CreateTemplate(lowId, highId, quality);
            ApplyStatsBlob(definition, statsBlob);

            int clampedQuality = definition.Quality;

            // A stored item owns its own count: it may have spent charges. Only a fresh mint takes
            // the full count off the template (MaxEnergy, else MultipleCount).
            int resolvedStack = stackCount;
            if (instanceId == 0 && definition.MintStackCount > 1)
                resolvedStack = definition.MintStackCount;
            // Weapons minted while the clip size was taken as the count were stored with it; they are single items.
            else if (definition.IsClipWeapon)
                resolvedStack = 1;

            Item item = new()
            {
                InstanceId = instanceId,
                Identity = ResolveMintedIdentity(
                    identity,
                    definition.DynelType != 0 ? definition.DynelType : definition.ItemType,
                    instanceId),
                LowId = lowId,
                HighId = highId,
                Quality = clampedQuality,
                StackCount = Math.Max(1, resolvedStack),
                Source = source,
                Definition = definition
            };
            item.ApplyContainerIdentityIfBag();
            _logger.Info(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Item created name={0} low={1} high={2} ql={3} itemType={4} dynelType={5} statItemType={6} identity={7}:{8}",
                    item.Name,
                    item.LowId,
                    item.HighId,
                    item.Quality,
                    definition.ItemType,
                    definition.DynelType,
                    item.GetStat(CharacterStat.ItemType),
                    item.Identity.Type,
                    item.Identity.Instance));
            return item;
        }

        public Item CreateWithNewInstance(
            int lowId,
            int highId,
            int quality,
            ItemSource source,
            int stackCount = 1)
        {
            Item item = Create(lowId, highId, quality, source, stackCount);
            item.AssignInstanceId((_ids ?? throw new InvalidOperationException("Item allocation requires the configured instance-id allocator.")).Allocate());
            return item;
        }

        static Identity ResolveMintedIdentity(Identity? identity, int itemType, int instanceId)
        {
            if (identity is Identity supplied && supplied.Type != IdentityType.None)
                return supplied;

            if (itemType == 0)
                return identity ?? Identity.None;

            return new Identity
            {
                Type = (IdentityType)itemType,
                Instance = instanceId
            };
        }

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality)
        {
            ItemTemplate low = ResolveTemplate(lowId);
            ItemTemplate high = highId == lowId || !_catalog.TryGet(highId, out ItemTemplate? highTemplate)
                ? low
                : highTemplate!;

            int clampedQuality = ClampQuality(quality, low.Quality, high.Quality);
            return BuildEffectiveDefinition(low, high, clampedQuality);
        }

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item)
        {
            ArgumentNullException.ThrowIfNull(row);
            try
            {
                Identity identity = row.ItemType != 0
                    ? new Identity { Type = (IdentityType)row.ItemType, Instance = row.InstanceId }
                    : Identity.None;

                item = Create(
                    row.LowId,
                    row.HighId,
                    row.Quality,
                    row.Source,
                    row.StackCount,
                    row.InstanceId,
                    identity);
                // Template MultipleCount controls minting defaults, never an already persisted stack.
                if (row.StackCount <= 0) throw new InvalidOperationException("Persisted item has an invalid stack count.");
                item.StackCount = item.Definition.IsClipWeapon ? 1 : row.StackCount;
                item.IsPersisted = true;
                item.ApplyContainerIdentityIfBag();
                return true;
            }
            catch (Exception exception)
            {
                _logger.Warn(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "ItemBuilder.TryFromInstanceRecord failed id={0} low={1}: {2}",
                        row.InstanceId,
                        row.LowId,
                        exception.Message));
                item = null!;
                return false;
            }
        }

        private ItemTemplate ResolveTemplate(int aoid)
        {
            if (_catalog.TryGet(aoid, out ItemTemplate? template))
                return template!;

            return new ItemTemplate
            {
                Id = aoid,
                Name = string.Format(CultureInfo.InvariantCulture, "Unknown {0}", aoid),
                Quality = 1
            };
        }

        private static int ClampQuality(int quality, int lowQl, int highQl)
        {
            int min = Math.Min(lowQl, highQl);
            int max = Math.Max(lowQl, highQl);
            if (max < 1)
                max = 1;
            if (min < 1)
                min = 1;

            if (quality < min)
                return min;
            if (quality > max)
                return max;
            return quality;
        }

        private static ItemTemplate BuildEffectiveDefinition(ItemTemplate low, ItemTemplate high, int quality)
        {
            if (ReferenceEquals(low, high) || low.Quality == high.Quality || low.Id == high.Id)
                return DeepCopy(low, quality);

            if (quality == low.Quality)
                return DeepCopy(low, quality);

            if (quality == high.Quality)
                return DeepCopy(high, quality);

            var ql = new QlSpan(low.Quality, high.Quality, quality);

            return new ItemTemplate
            {
                Id = low.Id,
                Name = low.Name,
                Quality = quality,
                Flags = low.Flags,
                ItemType = low.ItemType,
                DynelType = low.DynelType,
                MultipleCount = ql.Between(low.MultipleCount, high.MultipleCount),
                Stats = LerpIntMap(low.Stats, high.Stats, ql),
                Attack = LerpIntMap(low.Attack, high.Attack, ql),
                Defend = LerpIntMap(low.Defend, high.Defend, ql),
                SpellList = LerpSpellList(low.SpellList, high.SpellList, ql),
                Actions = LerpActions(low.Actions, high.Actions, ql),
                Relations = new List<int>(low.Relations),
                ShopInventory = low.ShopInventory
            };
        }

        private static ItemTemplate DeepCopy(ItemTemplate source, int quality)
        {
            var spellList = new Dictionary<AORebirth.Enums.EventType, List<ItemSpell>>();
            foreach (KeyValuePair<AORebirth.Enums.EventType, List<ItemSpell>> pair in source.SpellList)
            {
                var spells = new List<ItemSpell>(pair.Value.Count);
                foreach (ItemSpell spell in pair.Value)
                    spells.Add(spell.Copy());
                spellList[pair.Key] = spells;
            }

            var actions = new List<ItemAction>(source.Actions.Count);
            foreach (ItemAction action in source.Actions)
                actions.Add(action.Copy());

            return new ItemTemplate
            {
                Id = source.Id,
                Name = source.Name,
                Quality = quality,
                Flags = source.Flags,
                ItemType = source.ItemType,
                DynelType = source.DynelType,
                MultipleCount = source.MultipleCount,
                Stats = new Dictionary<CharacterStat, int>(source.Stats),
                Attack = new Dictionary<CharacterStat, int>(source.Attack),
                Defend = new Dictionary<CharacterStat, int>(source.Defend),
                SpellList = spellList,
                Actions = actions,
                Relations = new List<int>(source.Relations),
                ShopInventory = source.ShopInventory
            };
        }

        private static Dictionary<CharacterStat, int> LerpIntMap(
            Dictionary<CharacterStat, int> low,
            Dictionary<CharacterStat, int> high,
            QlSpan ql)
        {
            var result = new Dictionary<CharacterStat, int>();
            foreach (KeyValuePair<CharacterStat, int> pair in low)
            {
                int highValue = high.TryGetValue(pair.Key, out int hv) ? hv : pair.Value;
                result[pair.Key] = ql.Between(pair.Value, highValue);
            }

            foreach (KeyValuePair<CharacterStat, int> pair in high)
            {
                if (!result.ContainsKey(pair.Key))
                    result[pair.Key] = pair.Value;
            }

            return result;
        }

        private static Dictionary<AORebirth.Enums.EventType, List<ItemSpell>> LerpSpellList(
            Dictionary<AORebirth.Enums.EventType, List<ItemSpell>> low,
            Dictionary<AORebirth.Enums.EventType, List<ItemSpell>> high,
            QlSpan ql)
        {
            var result = new Dictionary<AORebirth.Enums.EventType, List<ItemSpell>>();
            foreach (KeyValuePair<AORebirth.Enums.EventType, List<ItemSpell>> pair in low)
            {
                if (!high.TryGetValue(pair.Key, out List<ItemSpell>? highSpells))
                {
                    var copied = new List<ItemSpell>(pair.Value.Count);
                    foreach (ItemSpell spell in pair.Value)
                        copied.Add(spell.Copy());
                    result[pair.Key] = copied;
                    continue;
                }

                int count = Math.Min(pair.Value.Count, highSpells.Count);
                var lerped = new List<ItemSpell>(count);
                for (int i = 0; i < count; i++)
                    lerped.Add(LerpSpell(pair.Value[i], highSpells[i], ql));
                result[pair.Key] = lerped;
            }

            return result;
        }

        private static ItemSpell LerpSpell(ItemSpell low, ItemSpell high, QlSpan ql)
        {
            ItemSpell copy = low.Copy();
            int reqCount = Math.Min(copy.Requirements.Count, high.Requirements.Count);
            for (int i = 0; i < reqCount; i++)
                copy.Requirements[i].Value = ql.Between(low.Requirements[i].Value, high.Requirements[i].Value);

            int argCount = Math.Min(copy.Arguments.Count, high.Arguments.Count);
            for (int i = 0; i < argCount; i++)
            {
                if (low.Arguments[i] is int lowId && high.Arguments[i] is int highId && IsIdentifierArgument(low, i))
                    copy.Arguments[i] = lowId != 0 ? lowId : highId;
                else if (low.Arguments[i] is int lowInt && high.Arguments[i] is int highInt)
                    copy.Arguments[i] = ql.Between(lowInt, highInt);
                else if (low.Arguments[i] is float lowFloat && high.Arguments[i] is float highFloat)
                    copy.Arguments[i] = lowFloat + (ql.Factor * (highFloat - lowFloat));
            }

            return copy;
        }

        /// <summary>
        /// Arguments that name something (a stat, a damage type) rather than an amount; QL interpolation keeps
        /// them instead of blending. Hit-family [stat, min, max, damage type]: Pulverize tier 2 low has damage type
        /// 91 (MeleeAC) and high 0, which a blend turned into an unknown type 22.
        /// </summary>
        private static bool IsIdentifierArgument(ItemSpell spell, int index)
            => index == 0
               || (index == 3 && (spell.Is(FunctionType.Hit) || spell.Is(FunctionType.SpecialHit)
                   || spell.Is(FunctionType.AreaHit) || spell.Is(FunctionType.DrainHit)));

        private static List<ItemAction> LerpActions(List<ItemAction> low, List<ItemAction> high, QlSpan ql)
        {
            var result = new List<ItemAction>(low.Count);
            foreach (ItemAction lowAction in low)
            {
                ItemAction highAction = high.Find(a => a.ActionType == lowAction.ActionType) ?? lowAction;
                ItemAction copy = lowAction.Copy();
                int reqCount = Math.Min(copy.Requirements.Count, highAction.Requirements.Count);
                for (int i = 0; i < reqCount; i++)
                {
                    copy.Requirements[i].Value = ql.Between(
                        lowAction.Requirements[i].Value,
                        highAction.Requirements[i].Value);
                }

                result.Add(copy);
            }

            return result;
        }

        /// <summary>
        /// The low template's QL, the high template's QL and the item's QL. Integer values blend the way stock does
        /// (Gamecode 100cb909) and the client builds the item: from the smaller value, a half rounds up. A QL 30 Belt
        /// Component Platform (2 slots at QL 20, 3 at QL 40) has 3 slots, where round-half-to-even gave 2.
        /// </summary>
        private readonly struct QlSpan
        {
            private readonly int ql1;
            private readonly int ql2;
            private readonly int ql;

            public QlSpan(int ql1, int ql2, int ql)
            {
                this.ql1 = ql1;
                this.ql2 = ql2;
                this.ql = ql;
            }

            /// <summary>Float arguments only; integer values use <see cref="Between"/>.</summary>
            public float Factor => (ql - ql1) / (float)(ql2 - ql1);

            /// <summary>
            /// Stock 100cb909: the value at ql between v1 (at ql1) and v2 (at ql2). The difference wraps in Int32
            /// as in stock; the step is done in long, so far-apart values (a stat near the Int32 limit on one end)
            /// never throw out of corpse loot creation (2026-10-13: Stim Fiend loot).
            /// </summary>
            public int Between(int v1, int v2)
            {
                ushort q1 = (ushort)ql1, q2 = (ushort)ql2, q = (ushort)ql;
                if (v1 == v2 || q1 == q2 || q1 == q)
                    return v1;
                if (q2 == q)
                    return v2;
                if (v1 < v2)
                    return unchecked(Step(q - q1, v2 - v1, q2 - q1) + v1);
                return unchecked(Step(q - q2, v1 - v2, q1 - q2) + v2);
            }

            /// <summary>trunc(a * b / d + 0.5), exactly: (2ab + d) / (2d), truncating.</summary>
            private static int Step(int a, int b, int d)
            {
                long x = (long)a * b;
                return unchecked((int)((2 * x + d) / (2L * d)));
            }
        }

        private static void ApplyStatsBlob(ItemTemplate definition, byte[]? statsBlob)
        {
            if (statsBlob == null || statsBlob.Length < 8)
                return;

            for (int offset = 0; offset + 8 <= statsBlob.Length; offset += 8)
            {
                var attrId = (CharacterStat)BitConverter.ToInt32(statsBlob, offset);
                int value = BitConverter.ToInt32(statsBlob, offset + 4);
                definition.Stats[attrId] = value;
            }
        }
    }
}
