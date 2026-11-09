namespace AORebirth.ChatModules
{
    using AORebirth.ChatModules.Channels;

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
        /// Reads ChatModules/&lt;Id&gt;/config.json (case-insensitive, comments allowed), or returns null when it is absent.
        /// </summary>
        T? GetConfig<T>() where T : class;
    }
}
