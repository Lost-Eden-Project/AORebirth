namespace ZoneEngine_New.Core.Mobs
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;

    /// <summary>
    /// GameData/MonsterCombatRules.json: combat behaviour keyed by mob hash, e.g.
    /// <c>{ "ASSU": { "Pets": { "MaxPets": 2 } }, "NEBG": { "Features": [ "Guard" ] }, "MUSN": { "Hates": [ 42 ] } }</c>.
    /// Keys are NpcTemplates.json leaf hashes. Loaded once per GameData root (through the GameData override folder) and
    /// swapped whole by <see cref="Reload"/>: a lookup sees the old rules or the new, never a mix. NPCs already spawned
    /// keep what they read at spawn until they respawn.
    /// </summary>
    public sealed class MonsterCombatRulesCatalog
    {
        public const string FileName = "MonsterCombatRules.json";

        /// <summary>The name <see cref="Reload"/> is registered under.</summary>
        public const string ReloadName = "MonsterCombatRules";

        static readonly ConcurrentDictionary<string, Holder> ByRoot = new(StringComparer.OrdinalIgnoreCase);

        static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        static readonly System.Threading.Lock WriteLock = new();

        readonly Dictionary<string, MonsterCombatRules> _rules;

        MonsterCombatRulesCatalog(Dictionary<string, MonsterCombatRules> rules)
        {
            _rules = rules;
        }

        public int Count => _rules.Count;

        /// <summary>The current rules for <paramref name="gameDataRoot"/>; take it once per lookup.</summary>
        public static MonsterCombatRulesCatalog For(string gameDataRoot)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(gameDataRoot);
            return ByRoot.GetOrAdd(gameDataRoot, root => new Holder(Load(root))).Current;
        }

        /// <summary>
        /// Reads the file again and swaps it in for <paramref name="gameDataRoot"/>. A file that fails to read or parse
        /// keeps the current rules and reports why. Call off the playfield tick: it reads the file.
        /// </summary>
        public static AORebirth.Modules.Content.GameDataReloadResult Reload(string gameDataRoot)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(gameDataRoot);
            MonsterCombatRulesCatalog loaded;
            try
            {
                loaded = Load(gameDataRoot);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                or NotSupportedException)
            {
                int kept = ByRoot.TryGetValue(gameDataRoot, out Holder? old) ? old.Current.Count : 0;
                return new AORebirth.Modules.Content.GameDataReloadResult(ReloadName, false, kept,
                    [FileName + ": " + exception.Message]);
            }

            Holder holder = ByRoot.GetOrAdd(gameDataRoot, _ => new Holder(loaded));
            holder.Current = loaded;
            return new AORebirth.Modules.Content.GameDataReloadResult(ReloadName, true, loaded.Count, []);
        }

        /// <summary>
        /// Adds <paramref name="feature"/> to <paramref name="hash"/>'s "Features" in the MonsterCombatRules.json this root
        /// reads (the override folder's copy when it has one), or removes it, then reloads the rules. The file is written
        /// back in its own layout (2-space JSON, its newline, its key order; a new hash goes in sorted place when the keys
        /// are sorted); a hash left with no rules is dropped. The previous file is kept beside it as
        /// MonsterCombatRules.json.bak. One edit at a time per process. Call off the playfield tick: it reads and writes
        /// the file. NPCs already spawned keep their features until they respawn.
        /// </summary>
        public static FeatureEditResult SetFeature(string gameDataRoot, string hash, NpcFeature feature, bool add)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(gameDataRoot);
            if (string.IsNullOrWhiteSpace(hash))
                return FeatureEditResult.Failed(string.Empty, "a hash is required.");

            hash = hash.Trim();
            string name = feature.ToString();
            lock (WriteLock)
            {
                string path = AORebirth.Core.GameData.GameDataPaths.ResolveFile(gameDataRoot, FileName);
                try
                {
                    bool exists = File.Exists(path);
                    string text = exists ? File.ReadAllText(path) : "{}\n";
                    string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                    if (System.Text.Json.Nodes.JsonNode.Parse(text, null, DocumentOptions) is not System.Text.Json.Nodes.JsonObject root)
                        return FeatureEditResult.Failed(path, FileName + " must be an object.");

                    string key = FindKey(root, hash) ?? hash;
                    var entry = root[key] as System.Text.Json.Nodes.JsonObject;
                    if (entry == null && root.ContainsKey(key))
                        return FeatureEditResult.Failed(path, key + " in " + FileName + " is not an object; fix it in the file.");
                    string? featuresKey = entry == null ? null : FindKey(entry, nameof(MonsterCombatRules.Features));
                    var features = featuresKey == null ? null : entry![featuresKey] as System.Text.Json.Nodes.JsonArray;
                    int index = IndexOfFeature(features, feature);
                    if (add == (index >= 0))
                        return new FeatureEditResult(true, false, path, null);

                    if (add)
                    {
                        if (entry == null)
                        {
                            entry = new System.Text.Json.Nodes.JsonObject();
                            InsertKey(root, key, entry);
                        }

                        if (features == null)
                        {
                            features = new System.Text.Json.Nodes.JsonArray();
                            entry[featuresKey ?? nameof(MonsterCombatRules.Features)] = features;
                        }

                        features.Add(name);
                    }
                    else
                    {
                        features!.RemoveAt(index);
                        if (features.Count == 0)
                            entry!.Remove(featuresKey!);
                        if (entry!.Count == 0)
                            root.Remove(key);
                    }

                    string updated = root.ToJsonString(new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        NewLine = newline,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    }) + (text.EndsWith('\n') || !exists ? newline : string.Empty);

                    // It must load as the rules it is meant to be before it replaces anything.
                    Dictionary<string, MonsterCombatRules>? check =
                        JsonSerializer.Deserialize<Dictionary<string, MonsterCombatRules>>(updated, JsonOptions);
                    MonsterCombatRules? written = null;
                    check?.TryGetValue(key, out written);
                    if ((written?.Features?.Contains(feature) ?? false) != add)
                        return FeatureEditResult.Failed(path, "the rewritten file did not read back as asked; nothing written.");

                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    string temporary = path + ".tmp";
                    File.WriteAllText(temporary, updated, new System.Text.UTF8Encoding(false));
                    if (exists)
                        File.Copy(path, path + ".bak", overwrite: true);
                    File.Move(temporary, path, overwrite: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                    or NotSupportedException or InvalidOperationException or ArgumentException)
                {
                    return FeatureEditResult.Failed(path, exception.Message);
                }

                AORebirth.Modules.Content.GameDataReloadResult reload = Reload(gameDataRoot);
                return reload.Ok
                    ? new FeatureEditResult(true, true, path, null)
                    : new FeatureEditResult(false, true, path, "written, but the reload failed: " + string.Join("; ", reload.Problems));
            }
        }

        static string? FindKey(System.Text.Json.Nodes.JsonObject node, string name)
        {
            foreach (KeyValuePair<string, System.Text.Json.Nodes.JsonNode?> property in node)
            {
                if (string.Equals(property.Key.Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return property.Key;
            }

            return null;
        }

        static int IndexOfFeature(System.Text.Json.Nodes.JsonArray? features, NpcFeature feature)
        {
            for (int i = 0; features != null && i < features.Count; i++)
            {
                if (features[i] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue(out string? text)
                    && Enum.TryParse(text, ignoreCase: true, out NpcFeature parsed) && parsed == feature)
                    return i;
            }

            return -1;
        }

        /// <summary>Adds <paramref name="key"/> in ordinal-sorted place when the object's keys are sorted, else at the end.</summary>
        static void InsertKey(System.Text.Json.Nodes.JsonObject root, string key, System.Text.Json.Nodes.JsonNode value)
        {
            var properties = new List<KeyValuePair<string, System.Text.Json.Nodes.JsonNode?>>(root);
            bool sorted = true;
            for (int i = 1; i < properties.Count && sorted; i++)
                sorted = string.CompareOrdinal(properties[i - 1].Key, properties[i].Key) <= 0;

            int at = properties.Count;
            if (sorted)
            {
                at = 0;
                while (at < properties.Count && string.CompareOrdinal(properties[at].Key, key) < 0)
                    at++;
            }

            root.Clear();
            properties.Insert(at, new KeyValuePair<string, System.Text.Json.Nodes.JsonNode?>(key, value));
            foreach (KeyValuePair<string, System.Text.Json.Nodes.JsonNode?> property in properties)
                root.Add(property.Key, property.Value);
        }

        public bool TryGet(string? hash, out MonsterCombatRules rules)
        {
            if (!string.IsNullOrWhiteSpace(hash) && _rules.TryGetValue(hash.Trim(), out MonsterCombatRules? found))
            {
                rules = found;
                return true;
            }

            rules = null!;
            return false;
        }

        static MonsterCombatRulesCatalog Load(string root)
        {
            var rules = new Dictionary<string, MonsterCombatRules>(StringComparer.OrdinalIgnoreCase);
            string path = AORebirth.Core.GameData.GameDataPaths.ResolveFile(root, FileName);
            if (!File.Exists(path))
                return new MonsterCombatRulesCatalog(rules);

            Dictionary<string, MonsterCombatRules>? file =
                JsonSerializer.Deserialize<Dictionary<string, MonsterCombatRules>>(File.ReadAllText(path), JsonOptions);
            foreach (KeyValuePair<string, MonsterCombatRules> entry in file ?? new())
            {
                if (!string.IsNullOrWhiteSpace(entry.Key) && entry.Value != null)
                    rules[entry.Key.Trim()] = entry.Value;
            }

            return new MonsterCombatRulesCatalog(rules);
        }

        /// <summary>One root's current rules; the reference is swapped whole.</summary>
        sealed class Holder(MonsterCombatRulesCatalog current)
        {
            volatile MonsterCombatRulesCatalog _current = current;

            public MonsterCombatRulesCatalog Current
            {
                get => _current;
                set => _current = value;
            }
        }
    }

    /// <summary>What <see cref="MonsterCombatRulesCatalog.SetFeature"/> did.</summary>
    /// <param name="Ok">The file now says what was asked and the rules were reloaded.</param>
    /// <param name="Changed">The file was rewritten; false when it already said so.</param>
    /// <param name="File">The MonsterCombatRules.json edited.</param>
    /// <param name="Problem">Why it failed, when it did.</param>
    public readonly record struct FeatureEditResult(bool Ok, bool Changed, string File, string? Problem)
    {
        public static FeatureEditResult Failed(string file, string problem) => new(false, false, file, problem);
    }

    /// <summary>One mob hash's combat rules.</summary>
    public sealed class MonsterCombatRules
    {
        HashSet<int>? _hatedFamilies;

        public MonsterPetRule? Pets { get; set; }

        /// <summary>Behaviour switches: NoCombat, NoMove, Wander, Guard, Boss, Social, Summoner, SpawnsExitDevice, NoXp (<see cref="NpcFeature"/>).</summary>
        public List<NpcFeature>? Features { get; set; }

        /// <summary>
        /// NPCFamily (stat 455) values this NPC attacks on sight: an NPC of one of these families that comes within
        /// its aggro range (ProximityRangeOutdoors/Indoors) is attacked as a player would be. Players' pets never are.
        /// </summary>
        public int[]? Hates { get; set; }

        /// <summary><see cref="Hates"/> as a set; null when the NPC hates no family. Not part of the file.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public IReadOnlySet<int>? HatedFamilies
            => Hates is not { Length: > 0 } ? null : _hatedFamilies ??= new HashSet<int>(Hates);
    }

    /// <summary>
    /// Pets the NPC raises while it fights with the "NPC Pet Creation Item" in its equipment (the first, by slot); the
    /// item's OnUse names the pet's mob hash and level. Every NPC with that item uses these defaults unless its hash
    /// overrides them. Pets leave when the NPC resets, dies or despawns.
    /// </summary>
    public sealed class MonsterPetRule
    {
        /// <summary>Most pets alive at once.</summary>
        public int MaxPets { get; set; } = 1;

        /// <summary>Seconds from combat start to the first pet, and between each pet after it.</summary>
        public double SummonIntervalSeconds { get; set; } = 1;

        /// <summary>Seconds after a pet dies before its replacement, while the fight goes on.</summary>
        public double RespawnDelaySeconds { get; set; } = 10;
    }
}
