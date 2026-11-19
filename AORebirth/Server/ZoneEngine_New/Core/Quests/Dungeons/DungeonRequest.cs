namespace ZoneEngine_New.Core.Quests.Dungeons;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Logging;

/// <summary>
/// What a dungeon is made of, as any mission asks for it: a layout, a population, chests and an objective. Each slot
/// takes a preset name (<c>"caveDwellers"</c>) or a block written in place; a block written in place may name a
/// <c>Preset</c> and change some of its fields. A field left out comes from the preset, else from
/// GameData/Dungeons/Defaults.json. The code that builds and fills the dungeon never knows who asked.
/// </summary>
public sealed class DungeonRequest
{
    [JsonConverter(typeof(DungeonBlockConverter<DungeonLayoutBlock>))]
    public DungeonLayoutBlock? Layout { get; set; }

    [JsonConverter(typeof(DungeonBlockConverter<DungeonPopulationBlock>))]
    public DungeonPopulationBlock? Population { get; set; }

    [JsonConverter(typeof(DungeonBlockConverter<DungeonChestBlock>))]
    public DungeonChestBlock? Chests { get; set; }

    [JsonConverter(typeof(DungeonBlockConverter<DungeonObjectiveBlock>))]
    public DungeonObjectiveBlock? Objective { get; set; }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>A request block: a preset to start from (or none) and the fields that change it.</summary>
public interface IDungeonBlock<T> where T : class, IDungeonBlock<T>
{
    /// <summary>The preset this block starts from; null: the defaults.</summary>
    string? Preset { get; set; }

    /// <summary>This block's fields over <paramref name="under"/>'s: each field this block sets wins.</summary>
    T Over(T under);
}

/// <summary>Size and shape: which style, how many rooms per floor, how many floors (more than one: a team dungeon with a boss floor).</summary>
public sealed class DungeonLayoutBlock : IDungeonBlock<DungeonLayoutBlock>
{
    public string? Preset { get; set; }

    /// <summary>ACG building styles; the dungeon's seed picks one.</summary>
    public int[]? Styles { get; set; }

    /// <summary>Rooms per floor, lowest and highest. The generator is asked for sizes until a layout lands in the range.</summary>
    public int[]? Rooms { get; set; }

    /// <summary>The generator's floor size, when <see cref="Rooms"/> is not set (3 to 40).</summary>
    public int? Size { get; set; }

    /// <summary>Floors; more than one ends in a boss floor (team dungeons).</summary>
    public int? Floors { get; set; }

    /// <summary>Height between floors.</summary>
    public int? FloorHeight { get; set; }

    /// <summary>Percent of interior doors that start locked.</summary>
    public int? LockedDoorChancePercent { get; set; }

    /// <summary>
    /// Layouts by mission quality: the band whose <see cref="Quality"/> holds the mission's quality changes this block
    /// (its fields win). A block with bands must have one for every quality it is asked for.
    /// </summary>
    public List<DungeonLayoutBlock>? Bands { get; set; }

    /// <summary>In a band: the mission qualities it is for, [lowest, highest].</summary>
    public int[]? Quality { get; set; }

    public DungeonLayoutBlock Over(DungeonLayoutBlock under) => new()
    {
        Bands = Bands ?? under.Bands,
        Styles = Styles ?? under.Styles,
        Rooms = Rooms ?? (Size != null ? null : under.Rooms),
        // A block that sets Rooms drops a Size it inherits, and the other way round: the one it names is meant.
        Size = Size ?? (Rooms != null ? null : under.Size),
        Floors = Floors ?? under.Floors,
        FloorHeight = FloorHeight ?? under.FloorHeight,
        LockedDoorChancePercent = LockedDoorChancePercent ?? under.LockedDoorChancePercent
    };

    /// <summary>
    /// This block for a mission of <paramref name="quality"/>: its band for that quality over it, without bands; null with
    /// the reason when no band holds the quality.
    /// </summary>
    internal DungeonLayoutBlock? ForQuality(int quality, out string? problem)
    {
        problem = null;
        if (Bands is not { Count: > 0 } bands)
            return this;
        foreach (DungeonLayoutBlock band in bands)
        {
            if (band?.Quality is not [int lowest, int highest] || highest < lowest)
            {
                problem = "Layout.Bands need a Quality [lowest, highest]";
                return null;
            }
            if (band.Bands != null)
            {
                problem = "Layout.Bands cannot hold bands";
                return null;
            }
        }
        DungeonLayoutBlock? match = bands.FirstOrDefault(band => quality >= band.Quality![0] && quality <= band.Quality[1]);
        if (match == null)
        {
            problem = "Layout.Bands have none for quality " + quality.ToString(CultureInfo.InvariantCulture);
            return null;
        }
        DungeonLayoutBlock resolved = match.Over(this);
        resolved.Bands = null;
        return resolved;
    }

