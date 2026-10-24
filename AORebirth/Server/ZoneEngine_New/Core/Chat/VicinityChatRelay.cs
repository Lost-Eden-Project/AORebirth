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
