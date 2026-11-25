namespace ZoneEngine_New.Core.Network
{
    using LostEden.Protocol.Extensions;

    /// <summary>
    /// N3 type ids of Lost Eden's own messages, beyond the stock protocol: "LEI" + n. The messages themselves (client
    /// hello, team loot, bag names, player shops) are LostEden.Protocol.Extensions', the library the zone server and the
    /// Lost Eden client share (namespace LostEden.Protocol.Messages.N3Messages); 0x4C454901/02 are the ICC shop's,
    /// handled by its module. The stock client neither sends nor reads them; only a client that said hello is sent them.
    /// </summary>
    public static class ExtendedMessageType
    {
        /// <summary>Client to server: an updated client says it reads these messages (ClientHelloMessage).</summary>
        public const int ClientHello = LostEdenMessageType.ClientHello;

        /// <summary>Server to client: the team's loot mode (TeamLootModeMessage).</summary>
        public const int TeamLootMode = LostEdenMessageType.TeamLootMode;

        /// <summary>Server to client: a Need or Greed roll opened for an item (LootRollStartMessage).</summary>
        public const int LootRollStart = LostEdenMessageType.LootRollStart;

        /// <summary>Client to server: Need, Greed or Pass on an open roll (LootRollChoiceMessage).</summary>
        public const int LootRollChoice = LostEdenMessageType.LootRollChoice;

        /// <summary>Server to client: who won a roll and what everyone rolled (LootRollResultMessage).</summary>
        public const int LootRollResult = LostEdenMessageType.LootRollResult;

        /// <summary>Client to server: name (or clear the name of) one of the player's bags (BagNameSetMessage).</summary>
        public const int BagNameSet = LostEdenMessageType.BagNameSet;

        /// <summary>Server to client: a bag's stored name (BagNameMessage).</summary>
        public const int BagName = LostEdenMessageType.BagName;

        /// <summary>Client to server: a player shop action (PlayerShopActionMessage).</summary>
        public const int PlayerShopAction = LostEdenMessageType.PlayerShopAction;

        /// <summary>Server to client: a player shop's contents as the viewer may see them (PlayerShopViewMessage).</summary>
        public const int PlayerShopView = LostEdenMessageType.PlayerShopView;

        /// <summary>Server to client: how a player shop action went (PlayerShopResultMessage).</summary>
        public const int PlayerShopResult = LostEdenMessageType.PlayerShopResult;

        /// <summary>Server to client: a player shop terminal's name and owner (PlayerShopNameMessage).</summary>
        public const int PlayerShopName = LostEdenMessageType.PlayerShopName;
    }

    /// <summary>Registers Lost Eden's messages with the codec (once per process; safe to repeat).</summary>
    public static class ExtendedMessages
    {
        public static void Register() => LostEdenMessages.Register();
    }
}
