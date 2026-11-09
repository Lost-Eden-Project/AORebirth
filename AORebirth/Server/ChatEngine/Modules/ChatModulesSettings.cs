namespace ChatEngine.Modules
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;

    /// <summary>
    /// Config/ChatModules.json: which external modules ChatEngine loads. Resolved like ZoneEngine_New's Modules.json: a
    /// deployment's own Config folder beside the GameData tree first, else beside the process. No file means no modules.
    /// </summary>
    public sealed class ChatModulesSettings
    {
        public const string ConfigFileName = "ChatModules.json";

        internal static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>Module ids to load, in load order.</summary>
        public List<string> Enabled { get; set; } = new List<string>();

        /// <summary>Enabled module ids whose load failure stops chat startup instead of skipping the module.</summary>
        public List<string> Required { get; set; } = new List<string>();

        /// <summary>Modules folder; a relative path is taken from the folder that holds Config. Default "ChatModules".</summary>
        public string Directory { get; set; }

        /// <summary>
        /// Capabilities by module id (see AORebirth.ChatModules.ChatModuleCapabilities). A module with an entry may use
        /// only what it lists; a module without one may use everything.
        /// </summary>
        public Dictionary<string, List<string>> Capabilities { get; set; }

        public static string ResolvePath(string gameDataRoot)
        {
            if (!string.IsNullOrWhiteSpace(gameDataRoot))
            {
                string parent = System.IO.Directory.GetParent(Path.GetFullPath(gameDataRoot))?.FullName;
                if (!string.IsNullOrEmpty(parent))
                {
                    string besideGameData = Path.Combine(parent, "Config", ConfigFileName);
                    if (File.Exists(besideGameData))
                    {
                        return besideGameData;
                    }
                }
            }

            string besideProcess = Path.Combine(AppContext.BaseDirectory, "Config", ConfigFileName);
            return File.Exists(besideProcess) ? besideProcess : null;
        }

        public static ChatModulesSettings Load(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                return JsonSerializer.Deserialize<ChatModulesSettings>(stream, JsonOptions)
                    ?? throw new InvalidDataException(ConfigFileName + " is empty: " + path);
            }
        }

        /// <summary>The modules folder for the ChatModules.json at <paramref name="settingsPath"/> (…/Config/ChatModules.json).</summary>
        public string ResolveModulesDirectory(string settingsPath)
        {
            string configDirectory = Path.GetDirectoryName(Path.GetFullPath(settingsPath));
            string root = System.IO.Directory.GetParent(configDirectory)?.FullName ?? configDirectory;
            string directory = string.IsNullOrWhiteSpace(this.Directory) ? "ChatModules" : this.Directory;
            return Path.GetFullPath(Path.Combine(root, directory));
        }
    }

    /// <summary>ChatModules/&lt;Id&gt;/module.json.</summary>
    public sealed class ChatModuleManifest
    {
        public const string FileName = "module.json";

        public string Id { get; set; } = string.Empty;

        /// <summary>The module's entry assembly, relative to its folder.</summary>
        public string Assembly { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;
    }
}