    internal string? Problem()
    {
        if (Bands != null)
            return "Layout.Bands were not resolved for a quality";
        if (Styles is not { Length: > 0 } || Styles.Any(s => s <= 0))
            return "Layout.Styles must list at least one positive style";
        if (Rooms == null && Size == null)
            return "Layout needs Rooms or Size";
        if (Rooms != null && (Rooms.Length != 2 || Rooms[0] < 1 || Rooms[1] < Rooms[0]))
            return "Layout.Rooms must be [lowest, highest] with 1 <= lowest <= highest";
        if (Size is < AORebirth.DungeonGenerator.DungeonGenerator.MinFloorSize or > AORebirth.DungeonGenerator.DungeonGenerator.MaxFloorSize)
            return "Layout.Size must be " + AORebirth.DungeonGenerator.DungeonGenerator.MinFloorSize + "-" + AORebirth.DungeonGenerator.DungeonGenerator.MaxFloorSize;
        if (Floors is not (>= 1 and <= AORebirth.DungeonGenerator.DungeonGenerator.MaxFloors))
            return "Layout.Floors must be 1-" + AORebirth.DungeonGenerator.DungeonGenerator.MaxFloors;
        if (FloorHeight is not (> 0 and <= 255))
            return "Layout.FloorHeight must be 1-255";
        if (LockedDoorChancePercent is not (>= 0 and <= 100))
            return "Layout.LockedDoorChancePercent must be 0-100";
        return null;
    }
}

/// <summary>The ordinary monsters: which, how many, how strong, how close together, and what they drop besides their own tables.</summary>
public sealed class DungeonPopulationBlock : IDungeonBlock<DungeonPopulationBlock>
{
    public string? Preset { get; set; }

    /// <summary>NPC hashes; each spawn picks one.</summary>
    public string[]? Npcs { get; set; }

    /// <summary>Chance, in percent, that a room's spawn point gets a monster.</summary>
    public int? SpawnChancePercent { get; set; }

    /// <summary>Each monster's level rolls this many percent either side of the mission quality (0 to 90).</summary>
    public int? LevelSpreadPercent { get; set; }

    /// <summary>Most monsters within <see cref="GroupRadius"/> of each other: no monster is placed with this many already that near.</summary>
    public int? MaxGroup { get; set; }

    /// <summary>Distance at which two monsters count as one group.</summary>
    public float? GroupRadius { get; set; }

    /// <summary>Rows every monster's corpse rolls besides its own table; empty: none.</summary>
    public List<Mobs.MobItemTableEntry>? Loot { get; set; }

    /// <summary>
    /// Picks the NPCs from GameData/Dungeons/Families.json instead of <see cref="Npcs"/>: weights per category (human,
    /// monster, robot). A <see cref="Kinds"/> count of families is drawn when the mission is accepted, each from a category rolled by
    /// these weights, among the families fit for missions whose levels hold the mission quality.
    /// </summary>
    public Dictionary<string, double>? Mix { get; set; }

    /// <summary>With <see cref="Mix"/>: weights per combat style (physical, mystical, mixed) for the human families.</summary>
    public Dictionary<string, double>? Styles { get; set; }

    /// <summary>
    /// With <see cref="Mix"/>: how many of stock's NPC families (stat 455) the population is drawn from, [lowest, highest];
    /// the mission's seed picks the count, and every fit member of a drawn family can spawn.
    /// </summary>
    public int[]? Kinds { get; set; }

    /// <summary>Percent of each monster's own unprovoked aggro range (100: unchanged).</summary>
    public int? AggroRangePercent { get; set; }

    /// <summary>Percent of how far a monster's call for help carries (100: unchanged).</summary>
    public int? HelpRangePercent { get; set; }

    /// <summary>
    /// Every monster's Aggressiveness (BreedHostility, stat 204): above 0 it attacks players on sight, 0 only fights
    /// back. Null: each template's own.
    /// </summary>
    public int? Aggressiveness { get; set; }

