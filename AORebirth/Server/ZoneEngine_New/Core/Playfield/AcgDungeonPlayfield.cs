namespace ZoneEngine_New.Core.Playfield;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using AORebirth.Core.GameData;

using AORebirth.Interfaces.Persistence.Shops;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages.N3Messages;

using Utility.GameData.Missions;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Metrics;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.Quests.Dungeons;
using ZoneEngine_New.Core.Trade;
using ZoneEngine_New.Core.WorldSimulation;

/// <summary>
/// What every ACG building the server runs shares, whoever asked for it: the room geometry and collision built from
/// the building generator (<see cref="DungeonPlayfieldBinder"/>), the locality rooms, the world simulation and the
/// pathfinder, the doors with their twins, the floor lifts, the exit door that walks its users back out in front of
/// the entrance, the client's landing veto and the PlayfieldAnarchyF the client builds the building from.
/// <see cref="QuestDungeonPlayfield"/> (a mission's dungeon) and <see cref="StaticDungeonPlayfield"/> (a fixed
/// dungeon behind a world door) both derive from it; what stands inside is theirs.
/// </summary>
public abstract class AcgDungeonPlayfield : Playfield
{
    PlayfieldWorldSimulation? _simulation;
    AORebirth.World.Collision.DungeonWorldLayout? _dungeon;

    protected AcgDungeonPlayfield(int playfieldId, DungeonLayout layout, MissionEntrancePlacement entrance, int quality,
        IZoneLogger logger, IMessageRouter router, PlayfieldManager manager, PlayerHydrator hydrator, IGameData data,
        IItemBuilder items, HashItemMinter hashItems, IInventoryRepository inventory, IItemInstanceIdAllocator ids,
        InventoryMoveService moves, InventoryFlushService flush, TradeService trades, CharacterSnapshotService snapshot,
        IPlayfieldMetricsRegistry metrics, IShopDao shopDao)
        : base(new Identity { Type = IdentityType.Playfield, Instance = playfieldId }, logger, router,
            manager, hydrator, data, items, hashItems, inventory, ids, moves, flush, trades, snapshot, metrics, shopDao)
    {
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
        Entrance = entrance ?? throw new ArgumentNullException(nameof(entrance));
        Quality = Math.Max(0, quality);
    }

    public DungeonLayout Layout { get; }

    /// <summary>Where the dungeon is entered from, and where its occupants return to.</summary>
    public MissionEntrancePlacement Entrance { get; }

    /// <summary>The dungeon's quality level (0 when unknown); locked doors use it as their lock difficulty.</summary>
    public int Quality { get; }

    /// <summary>The population's scale on every monster's unprovoked aggro range here, as a factor (1: unchanged).</summary>
    internal float AggroRangeScale => (Layout.Population.AggroRangePercent ?? 100) / 100f;

    /// <summary>The population's scale on how far a monster's call for help carries here, as a factor (1: unchanged).</summary>
    internal float HelpRangeScale => (Layout.Population.HelpRangePercent ?? 100) / 100f;

    /// <summary>"Quest dungeon D123" or "Static dungeon 3221360854": how this dungeon names itself in the log.</summary>
    protected abstract string Label { get; }

    /// <summary>The world simulation, once <see cref="BuildWorld"/> gave the dungeon collision.</summary>
    protected PlayfieldWorldSimulation? Simulation => _simulation;

    /// <summary>The placed rooms, doors and collision <see cref="BuildWorld"/> produced; null when the style's geometry failed.</summary>
    protected AORebirth.World.Collision.DungeonWorldLayout? Dungeon => _dungeon;

