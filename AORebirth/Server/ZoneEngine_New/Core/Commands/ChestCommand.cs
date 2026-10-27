namespace ZoneEngine_New.Core.Commands;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using SmokeLounge.AOtomation.Messaging.GameData;

using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Quests.Dungeons;

/// <summary>Read-only simulation through the production mission chest selector.</summary>
public sealed class ChestCommand : IGmCommand
{
    internal const int MaxRolls = 10000;
    internal const int MaxDisplayedEntries = 100;
    readonly Func<MissionChestLootService> _createSelector;

    public ChestCommand(IGameData gameData, IItemTemplateCatalog templates, IItemBuilder items,
        DungeonLayoutGenerator dungeons)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(dungeons);
        _createSelector = () => new MissionChestLootService(templates, gameData, items,
            dungeons.ChestPolicy ?? throw new InvalidOperationException("Mission chest generation policy is unavailable."));
    }

    internal ChestCommand(Func<MissionChestLootService> createSelector)
    {
        ArgumentNullException.ThrowIfNull(createSelector);
        _createSelector = createSelector;
    }

    public string Name => "chest";
    public int RequiredGmLevel => 1;
    public string Usage => ".chest roll <1-10000>";

    public GmCommandHelp Help { get; } = new(
        "Simulates mission chest populations using the production RK allowlist and loot selector.",
        [new(".chest roll <amount>", "Rolls 1-10000 chest populations without awarding items or allocating item instances.")],
        [new(".chest roll 100", "Shows generated identities, QLs, rarity and eligibility evidence for one hundred chests.")],
        "Uses the generated mission's QL when available; otherwise explicitly reports a player-level fallback. At most 100 distinct results are displayed.");

    public void Execute(GmCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!TryParseRolls(context.Args, out int rolls))
        {
            context.SendUsage("Usage: " + Usage);
            return;
        }

        int? missionQuality = (context.Player.Playfield as QuestDungeonPlayfield)?.Quality;
        (int quality, string source) = ResolveQuality(missionQuality, context.Player.Stats.GetOrOne(CharacterStat.Level));
        GmCommandFeedback.SendLines(context.Session, context.Player,
            Simulate(_createSelector(), quality, source, rolls, Random.Shared), maxLines: 32);
    }

    internal static bool TryParseRolls(string[] args, out int rolls)
    {
        rolls = 0;
        return args.Length == 2 && string.Equals(args[0], "roll", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out rolls)
            && rolls is >= 1 and <= MaxRolls;
    }

    internal static (int Quality, string Source) ResolveQuality(int? missionQuality, int playerLevel)
        => missionQuality is >= 1 and <= 250
            ? (missionQuality.Value, "accepted generated mission QL")
            : (Math.Clamp(playerLevel, 1, 250), "player-level fallback; no supported generated mission QL available");

    internal static IReadOnlyList<string> Simulate(MissionChestLootService selector, int quality,
        string qualitySource, int rolls, Random random)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(random);
        if (rolls is < 1 or > MaxRolls)
            throw new ArgumentOutOfRangeException(nameof(rolls));
        MissionChestLootDiagnostics diagnostics = selector.GetDiagnostics(quality);
        var drops = new Dictionary<(int LowId, int HighId, int Quality, string Rarity), (MissionChestLootPreview Item, int Count)>();
        int total = 0;
        for (int index = 0; index < rolls; index++)
        {
            // RollPreview shares selection with Roll and creates definitions only, without allocating durable IDs.
            foreach (MissionChestLootPreview item in selector.RollPreview(quality, random))
            {
                var key = (item.LowId, item.HighId, item.Quality, item.Rarity);
                drops[key] = drops.TryGetValue(key, out var previous) ? (previous.Item, previous.Count + 1) : (item, 1);
                total++;
            }
        }
        return FormatResults(quality, qualitySource, rolls, total, diagnostics, drops.Values.ToArray());
    }

    internal static IReadOnlyList<string> FormatResults(int quality, string qualitySource, int rolls, int total,
        MissionChestLootDiagnostics diagnostics, IReadOnlyCollection<(MissionChestLootPreview Item, int Count)> drops)
    {
        var rows = new List<string>
        {
            DiagAoml.Title("Mission chest loot simulation"),
            string.Format(CultureInfo.InvariantCulture, "QL {0}: {1}", quality, Safe(qualitySource, 120)),
            string.Format(CultureInfo.InvariantCulture, "Chest populations: {0}; total generated items: {1}", rolls, total),
            string.Format(CultureInfo.InvariantCulture,
                "Eligibility pool: approved candidates {0}; rejected candidates {1}; eligible QL choices {2}",
                diagnostics.ApprovedCandidates, diagnostics.RejectedCandidates, diagnostics.EligibleChoices),
            "Rejected candidates describe eligibility-pool construction, not repeated failed loot rolls.",
            "Simulation only; no items awarded or inventory changes.",
            string.Empty
        };
        foreach (var drop in drops.OrderByDescending(value => value.Count).ThenBy(value => value.Item.LowId)
                     .ThenBy(value => value.Item.HighId).ThenBy(value => value.Item.Quality)
                     .ThenBy(value => value.Item.Rarity, StringComparer.Ordinal).Take(MaxDisplayedEntries))
        {
            MissionChestLootPreview item = drop.Item;
            rows.Add(string.Format(CultureInfo.InvariantCulture,
                "ID {0}/{1}; QL {2}; rarity {3}; {4}; count {5}; classification {6}", item.LowId, item.HighId,
                item.Quality, Safe(item.Rarity, 32), Safe(item.Name, 160), drop.Count, Safe(item.Classification, 64)));
            string evidence = string.Join("; ", item.Evidence.Take(3).Select(value => Safe(value, 180)));
            if (item.Evidence.Length > 3)
                evidence += string.Format(CultureInfo.InvariantCulture, " (+{0} additional references)", item.Evidence.Length - 3);
            rows.Add("Eligibility evidence: " + evidence);
        }
        if (drops.Count > MaxDisplayedEntries)
            rows.Add(string.Format(CultureInfo.InvariantCulture,
                "Showing {0} of {1} distinct identity/QL/rarity results; totals include every roll.", MaxDisplayedEntries, drops.Count));
        IReadOnlyList<string> chunks = GetStatsAomlBuilder.ChunkRows(rows, GetStatsAomlBuilder.DefaultMaxBodyLength);
        var links = new List<string>(chunks.Count);
        for (int index = 0; index < chunks.Count; index++)
        {
            string label = chunks.Count == 1 ? "Chest loot roll results" : string.Format(CultureInfo.InvariantCulture,
                "Chest loot roll results ({0}/{1})", index + 1, chunks.Count);
            links.Add(GetStatsAomlBuilder.BuildLink(chunks[index], label));
        }
        return links;
    }

    static string Safe(string value, int maximum)
    {
        string text = value.Replace('\r', ' ').Replace('\n', ' ');
        return DiagAoml.Safe(text.Length <= maximum ? text : text[..maximum] + "...");
    }
}