    /// <summary>
    /// Every monster's Clan (stat 5): with Gameplay.json socialAggro.sameClanOnly, monsters answer only their own clan's
    /// calls for help, so one clan for the whole population makes them help each other. Null: each template's own.
    /// </summary>
    public int? Clan { get; set; }

    /// <summary>
    /// Every monster's AssistanceRadius: how far its call for help carries, before socialAggro's maxRadius and
    /// radiusPercent and the population's <see cref="HelpRangePercent"/>. 0: socialAggro's defaultRadius.
    /// </summary>
    public int? AssistanceRadius { get; set; }

    /// <summary>
    /// <see cref="Mobs.NpcFeature"/> names every monster of the population carries besides its template's own. Only
    /// the features an NPC is asked about one by one can be given this way: <see cref="PopulationFeatures"/>.
    /// </summary>
    public string[]? Features { get; set; }

    /// <summary>
    /// Scale (stat 360) by NPC spawn hash: a monster spawned from a listed hash, or the objective NPC, takes this Scale
    /// in place of its template's (100 is a template's ordinary size). Unlisted hashes keep their own.
    /// </summary>
    public Dictionary<string, int>? Scales { get; set; }

    /// <summary>The features a population may list.</summary>
    public static readonly Mobs.NpcFeature[] PopulationFeatures = [Mobs.NpcFeature.AggroesAllSides];

    public const int MaxSpreadPercent = 90;
    public const int MaxRangePercent = 400;
    public const int MaxKinds = 20;
    public const int MaxScale = 2000;
    public static readonly string[] Categories = ["human", "monster", "robot"];
    public static readonly string[] StyleNames = ["physical", "mystical", "mixed"];

    public DungeonPopulationBlock Over(DungeonPopulationBlock under) => new()
    {
        Npcs = Npcs ?? under.Npcs,
        SpawnChancePercent = SpawnChancePercent ?? under.SpawnChancePercent,
        LevelSpreadPercent = LevelSpreadPercent ?? under.LevelSpreadPercent,
        MaxGroup = MaxGroup ?? under.MaxGroup,
        GroupRadius = GroupRadius ?? under.GroupRadius,
        Loot = Loot ?? under.Loot,
        Mix = Mix ?? under.Mix,
        Styles = Styles ?? under.Styles,
        Kinds = Kinds ?? under.Kinds,
        AggroRangePercent = AggroRangePercent ?? under.AggroRangePercent,
        HelpRangePercent = HelpRangePercent ?? under.HelpRangePercent,
        Aggressiveness = Aggressiveness ?? under.Aggressiveness,
        Clan = Clan ?? under.Clan,
        AssistanceRadius = AssistanceRadius ?? under.AssistanceRadius,
        Features = Features ?? under.Features,
        Scales = Scales ?? under.Scales
    };

    /// <summary>
    /// Puts the population's Aggressiveness and Clan on one of its monsters (each left as the template's when unset),
    /// and gives it the population's Features and its hash's <see cref="Scales"/> entry.
    /// </summary>
    internal void ApplyTo(Entities.NpcCharacter npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ApplyScale(npc);
        if (Aggressiveness is int aggressiveness)
            npc.Stats.Set(Characters.CharacterStat.BreedHostility, aggressiveness);
        if (Clan is int clan)
            npc.Stats.Set(Characters.CharacterStat.Clan, clan);
        if (Features != null)
            foreach (string name in Features)
                if (TryParseFeature(name, out Mobs.NpcFeature feature))
                    npc.AddFeature(feature);
    }

    /// <summary>Sets <paramref name="npc"/>'s Scale from <see cref="Scales"/> when its spawn hash is listed.</summary>
    internal void ApplyScale(Entities.NpcCharacter npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        if (Scales != null && npc.SpawnHash is string hash && Scales.TryGetValue(hash, out int scale))
            npc.Stats.Set(Characters.CharacterStat.Scale, scale);
    }

    static bool TryParseFeature(string? name, out Mobs.NpcFeature feature)
    {
        feature = default;
        return name != null && Enum.TryParse(name.Trim(), ignoreCase: false, out feature) && Enum.IsDefined(feature)
            && Array.IndexOf(PopulationFeatures, feature) >= 0;
    }

