namespace ZoneEngine_New.Core.Quests
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;

    using AORebirth.Core.GameData;
    using AORebirth.Modules.Content;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Knubot;
    using ZoneEngine_New.Core.Logging;

    /// <summary>
    /// GameData/QuestRewards.json: the only source of the items a Quests.json quest pays, keyed by the quest's
    /// <c>Reward.Hash</c> (retail's mission treasure). Each hash lists entries checked in order; the first whose <c>If</c>
    /// rows (item requirement form, as Knubot <c>Requirements</c>) the player meets gives all of its <c>Items</c>, so an
    /// entry with no <c>If</c> last is the default. <c>PickOne</c> on a hash gives one of the matching entries instead,
    /// chosen once per accepted run. An item is an item <c>Hash</c> or a template <c>Id</c> [<c>HighId</c>], at <c>Ql</c>
    /// (a number, or "level" for the player's level); <c>Name</c> is for the reader. A hash with no entry, or no matching
    /// entry, pays no item. Loaded whole and swapped on reload; a file that fails to load keeps the old table.
    /// </summary>
    public sealed class QuestRewardTable
    {
        public const string FileName = "QuestRewards.json";
        public const string ReloadName = "QuestRewards";

        static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        readonly IGameData _gameData;
        readonly HashItemMinter _minter;
        readonly IZoneLogger _logger;
        volatile IReadOnlyDictionary<string, RewardTable> _rewards = new Dictionary<string, RewardTable>(StringComparer.Ordinal);

        public QuestRewardTable(IGameData gameData, HashItemMinter minter, IZoneLogger logger)
        {
            _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
            _minter = minter ?? throw new ArgumentNullException(nameof(minter));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            Reload();
        }

        public int Count => _rewards.Count;

        public bool Has(string? rewardHash) => !string.IsNullOrEmpty(rewardHash) && _rewards.ContainsKey(rewardHash);

        /// <summary>
        /// The items <paramref name="rewardHash"/> gives <paramref name="player"/> now, as template ids and QL: the first
        /// entry they meet, or with PickOne one of the entries they meet, stable for <paramref name="quest"/>'s accepted run
        /// (the first when there is no quest). False when the hash is not in the table; true with no items when no entry
        /// matches. An item that no longer resolves throws, so a delivery stays pending instead of paying short.
        /// </summary>
        public bool TryResolve(string? rewardHash, Player player, PlayerQuest? quest, out IReadOnlyList<QuestRewardItem> items)
        {
            ArgumentNullException.ThrowIfNull(player);
            items = Array.Empty<QuestRewardItem>();
            if (string.IsNullOrEmpty(rewardHash) || !_rewards.TryGetValue(rewardHash, out RewardTable? table))
                return false;

            var matching = new List<RewardEntry>(table.Entries.Length);
            foreach (RewardEntry entry in table.Entries)
            {
                if (entry.Requirements.Length == 0
                    || ItemTemplate.MeetsRequirements(entry.Requirements, stat => player.Stats.Get(stat)))
                {
                    matching.Add(entry);
                    if (!table.PickOne)
                        break;
                }
            }

            if (matching.Count == 0)
                return true;

            RewardEntry chosen = matching[quest != null && matching.Count > 1 ? StableIndex(quest, matching.Count) : 0];
            var resolved = new List<QuestRewardItem>(chosen.Items.Length);
            foreach (RewardItem item in chosen.Items)
                resolved.Add(Resolve(item, player, rewardHash));
            items = resolved;
            return true;
        }

        /// <summary>
        /// An index in <c>[0, count)</c> fixed for the quest's accepted run: reconnects and failed deliveries cannot reroll
        /// an alternative.
        /// </summary>
        public static int StableIndex(PlayerQuest quest, int count)
        {
            ArgumentNullException.ThrowIfNull(quest);
            if (count <= 1)
                return 0;

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(quest.QuestId + ":" + quest.AssignedAtUtc.Ticks));
            return (int)(BitConverter.ToUInt32(hash, 0) % (uint)count);
        }

        QuestRewardItem Resolve(RewardItem item, Player player, string rewardHash)
        {
            int quality = item.Quality ?? player.Stats.GetOrOne(CharacterStat.Level);
            if (item.Hash == null)
                return new QuestRewardItem { Id = item.Id, HighId = item.HighId ?? item.Id, Quality = Math.Max(1, quality), Name = item.Name ?? string.Empty };

            if (!_minter.TryRollIds(item.Hash, quality, out int lowId, out int highId, out int rolled))
                throw new InvalidOperationException("Quest reward " + rewardHash + " item hash " + item.Hash + " does not resolve.");
            return new QuestRewardItem { Id = lowId, HighId = highId, Quality = rolled, Name = item.Name ?? item.Hash };
        }

        /// <summary>Reads the file (through the GameData override folder) and swaps it in; on any error the old table stays.</summary>
        public GameDataReloadResult Reload()
        {
            string path = GameDataPaths.ResolveFile(_gameData.RootPath, FileName);
            if (!File.Exists(path))
            {
                _rewards = new Dictionary<string, RewardTable>(StringComparer.Ordinal);
                _logger.Info("GameData quest rewards: none (" + path + " not found)");
                return new GameDataReloadResult(ReloadName, true, 0, []);
            }

            var problems = new List<string>();
            Dictionary<string, RewardTable> loaded;
            try
            {
                loaded = Parse(File.ReadAllText(path), problems);
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                problems.Add(exception.Message);
                loaded = null!;
            }

            if (problems.Count != 0)
            {
                _logger.Warn("GameData quest rewards not loaded from " + path + "; table unchanged: " + string.Join("; ", problems));
                return new GameDataReloadResult(ReloadName, false, _rewards.Count, problems);
            }

            _rewards = loaded;
            _logger.Info(string.Format(CultureInfo.InvariantCulture, "GameData quest rewards={0} from {1}", loaded.Count, path));
            return new GameDataReloadResult(ReloadName, true, loaded.Count, []);
        }

        Dictionary<string, RewardTable> Parse(string json, List<string> problems)
        {
            var result = new Dictionary<string, RewardTable>(StringComparer.Ordinal);
            Dictionary<string, RewardFile>? file = JsonSerializer.Deserialize<Dictionary<string, RewardFile>>(json, JsonOptions);
            foreach (KeyValuePair<string, RewardFile> pair in file ?? [])
            {
                string where = pair.Key + ": ";
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value?.Entries is not { Length: > 0 } entries)
                {
                    problems.Add(where + "needs at least one entry");
                    continue;
                }

                var parsed = new List<RewardEntry>(entries.Length);
                for (int i = 0; i < entries.Length; i++)
                {
                    string at = where + "entry " + (i + 1).ToString(CultureInfo.InvariantCulture) + ": ";
                    RewardEntryFile entry = entries[i] ?? new RewardEntryFile();
                    var errors = new List<string>();
                    KnubotRequirementCondition? condition = KnubotCondition.TryParseRequirements(entry.If, errors);
                    foreach (string error in errors)
                        problems.Add(at + error);

                    var items = new List<RewardItem>();
                    foreach (RewardItemFile item in entry.Items ?? [])
                    {
                        if (TryParseItem(item, at, problems, out RewardItem parsedItem))
                            items.Add(parsedItem);
                    }

                    if (items.Count == 0)
                        problems.Add(at + "needs at least one item");
                    parsed.Add(new RewardEntry(condition?.Requirements ?? [], items.ToArray()));
                }

                result[pair.Key] = new RewardTable(pair.Value.PickOne, parsed.ToArray());
            }

            return result;
        }

        bool TryParseItem(RewardItemFile? item, string at, List<string> problems, out RewardItem parsed)
        {
            parsed = null!;
            if (item == null)
            {
                problems.Add(at + "empty item");
                return false;
            }

            int? quality = null;
            switch (item.Ql.ValueKind)
            {
                case JsonValueKind.Undefined:
                case JsonValueKind.Null:
                    quality = 1;
                    break;
                case JsonValueKind.Number when item.Ql.TryGetInt32(out int ql) && ql > 0:
                    quality = ql;
                    break;
                case JsonValueKind.String when string.Equals(item.Ql.GetString(), "level", StringComparison.OrdinalIgnoreCase):
                    break;
                default:
                    problems.Add(at + "Ql must be a positive number or \"level\"");
                    return false;
            }

            bool hasHash = !string.IsNullOrWhiteSpace(item.Hash);
            if (hasHash == (item.Id > 0))
            {
                problems.Add(at + "an item needs exactly one of Hash or Id");
                return false;
            }

            if (hasHash && !_gameData.CanResolveItemHash(item.Hash!.Trim()))
            {
                problems.Add(at + "unknown item hash '" + item.Hash + "'");
                return false;
            }

            parsed = new RewardItem(hasHash ? item.Hash!.Trim() : null, item.Id, item.HighId, item.Name, quality);
            return true;
        }

        sealed record RewardTable(bool PickOne, RewardEntry[] Entries);

        sealed record RewardEntry(ItemRequirement[] Requirements, RewardItem[] Items);

        /// <summary><see cref="Quality"/> null means the player's level.</summary>
        sealed record RewardItem(string? Hash, int Id, int? HighId, string? Name, int? Quality);

        sealed class RewardFile
        {
            public string? Notes { get; set; }

            /// <summary>Give one of the matching entries (stable per accepted run) instead of the first.</summary>
            public bool PickOne { get; set; }

            public RewardEntryFile[]? Entries { get; set; }
        }

        sealed class RewardEntryFile
        {
            public KnubotRequirementFile[]? If { get; set; }

            public RewardItemFile[]? Items { get; set; }
        }

        sealed class RewardItemFile
        {
            public string? Hash { get; set; }

            public int Id { get; set; }

            public int? HighId { get; set; }

            /// <summary>For the reader and .quests info; the ids decide the item.</summary>
            public string? Name { get; set; }

            public JsonElement Ql { get; set; }
        }
    }

    /// <summary>QuestRewards.json, swapped inside <see cref="QuestRewardTable"/>.</summary>
    public sealed class QuestRewardsReload(QuestRewardTable table) : IReloadableGameData
    {
        public string Name => QuestRewardTable.ReloadName;

        public IReadOnlyList<string> Files { get; } = [QuestRewardTable.FileName];

        public GameDataReloadResult Reload() => table.Reload();
    }
}
