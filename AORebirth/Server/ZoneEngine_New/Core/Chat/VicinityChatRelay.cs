namespace ZoneEngine_New.Core.Chat
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Playfield;

    public sealed class VicinityChatRelay
    {
        private readonly IChatEngineLink _chatLink;
        private readonly IZoneLogger _logger;

        public VicinityChatRelay(IChatEngineLink chatLink, IZoneLogger logger)
        {
            ArgumentNullException.ThrowIfNull(chatLink);
            ArgumentNullException.ThrowIfNull(logger);
            _chatLink = chatLink;
            _logger = logger;
        }

        public void Relay(Player speaker, TextMessage message)
        {
            ArgumentNullException.ThrowIfNull(speaker);
            ArgumentNullException.ThrowIfNull(message);

            ChatMessage? chat = message.Message;
            if (chat == null || string.IsNullOrEmpty(chat.Text))
                return;

            Modules.ModuleHooks.PlayerSpoke(speaker, chat.Type switch
            {
                ChatMessageType.Say => AORebirth.Modules.Events.ChatChannelKind.Say,
                ChatMessageType.Shout => AORebirth.Modules.Events.ChatChannelKind.Shout,
                ChatMessageType.Whisper => AORebirth.Modules.Events.ChatChannelKind.Whisper,
                _ => AORebirth.Modules.Events.ChatChannelKind.Other
            }, chat.Text);
            Send(speaker, chat.Type, chat.Text, senderName: null);
        }

        /// <summary>
        /// An NPC speaks in vicinity chat (a quest line: Lolly, the Omni-AF Private, the Wounded Dockworker): the
        /// players within the chat type's range get it, as a player's /say.
        /// </summary>
        public void Speak(Character speaker, string text, ChatMessageType type = ChatMessageType.Say)
        {
            ArgumentNullException.ThrowIfNull(speaker);
            if (!string.IsNullOrEmpty(text))
                Send(speaker, type, text, speaker.Name);
        }

        /// <summary>
        /// A line from no one (chat-server type 0x23) to <paramref name="recipients"/>, wherever they are, with the
        /// message's blob (<see cref="PetAnswerBlob"/> for a pet's answer; none for loot rights). False when the chat
        /// server could not be reached.
        /// </summary>
        public bool Announce(string text, IReadOnlyList<int> recipients, byte[]? blob = null)
        {
            ArgumentNullException.ThrowIfNull(recipients);
            return !string.IsNullOrEmpty(text) && recipients.Count > 0
                && _chatLink.SendAnonymousVicinity(text, recipients, blob);
        }

        /// <summary>The blob live sends with a pet's answer to its owner (capture 2026-10-24T02:22).</summary>
        public static readonly byte[] PetAnswerBlob = [0x04];

        /// <summary>
        /// Loot line blobs. The client routes a 0x23 line on its first blob byte (GUI.dll
        /// ChatGUIModule_c::HandleVicinityMessage 0x10086c53): 6 to Vicinity Loot Messages (group 0x4200001B), 7 to Team
        /// Loot Messages (0x4200001A). Live sends one of each per item looted (retail 2026-10-28).
        /// </summary>
        public static readonly byte[] VicinityLootBlob = [0x06];

        public static readonly byte[] TeamLootBlob = [0x07];

        /// <summary>
        /// "<paramref name="looter"/> looted <paramref name="item"/> from <paramref name="corpse"/>.": to the looter's team
        /// (Team Loot Messages) and to the players in say range of the corpse (Vicinity Loot Messages).
        /// </summary>
        public void AnnounceLoot(Player looter, Corpse corpse, Inventory.Item item)
        {
            ArgumentNullException.ThrowIfNull(looter);
            ArgumentNullException.ThrowIfNull(corpse);
            ArgumentNullException.ThrowIfNull(item);

            Playfield? playfield = corpse.Playfield;
            if (playfield == null)
                return;

            string text = Helpers.ClientFeedback.FormatText(
                Helpers.ClientFeedback.LootedItemFrom,
                looter.Name ?? string.Empty,
                item.LowId,
                item.HighId > 0 ? item.HighId : item.LowId,
                item.Quality,
                item.Name ?? string.Empty,
                corpse.Name);

            if (playfield.GetService<Teams.TeamService>()?.GetTeam(looter) is { } team)
                Announce(text, team.MemberIds, TeamLootBlob);

            TryResolveRange(ChatMessageType.Say, out float range);
            var nearby = new List<int>();
            foreach (Player other in playfield.GetRequiredService<DynelRegistry>().PlayerEntities())
            {
                if (corpse.GetEdgeDistanceTo(other) <= range)
                    nearby.Add(other.Identity.Instance);
            }

            Announce(text, nearby, VicinityLootBlob);
        }

        private void Send(Character speaker, ChatMessageType type, string text, string? senderName)
        {
            Playfield? playfield = speaker.Playfield;
            if (playfield == null)
                return;

            if (!TryResolveRange(type, out float range))
                return;

            List<int> recipientIds = new List<int>();
            int senderId = speaker.Identity.Instance;
            if (speaker is Player)
                recipientIds.Add(senderId);

            DynelRegistry registry = playfield.GetRequiredService<DynelRegistry>();
            foreach (Player other in registry.PlayerEntities())
            {
                if (other.Identity.Instance == senderId)
                    continue;
                if (speaker.GetEdgeDistanceTo(other) > range)
                    continue;
                recipientIds.Add(other.Identity.Instance);
            }

            if (recipientIds.Count == 0)
                return;

            if (_chatLink.SendVicinity(senderId, senderName, (byte)type, text, recipientIds))
                return;

            _logger.Warn(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Vicinity chat send failed character={0} type={1} recipients={2}",
                    senderId,
                    type,
                    recipientIds.Count));
        }

        private static bool TryResolveRange(ChatMessageType type, out float range)
        {
            switch (type)
            {
                case ChatMessageType.Whisper:
                    range = 1.5f;
                    return true;
                case ChatMessageType.Say:
                    range = 10.0f;
                    return true;
                case ChatMessageType.Shout:
                    range = 60.0f;
                    return true;
                default:
                    range = 0;
                    return false;
            }
        }
    }
}
