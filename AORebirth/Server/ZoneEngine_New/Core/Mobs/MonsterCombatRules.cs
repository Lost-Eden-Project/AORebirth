namespace ZoneEngine_New.Core.Mobs
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;

    /// <summary>
    /// GameData/MonsterCombatRules.json: combat behaviour keyed by mob hash, e.g.
    /// <c>{ "ASSU": { "Pets": { "MaxPets": 2 } } }</c>. Loaded once per GameData root.
    /// </summary>
    public sealed class MonsterCombatRulesCatalog
    {
        public const string FileName = "MonsterCombatRules.json";

        static readonly ConcurrentDictionary<string, MonsterCombatRulesCatalog> ByRoot = new(StringComparer.OrdinalIgnoreCase);

        static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        readonly Dictionary<string, MonsterCombatRules> _rules;

        MonsterCombatRulesCatalog(Dictionary<string, MonsterCombatRules> rules)
        {
            _rules = rules;
        }

        public int Count => _rules.Count;

        public static MonsterCombatRulesCatalog For(string gameDataRoot)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(gameDataRoot);
            return ByRoot.GetOrAdd(gameDataRoot, Load);
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
            string path = Path.Combine(root, FileName);
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
    }

    /// <summary>One mob hash's combat rules.</summary>
    public sealed class MonsterCombatRules
    {
        public MonsterPetRule? Pets { get; set; }
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