    /// <summary>
    /// Builds the room geometry from the layout's generator, lays its rooms over the locality cells, starts the world
    /// simulation on its collision and takes the baked navmesh. Returns the placed layout, or null when the style's
    /// geometry could not be built (logged by the binder); the dungeon then has no rooms to furnish.
    /// </summary>
    protected AORebirth.World.Collision.DungeonWorldLayout? BuildWorld(PlayfieldLocality locality)
    {
        ArgumentNullException.ThrowIfNull(locality);
        AORebirth.World.Collision.DungeonWorldLayout? dungeon = DungeonPlayfieldBinder.TryBuild(
            GameData.RootPath, Identity.Instance, Layout.Generator, Logger);
        _dungeon = dungeon;
        if (dungeon != null)
            locality.ApplyDungeonRooms(dungeon);

        var geometry = DungeonPlayfieldBinder.WithDungeonCollision(Identity.Instance, Geometry, dungeon);
        if (geometry.Collision?.HasCollision == true)
        {
            _simulation = PlayfieldWorldSimulation.Create(
                Identity.Instance, geometry, MetaData, DestinationsCatalog.Instance, GameData, Logger, ItemTemplates);
            RegisterWorldServices(_simulation);
            // Before anything spawns: NPC placement and wandering go through the pathfinder. The bake normally
            // finished while the player travelled to the entrance; this build runs off the tick, so waiting is fine.
            if (dungeon != null)
                Pathfinder = DungeonNavMeshes.TryCreatePathfinder(Identity.Instance, Layout);
        }

        return dungeon;
    }

    /// <summary>
    /// The client vetoes a placement outside every room: N3.dll n3RoomSurface_t::VetoRoomTransition (0x1001462f) moves
    /// the character to Gamecode.dll PlayfieldAnarchy_t::GetSafePos (0x10121b3f), the middle of room 0
    /// (<see cref="AORebirth.World.Collision.DungeonSafePosition"/>), and keeps its heading. Left at the vetoed point
    /// the server refused every move the client then sent (the Exit Device's (1, 0, 1) rubberbanded non-GMs).
    /// </summary>
    internal bool TryVetoLanding(AORebirth.Core.Vector.Vector3 landing, out AORebirth.Core.Vector.Vector3 safe)
    {
        safe = landing;
        if (_dungeon?.SafePosition is not System.Numerics.Vector3 position
            || GetService<PlayfieldLocality>()?.IsOutsideDungeonRooms(landing) != true)
            return false;

        safe = new AORebirth.Core.Vector.Vector3(position.X, position.Y, position.Z);
        return true;
    }

    static readonly ConcurrentDictionary<(string Root, int Style), PlayfieldDistrictsData?> StyleDistricts = new();

    static readonly JsonSerializerOptions DistrictJsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The style playfield's Districts.json, read once per style; null (logged) when it has none.</summary>
    internal static PlayfieldDistrictsData? LoadStyleDistricts(string root, int style, IZoneLogger logger)
        => StyleDistricts.GetOrAdd((root, style), key =>
        {
            string path = Path.Combine(key.Root, GameDataPaths.PlayfieldDistrictsRelativePath(key.Style));
            if (!File.Exists(path))
            {
                logger.Warn("No Districts.json for dungeon style " + key.Style + " at " + path + "; dungeon has no NPC spawns");
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<PlayfieldDistrictsData>(File.ReadAllText(path), DistrictJsonOptions);
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Failed to read " + path);
                return null;
            }
        });

    /// <summary>
    /// The district a placed room's template maps to in the style's Districts.json, with its spawn points; null when
    /// the style has no district for that room.
    /// </summary>
    internal static PlayfieldDistrictEntry? DistrictOf(PlayfieldDistrictsData districts, AORebirth.World.Collision.DungeonRoomPlacement placement)
    {
        if (districts.Districts == null)
            return null;
        int zone = placement.Template.InstanceId;
        int districtIndex = districts.ZoneToDistrictMap is { } map && zone >= 0 && zone < map.Length ? map[zone] : zone;
        foreach (PlayfieldDistrictEntry district in districts.Districts)
        {
            if (district != null && district.DistrictIndex == districtIndex)
                return district;
        }
        return null;
    }

