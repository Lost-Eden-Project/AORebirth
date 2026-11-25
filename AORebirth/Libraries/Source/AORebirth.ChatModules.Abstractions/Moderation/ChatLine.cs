namespace AORebirth.ChatModules.Moderation
{
    using System;

    using AORebirth.ChatModules.Channels;

    /// <summary>Where a <see cref="ChatLine"/> was said (1.2).</summary>
    public enum ChatLineScope
    {
        /// <summary>A chat channel: <see cref="ChatLine.Channel"/> names it (Global, Shopping, team, raid, organization...).</summary>
        Channel = 0,

        /// <summary>Vicinity chat: <see cref="ChatLine.VicinityType"/> says whether it was a say, whisper or shout.</summary>
        Vicinity = 1,
    }

    /// <summary>The kind of vicinity line (1.2); the value is the chat type byte on the wire.</summary>
    public enum VicinityChatType : byte
    {
        Say = 0,
        Whisper = 1,
        Shout = 2,
    }

    /// <summary>
    /// A player's line before anyone receives it (1.2), handed to each <see cref="IChatLineFilter"/> in turn. Immutable.
    /// </summary>
    public sealed class ChatLine
    {
        public ChatLine(ChatLineScope scope, ChatChannelInfo? channel, VicinityChatType vicinityType, uint senderId,
            string senderName, int senderGmLevel, string text, string originalText, DateTime timestamp)
        {
            if (scope == ChatLineScope.Channel && channel == null)
            {
                throw new ArgumentNullException(nameof(channel), "A channel line names its channel.");
            }

            Scope = scope;
            Channel = scope == ChatLineScope.Channel ? channel : null;
            VicinityType = vicinityType;
            SenderId = senderId;
            SenderName = senderName ?? string.Empty;
            SenderGmLevel = senderGmLevel;
            Text = text ?? string.Empty;
            OriginalText = originalText ?? string.Empty;
            Timestamp = timestamp;
        }

        public ChatLineScope Scope { get; }

        /// <summary>The channel for <see cref="ChatLineScope.Channel"/>; null for vicinity.</summary>
        public ChatChannelInfo? Channel { get; }

        /// <summary>Say, whisper or shout for <see cref="ChatLineScope.Vicinity"/>; <see cref="VicinityChatType.Say"/> otherwise.</summary>
        public VicinityChatType VicinityType { get; }

        /// <summary>The speaking character's id.</summary>
        public uint SenderId { get; }

        public string SenderName { get; }

        /// <summary>The speaker's GM level when the line was said; 0 for a player.</summary>
        public int SenderGmLevel { get; }

        /// <summary>The text as it stands now: an earlier module's filter may already have replaced words.</summary>
        public string Text { get; }

        /// <summary>The text exactly as the player sent it, AO markup included.</summary>
        public string OriginalText { get; }

        /// <summary>When the chat engine accepted the line, UTC.</summary>
        public DateTime Timestamp { get; }
    }
}
