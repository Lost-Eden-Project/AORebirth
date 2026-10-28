namespace ZoneEngine_New.Core.Configuration
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;

    /// <summary>
    /// Config/GmPermissions.json: the GM level each command needs, by command name (without the dot), and the levels
    /// for actions inside a command. A command or action the file does not list keeps its built-in level, so module
    /// commands and new commands work without an entry. Resolved like Gameplay.json: beside the GameData tree first,
    /// else beside the process. Loaded once at startup.
    /// </summary>
    public sealed class GmPermissions
    {
        public const int CurrentVersion = 1;

        public const string ConfigFileName = "GmPermissions.json";

        /// <summary>Setting another character's stats with .set.</summary>
        public const string SetOtherCharacterAction = "setOtherCharacter";

        static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public int SchemaVersion { get; init; } = CurrentVersion;

        /// <summary>Command name (case does not matter) to the GM level it needs.</summary>
        public Dictionary<string, int> Commands { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Action name to the GM level it needs (<see cref="SetOtherCharacterAction"/>).</summary>
        public Dictionary<string, int> Actions { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The level <paramref name="command"/> needs: the file's, else <paramref name="builtIn"/>.</summary>
        public int CommandLevel(string command, int builtIn)
            => Commands.TryGetValue(command, out int level) ? level : builtIn;

        /// <summary>The level <paramref name="action"/> needs: the file's, else <paramref name="builtIn"/>.</summary>
        public int ActionLevel(string action, int builtIn)
            => Actions.TryGetValue(action, out int level) ? level : builtIn;

        public static string ResolvePath(string gameDataRoot)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(gameDataRoot);
            string? parent = Directory.GetParent(Path.GetFullPath(gameDataRoot))?.FullName;
            if (!string.IsNullOrEmpty(parent))
            {
                string besideGameData = Path.Combine(parent, "Config", ConfigFileName);
                if (File.Exists(besideGameData))
                    return besideGameData;
            }

            return Path.Combine(AppContext.BaseDirectory, "Config", ConfigFileName);
        }

        /// <summary>The file at <paramref name="path"/>; built-in levels for everything when there is none.</summary>
        public static GmPermissions Load(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (!File.Exists(path))
                return new GmPermissions();

            GmPermissions? permissions;
            try
            {
                permissions = JsonSerializer.Deserialize<GmPermissions>(File.ReadAllText(path), JsonOptions);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("GM permissions config is invalid: " + path, exception);
            }

            if (permissions == null)
                throw new InvalidDataException("GM permissions config is empty: " + path);

            // The serializer builds its own dictionaries; command names are matched without regard to case.
            permissions = new GmPermissions
            {
                SchemaVersion = permissions.SchemaVersion,
                Commands = new Dictionary<string, int>(permissions.Commands ?? new(), StringComparer.OrdinalIgnoreCase),
                Actions = new Dictionary<string, int>(permissions.Actions ?? new(), StringComparer.OrdinalIgnoreCase)
            };
            permissions.Validate(path);
            return permissions;
        }

        void Validate(string path)
        {
            if (SchemaVersion != CurrentVersion)
                throw new InvalidDataException(path + ": schemaVersion must be " + CurrentVersion + ".");
            foreach (KeyValuePair<string, int> entry in Commands)
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || entry.Key.StartsWith('.') || entry.Value < 0)
                    throw new InvalidDataException(path + ": commands." + entry.Key + " must be a name without the dot and a level of 0 or more.");
            }

            foreach (KeyValuePair<string, int> entry in Actions)
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value < 0)
                    throw new InvalidDataException(path + ": actions." + entry.Key + " must be a level of 0 or more.");
            }
        }
    }
}
