namespace AORebirth.ChatModules.Channels
{
    using System;

    /// <summary>Who spoke a <see cref="ChatChannelMessage"/>.</summary>
    public enum ChatMessageSource
    {
        /// <summary>A logged-in character. <see cref="ChatChannelMessage.SenderId"/> is the character id.</summary>
        Player = 0,

        /// <summary>A BotService bot. <see cref="ChatChannelMessage.SenderId"/> is the bot's chat wire id.</summary>
        Bot = 1,

        /// <summary>
        /// Another chat module's <see cref="IChatChannels.Post"/>; <see cref="ChatChannelMessage.SourceModuleId"/> names
        /// it. A module is never told about its own posts.
        /// </summary>
        Module = 2,
    }

    /// <summary>A line said in a chat channel. Immutable; safe to keep and hand to other threads.</summary>
    public sealed class ChatChannelMessage
    {
        public ChatChannelMessage(ChatChannelInfo channel, ChatMessageSource source, uint senderId, string senderName,
            string text, DateTime timestamp, string? sourceModuleId)
        {
            Channel = channel ?? throw new ArgumentNullException(nameof(channel));
            Source = source;
            SenderId = senderId;
            SenderName = senderName ?? string.Empty;
            Text = text ?? string.Empty;
            Timestamp = timestamp;
            SourceModuleId = sourceModuleId;
        }

        public ChatChannelInfo Channel { get; }

        public ChatMessageSource Source { get; }

        public uint SenderId { get; }

        public string SenderName { get; }

        /// <summary>The text exactly as the client or poster sent it, including any AO markup.</summary>
        public string Text { get; }

        /// <summary>When the chat engine accepted the line, UTC.</summary>
        public DateTime Timestamp { get; }

        /// <summary>The posting module's id when <see cref="Source"/> is <see cref="ChatMessageSource.Module"/>, else null.</summary>
        public string? SourceModuleId { get; }
    }
}
