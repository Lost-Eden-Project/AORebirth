namespace ZoneEngine_New.Core.Quests.Dungeons;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

using ZoneEngine_New.Core.Logging;

/// <summary>
/// A mission dungeon, however it is given (a terminal roll or a quest): where it is entered, how long it lasts, whether
/// its kills pay the token reward, and what the dungeon is made of. GameData/Missions/Missions.json,
/// <c>{ "name": { definition } }</c>; a terminal or a quest only names one.
/// </summary>
/// <summary>
/// How a mission pays its credits and experience: <see cref="CompletionPercent"/> of them for the objective, the rest by
/// the share of the dungeon's population killed (the kill share the reward roll uses). With a split
/// the experience no longer depends on the reward roll; the reward items still do. Frozen into the accepted mission.
/// </summary>
public sealed class MissionRewardSplit
{
    public int CompletionPercent { get; set; } = 100;

    /// <summary>The share of the advertised credits and experience paid with <paramref name="killed"/> of <paramref name="total"/> killed.</summary>
    public double Factor(int killed, int total)
    {
        double share = total > 0 ? Math.Clamp((double)killed / total, 0, 1) : 0;
        double completion = Math.Clamp(CompletionPercent, 0, 100) / 100.0;
        return completion + (1 - completion) * share;
    }

    internal string? Problem() => CompletionPercent is < 0 or > 100 ? "CompletionPercent must be 0-100" : null;

    internal bool IsValid => Problem() == null;

    public MissionRewardSplit Clone() => new() { CompletionPercent = CompletionPercent };
}

public sealed class MissionDefinition
{
    /// <summary>Playfields with ACG entrances; a mission picks one, then a random entrance on it.</summary>
    public int[]? Playfields { get; set; }

    /// <summary>One MissionEntrance instance, only with a single playfield; null: a random one.</summary>
    public uint? Entrance { get; set; }

    /// <summary>Whether the dungeon's kills pay DungeonGeneration.json's sided token reward.</summary>
    public bool AwardsTokens { get; set; } = true;

    /// <summary>Hours an accepted mission lasts; null: DungeonGeneration.json's AcceptedLifetimeSeconds.</summary>
    public int? LifetimeHours { get; set; }

    /// <summary>What the dungeon is made of (GameData/Dungeons); null: the defaults.</summary>
    public DungeonRequest? Dungeon { get; set; }

    /// <summary>
    /// How the terminal's sliders change the dungeon, by slider name (OrderChaos, OpenHidden, PhysicalMystical,
    /// HeadOnStealth): values at its Left end (-100), Middle (0) and Right end (100) for dungeon fields, interpolated
    /// in a straight line between them by the slider's position (<see cref="MissionSliderRule.Fields"/>). Null: the
    /// sliders change nothing.
    /// </summary>
    public Dictionary<string, MissionSliderRule>? Sliders { get; set; }

    /// <summary>How the credits and experience pay by the share cleared (<see cref="MissionRewardSplit"/>); null: in full
    /// on completion, experience by the reward roll.</summary>
    public MissionRewardSplit? Rewards { get; set; }

    internal string? Problem()
    {
        if (Playfields is not { Length: > 0 } || Playfields.Any(p => p <= 0))
            return "Playfields must list at least one playfield";
        if (Rewards?.Problem() is string rewards)
            return "Rewards: " + rewards;
        if (Entrance != null && Playfields.Length != 1)
            return "Entrance needs exactly one playfield";
        if (LifetimeHours is <= 0)
            return "LifetimeHours must be positive or null";
        foreach ((string slider, MissionSliderRule rule) in Sliders ?? [])
        {
            if (!MissionSliderSettings.Names.Contains(slider))
                return "Sliders." + slider + " is not a slider (" + string.Join(", ", MissionSliderSettings.Names) + ")";
            if (rule?.Problem() is string problem)
                return "Sliders." + slider + ": " + problem;
        }
        return null;
    }

    /// <summary><paramref name="dungeon"/> changed by the slider rules for <paramref name="sliders"/>; no sliders: unchanged.</summary>
    public void ApplySliders(ResolvedDungeon dungeon, MissionSliderSettings? sliders)
    {
        if (Sliders == null || sliders == null)
            return;
        foreach ((string slider, MissionSliderRule rule) in Sliders)
            rule.Apply(dungeon, sliders.Value(slider));
    }
}