    /// <summary>One monster's level for a mission of <paramref name="quality"/>, from the room's seeded <paramref name="roll"/>.</summary>
    public int RollLevel(Random roll, int quality)
    {
        ArgumentNullException.ThrowIfNull(roll);
        int spread = LevelSpreadPercent ?? 0;
        if (spread <= 0 || quality <= 0)
            return quality;
        int low = Math.Max(1, (int)Math.Round(quality * (100 - spread) / 100.0, MidpointRounding.AwayFromZero));
        int high = Math.Max(low, (int)Math.Round(quality * (100 + spread) / 100.0, MidpointRounding.AwayFromZero));
        return roll.Next(low, high + 1);
    }

    internal string? Problem()
    {
        if (Mix != null)
        {
            if (Mix.Count == 0 || Mix.Keys.Any(k => !Categories.Contains(k)) || Mix.Values.Any(w => !double.IsFinite(w) || w < 0) || Mix.Values.Sum() <= 0)
                return "Population.Mix needs weights (0 or more, not all 0) for human, monster or robot";
            if (Styles != null && (Styles.Keys.Any(k => !StyleNames.Contains(k)) || Styles.Values.Any(w => !double.IsFinite(w) || w < 0) || Styles.Values.Sum() <= 0))
                return "Population.Styles needs weights (0 or more, not all 0) for physical, mystical or mixed";
            if (Kinds is not [int fewest, int most] || fewest < 1 || most < fewest || most > MaxKinds)
                return "Population.Kinds must be [lowest, highest] with 1 <= lowest <= highest <= " + MaxKinds;
        }
        else if (Npcs is not { Length: > 0 } || Npcs.Any(string.IsNullOrWhiteSpace))
            return "Population.Npcs must name at least one NPC hash (or Mix pick them)";
        if (AggroRangePercent is not (>= 0 and <= MaxRangePercent) || HelpRangePercent is not (>= 0 and <= MaxRangePercent))
            return "Population.AggroRangePercent and HelpRangePercent must be 0-" + MaxRangePercent;
        if (Aggressiveness is < 0 || Clan is < 0 || AssistanceRadius is < 0)
            return "Population.Aggressiveness, Clan and AssistanceRadius must be 0 or more";
        if (SpawnChancePercent is not (>= 0 and <= 100))
            return "Population.SpawnChancePercent must be 0-100";
        if (LevelSpreadPercent is not (>= 0 and <= MaxSpreadPercent))
            return "Population.LevelSpreadPercent must be 0-" + MaxSpreadPercent;
        if (MaxGroup is not >= 1)
            return "Population.MaxGroup must be at least 1";
        if (GroupRadius is not { } radius || !float.IsFinite(radius) || radius < 0)
            return "Population.GroupRadius must be 0 or more";
        if (Loot != null && !Loot.TrueForAll(Mobs.MobItemTableEntry.IsWellFormed))
            return "Population.Loot " + LootRowRule;
        if (Features != null && !Array.TrueForAll(Features, name => TryParseFeature(name, out _)))
            return "Population.Features may only list " + string.Join(", ", PopulationFeatures);
        if (Scales != null && Scales.Any(kv => string.IsNullOrWhiteSpace(kv.Key) || kv.Value is < 1 or > MaxScale))
            return "Population.Scales maps NPC hashes to a Scale of 1-" + MaxScale;
        return null;
    }

    internal const string LootRowRule = "rows need a Hash, Repeats of at least 1, Chance 1-100, LevelMod of at least 0 and QualityPercent 1-1000";
}

/// <summary>The chests: whether there are any, how many, which rooms they avoid, and what they hold.</summary>
public sealed class DungeonChestBlock : IDungeonBlock<DungeonChestBlock>
{
    public string? Preset { get; set; }

    public bool? Enabled { get; set; }

    /// <summary>Chests per eligible room: 0.3 puts one in about a third of the rooms, 2 about two in each.</summary>
    public double? RoomDensity { get; set; }

    /// <summary>How far the chest count varies either way, as a fraction (0 to 1).</summary>
    public double? CountVariation { get; set; }

    /// <summary>Parts of room names chests never stand in (hallway, stair, ...).</summary>
    public string[]? ExcludedRooms { get; set; }

    /// <summary>What a chest holds, rolled at the mission quality; empty: no chests.</summary>
    public List<Mobs.MobItemTableEntry>? LootTable { get; set; }

    /// <summary>Percent of chests that are locked (a Break and Enter lock at the mission quality).</summary>
    public int? LockedChancePercent { get; set; }

    public const double MaxRoomDensity = 4;

