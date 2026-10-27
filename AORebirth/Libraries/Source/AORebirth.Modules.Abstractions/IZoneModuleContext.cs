namespace AORebirth.Modules
{
    using AORebirth.Modules.Content;
    using AORebirth.Modules.Events;
    using AORebirth.Modules.Network;
    using AORebirth.Modules.World;

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

        /// <summary>The zone's Knubot dialog scripts: check, reload, and where overrides go (module API 1.1).</summary>
        IKnubotScripts Knubot { get; }

        /// <summary>Read-only snapshots of this zone instance's players and NPCs (module API 1.2).</summary>
        IZoneWorld World { get; }

        /// <summary>Messages and NPC refreshes, run on the owning playfield tick (module API 1.2).</summary>
        IZoneActions Actions { get; }

        /// <summary>Reloadable GameData and the override folder it is read through (module API 1.3).</summary>
        IZoneGameData GameData { get; }

        /// <summary>
        /// Reads Modules/&lt;Id&gt;/config.json (case-insensitive, comments allowed), or returns null when it is absent.
        /// </summary>
        T? GetConfig<T>() where T : class;

        /// <summary>
        /// Installs the byte filter every new zone connection goes through. Only one module may register one; a second
        /// registration fails module startup.
        /// </summary>
        void RegisterConnectionFilter(IConnectionFilterProvider provider);

        /// <summary>
        /// Adds a GM command (module API 1.2). Only during <see cref="IZoneModule.Initialize"/>. A name that a built-in
        /// command or another module already uses fails this module's startup (the zone's, when the module is Required).
        /// </summary>
        void RegisterGmCommand(Commands.ModuleGmCommand command);

        /// <summary>
        /// Installs the hook that decides on login handoffs to this zone (module API 1.4). Only during
        /// <see cref="IZoneModule.Initialize"/>, and only one module may register one; a second registration fails that
        /// module's startup.
        /// </summary>
        void RegisterHandoffHook(IHandoffHook hook);
    }
}
