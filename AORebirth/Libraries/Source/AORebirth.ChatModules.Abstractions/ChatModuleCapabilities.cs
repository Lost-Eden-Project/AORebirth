namespace AORebirth.ChatModules
{
    /// <summary>
    /// Capability names for Config/ChatModules.json <c>Capabilities</c>. A module with an entry may use only what it
    /// lists, and any other member throws <see cref="System.UnauthorizedAccessException"/>; a module without an entry may
    /// use everything.
    /// </summary>
    public static class ChatModuleCapabilities
    {
        /// <summary><see cref="Channels.IChatChannels.MessagePosted"/>: see what is said in channels.</summary>
        public const string ReadChannels = "read-channels";

        /// <summary><see cref="Channels.IChatChannels.Post"/>: speak into channels.</summary>
        public const string PostChannels = "post-channels";

        /// <summary>
        /// <see cref="Channels.IChatChannels.RegisterCommand"/> and <see cref="Channels.IChatCommand.ReplyPrivate"/>:
        /// hide matching channel lines and answer the issuer privately (1.1).
        /// </summary>
        public const string ConsumeCommands = "consume-commands";

        /// <summary>
        /// <see cref="IChatModuleContext.RegisterLineFilter"/>: replace or block player channel and vicinity lines before
        /// delivery (1.2).
        /// </summary>
        public const string FilterChat = "filter-chat";

        /// <summary><see cref="Players.IChatPlayers.SendSystemMessage"/>: system lines to one online character (1.2).</summary>
        public const string MessagePlayers = "message-players";
    }
}