    public DungeonChestBlock Over(DungeonChestBlock under) => new()
    {
        LockedChancePercent = LockedChancePercent ?? under.LockedChancePercent,
        Enabled = Enabled ?? under.Enabled,
        RoomDensity = RoomDensity ?? under.RoomDensity,
        CountVariation = CountVariation ?? under.CountVariation,
        ExcludedRooms = ExcludedRooms ?? under.ExcludedRooms,
        LootTable = LootTable ?? under.LootTable
    };

    /// <summary>True when the dungeon gets chests: enabled, a density above 0 and something to hold.</summary>
    [JsonIgnore]
    public bool Places => Enabled == true && RoomDensity > 0 && LootTable is { Count: > 0 };

    /// <summary>The chest count over <paramref name="roomCount"/> eligible rooms; at least one when any room is eligible.</summary>
    public int RollCount(int roomCount, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (roomCount < 0)
            throw new ArgumentOutOfRangeException(nameof(roomCount));
        double density = RoomDensity ?? 0;
        double factor = 1 + ((2 * random.NextDouble() - 1) * (CountVariation ?? 0));
        double count = Math.Round(roomCount * density * factor, MidpointRounding.AwayFromZero);
        if (roomCount > 0 && density > 0)
            count = Math.Max(1, count);
        return checked((int)count);
    }

    internal string? Problem()
    {
        if (Enabled == null)
            return "Chests.Enabled must be set";
        if (LockedChancePercent is not (>= 0 and <= 100))
            return "Chests.LockedChancePercent must be 0-100";
        if (RoomDensity is not { } density || !double.IsFinite(density) || density is < 0 or > MaxRoomDensity)
            return "Chests.RoomDensity must be 0-" + MaxRoomDensity.ToString(CultureInfo.InvariantCulture);
        if (CountVariation is not { } variation || !double.IsFinite(variation) || variation is < 0 or > 1)
            return "Chests.CountVariation must be 0-1";
        if (ExcludedRooms == null || ExcludedRooms.Any(string.IsNullOrWhiteSpace))
            return "Chests.ExcludedRooms must be a list of name parts (it may be empty)";
        if (LootTable != null && !LootTable.TrueForAll(Mobs.MobItemTableEntry.IsWellFormed))
            return "Chests.LootTable " + DungeonPopulationBlock.LootRowRule;
        return null;
    }
}

/// <summary>Where the objective stands and how strong an objective NPC is.</summary>
public sealed class DungeonObjectiveBlock : IDungeonBlock<DungeonObjectiveBlock>
{
    public string? Preset { get; set; }

    /// <summary>The objective stands in one of this many rooms farthest from the entrance (1: the farthest). A multi-floor dungeon's stays in its boss room.</summary>
    public int? RoomChoices { get; set; }

    /// <summary>Percent added to the mission quality for an objective NPC's level (-90 to 400).</summary>
    public int? LevelPercent { get; set; }

    public const int MaxRoomChoices = 10;

    public DungeonObjectiveBlock Over(DungeonObjectiveBlock under) => new()
    {
        RoomChoices = RoomChoices ?? under.RoomChoices,
        LevelPercent = LevelPercent ?? under.LevelPercent
    };

    /// <summary>The objective NPC's level for a mission of <paramref name="quality"/>.</summary>
    public int LevelFor(int quality)
        => Math.Max(1, (int)Math.Round(quality * (100 + (LevelPercent ?? 0)) / 100.0, MidpointRounding.AwayFromZero));

    internal string? Problem()
    {
        if (RoomChoices is not (>= 1 and <= MaxRoomChoices))
            return "Objective.RoomChoices must be 1-" + MaxRoomChoices;
        if (LevelPercent is not (>= -90 and <= 400))
            return "Objective.LevelPercent must be -90 to 400";
        return null;
    }
}

/// <summary>
/// A request with every block resolved to its full values: what a mission freezes at acceptance, so its dungeon rebuilds
/// the same whatever the presets become later.
/// </summary>
public sealed class ResolvedDungeon
{
    public DungeonLayoutBlock Layout { get; set; } = new();

    public DungeonPopulationBlock Population { get; set; } = new();

    public DungeonChestBlock Chests { get; set; } = new();

    public DungeonObjectiveBlock Objective { get; set; } = new();

