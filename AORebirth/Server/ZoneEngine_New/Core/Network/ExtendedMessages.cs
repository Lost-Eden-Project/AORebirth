namespace ZoneEngine_New.Core.Network
{
    using System;
    using System.Collections.Generic;

    using LostEden.Protocol;
    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages;
    using LostEden.Protocol.Messages.N3Messages;

    /// <summary>
    /// N3 type ids of the server's own messages, beyond the stock protocol: "LEI" + n, the range Lost Eden's messages
    /// use (0x4C454901/02 are the ICC shop's, handled by its module). The stock client neither sends nor reads them; an
    /// updated client does. Every body is big-endian after the usual N3 header, whose identity is the client's
    /// character. A string is a 16-bit byte length and that many printable ASCII bytes, no terminator.
    /// </summary>
    public static class ExtendedMessageType
    {
        /// <summary>
        /// Client to server: an updated client says it reads these messages (<see cref="ClientHelloMessage"/>). The
        /// leader sets the loot order with stock's CharacterAction 0x7F (/team loot), not with a message of ours.
        /// </summary>
        public const int ClientHello = 0x4C454903;

        /// <summary>Server to client: the team's loot mode (<see cref="TeamLootModeMessage"/>).</summary>
        public const int TeamLootMode = 0x4C454904;

        /// <summary>Server to client: a Need or Greed roll opened for an item (<see cref="LootRollStartMessage"/>).</summary>
        public const int LootRollStart = 0x4C454905;

        /// <summary>Client to server: Need, Greed or Pass on an open roll (<see cref="LootRollChoiceMessage"/>).</summary>
        public const int LootRollChoice = 0x4C454906;

        /// <summary>Server to client: who won a roll and what everyone rolled (<see cref="LootRollResultMessage"/>).</summary>
        public const int LootRollResult = 0x4C454907;

        /// <summary>Client to server: name (or clear the name of) one of the player's bags (<see cref="BagNameSetMessage"/>).</summary>
        public const int BagNameSet = 0x4C454908;

        /// <summary>Server to client: a bag's stored name (<see cref="BagNameMessage"/>).</summary>
        public const int BagName = 0x4C454909;
    }

    /// <summary>Registers <see cref="ExtendedMessageType"/>'s messages with the codec (once per process; safe to repeat).</summary>
    public static class ExtendedMessages
    {
        static readonly object Sync = new();
        static bool _registered;

        public static void Register()
        {
            lock (Sync)
            {
                if (_registered)
                    return;
                Add(ExtendedMessageType.ClientHello, () => new ClientHelloMessage());
                Add(ExtendedMessageType.TeamLootMode, () => new TeamLootModeMessage());
                Add(ExtendedMessageType.LootRollStart, () => new LootRollStartMessage());
                Add(ExtendedMessageType.LootRollChoice, () => new LootRollChoiceMessage());
                Add(ExtendedMessageType.LootRollResult, () => new LootRollResultMessage());
                Add(ExtendedMessageType.BagNameSet, () => new BagNameSetMessage());
                Add(ExtendedMessageType.BagName, () => new BagNameMessage());
                _registered = true;
            }
        }

        static void Add(int type, Func<N3Message> factory)
        {
            if (!MessageRegistry.HasN3(type))
                MessageRegistry.RegisterN3(type, factory);
        }

        /// <summary>The longest string these messages carry (a bag name, an item name, a character name).</summary>
        public const int MaxTextLength = 255;

        internal static void WriteText(PacketWriter w, string? text)
        {
            text ??= string.Empty;
            if (text.Length > MaxTextLength)
                text = text[..MaxTextLength];
            var bytes = new byte[text.Length];
            for (int i = 0; i < text.Length; i++)
                bytes[i] = text[i] is >= ' ' and <= '~' ? (byte)text[i] : (byte)'?';
            w.WriteUInt16((ushort)bytes.Length);
            w.WriteBytes(bytes);
        }

        internal static string ReadText(ref SpanReader r)
        {
            int length = r.ReadUInt16();
            return length > MaxTextLength ? r.ReadString(MaxTextLength) : r.ReadString(length);
        }
    }

    /// <summary>A team's loot rule.</summary>
    public enum TeamLootMode
    {
        /// <summary>Whoever the corpse is open to takes what they like (the default).</summary>
        FreeForAll = 0,

        /// <summary>Every item is rolled for: Need beats Greed, the highest roll wins, Pass leaves it.</summary>
        NeedOrGreed = 1,
    }

    /// <summary>A member's answer to a loot roll.</summary>
    public enum LootRollChoice
    {
        Pass = 0,
        Need = 1,
        Greed = 2,
    }

    /// <summary>
    /// Wire: int version (1). An updated client sends it after every FullCharacter (login, zoning, reconnect). Only a
    /// client that said hello is sent the server's own messages; the server then sends it the team's loot order and the
    /// player's bag names. The stock client never sends it and only gets chat lines.
    /// </summary>
    public sealed class ClientHelloMessage : N3Message
    {
        public ClientHelloMessage() : base((N3MessageType)ExtendedMessageType.ClientHello)
        {
        }

        public int Version { get; set; } = 1;

        protected override void ReadBody(ref SpanReader r) => Version = r.ReadInt32();

        protected override void WriteBody(PacketWriter w) => w.WriteInt32(Version);
    }

    /// <summary>
    /// The loot order values of stock's /team loot (CharacterAction 0x7F, Parameter2; the client's word table at
    /// Gamecode 10040949), plus the server's own Need or Greed.
    /// </summary>
    public static class TeamLootOrder
    {
        /// <summary>"/team loot all": free for all.</summary>
        public const int All = 3;

        /// <summary>"/team loot leader": stock's, not offered by this server.</summary>
        public const int Leader = 4;

        /// <summary>"/team loot alpha": stock's, not offered by this server.</summary>
        public const int Alpha = 5;

        /// <summary>"/team loot needgreed": Need or Greed rolls. Ours; an updated client adds the word.</summary>
        public const int NeedGreed = 6;

        /// <summary>The order value for a mode.</summary>
        public static int Of(TeamLootMode mode) => mode == TeamLootMode.NeedOrGreed ? NeedGreed : All;
    }

    /// <summary>
    /// Wire: int loot order (<see cref="TeamLootOrder"/>: 3 all, 6 Need or Greed), int the leader who just set it (0
    /// when the server is only telling a member the team's current order: on joining, zoning, logging in, or answering
    /// /team loot). A player who leaves the team gets 3 (all) with 0.
    /// </summary>
    public sealed class TeamLootModeMessage : N3Message
    {
        public TeamLootModeMessage() : base((N3MessageType)ExtendedMessageType.TeamLootMode)
        {
        }

        public int Order { get; set; }

        public int SetBy { get; set; }

        protected override void ReadBody(ref SpanReader r)
        {
            Order = r.ReadInt32();
            SetBy = r.ReadInt32();
        }

        protected override void WriteBody(PacketWriter w)
        {
            w.WriteInt32(Order);
            w.WriteInt32(SetBy);
        }
    }

    /// <summary>
    /// Wire: int roll id, identity corpse (int type, int instance), int low id, int high id, int quality, int stack
    /// count, int seconds left to answer, string item name.
    /// </summary>
    public sealed class LootRollStartMessage : N3Message
    {
        public LootRollStartMessage() : base((N3MessageType)ExtendedMessageType.LootRollStart)
        {
        }

        public int RollId { get; set; }

        public Identity Corpse { get; set; }

        public int LowId { get; set; }

        public int HighId { get; set; }

        public int Quality { get; set; }

        public int Count { get; set; }

        public int Seconds { get; set; }

        public string ItemName { get; set; } = string.Empty;

        protected override void ReadBody(ref SpanReader r)
        {
            RollId = r.ReadInt32();
            Corpse = r.ReadIdentity();
            LowId = r.ReadInt32();
            HighId = r.ReadInt32();
            Quality = r.ReadInt32();
            Count = r.ReadInt32();
            Seconds = r.ReadInt32();
            ItemName = ExtendedMessages.ReadText(ref r);
        }

        protected override void WriteBody(PacketWriter w)
        {
            w.WriteInt32(RollId);
            w.WriteIdentity(Corpse);
            w.WriteInt32(LowId);
            w.WriteInt32(HighId);
            w.WriteInt32(Quality);
            w.WriteInt32(Count);
            w.WriteInt32(Seconds);
            ExtendedMessages.WriteText(w, ItemName);
        }
    }

    /// <summary>Wire: int roll id, int choice (<see cref="LootRollChoice"/>). The first answer to a roll is final.</summary>
    public sealed class LootRollChoiceMessage : N3Message
    {
        public LootRollChoiceMessage() : base((N3MessageType)ExtendedMessageType.LootRollChoice)
        {
        }

        public int RollId { get; set; }

        public LootRollChoice Choice { get; set; }

        protected override void ReadBody(ref SpanReader r)
        {
            RollId = r.ReadInt32();
            Choice = (LootRollChoice)r.ReadInt32();
        }

        protected override void WriteBody(PacketWriter w)
        {
            w.WriteInt32(RollId);
            w.WriteInt32((int)Choice);
        }
    }

    /// <summary>One member's part in a finished roll.</summary>
    public readonly record struct LootRollEntry(int CharacterId, LootRollChoice Choice, int Roll);

    /// <summary>
    /// Wire: int roll id, int winner character (0 when nobody won: everyone passed, or no winner could take it),
    /// int the winner's choice, int the winning roll (1-100), string the winner's name, int entry count, then per entry
    /// int character, int choice, int roll (0 for a pass).
    /// </summary>
    public sealed class LootRollResultMessage : N3Message
    {
        public const int MaxEntries = 300;

        public LootRollResultMessage() : base((N3MessageType)ExtendedMessageType.LootRollResult)
        {
        }

        public int RollId { get; set; }

        public int Winner { get; set; }

        public LootRollChoice WinnerChoice { get; set; }

        public int WinnerRoll { get; set; }

        public string WinnerName { get; set; } = string.Empty;

        public IReadOnlyList<LootRollEntry> Entries { get; set; } = [];

        protected override void ReadBody(ref SpanReader r)
        {
            RollId = r.ReadInt32();
            Winner = r.ReadInt32();
            WinnerChoice = (LootRollChoice)r.ReadInt32();
            WinnerRoll = r.ReadInt32();
            WinnerName = ExtendedMessages.ReadText(ref r);
            int count = Math.Clamp(r.ReadInt32(), 0, MaxEntries);
            var entries = new List<LootRollEntry>(count);
            for (int i = 0; i < count; i++)
                entries.Add(new LootRollEntry(r.ReadInt32(), (LootRollChoice)r.ReadInt32(), r.ReadInt32()));
            Entries = entries;
        }

        protected override void WriteBody(PacketWriter w)
        {
            w.WriteInt32(RollId);
            w.WriteInt32(Winner);
            w.WriteInt32((int)WinnerChoice);
            w.WriteInt32(WinnerRoll);
            ExtendedMessages.WriteText(w, WinnerName);
            int count = Math.Min(Entries.Count, MaxEntries);
            w.WriteInt32(count);
            for (int i = 0; i < count; i++)
            {
                w.WriteInt32(Entries[i].CharacterId);
                w.WriteInt32((int)Entries[i].Choice);
                w.WriteInt32(Entries[i].Roll);
            }
        }
    }

    /// <summary>
    /// Wire: identity bag (int type 0xC749 Container, int instance: the bag's identity as FullCharacter lists it),
    /// string name (1-31 printable ASCII; empty clears the name).
    /// </summary>
    public sealed class BagNameSetMessage : N3Message
    {
        public BagNameSetMessage() : base((N3MessageType)ExtendedMessageType.BagNameSet)
        {
        }

        public Identity Bag { get; set; }

        public string Name { get; set; } = string.Empty;

        protected override void ReadBody(ref SpanReader r)
        {
            Bag = r.ReadIdentity();
            Name = ExtendedMessages.ReadText(ref r);
        }

        protected override void WriteBody(PacketWriter w)
        {
            w.WriteIdentity(Bag);
            ExtendedMessages.WriteText(w, Name);
        }
    }

    /// <summary>
    /// Wire: identity bag (0xC749 Container, instance), string name (empty: the bag has no name, or lost it). Sent for
    /// every named bag after the character's FullCharacter (login, zoning, reconnect) and as the answer to a
    /// <see cref="BagNameSetMessage"/> (the name the server kept, or empty when it refused).
    /// </summary>
    public sealed class BagNameMessage : N3Message
    {
        public BagNameMessage() : base((N3MessageType)ExtendedMessageType.BagName)
        {
        }

        public Identity Bag { get; set; }

        public string Name { get; set; } = string.Empty;

        protected override void ReadBody(ref SpanReader r)
        {
            Bag = r.ReadIdentity();
            Name = ExtendedMessages.ReadText(ref r);
        }

        protected override void WriteBody(PacketWriter w)
        {
            w.WriteIdentity(Bag);
            ExtendedMessages.WriteText(w, Name);
        }
    }
}
