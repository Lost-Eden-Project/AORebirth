#nullable enable

namespace ZoneEngine_New.Core.Inventory.Dat
{
    using System;
    using System.Collections.Generic;

    using AORebirth.Enums;

    using MsgPack;
    using MsgPack.Serialization;

    /// <summary>
    /// MessagePack mirror of legacy Core.ItemTemplate for items.dat slices.
    /// Member ids 0-12 pin the alphabetical order existing items.dat files were written in;
    /// <see cref="Rdb"/> carries everything else AODB reads, so new data needs no exporter change;
    /// new fields go on the end so those files still unpack with the new field left empty.
    /// </summary>
    [Serializable]
    public sealed class DatItemTemplate
    {
        [MessagePackMember(1)]
        public Dictionary<int, int> Attack = new();

        [MessagePackMember(2)]
        public Dictionary<int, int> Defend = new();

        [MessagePackMember(5)]
        public int Flags;

        [MessagePackMember(6)]
        public int ID;

        [MessagePackMember(7)]
        public int ItemType;

        [MessagePackMember(8)]
        public int MultipleCount;

        [MessagePackMember(9)]
        public int Nothing;

        [MessagePackMember(10)]
        public int Quality;

        [MessagePackMember(11)]
        public List<int> Relations = new();

        [MessagePackMember(12)]
        public Dictionary<int, int> Stats = new();

        [MessagePackMember(0)]
        public List<DatAction> Actions { get; set; } = new();

        [MessagePackMember(4)]
        public List<DatEvent> Events { get; set; } = new();

        /// <summary>RDB record DynelType (e.g. Container = 51017).</summary>
        [MessagePackMember(3)]
        public int DynelType;

        /// <summary>RDB item or nano name. Null in files exported before names were written.</summary>
        [MessagePackMember(13)]
        public string? Name;

        /// <summary>
        /// The whole AODB template as the client reads it: every block, the live stats, and the
        /// spells with all their stats and strings. Null in files exported before it was written.
        /// </summary>
        [MessagePackMember(14)]
        public DatRdbTemplate? Rdb;
    }

    /// <summary>AODB <c>ItemBase</c> (and <c>NanoObject</c>) field for field.</summary>
    [Serializable]
    public sealed class DatRdbTemplate
    {
        /// <summary>The template's leading type value (AODB <c>TemplateType</c>).</summary>
        [MessagePackMember(0)]
        public int TemplateType;

        [MessagePackMember(1)]
        public string? Description;

        /// <summary>The client's live stats after the read: class defaults with the template applied.</summary>
        [MessagePackMember(2)]
        public Dictionary<int, int> LiveStats = new();

        /// <summary>Key 0xf subkey 0x17, in file order.</summary>
        [MessagePackMember(3)]
        public List<DatIntPair> Stats = new();

        /// <summary>Key 0xf subkey 0x18.</summary>
        [MessagePackMember(4)]
        public List<DatIntPair> Stats18 = new();

        /// <summary>Key 0xf subkey 0x2b.</summary>
        [MessagePackMember(5)]
        public List<DatIntPair> Stats2B = new();

        /// <summary>Key 4: SkillPst lists by key (0xc attack, 0xd defense).</summary>
        [MessagePackMember(6)]
        public Dictionary<int, List<DatIntPair>> SkillPstLists = new();

        /// <summary>Key 6 subkey 0x1b.</summary>
        [MessagePackMember(7)]
        public List<DatIntPair> Pairs1B = new();

        /// <summary>Key 6 subkey 9.</summary>
        [MessagePackMember(8)]
        public List<DatIntPair> Pairs09 = new();

        /// <summary>Keys 0xe and 0x12.</summary>
        [MessagePackMember(9)]
        public List<DatIntPair> Entries78 = new();

        /// <summary>Key 0x14 subkey 5.</summary>
        [MessagePackMember(10)]
        public List<DatIntPair> Entries88 = new();

        /// <summary>Key 2: spells per event.</summary>
        [MessagePackMember(11)]
        public Dictionary<int, List<DatSpell>> Events = new();

        /// <summary>Key 0x16 subkey 0x24: criteria per action.</summary>
        [MessagePackMember(12)]
        public Dictionary<int, List<DatCriterion>> ActionCriteria = new();

        /// <summary>Key 0x17 subkey 0x25: vendor stock.</summary>
        [MessagePackMember(13)]
        public List<DatShopItem> ShopInventory = new();

        [MessagePackMember(14)]
        public int Flags4C;

        [MessagePackMember(15)]
        public bool ReadComplete;

        [MessagePackMember(16)]
        public string? ReadStopReason;

        [MessagePackMember(17)]
        public long BytesLeft;

        /// <summary>Nano-only fields; null for items.</summary>
        [MessagePackMember(18)]
        public DatRdbNano? Nano;