/// <summary>
/// One slider's effect: dungeon field values at the slider's Left end, Middle and Right end. Every end names the same
/// fields; between ends the value runs in a straight line. Integer fields are rounded.
/// </summary>
public sealed class MissionSliderRule
{
    public Dictionary<string, double> Left { get; set; } = [];
    public Dictionary<string, double> Middle { get; set; } = [];
    public Dictionary<string, double> Right { get; set; } = [];

    /// <summary>The dungeon fields a slider may set; Population.Mix.* and Population.Styles.* take a category or style.</summary>
    public static readonly string[] Fields =
    [
        "Population.Mix.human", "Population.Mix.monster", "Population.Mix.robot",
        "Population.Styles.physical", "Population.Styles.mystical", "Population.Styles.mixed",
        "Population.AggroRangePercent", "Population.HelpRangePercent", "Population.SpawnChancePercent", "Population.MaxGroup",
        "Layout.LockedDoorChancePercent", "Chests.LockedChancePercent", "Objective.LevelPercent"
    ];

    internal string? Problem()
    {
        if (Left == null || Middle == null || Right == null || Left.Count == 0)
            return "needs Left, Middle and Right";
        var keys = Left.Keys.ToHashSet(StringComparer.Ordinal);
        if (!keys.SetEquals(Middle.Keys) || !keys.SetEquals(Right.Keys))
            return "Left, Middle and Right must name the same fields";
        if (keys.FirstOrDefault(k => !Fields.Contains(k)) is string unknown)
            return unknown + " is not a field a slider sets (" + string.Join(", ", Fields) + ")";
        if (Left.Values.Concat(Middle.Values).Concat(Right.Values).Any(v => !double.IsFinite(v)))
            return "values must be numbers";
        return null;
    }

    /// <summary>Sets every field at slider position <paramref name="position"/> (-100 Left, 0 Middle, 100 Right).</summary>
    internal void Apply(ResolvedDungeon dungeon, int position)
    {
        double t = Math.Clamp(position, -100, 100) / 100.0;
        foreach (string field in Left.Keys)
        {
            double value = t <= 0
                ? Middle[field] + ((Left[field] - Middle[field]) * -t)
                : Middle[field] + ((Right[field] - Middle[field]) * t);
            Set(dungeon, field, value);
        }
    }

    static void Set(ResolvedDungeon d, string field, double value)
    {
        int whole = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        string[] parts = field.Split('.');
        switch (parts[0], parts[1])
        {
            case ("Population", "Mix"):
                // A copy: the resolved block shares its dictionary with the preset it came from.
                d.Population.Mix = new Dictionary<string, double>(d.Population.Mix ?? []) { [parts[2]] = Math.Max(0, value) };
                break;
            case ("Population", "Styles"):
                d.Population.Styles = new Dictionary<string, double>(d.Population.Styles ?? []) { [parts[2]] = Math.Max(0, value) };
                break;
            case ("Population", "AggroRangePercent"): d.Population.AggroRangePercent = whole; break;
            case ("Population", "HelpRangePercent"): d.Population.HelpRangePercent = whole; break;
            case ("Population", "SpawnChancePercent"): d.Population.SpawnChancePercent = whole; break;
            case ("Population", "MaxGroup"): d.Population.MaxGroup = whole; break;
            case ("Layout", "LockedDoorChancePercent"): d.Layout.LockedDoorChancePercent = whole; break;
            case ("Chests", "LockedChancePercent"): d.Chests.LockedChancePercent = whole; break;
            case ("Objective", "LevelPercent"): d.Objective.LevelPercent = whole; break;
        }
    }
}

/// <summary>The terminal's six sliders as a mission was rolled with them, each -100 (left end) to 100 (right end).</summary>
public sealed class MissionSliderSettings
{
    public static readonly string[] Names = ["GoodBad", "OrderChaos", "OpenHidden", "PhysicalMystical", "HeadOnStealth", "MoneyExperience"];

