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

        /// <summary>
        /// The module holds its fixed listener ports, single-writer files and world-wide timers only while
        /// IZoneModuleContext.IsPrimary (module API 1.19), so it can run on two instances during a zone swap.
        /// </summary>
        public bool PrimaryAware { get; init; }
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

        /// <summary>
        /// Where each loaded generation's code is copied: env AO_REBIRTH_MODULE_STAGING_DIR, else this, else the user's
        /// private data folder. It must be writable by nobody else (checked at startup), since the zone runs what is there.
        /// </summary>
        public string? StagingDirectory { get; init; }

        /// <summary>
        /// The operator request file: env AO_REBIRTH_MODULE_RELOAD_REQUEST_FILE, else this, else the user's private data
        /// folder. Its folder must be writable by nobody else (checked at startup).
        /// </summary>
        public string? RequestFile { get; init; }

        /// <summary>Unloads that left their code in memory before this process refuses any further reload.</summary>
        public int MaxLeaks { get; init; } = 2;

        /// <summary>How long the old version's Shutdown may take.</summary>
        public int ShutdownTimeoutSeconds { get; init; } = 10;

        public string ResolveStagingDirectory()
            => Pick(Environment.GetEnvironmentVariable(StagingVariable), StagingDirectory)
                ?? Path.Combine(PrivateRoot(), "module-staging");

        public string ResolveRequestFile()
            => Pick(Environment.GetEnvironmentVariable(RequestFileVariable), RequestFile)
                ?? Path.Combine(PrivateRoot(), "module-reload-request");

        static string? Pick(string? first, string? second)
            => !string.IsNullOrWhiteSpace(first) ? Path.GetFullPath(first.Trim())
                : !string.IsNullOrWhiteSpace(second) ? Path.GetFullPath(second.Trim()) : null;

        /// <summary>The account's own data folder (never the shared temp folder, where anyone could plant files).</summary>
        static string PrivateRoot()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
                throw new InvalidOperationException("No private data folder (HOME is unset); set " + StagingVariable + " and "
                    + RequestFileVariable + " to a folder only the zone's account can write.");
            return Path.Combine(local, "AORebirth");
        }

        /// <summary>
        /// Creates <paramref name="directory"/> for this account only and refuses one others can write: whatever is there
        /// becomes code the zone runs, or a request it follows.
        /// </summary>
        public static void EnsurePrivateDirectory(string directory)
        {
            if (OperatingSystem.IsWindows())
            {
                System.IO.Directory.CreateDirectory(directory);
                return;
            }

            const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            System.IO.Directory.CreateDirectory(directory, OwnerOnly);
            if ((File.GetUnixFileMode(directory) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new InvalidOperationException(directory + " is writable by other accounts; module hot reload stays off.");
            if (new DirectoryInfo(directory).LinkTarget != null)
                throw new InvalidOperationException(directory + " is a symbolic link; module hot reload stays off.");
        }
    }
}