    /// <summary>Why this frozen dungeon cannot be built, or null.</summary>
    internal string? Problem()
        => Layout.Problem() ?? Population.Problem() ?? Chests.Problem() ?? Objective.Problem();
}

/// <summary>A slot holds a preset name or a block; a name reads as a block naming that preset.</summary>
public sealed class DungeonBlockConverter<T> : JsonConverter<T> where T : class, IDungeonBlock<T>, new()
{
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        if (reader.TokenType == JsonTokenType.String)
            return new T { Preset = reader.GetString() };
        // The converter sits on the request's properties, not on T, so this reads the block's own fields.
        return JsonSerializer.Deserialize<T>(ref reader, options);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, options);
}

/// <summary>
/// GameData/Dungeons: Defaults.json (one block of each kind, every field set) and one file of named presets per block
/// kind (Layouts.json, Population.json, Chests.json, Objectives.json, each <c>{ "name": { block } }</c>). Resolves a
/// request to its full values.
/// </summary>
public sealed class DungeonPresets
{
    public const string Folder = "Dungeons";

    sealed class DefaultsFile
    {
        public DungeonLayoutBlock Layout { get; set; } = new();
        public DungeonPopulationBlock Population { get; set; } = new();
        public DungeonChestBlock Chests { get; set; } = new();
        public DungeonObjectiveBlock Objective { get; set; } = new();
    }

    readonly DefaultsFile _defaults;
    readonly Dictionary<string, DungeonLayoutBlock> _layouts;
    readonly Dictionary<string, DungeonPopulationBlock> _population;
    readonly Dictionary<string, DungeonChestBlock> _chests;
    readonly Dictionary<string, DungeonObjectiveBlock> _objectives;
    readonly IGameData _gameData;

    DungeonPresets(IGameData gameData, DefaultsFile defaults, Dictionary<string, DungeonLayoutBlock> layouts,
        Dictionary<string, DungeonPopulationBlock> population, Dictionary<string, DungeonChestBlock> chests,
        Dictionary<string, DungeonObjectiveBlock> objectives)
    {
        _gameData = gameData;
        _defaults = defaults;
        _layouts = layouts;
        _population = population;
        _chests = chests;
        _objectives = objectives;
    }

    /// <summary>Reads GameData/Dungeons; throws naming what is wrong. Every preset must resolve over the defaults.</summary>
    public static DungeonPresets Load(IGameData gameData, IZoneLogger logger)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        string folder = Path.Combine(gameData.RootPath, Folder);
        DefaultsFile defaults = Read<DefaultsFile>(Path.Combine(folder, "Defaults.json"))
            ?? throw new InvalidDataException(Folder + "/Defaults.json is empty");
        var presets = new DungeonPresets(gameData, defaults,
            Named<DungeonLayoutBlock>(folder, "Layouts.json"), Named<DungeonPopulationBlock>(folder, "Population.json"),
            Named<DungeonChestBlock>(folder, "Chests.json"), Named<DungeonObjectiveBlock>(folder, "Objectives.json"));
        presets.LoadFamilies(folder);

        var problems = new List<string>();
        void Check(string where, string? problem)
        {
            if (problem != null)
                problems.Add(where + ": " + problem);
        }
        Check("Defaults", defaults.Layout.Problem() ?? defaults.Population.Problem() ?? defaults.Chests.Problem() ?? defaults.Objective.Problem());
        foreach (string name in presets._layouts.Keys)
        {
            string? problem = ResolveBlock(new DungeonLayoutBlock { Preset = name }, presets._layouts, defaults.Layout, out var b);
            // A banded layout is checked once per band, at the band's lowest quality.
            foreach (int quality in b?.Bands?.Select(band => band?.Quality is [int lowest, _] ? lowest : 0) ?? [1])
                problem ??= b!.ForQuality(quality, out string? bandProblem)?.Problem() ?? bandProblem;
            Check("Layouts." + name, problem);
        }
        foreach (string name in presets._population.Keys)
            Check("Population." + name, ResolveBlock(new DungeonPopulationBlock { Preset = name }, presets._population, defaults.Population, out var b) ?? presets.PopulationProblem(b!));
        foreach (string name in presets._chests.Keys)
            Check("Chests." + name, ResolveBlock(new DungeonChestBlock { Preset = name }, presets._chests, defaults.Chests, out var b) ?? b!.Problem());
        foreach (string name in presets._objectives.Keys)
            Check("Objectives." + name, ResolveBlock(new DungeonObjectiveBlock { Preset = name }, presets._objectives, defaults.Objective, out var b) ?? b!.Problem());
        Check("Defaults.Population", presets.PopulationProblem(defaults.Population));
        if (problems.Count > 0)
            throw new InvalidDataException(Folder + ": " + string.Join("; ", problems));

