namespace ZoneEngine_New.Core.GameData
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;

    using AORebirth.Core.GameData;

    using Utility;

    /// <summary>
    /// Personal research tiers from Research.json (the client's perk records with flag 0x40): per research id, the research
    /// XP the tier needs (the client's progress is (needed - remaining) / needed, Gamecode.dll N3Msg_GetPerkProgress), the
    /// tier before and after it in its line, the professions that may research it (ProfessionMask, 1 &lt;&lt; profession; 0 is
    /// every profession, as the client's FUN_10052d48 reads the tier item's Profession criteria) and its minimum level.
    /// </summary>
    public sealed class ResearchCatalog
    {
        static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        static readonly Lazy<ResearchCatalog> DefaultCatalog = new(() => LoadOrEmpty(GameDataPaths.ResolveRuntimeRoot()));

        readonly Dictionary<int, ResearchTier> _tiers;

        ResearchCatalog(Dictionary<int, ResearchTier> tiers) => _tiers = tiers;

        public static ResearchCatalog Empty { get; } = new(new Dictionary<int, ResearchTier>());

        /// <summary>Catalog from the runtime GameData folder. Empty, with an error logged, when it cannot be loaded.</summary>
        public static ResearchCatalog Default => DefaultCatalog.Value;

        public int Count => _tiers.Count;

        public bool TryGet(int researchId, out ResearchTier tier) => _tiers.TryGetValue(researchId, out tier);

        public static ResearchCatalog Load(string root)
        {
            string path = Path.Combine(root, GameDataPaths.ResearchFileName);
            ResearchFile file = JsonSerializer.Deserialize<ResearchFile>(File.ReadAllText(path), JsonOptions)
                ?? throw Invalid("empty file");

            var tiers = new Dictionary<int, ResearchTier>();
            foreach (LineRow? line in file.PersonalResearch ?? [])
            {
                if (line == null || line.ProfessionMask < 0)
                    throw Invalid("line");

                TierRow[] rows = (line.Tiers ?? []).Where(t => t != null).Select(t => t!).OrderBy(t => t.Tier).ToArray();
                for (int i = 0; i < rows.Length; i++)
                {
                    TierRow row = rows[i];
                    if (row.Id <= 0 || row.Xp <= 0 || row.MinLevel < 0)
                        throw Invalid("tier " + row.Id);

                    int previous = i > 0 ? rows[i - 1].Id : 0;
                    int next = i + 1 < rows.Length ? rows[i + 1].Id : 0;
                    if (!tiers.TryAdd(row.Id, new ResearchTier(row.Id, line.Name ?? string.Empty, row.Tier, row.Xp,
                            Math.Max(1, row.MinLevel), line.ProfessionMask, previous, next)))
                        throw Invalid("duplicate tier " + row.Id);
                }
            }

            return new ResearchCatalog(tiers);
        }

        static ResearchCatalog LoadOrEmpty(string root)
        {
            try
            {
                return Load(root);
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                LogUtil.ErrorException(exception, "Research catalog not loaded from {0}; personal research is disabled", root);
                return Empty;
            }
        }

        static InvalidDataException Invalid(string detail) => new(GameDataPaths.ResearchFileName + ": invalid " + detail);

        sealed class ResearchFile
        {
            public LineRow?[]? PersonalResearch { get; set; }
        }

        sealed class LineRow
        {
            public string? Name { get; set; }

            public int ProfessionMask { get; set; }

            public TierRow?[]? Tiers { get; set; }
        }

        sealed class TierRow
        {
            public int Tier { get; set; }

            public int Id { get; set; }

            public int Xp { get; set; }

            public int MinLevel { get; set; }
        }
    }

    /// <summary>One personal research tier.</summary>
    public readonly record struct ResearchTier(
        int Id, string LineName, int Tier, int Xp, int MinLevel, int ProfessionMask, int PreviousId, int NextId)
    {
        /// <summary>A character of <paramref name="profession"/> may research this line (mask 0: everyone).</summary>
        public bool AllowsProfession(int profession)
            => ProfessionMask == 0 || (profession is > 0 and < 31 && (ProfessionMask & (1 << profession)) != 0);
    }
}
