namespace AORebirth.ChatModules.Channels
{
    using System;
    using System.Collections.Generic;

    /// <summary>Result of <see cref="IChatChannels.Post"/>.</summary>
    public enum ChatPostResult
    {
        Posted = 0,

        /// <summary>No channel has that type and id (a team or raid channel goes away when its last member leaves).</summary>
        ChannelNotFound = 1,

        /// <summary>
        /// The sender name is empty, longer than <see cref="IChatChannels.MaxSenderNameLength"/>, or has control characters.
        /// </summary>
        InvalidSenderName = 2,

        /// <summary>The text is empty or longer than <see cref="IChatChannels.MaxTextLength"/>.</summary>
        InvalidText = 3,

        /// <summary>The engine has handed out every sender identity it keeps for module posts; reuse fewer names.</summary>
        SenderLimitReached = 4,

        /// <summary><see cref="IChatCommand.ReplyPrivate"/>: the issuer is no longer online (1.1).</summary>
        RecipientOffline = 5,
    }

    /// <summary>
    /// The chat engine's channels. Every member is safe from any thread. With a Capabilities entry, watching needs
    /// <c>read-channels</c> and posting needs <c>post-channels</c>.
    /// </summary>
    public interface IChatChannels
    {
        /// <summary>Longest sender name <see cref="Post"/> accepts.</summary>
        int MaxSenderNameLength { get; }

        /// <summary>Longest text <see cref="Post"/> accepts.</summary>
        int MaxTextLength { get; }

        /// <summary>
        /// A snapshot of every channel that exists now: the GameData ChatChannels.json channels, and the organization,
        /// team and raid channels while they exist.
        /// </summary>
        IReadOnlyList<ChatChannelInfo> List();

        /// <summary>The channel with this type and id, or null.</summary>
        ChatChannelInfo? Find(byte type, uint id);

        /// <summary>
        /// Raised for every line a player, a BotService bot or another module says in a channel. Chat commands (lines
        /// starting with '.' or '/') and lines consumed by a module's <see cref="RegisterCommand"/> are not raised. Handlers run on this module's own delivery thread, one line at a
        /// time and in the order the engine accepted them, never on a network thread, so a slow handler delays only
        /// this module. While this module is 4096 lines behind, newer lines are dropped and the drop is logged. A
        /// handler that throws is logged; after 10 failures in a row it is unsubscribed.
        /// </summary>
        event Action<ChatChannelMessage> MessagePosted;

        /// <summary>
        /// Says <paramref name="text"/> in the channel as <paramref name="senderName"/>, to every member, the way the
        /// chat server's bots speak. Each distinct name (ignoring case) gets one sender identity for the life of the
        /// process. The text is sent as given, AO markup included: a module relaying untrusted outside text decides
        /// what it lets through. The line is also raised to the other modules and written to the chat log.
        /// </summary>
        ChatPostResult Post(byte type, uint id, string senderName, string text);

        /// <summary>
        /// Claims a channel command such as <c>!level</c> (1.1). <paramref name="name"/> is one prefix character (any
        /// punctuation or symbol except '.' and '/', which belong to the zone) followed by 1 to 32 letters, digits, '-'
        /// or '_', compared ignoring case. A player line whose first word is the name is consumed: no channel member,
        /// bot or other module sees it, and <paramref name="handler"/> runs on this module's delivery thread with the
        /// command. Unregistered lines, <c>!roll</c> for example, still go out and reach <see cref="MessagePosted"/>.
        /// Only during <see cref="IChatModule.Initialize"/>; a name another module claimed fails this module's load.
        /// Needs <c>consume-commands</c> with a Capabilities entry.
        /// </summary>
        void RegisterCommand(string name, Action<IChatCommand> handler);
    }
}