    protected void SpawnFloorTransitions(PlayfieldLocality locality)
    {
        var registry = GetRequiredService<DynelRegistry>();
        foreach (var transition in Layout.FloorTransitions)
        {
            SpawnEndpoint(transition.From, transition.To);
            SpawnEndpoint(transition.To, transition.From);
        }

        void SpawnEndpoint(AORebirth.DungeonGenerator.DungeonTransitionEndpoint source,
            AORebirth.DungeonGenerator.DungeonTransitionEndpoint target)
        {
            foreach (int templateId in new[] { source.PlatformTemplateId, source.ButtonTemplateId })
            {
                if (!ItemTemplates.TryGet(templateId, out ItemTemplate? template) || template == null)
                    throw new InvalidDataException("Dungeon floor transition item template is unavailable: " + templateId);
                var dynel = new DungeonFloorTransitionDynel(registry.AllocateStaticDynelIdentity(),
                    template, new AORebirth.Core.Vector.Vector3(target.Position.X, target.Position.Y, target.Position.Z))
                {
                    Playfield = this,
                    Position = new AORebirth.Core.Vector.Vector3(source.Position.X, source.Position.Y, source.Position.Z),
                    // Platform/button updates retain world identity heading even in rotated captured rooms.
                    Rotation = new AORebirth.Core.Vector.Quaternion(0, 0, 0, 1)
                };
                registry.Register(dynel);
                locality.RegisterDynel(dynel);
            }
        }
    }

    /// <summary>First door instance; doors are numbered in layout order, exit first, as live dungeons do.</summary>
    const int FirstDoorInstance = 0x10000001;

    /// <summary>
    /// One Door per layout doorway, from the style's door templates (the exit has its own). The exit always starts
    /// open; interior doors roll locked (<see cref="AORebirth.DungeonGenerator.DungeonDoorLocks"/>) or open from the
    /// dungeon seed, so a rebuilt dungeon gets the same doors.
    /// <para>
    /// Every doorway between the same two rooms shares one state: the first one's roll. The client keeps one door per
    /// room pair (N3.dll n3Playfield_t::RegisterDoorWithRoomLink 0x1000d06f overwrites it, FindDoorIDFromRoomLink
    /// 0x1000d0e7 returns it) and its room-transition veto checks that one door whichever doorway is walked through,
    /// so a locked twin refused entry through an unlocked doorway (and an unlocked twin let players through a locked one).
    /// </para>
    /// </summary>
    protected void SpawnDoors(IReadOnlyList<AORebirth.World.Collision.DungeonDoorPlacement> placements, PlayfieldLocality locality)
    {
        DungeonDoorStyle? style = Layout.Doors;
        if (style == null || placements.Count == 0)
            return;

        var registry = GetRequiredService<DynelRegistry>();
        // Room pair (low room << 16 | high room) -> the doors spawned between them and the state the first one rolled.
        var pairs = new Dictionary<int, (int State, List<Door> Doors)>();
        for (int i = 0; i < placements.Count; i++)
        {
            AORebirth.World.Collision.DungeonDoorPlacement placement = placements[i];
            int templateId = placement.IsExit ? style.ExitTemplate : style.DoorTemplate;
            if (!ItemTemplates.TryGet(templateId, out ItemTemplate? template) || template == null)
            {
                Logger.Warn("Dungeon door template " + templateId + " is not in items.dat; door skipped");
                continue;
            }

            int flags = template.Stats.TryGetValue(CharacterStat.Flags, out int baseFlags) ? baseFlags : 0;
            flags &= ~(Door.OpenFlag | Door.LockedFlag);
            int pair = 0;
            if (placement.IsExit)
                flags |= Door.OpenFlag;
            else
            {
                pair = (Math.Min(placement.Room, placement.LinkedRoom) << 16) | (Math.Max(placement.Room, placement.LinkedRoom) & 0xFFFF);
                if (pairs.TryGetValue(pair, out var shared))
                    flags |= shared.State;
                else
                {
                    // Same seed and door index as the generator (and its viewer), so both agree on which doors are locked.
                    var roll = new Random(unchecked((Layout.Seed * 397) ^ (i + 1)));
                    if (AORebirth.DungeonGenerator.DungeonDoorLocks.IsLocked(Layout.Seed, i, style.LockedChancePercent))
                        flags |= Door.LockedFlag;
                    else if (roll.Next(100) < style.OpenChancePercent)
                        flags |= Door.OpenFlag;
                }
            }

            var door = new Door(new Identity { Type = IdentityType.Door, Instance = FirstDoorInstance + i }, templateId, flags,
                (placement.Room << 16) | (placement.LinkedRoom & 0xFFFF), lockDifficulty: Math.Max(1, Quality))
            {
                Playfield = this,
                Position = new AORebirth.Core.Vector.Vector3(placement.Position.X, placement.Position.Y, placement.Position.Z),
                Rotation = Door.Heading(placement.YawDegrees)
            };
            registry.Register(door);
            if (!placement.IsExit)
            {
                if (pairs.TryGetValue(pair, out var twins))
                {
                    foreach (Door twin in twins.Doors)
                    {
                        twin.AddTwin(door);
                        door.AddTwin(twin);
                    }
                    twins.Doors.Add(door);
                }
                else
                    pairs[pair] = (flags & (Door.OpenFlag | Door.LockedFlag), [door]);
            }
            // In its owning room's cell, not the cell its position resolves to: the door sits on that room's floor,
            // which a lower floor's room box (a floor tall from its own base) can also contain, and a door in the
            // wrong cell never reaches the players who can see it. The exit belongs to the entrance room.
            locality.RegisterDynel(door, placement.IsExit ? placement.LinkedRoom : placement.Room);
            if (_simulation != null && !_simulation.RegisterDoorBlocker(door))
                Logger.Warn("Dungeon door template " + templateId + " has no collision sphere; it does not block sight");

            if (placement.IsExit)
                RegisterExit(door);
        }
    }