        /// <summary>
        /// The record's bytes after its 12-byte RDB header, kept only when the read stopped early
        /// (<see cref="ReadComplete"/> false or <see cref="BytesLeft"/> above 0) so the blocks the
        /// client reader skips are not lost.
        /// </summary>
        [MessagePackMember(19)]
        public byte[]? Record;
    }

    [Serializable]
    public sealed class DatRdbNano
    {
        [MessagePackMember(0)]
        public int BuffTotalTime;

        [MessagePackMember(1)]
        public int FormulaRadius;

        [MessagePackMember(2)]
        public int FieldAC;

        [MessagePackMember(3)]
        public float FieldB0;

        [MessagePackMember(4)]
        public int FieldB4;

        [MessagePackMember(5)]
        public int FieldB8;

        [MessagePackMember(6)]
        public bool FieldBC;
    }

    [Serializable]
    public sealed class DatIntPair
    {
        [MessagePackMember(0)]
        public int Key;

        [MessagePackMember(1)]
        public int Value;
    }

    /// <summary>AODB <c>Criterion</c>: two values and the criteria operator.</summary>
    [Serializable]
    public sealed class DatCriterion
    {
        [MessagePackMember(0)]
        public int Value1;

        [MessagePackMember(1)]
        public int Value2;

        [MessagePackMember(2)]
        public int Operator;
    }

    /// <summary>AODB <c>SpellData</c>: the spell type, header, every spell stat and string.</summary>
    [Serializable]
    public sealed class DatSpell
    {
        [MessagePackMember(0)]
        public int TypeId;

        [MessagePackMember(1)]
        public int Header2;

        [MessagePackMember(2)]
        public int Version;

        /// <summary>Spell stats by SpellStat id, the spell's arguments included.</summary>
        [MessagePackMember(3)]
        public Dictionary<int, int> Stats = new();

        [MessagePackMember(4)]
        public List<string> Strings = new();

        [MessagePackMember(5)]
        public List<DatCriterion> Criteria = new();

        /// <summary>Read only for spell type 0xcf17.</summary>
        [MessagePackMember(6)]
        public List<DatCriterion> SpecialCriteria = new();

        /// <summary>
        /// Spell type 0xcf20's expression, nil otherwise. Packed as arrays: [1, float] leaf,
        /// [2, stat, flag] stat leaf, [3 or 4, operator, left, right] compound or function.
        /// </summary>
        [MessagePackMember(7)]
        public MessagePackObject Expression;
    }

    /// <summary>AODB <c>ShopItem</c>: one vendor stock line.</summary>
    [Serializable]
    public sealed class DatShopItem
    {
        [MessagePackMember(0)]
        public uint Hash;

        [MessagePackMember(1)]
        public int MinQl;

        [MessagePackMember(2)]
        public int MaxQl;

        [MessagePackMember(3)]
        public int Amount;

        [MessagePackMember(4)]
        public int Regen;

        /// <summary>Present only when both qualities are 0.</summary>
        [MessagePackMember(5)]
        public uint? Extra;

        [MessagePackMember(6)]
        public int Interval;

        [MessagePackMember(7)]
        public int Unknown1;

        [MessagePackMember(8)]
        public int Unknown2;

        [MessagePackMember(9)]
        public uint Cost;
    }

    [Serializable]
    public sealed class DatEvent
    {
        public EventType EventType { get; set; }

        public List<DatFunction> Functions { get; set; } = new();
    }

    [Serializable]
    public sealed class DatFunction
    {
        public DatFunctionArguments Arguments { get; set; } = new();

        public int FunctionType { get; set; }

        public List<DatRequirement> Requirements { get; set; } = new();

        public int Target { get; set; }

        public int TickCount { get; set; }

        public uint TickInterval { get; set; }

        public bool dolocalstats { get; set; } = true;
    }

    [Serializable]
    public sealed class DatFunctionArguments : IPackable, IUnpackable
    {
        public List<MessagePackObject> Values { get; set; } = new();

        public void PackToMessage(Packer packer, PackingOptions options)
        {
            packer.PackArrayHeader(Values.Count);
            foreach (MessagePackObject value in Values)
                packer.Pack(value);
        }

        public void UnpackFromMessage(Unpacker unpacker)
        {
            Values = new List<MessagePackObject>();
            if (!unpacker.IsArrayHeader)
                return;

            long count = unpacker.LastReadData.AsInt64();
            for (int i = 0; i < count; i++)
            {
                unpacker.Read();
                Values.Add(unpacker.LastReadData);
            }
        }
    }

    [Serializable]
    public sealed class DatAction
    {
        public ActionType ActionType { get; set; }

        public List<DatRequirement> Requirements { get; set; } = new();
    }

    [Serializable]
    public sealed class DatRequirement
    {
        public Operator ChildOperator { get; set; }

        public Operator Operator { get; set; }

        public int Statnumber { get; set; }

        public ItemTarget Target { get; set; }

        public int Value { get; set; }
    }
}
