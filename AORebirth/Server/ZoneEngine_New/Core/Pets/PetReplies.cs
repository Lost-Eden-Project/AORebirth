namespace ZoneEngine_New.Core.Pets
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;

    /// <summary>
    /// GameData/PetReplies.json: what a pet says to its owner, e.g.
    /// <c>{ "Attack": [ "Yes! Let me kill it, master!", "I will kill it for you, master!" ] }</c>. Keys are
    /// <see cref="PetCommandCode"/> names for a command carried out, or an event: <see cref="Summoned"/>,
    /// <see cref="AttackRefused"/> (no valid target), <see cref="HealRefused"/> (a heal pet told to heal what it
    /// cannot). One line of a list is picked at random. Live sends the answer from the chat
    /// server as an anonymous vicinity line to the owner, "&lt;owner&gt;'s pet, &lt;pet&gt;: &lt;line&gt;" with blob 04
    /// (capture 2026-10-24T02:22). A command with no lines gets no answer. Loaded once per GameData root.
    /// </summary>
    public sealed class PetReplyCatalog
    {
        public const string FileName = "PetReplies.json";

        public const string Summoned = "Summoned";

        public const string AttackRefused = "AttackRefused";

        public const string HealRefused = "HealRefused";

        static readonly ConcurrentDictionary<string, PetReplyCatalog> ByRoot = new(StringComparer.OrdinalIgnoreCase);

        readonly Dictionary<string, string[]> _lines;

        PetReplyCatalog(Dictionary<string, string[]> lines)
        {
            _lines = lines;
        }

        public static PetReplyCatalog For(string gameDataRoot)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(gameDataRoot);
            return ByRoot.GetOrAdd(gameDataRoot, Load);
        }

        /// <summary>A random line for a command carried out, or null when it has none.</summary>
        public string? Pick(PetCommandCode command) => Pick(command.ToString());

        /// <summary>A random line for <paramref name="key"/> (a command name or an event), or null when it has none.</summary>
        public string? Pick(string key)
            => _lines.TryGetValue(key, out string[]? lines) && lines.Length > 0
                ? lines[Random.Shared.Next(lines.Length)]
                : null;

        static PetReplyCatalog Load(string root)
        {
            var lines = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(root, FileName);
            if (!File.Exists(path))
                return new PetReplyCatalog(lines);

            Dictionary<string, string[]>? file = JsonSerializer.Deserialize<Dictionary<string, string[]>>(
                File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            foreach (KeyValuePair<string, string[]> entry in file ?? new())
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || entry.Value == null)
                    continue;
                var kept = new List<string>();
                foreach (string line in entry.Value)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        kept.Add(line.Trim());
                }

                if (kept.Count > 0)
                    lines[entry.Key.Trim()] = kept.ToArray();
            }

            return new PetReplyCatalog(lines);
        }
    }
}
