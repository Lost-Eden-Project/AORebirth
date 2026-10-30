namespace ZoneEngine_New.Core.DebugMcp
{
    using System;
    using System.Text.Json;

    using AORebirth.Modules.Content;

    using ModelContextProtocol.Server;

    using ZoneEngine_New.Core.GameData;

    /// <summary>
    /// Reloads a registered GameData catalog (NpcTemplates, ItemTemplates, MonsterCombatRules, ...) from the zone's
    /// GameData root while it runs, through <see cref="GameDataReloadRegistry"/>. Already spawned NPCs keep the template
    /// they spawned with; the next spawn uses the reloaded one.
    /// </summary>
    [McpServerToolType]
    public sealed class GameDataReloadTools
    {
        static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        readonly GameDataReloadRegistry _registry;
        readonly DebugMcpEndpoint _endpoint;

        public GameDataReloadTools(GameDataReloadRegistry registry, DebugMcpEndpoint endpoint)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(endpoint);
            _registry = registry;
            _endpoint = endpoint;
        }

        [McpServerTool(Name = "gamedata_reload")]
        [System.ComponentModel.Description("WRITE: reloads one GameData catalog from disk by name (e.g. NpcTemplates, ItemTemplates, MonsterCombatRules, NpcScripts, Knubot); an empty name lists the reloadable names. A file that fails to read keeps the old data. Spawned NPCs keep their template until they respawn. Needs DebugMcp AllowWrites.")]
        public string Reload(string name = "")
        {
            if (string.IsNullOrWhiteSpace(name))
                return Json(new { reloadable = _registry.Names });
            if (!_endpoint.AllowWrites)
                return Json(new { error = "Write tools are off. Set DebugMcp AllowWrites to true in the zone config and restart the zone engine." });

            GameDataReloadResult result = _registry.Reload(name);
            return Json(new { name = result.Name, ok = result.Ok, entries = result.Entries, problems = result.Problems });
        }

        static string Json(object value) => JsonSerializer.Serialize(value, JsonOptions);
    }
}
