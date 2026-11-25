namespace AORebirth.ChatModules
{
    using AORebirth.ChatModules.Channels;
    using AORebirth.ChatModules.Moderation;
    using AORebirth.ChatModules.Players;

    /// <summary>What the host offers a module during <see cref="IChatModule.Initialize"/>.</summary>
    public interface IChatModuleContext
    {
        /// <summary>The module id from its module.json.</summary>
        string ModuleId { get; }

        /// <summary>The module's own folder (ChatModules/&lt;Id&gt;).</summary>
        string ModuleDirectory { get; }

        IChatModuleLogger Log { get; }

        /// <summary>The chat channels: list them, watch what is said in them, and post into them.</summary>
        IChatChannels Channels { get; }

        /// <summary>
        /// The characters logged in to chat: find them by id or name and send them system lines (1.2). A host older than
        /// 1.2 throws <see cref="System.NotSupportedException"/>.
        /// </summary>
        IChatPlayers Players => throw new System.NotSupportedException("Chat players need a chat module API 1.2 host.");

        /// <summary>
        /// Reads ChatModules/&lt;Id&gt;/config.json (case-insensitive, comments allowed), or returns null when it is absent.
        /// </summary>
        T? GetConfig<T>() where T : class;

        /// <summary>
        /// Adds a filter that sees every player channel line and vicinity say, whisper and shout before delivery, and may
        /// replace or block it (1.2). Only during <see cref="IChatModule.Initialize"/>; a module may register several.
        /// Needs <c>filter-chat</c> with a Capabilities entry. A host older than 1.2 throws
        /// <see cref="System.NotSupportedException"/>.
        /// </summary>
        void RegisterLineFilter(IChatLineFilter filter) =>
            throw new System.NotSupportedException("Chat line filters need a chat module API 1.2 host.");
    }
}
