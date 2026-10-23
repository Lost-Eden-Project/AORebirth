namespace ZoneEngine_New.Core.Modules
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    using AORebirth.Core.GameData;
    using AORebirth.Modules.Content;
    using AORebirth.Modules.World;

    using ZoneEngine_New.Core.GameData;

    /// <summary>
    /// <see cref="IZoneGameData"/> over the zone's <see cref="GameDataReloadRegistry"/>. A reload reads its files on a
    /// thread-pool worker, never on a playfield tick. The registry is resolved on first use: modules initialize while the
    /// container is still building the catalogs.
    /// </summary>
    internal sealed class ModuleGameData : IZoneGameData
    {
        private readonly Lazy<GameDataReloadRegistry> _registry;

        public ModuleGameData(Func<GameDataReloadRegistry> registry)
        {
            ArgumentNullException.ThrowIfNull(registry);
            _registry = new Lazy<GameDataReloadRegistry>(registry);
        }

        public string? OverrideDirectory => GameDataPaths.ResolveOverrideRoot();

        public IReadOnlyList<string> Reloadable => _registry.Value.Names;

        public Task<GameDataReloadResult> Reload(string name)
            => Task.Run(() => _registry.Value.Reload(name));
    }

    /// <summary>Reload needs <c>gamedata-reload</c>; reading the override folder and the names needs nothing.</summary>
    internal sealed class GatedGameData(IZoneGameData gameData, CapabilityGate gate) : IZoneGameData
    {
        public string? OverrideDirectory => gameData.OverrideDirectory;

        public IReadOnlyList<string> Reloadable => gameData.Reloadable;

        public Task<GameDataReloadResult> Reload(string name)
        {
            gate.Require(ModuleCapabilities.GameDataReload);
            return gameData.Reload(name);
        }
    }
}
