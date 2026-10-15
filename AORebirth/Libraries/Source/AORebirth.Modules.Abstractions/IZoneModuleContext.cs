namespace AORebirth.Modules
{
    using AORebirth.Modules.Events;
    using AORebirth.Modules.Network;

    /// <summary>What the host offers a module during <see cref="IZoneModule.Initialize"/>.</summary>
    public interface IZoneModuleContext
    {
        /// <summary>The module id from its module.json.</summary>
        string ModuleId { get; }

        /// <summary>The module's own folder (Modules/&lt;Id&gt;).</summary>
        string ModuleDirectory { get; }

        IModuleLogger Log { get; }

        /// <summary>Gameplay events. Handlers run synchronously on the playfield's tick thread.</summary>
        IZoneEvents Events { get; }

        /// <summary>
        /// Reads Modules/&lt;Id&gt;/config.json (case-insensitive, comments allowed), or returns null when it is absent.
        /// </summary>
        T? GetConfig<T>() where T : class;

        /// <summary>
        /// Installs the byte filter every new zone connection goes through. Only one module may register one; a second
        /// registration fails module startup.
        /// </summary>
        void RegisterConnectionFilter(IConnectionFilterProvider provider);
    }
}
