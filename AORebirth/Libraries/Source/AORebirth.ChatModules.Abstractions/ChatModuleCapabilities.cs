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
    }
}
