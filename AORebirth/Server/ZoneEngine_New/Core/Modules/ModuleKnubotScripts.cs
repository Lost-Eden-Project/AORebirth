namespace ZoneEngine_New.Core.Modules
{
    using System;
    using System.Collections.Generic;

    using AORebirth.Modules.Content;

    using ZoneEngine_New.Core.Knubot;

    /// <summary>
    /// The module API's view of the zone's <see cref="KnubotCatalog"/>. The catalog is resolved on first use: modules
    /// initialize while the service container is still building it.
    /// </summary>
    internal sealed class ModuleKnubotScripts : IKnubotScripts
    {
        private readonly Lazy<KnubotCatalog> _catalog;

        public ModuleKnubotScripts(Func<KnubotCatalog> catalog)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            _catalog = new Lazy<KnubotCatalog>(catalog);
        }

        public string? OverrideDirectory => _catalog.Value.OverrideRoot;

        public IReadOnlyList<string> Check(string scriptId, string json) => _catalog.Value.Check(scriptId, json);

        public KnubotReloadResult Reload()
        {
            KnubotLoadResult result = _catalog.Value.Reload();
            return new KnubotReloadResult(result.Loaded, result.Npcs, result.Problems);
        }
    }
}
