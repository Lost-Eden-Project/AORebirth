namespace AORebirth.ChatModules.Channels
{
    using System;

    /// <summary>
    /// A registered command a player typed in a channel (1.1). The engine consumed the line: no channel member, bot or
    /// other module saw it; it is still written to the chat log. Valid for as long as the module keeps it.
    /// </summary>
    public interface IChatCommand
    {
        /// <summary>The channel it was typed in.</summary>
        ChatChannelInfo Channel { get; }

        /// <summary>The issuing character's id.</summary>
        uint SenderId { get; }

        string SenderName { get; }

        /// <summary>The issuing character's GM level when it typed the command; 0 for a player.</summary>
        int SenderGmLevel { get; }

        /// <summary>The command name as registered, for example <c>!level</c>.</summary>
        string Name { get; }

        /// <summary>The text after the command name, trimmed; empty when there is none.</summary>
        string Args { get; }

        /// <summary>The whole line exactly as typed.</summary>
        string Text { get; }

        /// <summary>When the chat engine accepted the line, UTC.</summary>
        DateTime Timestamp { get; }

        /// <summary>
        /// Sends <paramref name="text"/> to the issuer alone, as a tell from <paramref name="senderName"/>. Names and
        /// text follow the <see cref="IChatChannels.Post"/> rules and share its sender identities. Returns
        /// <see cref="ChatPostResult.RecipientOffline"/> when the issuer has logged off.
        /// </summary>
        ChatPostResult ReplyPrivate(string senderName, string text);
    }
}