        logger.Info(string.Format(CultureInfo.InvariantCulture, "GameData dungeon presets layouts={0} population={1} chests={2} objectives={3} missionFamilies={4}",
            presets._layouts.Count, presets._population.Count, presets._chests.Count, presets._objectives.Count, presets._families.Count));
        return presets;
    }

    /// <summary>
    /// The request's blocks over their presets over the defaults, the layout's band for <paramref name="quality"/>
    /// applied; null with the reason when it does not resolve.
    /// </summary>
    public ResolvedDungeon? Resolve(DungeonRequest? request, int quality, out string? problem)
    {
        request ??= new DungeonRequest();
        string? layoutProblem = ResolveBlock(request.Layout, _layouts, _defaults.Layout, out DungeonLayoutBlock? layout);
        string? populationProblem = ResolveBlock(request.Population, _population, _defaults.Population, out DungeonPopulationBlock? population);
        string? chestProblem = ResolveBlock(request.Chests, _chests, _defaults.Chests, out DungeonChestBlock? chests);
        string? objectiveProblem = ResolveBlock(request.Objective, _objectives, _defaults.Objective, out DungeonObjectiveBlock? objective);
        problem = layoutProblem ?? populationProblem ?? chestProblem ?? objectiveProblem;
        if (problem != null)
            return null;
        layout = layout!.ForQuality(quality, out problem);
        if (layout == null)
            return null;
        var resolved = new ResolvedDungeon { Layout = layout, Population = population!, Chests = chests!, Objective = objective! };
        problem = resolved.Problem() ?? PopulationProblem(resolved.Population);
        return problem == null ? resolved : null;
    }

    /// <summary>Whether every population NPC has a template (the block's shape is checked by its own Problem).</summary>
    string? PopulationProblem(DungeonPopulationBlock block)
        => block.Problem() ?? (block.Mix == null && block.Npcs!.FirstOrDefault(hash => !_gameData.HasMobTemplate(hash)) is string missing
            ? "Population.Npcs " + missing + " has no NPC template" : null);

    /// <summary>
    /// A block with <see cref="DungeonPopulationBlock.Mix"/> turned into a fixed NPC list for a mission of
    /// <paramref name="quality"/>: <see cref="DungeonPopulationBlock.Kinds"/> families, each from a category rolled by the
    /// Mix weights (and, for humans, a style rolled by the Styles weights), among the families fit for missions whose
    /// levels hold the quality, each family once. A rolled bucket with none falls back to any fit family of that
    /// category, then of any category. Null with the reason when no family holds the quality at all.
    /// </summary>
    public DungeonPopulationBlock? Pick(DungeonPopulationBlock block, int quality, int playerSide, Random random, out string? problem)
    {
        problem = null;
        if (block.Mix == null)
            return block;
        // A Clan (1) or Omni (2) player never meets humans of their own side: only the other side's or neutral ones.
        var fit = _families.Where(f => quality >= f.Low && quality <= f.High
            && !(f.Category == "human" && playerSide is 1 or 2 && f.Side == playerSide)).ToList();
        if (fit.Count == 0)
        {
            problem = "no mission family holds quality " + quality.ToString(CultureInfo.InvariantCulture);
            return null;
        }
        // Stock picks two or three of its NPC families (stat 455) per mission and spawns their members at the mission's
        // level (LiveCapture runs: Tac-V88 Criminal / Fugitive / Runaway of family 70 with the wolves of family 72). Each
        // pick rolls a category and, for humans, a style; a stock family with a member of that bucket is drawn, and all of
        // its fit members of that category (of that style when it has some) join the population.
        var picked = new List<string>();
        var usedFamilies = new HashSet<int>();
        int kinds = random.Next(block.Kinds![0], block.Kinds[1] + 1);
        for (int i = 0; i < kinds; i++)
        {
            string category = Roll(block.Mix, random);
            string? style = category == "human" && block.Styles != null ? Roll(block.Styles, random) : null;
            bool Free(MissionFamily f) => f.Family is int n && !usedFamilies.Contains(n);
            var pool = fit.Where(f => Free(f) && f.Category == category && (style == null || f.Style == style)).ToList();
            if (pool.Count == 0)
                pool = fit.Where(f => Free(f) && f.Category == category).ToList();
            if (pool.Count == 0)
                pool = fit.Where(Free).ToList();
            if (pool.Count == 0)
                break;
            // One member drawn decides the stock family, so a family's chance grows with its members in the bucket.
            MissionFamily drawn = pool[random.Next(pool.Count)];
            int family = drawn.Family!.Value;
            usedFamilies.Add(family);
            var members = fit.Where(f => f.Family == family && f.Category == drawn.Category).ToList();
            var styled = style == null ? members : members.Where(f => f.Style == style).ToList();
            foreach (MissionFamily member in styled.Count > 0 ? styled : members)
                if (!picked.Contains(member.Hash))
                    picked.Add(member.Hash);
        }
        DungeonPopulationBlock frozen = new DungeonPopulationBlock { Npcs = picked.ToArray() }.Over(block);
        frozen.Mix = null;
        frozen.Styles = null;
        frozen.Kinds = null;
        return frozen;
    }

    static string Roll(Dictionary<string, double> weights, Random random)
    {
        double roll = random.NextDouble() * weights.Values.Sum();
        foreach ((string key, double weight) in weights.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            roll -= weight;
            if (roll < 0)
                return key;
        }
        return weights.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key, StringComparer.Ordinal).Last().Key;
    }

    /// <summary>A Families.json family fit for mission populations.</summary>
    sealed record MissionFamily(string Hash, int? Family, string Category, string? Style, int? Side, int Low, int High);

    sealed class FamilyRow
    {
        public int? Family { get; set; }
        public string? Category { get; set; }
        public string? Style { get; set; }
        public int? Side { get; set; }
        public int[]? Levels { get; set; }
        public bool Mission { get; set; }
    }

    List<MissionFamily> _families = [];

    /// <summary>Families.json: the families fit for missions (Mission true) that have an NPC template, with their buckets and levels.</summary>
    void LoadFamilies(string folder)
    {
        string path = Path.Combine(folder, "Families.json");
        if (!File.Exists(path))
            return;
        var rows = JsonSerializer.Deserialize<Dictionary<string, FamilyRow>>(File.ReadAllText(path), DungeonRequest.JsonOptions) ?? [];
        _families = rows.Where(kv => kv.Value is { Mission: true, Levels: [_, _], Category: not null } && _gameData.HasMobTemplate(kv.Key))
            .Where(kv => kv.Value.Family != null)
            .Select(kv => new MissionFamily(kv.Key, kv.Value.Family, kv.Value.Category!, kv.Value.Style, kv.Value.Side, kv.Value.Levels![0], kv.Value.Levels[1]))
            .OrderBy(f => f.Hash, StringComparer.Ordinal)
            .ToList();
    }

    public int MissionFamilyCount => _families.Count;

    /// <summary><paramref name="block"/> over its preset (one level: a preset does not name another) over the defaults.</summary>
    static string? ResolveBlock<T>(T? block, Dictionary<string, T> named, T defaults, out T? resolved) where T : class, IDungeonBlock<T>
    {
        resolved = null;
        T under = defaults;
        if (block?.Preset is string name)
        {
            if (!named.TryGetValue(name, out T? preset))
                return "no " + typeof(T).Name.Replace("Dungeon", string.Empty).Replace("Block", string.Empty) + " preset '" + name + "'";
            under = preset.Over(defaults);
        }
        resolved = block == null ? under.Over(under) : block.Over(under);
        return null;
    }

    static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(path + " not found", path);
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), DungeonRequest.JsonOptions);
    }

    static Dictionary<string, T> Named<T>(string folder, string file) where T : class, IDungeonBlock<T>
    {
        string path = Path.Combine(folder, file);
        if (!File.Exists(path))
            return new Dictionary<string, T>(StringComparer.Ordinal);
        var named = JsonSerializer.Deserialize<Dictionary<string, T>>(File.ReadAllText(path), DungeonRequest.JsonOptions)
            ?? new Dictionary<string, T>();
        foreach ((string name, T block) in named)
        {
            if (block == null)
                throw new InvalidDataException(Folder + "/" + file + ": preset '" + name + "' is empty");
            if (block.Preset != null)
                throw new InvalidDataException(Folder + "/" + file + ": preset '" + name + "' names another preset; presets start from the defaults");
        }
        return new Dictionary<string, T>(named, StringComparer.Ordinal);
    }
}
