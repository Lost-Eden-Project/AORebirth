namespace ZoneEngine_New.Core.Modules
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;

    /// <summary>
    /// Config/Modules.json: which external modules ZoneEngine_New loads. Resolved like Gameplay.json (a deployment's own
    /// Config folder beside the GameData tree first, else beside the process). No file means no modules.
    /// </summary>
    public sealed class ModulesSettings
    {
        public const string ConfigFileName = "Modules.json";

        internal static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        /// <summary>Module ids to load, in load order.</summary>
        public List<string> Enabled { get; init; } = new();

        /// <summary>Enabled module ids whose load failure stops zone startup instead of skipping the module.</summary>
        public List<string> Required { get; init; } = new();

        /// <summary>Modules folder; a relative path is taken from the folder that holds Config. Default "Modules".</summary>
        public string? Directory { get; init; }

        /// <summary>
        /// Module API 1.2 capabilities by module id (see AORebirth.Modules.World.ModuleCapabilities). A module with an entry
        /// may use only what it lists; a module without one keeps 1.1 behaviour and may use everything.
        /// </summary>
        public Dictionary<string, List<string>>? Capabilities { get; init; }

        /// <summary>Module hot reload; off unless this section says Enabled. See <see cref="HotReloadSettings"/>.</summary>
        public HotReloadSettings? HotReload { get; init; }

        public static string? ResolvePath(string gameDataRoot)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(gameDataRoot);
            string? parent = System.IO.Directory.GetParent(Path.GetFullPath(gameDataRoot))?.FullName;
            if (!string.IsNullOrEmpty(parent))
            {
                string besideGameData = Path.Combine(parent, "Config", ConfigFileName);
                if (File.Exists(besideGameData))
                    return besideGameData;
            }

            string besideProcess = Path.Combine(AppContext.BaseDirectory, "Config", ConfigFileName);
            return File.Exists(besideProcess) ? besideProcess : null;
        }

        public static ModulesSettings Load(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<ModulesSettings>(stream, JsonOptions)
                ?? throw new InvalidDataException(ConfigFileName + " is empty: " + path);
        }

        /// <summary>The modules folder for the Modules.json at <paramref name="settingsPath"/> (…/Config/Modules.json).</summary>
        public string ResolveModulesDirectory(string settingsPath)
        {
            string configDirectory = Path.GetDirectoryName(Path.GetFullPath(settingsPath))!;
            string root = System.IO.Directory.GetParent(configDirectory)?.FullName ?? configDirectory;
            string directory = string.IsNullOrWhiteSpace(Directory) ? "Modules" : Directory;
            return Path.GetFullPath(Path.Combine(root, directory));
        }
    }

    /// <summary>Modules/&lt;Id&gt;/module.json.</summary>
    public sealed class ModuleManifest
    {
        public const string FileName = "module.json";

        public string Id { get; init; } = string.Empty;

        /// <summary>The module's entry assembly, relative to its folder.</summary>
        public string Assembly { get; init; } = string.Empty;

        public string Version { get; init; } = string.Empty;

        /// <summary>
        /// Operations the module invokes (module API 1.5). Each must exist on this engine and be granted by Modules.json
        /// Capabilities, or the module does not load.
        /// </summary>
        public List<string>? Operations { get; init; }

        /// <summary>
        /// Capabilities the module cannot work without (module API 1.14), e.g. <c>read-sessions</c>. Each must be a known
        /// capability granted by Modules.json Capabilities, or the module does not load, before it initializes.
        /// </summary>
        public List<string>? Capabilities { get; init; }

        /// <summary>
        /// The module may be unloaded and loaded again while the zone runs (module API 1.18). It must stop everything it
        /// started (threads, timers, listeners) in Shutdown; see <c>IZoneModuleStateTransfer</c> to carry state across.
        /// </summary>
        public bool SupportsHotReload { get; init; }
    }

    /// <summary>
    /// Modules.json "HotReload". With Enabled false (the default) every module loads as it always did and nothing can be
    /// reloaded. With Enabled true, a module whose module.json sets SupportsHotReload loads from a staged copy of its code
    /// into a collectible context and can be reloaded with the GM command <c>.module reload</c> or the request file.
    /// </summary>
    public sealed class HotReloadSettings
    {
        public const string StagingVariable = "AO_REBIRTH_MODULE_STAGING_DIR";

        public const string RequestFileVariable = "AO_REBIRTH_MODULE_RELOAD_REQUEST_FILE";

        public bool Enabled { get; init; }

        /// <summary>Where each loaded generation's code is copied; env AO_REBIRTH_MODULE_STAGING_DIR, else this, else the temp folder.</summary>
        public string? StagingDirectory { get; init; }

        /// <summary>The operator request file; env AO_REBIRTH_MODULE_RELOAD_REQUEST_FILE, else this, else the temp folder.</summary>
        public string? RequestFile { get; init; }

        /// <summary>Unloads that left their code in memory before this process refuses any further reload.</summary>
        public int MaxLeaks { get; init; } = 2;

        /// <summary>How long the old version's Shutdown may take.</summary>
        public int ShutdownTimeoutSeconds { get; init; } = 10;

        public string ResolveStagingDirectory()
            => Pick(Environment.GetEnvironmentVariable(StagingVariable), StagingDirectory)
                ?? Path.Combine(Path.GetTempPath(), "aorebirth-modules");

        public string ResolveRequestFile()
            => Pick(Environment.GetEnvironmentVariable(RequestFileVariable), RequestFile)
                ?? Path.Combine(Path.GetTempPath(), "aorebirth-module-reload-request");

        static string? Pick(string? first, string? second)
            => !string.IsNullOrWhiteSpace(first) ? Path.GetFullPath(first.Trim())
                : !string.IsNullOrWhiteSpace(second) ? Path.GetFullPath(second.Trim()) : null;
    }
}
