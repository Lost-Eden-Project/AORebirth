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
    }
}
