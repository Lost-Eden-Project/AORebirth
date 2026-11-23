namespace ZoneEngine_New.Core.Quests.Dungeons.Static;

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// GameData/Dungeons/Static/&lt;entrance instance&gt;.json as written: one fixed ACG dungeon behind a world door,
/// captured from live. The building, its monsters (on district spawn points), lifts and controllers are fixed;
/// chests, mines and turrets stand on spots the style's site pool (<see cref="StaticDungeonSitePoolFile"/>) holds
/// and are rolled anew for every instance. Positions are dungeon world coordinates; rotations are XYZW.
/// </summary>
public sealed class StaticDungeonFile
{
    public const int CurrentSchemaVersion = 1;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public int SchemaVersion { get; set; }

    /// <summary>The door's name, as the entrance placement carries it.</summary>
    public string? Name { get; set; }

    public StaticDungeonEntrance? Entrance { get; set; }

    public StaticDungeonBuilding? Building { get; set; }

    /// <summary>Where an entering character is put down, just inside the way-in door.</summary>
    public StaticDungeonArrival? Arrival { get; set; }

    /// <summary>"Team": one instance per team (a player without a team gets their own).</summary>
    public string? Instance { get; set; }

    /// <summary>Null: each NPC spawns once per instance. A number: a killed NPC respawns at its spot after that many seconds.</summary>
    public int? RespawnSeconds { get; set; }

    /// <summary>A Dungeons/Population.json block: what <see cref="DungeonPopulationBlock.ApplyTo"/> puts on every NPC.</summary>
    public string? Behaviour { get; set; }

    /// <summary>The DungeonDoorStyles.json style for the doorways.</summary>
    public int? DoorStyle { get; set; }

    public List<StaticDungeonTransition>? Transitions { get; set; }

    public List<StaticDungeonNpc>? Npcs { get; set; }

    public StaticDungeonChests? Chests { get; set; }

    public StaticDungeonMines? Mines { get; set; }

    public StaticDungeonTurrets? Turrets { get; set; }

    public List<StaticDungeonController>? Controllers { get; set; }
}

public sealed class StaticDungeonEntrance
{
    /// <summary>The ACG entrance dynel type, 56006 (0xDAC6).</summary>
    public uint IdentityType { get; set; }

    public uint IdentityInstance { get; set; }

    /// <summary>The playfield the entrance stands on.</summary>
    public int? Playfield { get; set; }
}

/// <summary>The client's ACGBuildingGeneratorData_t, field by field; sent exactly as given.</summary>
public sealed class StaticDungeonBuilding
{
    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>Stock sends 0 for the Shadowlands style: floors sit side by side in X, not stacked in Y.</summary>
    public int FloorHeight { get; set; }

    /// <summary>The ACG style playfield whose Rooms.json the rows index.</summary>
    public int TemplatePlayfield { get; set; }

    /// <summary>Ambient light, three bytes.</summary>
    public int[]? Ambient { get; set; }

    public List<StaticDungeonRoom>? Rooms { get; set; }
}

/// <summary>One BuildingRoomInfo_t row: the room template index in the style's Rooms.json, floor, grid cell and facing.</summary>
public sealed class StaticDungeonRoom
{
    public int Room { get; set; }

    public int Floor { get; set; }

    public int X { get; set; }

    public int Z { get; set; }

    public int Rotation { get; set; }
}

public sealed class StaticDungeonArrival
{
    public float[]? Position { get; set; }

    public float[]? Heading { get; set; }
}

public sealed class StaticDungeonTransition
{
    public StaticDungeonTransitionEndpoint? From { get; set; }

    public StaticDungeonTransitionEndpoint? To { get; set; }
}

/// <summary>A lift end: the room (an index into Building.Rooms), the platform's position and the platform and button item templates.</summary>
public sealed class StaticDungeonTransitionEndpoint
{
    public int Room { get; set; }

    public float[]? Position { get; set; }