    /// <summary>
    /// The exit door's walk-out volume: back outside the ACG entrance, a portal exit's clearance in front of the
    /// entrance along its heading, so the character lands clear of the entrance's own walk-in trigger.
    /// </summary>
    void RegisterExit(Door exit)
    {
        if (_simulation == null)
        {
            Logger.Warn(Label + " has no world simulation; its exit door has no walk-out");
            return;
        }

        AORebirth.Core.Vector.Vector3 landing = ExitLanding(Entrance, out AORebirth.Core.Vector.Quaternion entranceHeading);
        _simulation.RegisterDungeonExit(exit.Position.xf, exit.Position.yf, exit.Position.zf, exit.Identity.Instance,
            Entrance.PlayfieldId, landing, entranceHeading, exit.TemplateId, exit.Rotation);
    }

    /// <summary>Where the exit door puts a character down outside <paramref name="entrance"/>, facing as the entrance does.</summary>
    internal static AORebirth.Core.Vector.Vector3 ExitLanding(MissionEntrancePlacement entrance, out AORebirth.Core.Vector.Quaternion heading)
    {
        var entrancePosition = new AORebirth.Core.Vector.Vector3(entrance.LocalX, entrance.LocalY, entrance.LocalZ);
        // Raw placement components are WXYZ: exact-identity comparisons against the current Dynels reader
        // match all 2,242 catalog rows. The engine's Quaternion constructor takes XYZW.
        heading = new AORebirth.Core.Vector.Quaternion(
            entrance.RotationComponent1, entrance.RotationComponent2, entrance.RotationComponent3, entrance.RotationComponent0);
        return PortalDoorLandingResolver.LandingInFront(entrancePosition, heading, PortalDoorLandingResolver.ExitDoorClearance);
    }

    protected override void OnDispose()
    {
        _simulation?.Dispose();
        _simulation = null;
        base.OnDispose();
    }

    /// <summary>
    /// The captured ACG PlayfieldAnarchyF shape: the building's entrance identity as the playfield, and the generator
    /// the client builds from inline under that identity.
    /// </summary>
    public override PlayfieldAnarchyFMessage CreatePlayfieldAnarchyFMessage(Vector3 coordinates) => new()
    {
        Identity = new Identity { Type = IdentityType.Playfield2, Instance = Identity.Instance },
        CharacterCoordinates = coordinates,
        PlayfieldId1 = Layout.Entrance,
        Unknown3 = 0,
        SG = 0,
        ProxyId = new Identity { Type = IdentityType.Playfield2, Instance = Identity.Instance },
        InlineIdentity = Layout.Entrance,
        InlineVersion = 1,
        BuildingGenerator = Layout.Generator,
        PlayfieldX = -1,
        PlayfieldZ = -1
    };
}
