namespace AORebirth.Tools.RDBDataExtractor
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;

    using AODB.Common.RDBObjects;
    using AODB.Common.Spells;

    using MsgPack;

    using ZoneEngine_New.Core.Inventory.Dat;

    using ZeEventType = AORebirth.Enums.EventType;
    using ZeActionType = AORebirth.Enums.ActionType;
    using ZeOperator = AORebirth.Enums.Operator;
    using ZeItemTarget = AORebirth.Enums.ItemTarget;

    /// <summary>
    /// Turns an AODB item or nano template into <see cref="DatItemTemplate"/>: the positional
    /// layout ZoneEngine consumes, plus the whole template in <see cref="DatItemTemplate.Rdb"/>.
    /// </summary>
    internal static class ItemRdbMapper
    {
        private const int QualityStat = 54;
        private const int FlagsStat = 0;
        private const int MultipleCountStat = 412;

        /// <summary>Record type, id and version ahead of every RDB record's payload.</summary>
        private const int RecordHeaderSize = 12;

        /// <summary>Key 4 SkillPst list keys holding the attack and defense skills.</summary>
        private const int AttackSkillList = 0xc;
        private const int DefenseSkillList = 0xd;

        /// <summary>Spell stats every spell format starts with (SpellFormat_c ctor).</summary>
        private const int TickCountSpellStat = 3;
        private const int TickIntervalSpellStat = 4;
        private const int TargetSpellStat = 0x20;
        private const int BaseArgumentCount = 4;

        /// <summary>Spell argument complex type holding a four-character tag (item hash).</summary>
        private const int HashComplexType = 6;

        private const int SpecialCriteriaSpellType = 0xcf17;
        private const int ExpressionSpellType = 0xcf20;

        /// <summary>Stock TextureSpellFormat_c's own arguments, in its read order.</summary>
        private const int TextureSpellStat = 0x39;
        private const int TextureBodyPartSpellStat = 0x38;
        private const int TextureSeeSkinSpellStat = 0x3a;
        private const int TextureArg1SpellStat = 0x42;
        private const int TextureArg2SpellStat = 0x43;

        private static readonly Dictionary<int, string> FunctionSets = LoadFunctionSets();

        /// <param name="item">The template as the client builds it (class defaults included).</param>
        /// <param name="fileStats">
        /// The same record read as a plain <see cref="ItemBase"/>, so its stat lists hold only what
        /// the file holds; <see cref="DatItemTemplate.Stats"/> has always been the file's stats.
        /// </param>
        /// <param name="record">The raw record, header included.</param>
        internal static DatItemTemplate Map(ItemBase item, ItemBase fileStats, byte[] record)
        {
            if (item == null)
                throw new ArgumentNullException("item");
            if (fileStats == null)
                throw new ArgumentNullException("fileStats");

            var template = new DatItemTemplate
            {
                ID = item.RecordId,
                DynelType = item.TemplateType,
                Quality = 1,
                Name = item.Name,
                Rdb = ToRdb(item),
            };

            if ((!item.ReadComplete || item.BytesLeft > 0) && record != null && record.Length > RecordHeaderSize)
            {
                template.Rdb.Record = new byte[record.Length - RecordHeaderSize];
                Array.Copy(record, RecordHeaderSize, template.Rdb.Record, 0, template.Rdb.Record.Length);
            }

            MapStats(template, fileStats.Stats18);
            MapStats(template, fileStats.Stats2B);
            MapStats(template, fileStats.Stats);
            MapSkillList(template.Attack, item.SkillPstLists, AttackSkillList);
            MapSkillList(template.Defend, item.SkillPstLists, DefenseSkillList);
            MapEvents(template, item.Events);
            MapActions(template, item.ActionCriteria);
            return template;
        }

        static void MapStats(DatItemTemplate template, List<KeyValuePair<int, int>> stats)
        {
            foreach (KeyValuePair<int, int> pair in stats)
            {
                template.Stats[pair.Key] = pair.Value;
                if (pair.Key == QualityStat && pair.Value > 0)
                    template.Quality = pair.Value;
                else if (pair.Key == FlagsStat)
                    template.Flags = pair.Value;
                else if (pair.Key == MultipleCountStat)
                    template.MultipleCount = pair.Value;
            }
        }

        /// <summary>The first value for each skill wins, as the previous reader kept it.</summary>
        static void MapSkillList(
            Dictionary<int, int> target,
            Dictionary<int, List<KeyValuePair<int, int>>> lists,
            int key)
        {
            if (!lists.TryGetValue(key, out List<KeyValuePair<int, int>> list))
                return;

            foreach (KeyValuePair<int, int> pair in list)
            {
                if (!target.ContainsKey(pair.Key))
                    target[pair.Key] = pair.Value;
            }
        }

        static void MapEvents(DatItemTemplate template, Dictionary<int, List<SpellData>> events)
        {
            foreach (KeyValuePair<int, List<SpellData>> eventPair in events)
            {
                var functions = new List<DatFunction>();
                foreach (SpellData spell in eventPair.Value)
                    functions.Add(ToFunction(spell));

                if (functions.Count == 0)
                    continue;

                template.Events.Add(new DatEvent
                {
                    EventType = (ZeEventType)eventPair.Key,
                    Functions = functions,
                });
            }
        }

        static void MapActions(DatItemTemplate template, Dictionary<int, List<Criterion>> actions)
        {
            foreach (KeyValuePair<int, List<Criterion>> actionPair in actions)
            {
                if (actionPair.Value.Count == 0)
                    continue;

                template.Actions.Add(new DatAction
                {
                    ActionType = (ZeActionType)actionPair.Key,
                    Requirements = ToRequirements(actionPair.Value),
                });
            }
        }

        static List<DatRequirement> ToRequirements(List<Criterion> criteria)
        {
            var requirements = new List<DatRequirement>(criteria.Count);
            foreach (Criterion criterion in criteria)
            {
                requirements.Add(new DatRequirement
                {
                    Statnumber = criterion.Value1,
                    Value = criterion.Value2,
                    Operator = (ZeOperator)criterion.Operator,
                    Target = ZeItemTarget.Self,
                });
            }

            return requirements;
        }

        internal static DatFunction ToFunction(SpellData spell)
        {
            var function = new DatFunction
            {
                FunctionType = spell.TypeId,
                TickCount = 1,
                TickInterval = 0,
                Target = 0,
                Arguments = new DatFunctionArguments(),
                Requirements = ToRequirements(spell.Criteria),
            };

            if (spell.TryGetStat(TickCountSpellStat, out int duration) && duration > 0)
                function.TickCount = duration;
            if (spell.TryGetStat(TickIntervalSpellStat, out int interval))
                function.TickInterval = unchecked((uint)interval);
            if (spell.TryGetStat(TargetSpellStat, out int target))
                function.Target = target;

            foreach (MessagePackObject value in SelectPositionalArgs(spell.TypeId, SpellArguments(spell)))
                function.Arguments.Values.Add(value);

            return function;
        }

        /// <summary>
        /// The spell's own arguments in the order its format reads them (the four every format
        /// starts with are the tick count, interval, target and target list above). Special
        /// criteria and an expression follow as packed arrays.
        /// </summary>
        static List<MessagePackObject> SpellArguments(SpellData spell)
        {
            var values = new List<MessagePackObject>();
            SpellFormat format = SpellFormats.Get(spell.TypeId);
            if (format != null)
            {
                for (int i = BaseArgumentCount; i < format.Arguments.Count; i++)
                {
                    SpellArgument argument = format.Arguments[i];
                    if (argument.IsString)
                    {
                        values.Add(spell.Strings[argument.Index] ?? string.Empty);
                        continue;
                    }

                    int value = spell.TryGetStat(argument.Stat, out int stat) ? stat : argument.DefaultInteger;
                    values.Add(argument.ComplexType == HashComplexType ? HashTag(value) : value);
                }

                // TextureSpellFormat_c reads its arguments in its own override.
                if (format.Arguments.Count == BaseArgumentCount && spell.TryGetStat(TextureSpellStat, out int texture))
                {
                    values.Add(texture);
                    values.Add(SpellStatOrZero(spell, TextureBodyPartSpellStat));
                    values.Add(SpellStatOrZero(spell, TextureSeeSkinSpellStat));
                    int arg1 = SpellStatOrZero(spell, TextureArg1SpellStat);
                    int arg2 = SpellStatOrZero(spell, TextureArg2SpellStat);
                    if (arg1 != 0 || arg2 != 0)
                    {
                        values.Add(arg1);
                        values.Add(arg2);
                    }
                }
            }

            if (spell.TypeId == SpecialCriteriaSpellType && spell.SpecialCriteria.Count > 0)
                values.Add(PackCriteria(spell.SpecialCriteria));
            if (spell.TypeId == ExpressionSpellType)
                values.Add(PackExpression(spell.Expression));

            return values;
        }

        static int SpellStatOrZero(SpellData spell, int stat) => spell.TryGetStat(stat, out int value) ? value : 0;

        /// <summary>A tag such as an item hash: its four bytes as text, high byte first.</summary>
        static string HashTag(int value)
        {
            uint hash = unchecked((uint)value);
            return new string(new[]
            {
                (char)(hash >> 24),
                (char)((hash >> 16) & 0xff),
                (char)((hash >> 8) & 0xff),
                (char)(hash & 0xff),
            });
        }

        /// <summary>
        /// Legacy FunctionSets.cfg describes the positional consumer layout: typed tokens take
        /// values in read order, and <c>x</c> tokens skip that many bytes of values (4 bytes per
        /// skipped int). Values the format does not name are still appended.
        /// </summary>
        internal static List<MessagePackObject> SelectPositionalArgs(int functionType, List<MessagePackObject> values)
        {
            var result = new List<MessagePackObject>();
            var queue = new Queue<MessagePackObject>(values);

            if (FunctionSets.TryGetValue(functionType, out string format) && !string.IsNullOrWhiteSpace(format))
            {
                string[] tokens = format.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < tokens.Length; i++)
                {
                    string token = tokens[i].Trim().ToLowerInvariant();
                    if (token.Length < 2)
                        continue;

                    char kind = token[token.Length - 1];
                    if (!int.TryParse(token.Substring(0, token.Length - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
                        || count < 0)
                        continue;

                    if (kind == 'x')
                    {
                        int skipValues = count / 4;
                        for (int skipped = 0; skipped < skipValues && queue.Count > 0; skipped++)
                            queue.Dequeue();
                        continue;
                    }

                    for (int taken = 0; taken < count; taken++)
                    {
                        if (queue.Count == 0)
                            break;

                        MessagePackObject value = queue.Dequeue();
                        if (kind == 's' && value.IsTypeOf<string>() == true)
                            value = value.AsString().TrimEnd('\0');
                        result.Add(value);
                    }
                }
            }

            while (queue.Count > 0)
                result.Add(queue.Dequeue());

            return result;
        }

        static DatRdbTemplate ToRdb(ItemBase item)
        {
            var rdb = new DatRdbTemplate
            {
                TemplateType = item.TemplateType,
                Description = item.Description,
                Stats = ToPairs(item.Stats),
                Stats18 = ToPairs(item.Stats18),
                Stats2B = ToPairs(item.Stats2B),
                Pairs1B = ToPairs(item.Pairs1B),
                Pairs09 = ToPairs(item.Pairs09),
                Entries78 = ToPairs(item.Entries78),
                Entries88 = ToPairs(item.Entries88),
                Flags4C = item.Flags4C,
                ReadComplete = item.ReadComplete,
                ReadStopReason = item.ReadStopReason,
                BytesLeft = item.BytesLeft,
            };

            foreach (KeyValuePair<int, int> stat in item.LiveStats.All())
                rdb.LiveStats[stat.Key] = stat.Value;

            foreach (KeyValuePair<int, List<KeyValuePair<int, int>>> list in item.SkillPstLists)
                rdb.SkillPstLists[list.Key] = ToPairs(list.Value);

            foreach (KeyValuePair<int, List<SpellData>> eventPair in item.Events)
            {
                var spells = new List<DatSpell>(eventPair.Value.Count);
                foreach (SpellData spell in eventPair.Value)
                    spells.Add(ToSpell(spell));
                rdb.Events[eventPair.Key] = spells;
            }

            foreach (KeyValuePair<int, List<Criterion>> action in item.ActionCriteria)
                rdb.ActionCriteria[action.Key] = ToCriteria(action.Value);

            foreach (ShopItem line in item.ShopInventory)
            {
                rdb.ShopInventory.Add(new DatShopItem
                {
                    Hash = line.Hash,
                    MinQl = line.MinQl,
                    MaxQl = line.MaxQl,
                    Amount = line.Amount,
                    Regen = line.Regen,
                    Extra = line.Extra,
                    Interval = line.Interval,
                    Unknown1 = line.Unknown1,
                    Unknown2 = line.Unknown2,
                    Cost = line.Cost,
                });
            }

            if (item is NanoObject nano)
            {
                rdb.Nano = new DatRdbNano
                {
                    BuffTotalTime = nano.BuffTotalTime,
                    FormulaRadius = nano.FormulaRadius,
                    FieldAC = nano.FieldAC,
                    FieldB0 = nano.FieldB0,
                    FieldB4 = nano.FieldB4,
                    FieldB8 = nano.FieldB8,
                    FieldBC = nano.FieldBC,
                };
            }

            return rdb;
        }

        static DatSpell ToSpell(SpellData spell)
        {
            var result = new DatSpell
            {
                TypeId = spell.TypeId,
                Header2 = spell.Header2,
                Version = spell.Version,
                Strings = new List<string>(spell.Strings),
                Criteria = ToCriteria(spell.Criteria),
                SpecialCriteria = ToCriteria(spell.SpecialCriteria),
                Expression = PackExpression(spell.Expression),
            };

            foreach (KeyValuePair<int, int> stat in spell.Stats)
                result.Stats[stat.Key] = stat.Value;

            return result;
        }

        static List<DatIntPair> ToPairs(List<KeyValuePair<int, int>> pairs)
        {
            var result = new List<DatIntPair>(pairs.Count);
            foreach (KeyValuePair<int, int> pair in pairs)
                result.Add(new DatIntPair { Key = pair.Key, Value = pair.Value });
            return result;
        }

        static List<DatCriterion> ToCriteria(List<Criterion> criteria)
        {
            var result = new List<DatCriterion>(criteria.Count);
            foreach (Criterion criterion in criteria)
            {
                result.Add(new DatCriterion
                {
                    Value1 = criterion.Value1,
                    Value2 = criterion.Value2,
                    Operator = criterion.Operator,
                });
            }

            return result;
        }

        /// <summary>[1, float] leaf, [2, stat, flag] stat leaf, [3 or 4, operator, left, right].</summary>
        static MessagePackObject PackExpression(ExpressionData expression)
        {
            switch (expression)
            {
                case LeafExpressionData leaf:
                    return PackArray(new MessagePackObject(1), new MessagePackObject(leaf.Value));
                case LeafStatExpressionData stat:
                    return PackArray(
                        new MessagePackObject(2),
                        new MessagePackObject(stat.Stat),
                        new MessagePackObject(stat.Flag ? 1 : 0));
                case BinaryExpressionData binary:
                    return PackArray(
                        new MessagePackObject(binary is FunctionExpressionData ? 4 : 3),
                        new MessagePackObject(binary.Operator),
                        PackExpression(binary.Left),
                        PackExpression(binary.Right));
                default:
                    return MessagePackObject.Nil;
            }
        }

        static MessagePackObject PackCriteria(List<Criterion> criteria)
        {
            var packed = new MessagePackObject[criteria.Count];
            for (int i = 0; i < criteria.Count; i++)
            {
                packed[i] = PackArray(
                    new MessagePackObject(criteria[i].Value1),
                    new MessagePackObject(criteria[i].Value2),
                    new MessagePackObject(criteria[i].Operator));
            }

            return new MessagePackObject(packed);
        }

        static MessagePackObject PackArray(params MessagePackObject[] values)
        {
            return new MessagePackObject(values);
        }

        static Dictionary<int, string> LoadFunctionSets()
        {
            var result = new Dictionary<int, string>();
            string path = ResolveFunctionSetsPath();
            if (path == null || !File.Exists(path))
                throw new InvalidOperationException("FunctionSets.cfg was not found next to RDBDataExtractor.");

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                int split = line.IndexOf('=');
                if (split <= 0)
                    continue;

                if (!int.TryParse(line.Substring(0, split), NumberStyles.Integer, CultureInfo.InvariantCulture, out int functionType))
                    continue;

                result[functionType] = line.Substring(split + 1).Trim();
            }

            return result;
        }

        static string ResolveFunctionSetsPath()
        {
            string baseDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "FunctionSets.cfg"),
                Path.Combine(baseDir, "..", "..", "..", "FunctionSets.cfg"),
                Path.Combine(RepositoryRootResolver.Resolve(), "Tools", "RDBDataExtractor", "FunctionSets.cfg"),
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                string full = Path.GetFullPath(candidates[i]);
                if (File.Exists(full))
                    return full;
            }

            return null;
        }
    }
}