    public int GoodBad { get; set; }
    public int OrderChaos { get; set; }
    public int OpenHidden { get; set; }
    public int PhysicalMystical { get; set; }
    public int HeadOnStealth { get; set; }
    public int MoneyExperience { get; set; }

    public int Value(string name) => name switch
    {
        "GoodBad" => GoodBad,
        "OrderChaos" => OrderChaos,
        "OpenHidden" => OpenHidden,
        "PhysicalMystical" => PhysicalMystical,
        "HeadOnStealth" => HeadOnStealth,
        "MoneyExperience" => MoneyExperience,
        _ => 0
    };
}

/// <summary>GameData/Missions/Missions.json: the named mission definitions.</summary>
public sealed class MissionDefinitions
{
    public const string FileName = "Missions.json";

    readonly Dictionary<string, MissionDefinition> _missions;

    MissionDefinitions(Dictionary<string, MissionDefinition> missions) => _missions = missions;

    public int Count => _missions.Count;

    public MissionDefinition? Get(string? name)
        => name != null && _missions.TryGetValue(name, out MissionDefinition? mission) ? mission : null;

    /// <summary>The reloadable catalog name (<see cref="ZoneEngine_New.Core.GameData.MissionsReload"/>).</summary>
    public const string ReloadName = "Missions";

    /// <summary>The file's GameData-relative path.</summary>
    public const string RelativePath = "Missions/" + FileName;

    /// <summary>
    /// Reads the file (the GameData override folder's copy when it has one, else the shipped file); throws naming what
    /// is wrong. Each definition's dungeon must resolve against <paramref name="presets"/> at every mission quality.
    /// </summary>
    public static MissionDefinitions Load(string gameDataRoot, DungeonPresets presets, IZoneLogger logger)
    {
        string path = AORebirth.Core.GameData.GameDataPaths.ResolveFile(gameDataRoot, RelativePath);
        if (!File.Exists(path))
            throw new FileNotFoundException(path + " not found", path);
        Dictionary<string, MissionDefinition> missions = JsonSerializer.Deserialize<Dictionary<string, MissionDefinition>>(
            File.ReadAllText(path), DungeonRequest.JsonOptions) ?? new Dictionary<string, MissionDefinition>();

        var problems = new List<string>();
        foreach ((string name, MissionDefinition mission) in missions)
        {
            if (mission == null)
            {
                problems.Add(name + ": empty");
                continue;
            }
            if (mission.Problem() is string problem)
            {
                problems.Add(name + ": " + problem);
                continue;
            }
            // Every quality, with every slider at both ends and the middle.
            for (int quality = 1; quality <= QuestDungeonTemplate.MaxMissionQuality; quality++)
            {
                string? dungeonProblem = null;
                foreach (int position in new[] { -100, 0, 100 })
                {
                    ResolvedDungeon? resolved = presets.Resolve(mission.Dungeon, quality, out dungeonProblem);
                    if (resolved == null)
                        break;
                    var sliders = new MissionSliderSettings
                    {
                        GoodBad = position, OrderChaos = position, OpenHidden = position, PhysicalMystical = position,
                        HeadOnStealth = position, MoneyExperience = position
                    };
                    mission.ApplySliders(resolved, sliders);
                    dungeonProblem = resolved.Problem();
                    // Every player side must still find a population.
                    foreach (int side in new[] { 0, 1, 2 })
                        if (dungeonProblem == null && presets.Pick(resolved.Population, quality, side, new Random(quality), out string? pickProblem) == null)
                            dungeonProblem = pickProblem;
                    if (dungeonProblem != null)
                        break;
                }
                if (dungeonProblem != null)
                {
                    problems.Add(name + ": Dungeon at quality " + quality.ToString(CultureInfo.InvariantCulture) + ": " + dungeonProblem);
                    break;
                }
            }
        }
        if (problems.Count > 0)
            throw new InvalidDataException("Missions/" + FileName + ": " + string.Join("; ", problems));

        logger.Info(string.Format(CultureInfo.InvariantCulture, "GameData missions={0} ({1})", missions.Count, string.Join(", ", missions.Keys)));
        return new MissionDefinitions(new Dictionary<string, MissionDefinition>(missions, StringComparer.Ordinal));
    }
}