    public int Platform { get; set; }

    public int Button { get; set; }
}

/// <summary>
/// One fixed monster: the NPC hash at <see cref="Level"/>, on spawn point <see cref="Point"/> of the district its
/// room's template maps to in the style's Districts.json, facing <see cref="Heading"/>. <see cref="Name"/> and
/// <see cref="MonsterData"/> say which live monster the capture saw; an entry whose hash is not in NpcTemplates is
/// left out and reported until a template for that name exists and the file carries its hash.
/// </summary>
public sealed class StaticDungeonNpc
{
    public string? Hash { get; set; }

    public string? Name { get; set; }

    public int? MonsterData { get; set; }

    public int Level { get; set; }

    public int Room { get; set; }

    public int Point { get; set; }

    public float[]? Heading { get; set; }

    /// <summary>Where the capture saw it (tools and tests only; the server places it on its point).</summary>
    public float[]? Captured { get; set; }
}

public sealed class StaticDungeonChests
{
    /// <summary>Chest item templates (appearance); each chest takes one at random.</summary>
    public List<int>? Templates { get; set; }

    /// <summary>The QL the chests show and roll their loot at.</summary>
    public int Quality { get; set; }

    /// <summary>A Dungeons/Chests.json block whose LootTable each chest rolls.</summary>
    public string? Loot { get; set; }

    public int LockDifficulty { get; set; }

    /// <summary>Percent of chests holding a trap from <see cref="Traps"/> (not yet spawned: the client message is not in the protocol library).</summary>
    public int TrapChancePercent { get; set; }

    public List<int>? Traps { get; set; }
}

/// <summary>Mine item templates and QL (recorded; not yet spawned: the client message is not in the protocol library).</summary>
public sealed class StaticDungeonMines
{
    public List<int>? Templates { get; set; }

    public int Quality { get; set; }
}

/// <summary>The dungeon's sentry turret NPC, spawned on the site pool's turret spots.</summary>
public sealed class StaticDungeonTurrets
{
    public string? Hash { get; set; }

    public string? Name { get; set; }

    public int? MonsterData { get; set; }

    public int Level { get; set; }
}

/// <summary>A central (mine or sentry) controller (recorded; not yet spawned: the client message is not in the protocol library).</summary>
public sealed class StaticDungeonController
{
    public int Template { get; set; }

    public int Quality { get; set; }

    public string? Name { get; set; }

    public float[]? Position { get; set; }

    public int Room { get; set; }
}

/// <summary>
/// GameData/Dungeons/Sites/&lt;style&gt;.json: every chest, mine and turret spot ever captured in each of the style's
/// room templates, in the template's own coordinates (X and Z as Rooms.json and Districts.json have them, Y the
/// height above the placed room's floor), with how many of each a placed room of that template held on average.
/// </summary>
public sealed class StaticDungeonSitePoolFile
{
    public int SchemaVersion { get; set; }

    public int Style { get; set; }

    /// <summary>By room template name (Rooms.json name).</summary>
    public Dictionary<string, StaticDungeonRoomSites>? Rooms { get; set; }
}

public sealed class StaticDungeonRoomSites
{
    public StaticDungeonSiteKind? Chests { get; set; }

    public StaticDungeonSiteKind? Mines { get; set; }

    public StaticDungeonSiteKind? Turrets { get; set; }
}

public sealed class StaticDungeonSiteKind
{
    /// <summary>Items captured in placed rooms of the template, over placed rooms of the template captured.</summary>
    public double PerRoom { get; set; }

    public List<StaticDungeonSpot>? Spots { get; set; }
}

public sealed class StaticDungeonSpot
{
    public float[]? Position { get; set; }

    /// <summary>Facing in the template's frame; null: a random heading.</summary>
    public float[]? Heading { get; set; }

    /// <summary>How many captures had something on this spot (tools only).</summary>
    public int Seen { get; set; }
}
