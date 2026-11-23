namespace ZoneEngine_New.Core.Quests.Dungeons.Static
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text.Json;

    using AORebirth.Core.GameData;
    using AORebirth.Modules.Content;
    using AORebirth.World.Collision;

    using LostEden.Protocol;
    using LostEden.Protocol.GameData;

    using Utility.GameData.Missions;

    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Mobs;

    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>One fixed NPC of a static dungeon, resolved: the hash spawns at this level on this room's district point, facing this way.</summary>
    public sealed record StaticNpcPlacement(string Hash, int Level, int Room, int Point, int HeadingDegrees);

    /// <summary>The dungeon's chests: appearances, QL, the loot rows each rolls (none: the chests spawn empty), and the trap share (recorded).</summary>
    public sealed record StaticChestPlan(IReadOnlyList<int> Templates, int Quality, IReadOnlyList<MobItemTableEntry> LootTable,
        int LockDifficulty, int TrapChancePercent, IReadOnlyList<int> Traps);

    public sealed record StaticTurretPlan(string Hash, int Level);

    /// <summary>A style's site pool, validated: chest, mine and turret spots per room template name.</summary>
    public sealed class StaticDungeonSitePool
    {
        public StaticDungeonSitePool(int style, IReadOnlyDictionary<string, StaticDungeonRoomSites> rooms)
        {
            Style = style;
            Rooms = rooms ?? throw new ArgumentNullException(nameof(rooms));
        }

        public int Style { get; }

        public IReadOnlyDictionary<string, StaticDungeonRoomSites> Rooms { get; }
    }

    /// <summary>
    /// A static dungeon file resolved against GameData: the layout the building is sent and built from, the entrance it
    /// is behind, its placed NPCs, its chest and turret plans and the site pool of its style. Immutable: a live instance
    /// keeps the blueprint it was built from while the catalog reloads underneath.
    /// </summary>
    public sealed class StaticDungeonBlueprint
    {
        internal StaticDungeonBlueprint(string fileName, string name, MissionEntrancePlacement entrance, DungeonLayout layout, int quality,
            int? respawnSeconds, IReadOnlyList<StaticNpcPlacement> npcs, int unresolvedNpcs, StaticChestPlan? chests, StaticTurretPlan? turrets,
            StaticDungeonMines? mines, IReadOnlyList<StaticDungeonController> controllers, StaticDungeonSitePool? sites)
        {
            FileName = fileName;
            Name = name;
            Entrance = entrance;
            Layout = layout;
            Quality = quality;
            RespawnSeconds = respawnSeconds;
            Npcs = npcs;
            UnresolvedNpcs = unresolvedNpcs;
            Chests = chests;
            Turrets = turrets;
            Mines = mines;
            Controllers = controllers;
            Sites = sites;
        }

        public string FileName { get; }

        public string Name { get; }

        public MissionEntrancePlacement Entrance { get; }

        public DungeonLayout Layout { get; }

        /// <summary>The chest QL, which locked doors also take as their difficulty; 1 without chests.</summary>
        public int Quality { get; }

        public int? RespawnSeconds { get; }

        public IReadOnlyList<StaticNpcPlacement> Npcs { get; }

        /// <summary>Captured NPCs the file names without a hash NpcTemplates knows; they do not spawn.</summary>
        public int UnresolvedNpcs { get; }

        public StaticChestPlan? Chests { get; }

        public StaticTurretPlan? Turrets { get; }

        public StaticDungeonMines? Mines { get; }

        public IReadOnlyList<StaticDungeonController> Controllers { get; }

        public StaticDungeonSitePool? Sites { get; }

        public int Style => Layout.Generator.TemplatePlayfield;
    }

    /// <summary>
    /// GameData/Dungeons/Static/*.json and Dungeons/Sites/*.json: the fixed dungeons behind world doors, keyed by their
    /// entrance identity, and the site pools of their styles. Reloadable as <c>StaticDungeons</c>: a reload reads every
    /// file again (the override folder's copy first) and swaps an immutable snapshot whole; a file that fails
    /// validation is left out and reported in the problems, the rest load. Only instances built after the reload use
    /// the new definitions.
    /// </summary>
    public sealed class StaticDungeonCatalog : IReloadableGameData
    {
        public const string ReloadName = "StaticDungeons";

        public const string StaticFolder = "Dungeons/Static";

        public const string SitesFolder = "Dungeons/Sites";

        /// <summary>Building identity type of a static dungeon (ACG building, as stock sends it).</summary>
        const int BuildingIdentityType = DungeonLayoutGenerator.BuildingIdentityType;

        const int MaxFloor = 16;
        const int MaxGrid = 0x20;

        sealed record Snapshot(IReadOnlyDictionary<MissionPlacementIdentity, StaticDungeonBlueprint> ByEntrance,
            IReadOnlyDictionary<int, StaticDungeonSitePool> Sites, IReadOnlyList<string> Problems);

        readonly IGameData _gameData;
        readonly MissionDestinationCatalog _entrances;
        readonly DungeonLayoutGenerator _layouts;
        readonly IItemTemplateCatalog _items;
        readonly IZoneLogger _logger;
        volatile Snapshot _snapshot = new(new Dictionary<MissionPlacementIdentity, StaticDungeonBlueprint>(),
            new Dictionary<int, StaticDungeonSitePool>(), []);

        public StaticDungeonCatalog(IGameData gameData, MissionDestinationCatalog entrances, DungeonLayoutGenerator layouts,
            IItemTemplateCatalog items, IZoneLogger logger)
        {
            _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
            _entrances = entrances ?? throw new ArgumentNullException(nameof(entrances));
            _layouts = layouts ?? throw new ArgumentNullException(nameof(layouts));
            _items = items ?? throw new ArgumentNullException(nameof(items));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            GameDataReloadResult loaded = Reload();
            foreach (string problem in loaded.Problems)
                _logger.Warn("Static dungeons: " + problem);
        }

        public string Name => ReloadName;

        public IReadOnlyList<string> Files { get; } = [StaticFolder, SitesFolder];

        /// <summary>The dungeons that loaded, by entrance.</summary>
        public IReadOnlyCollection<StaticDungeonBlueprint> Dungeons => _snapshot.ByEntrance.Values.ToArray();

        /// <summary>What the last load left out and why.</summary>
        public IReadOnlyList<string> Problems => _snapshot.Problems;

        public bool TryGet(uint entranceType, uint entranceInstance, out StaticDungeonBlueprint blueprint)
            => _snapshot.ByEntrance.TryGetValue(new MissionPlacementIdentity(entranceType, entranceInstance), out blueprint!);

        public bool TryGet(Identity entrance, out StaticDungeonBlueprint blueprint)
            => TryGet(unchecked((uint)entrance.Type), unchecked((uint)entrance.Instance), out blueprint);

        public GameDataReloadResult Reload()
        {
            string root = _gameData.RootPath;
            var problems = new List<string>();
            var sites = new Dictionary<int, StaticDungeonSitePool>();
            foreach (string file in ListFiles(root, SitesFolder))
            {
                StaticDungeonSitePool? pool = LoadSites(root, file, problems);
                if (pool != null)
                    sites[pool.Style] = pool;
            }

            var byEntrance = new Dictionary<MissionPlacementIdentity, StaticDungeonBlueprint>();
            var styles = new Dictionary<int, DungeonStyleCatalog?>();
            foreach (string file in ListFiles(root, StaticFolder))
            {
                StaticDungeonBlueprint? blueprint = LoadDungeon(root, file, sites, styles, problems);
                if (blueprint == null)
                    continue;
                if (!byEntrance.TryAdd(blueprint.Entrance.Identity, blueprint))
                    problems.Add(file + ": entrance " + blueprint.Entrance.IdentityInstance.ToString(CultureInfo.InvariantCulture)
                        + " is already defined by " + byEntrance[blueprint.Entrance.Identity].FileName + "; this file is left out");
            }

            _snapshot = new Snapshot(byEntrance, sites, problems.AsReadOnly());
            _logger.Info(string.Format(CultureInfo.InvariantCulture, "GameData static dungeons={0} sitePools={1} problems={2}",
                byEntrance.Count, sites.Count, problems.Count));
            return new GameDataReloadResult(Name, true, byEntrance.Count, problems);
        }

        /// <summary>The GameData-relative *.json files of <paramref name="folder"/>: the shipped ones and the override folder's, by name.</summary>
        static IEnumerable<string> ListFiles(string root, string folder)
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string? baseRoot in new[] { root, GameDataPaths.ResolveOverrideRoot() })
            {
                if (baseRoot == null)
                    continue;
                string directory = Path.Combine(baseRoot, folder);
                if (!Directory.Exists(directory))
                    continue;
                foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
                    names.Add(Path.GetFileName(path));
            }

            foreach (string name in names)
                yield return folder + "/" + name;
        }

        static T? Read<T>(string root, string file) where T : class
            => JsonSerializer.Deserialize<T>(File.ReadAllText(GameDataPaths.ResolveFile(root, file)), StaticDungeonFile.JsonOptions);

        StaticDungeonSitePool? LoadSites(string root, string file, List<string> problems)
        {
            StaticDungeonSitePoolFile? pool;
            try
            {
                pool = Read<StaticDungeonSitePoolFile>(root, file);
            }
            catch (Exception exception)
            {
                problems.Add(file + ": " + exception.Message);
                return null;
            }

            if (pool == null || pool.Style <= 0)
            {
                problems.Add(file + ": needs a positive Style");
                return null;
            }

            var rooms = new Dictionary<string, StaticDungeonRoomSites>(StringComparer.Ordinal);
            foreach ((string name, StaticDungeonRoomSites? room) in pool.Rooms ?? [])
            {
                if (string.IsNullOrWhiteSpace(name) || room == null)
                    continue;
                foreach ((string kind, StaticDungeonSiteKind? sites) in new[] { ("Chests", room.Chests), ("Mines", room.Mines), ("Turrets", room.Turrets) })
                {
                    if (sites == null)
                        continue;
                    if (!double.IsFinite(sites.PerRoom) || sites.PerRoom < 0)
                    {
                        problems.Add(file + ": " + name + "." + kind + ".PerRoom must be 0 or more");
                        return null;
                    }
                    sites.Spots = (sites.Spots ?? []).Where(spot => spot?.Position is { Length: 3 } p && p.All(float.IsFinite)
                        && (spot.Heading == null || (spot.Heading.Length == 4 && spot.Heading.All(float.IsFinite)))).ToList();
                }
                rooms[name] = room;
            }

            return new StaticDungeonSitePool(pool.Style, rooms);
        }

        StaticDungeonBlueprint? LoadDungeon(string root, string file, Dictionary<int, StaticDungeonSitePool> sites,
            Dictionary<int, DungeonStyleCatalog?> styles, List<string> problems)
        {
            StaticDungeonFile? data;
            try
            {
                data = Read<StaticDungeonFile>(root, file);
            }
            catch (Exception exception)
            {
                problems.Add(file + ": " + exception.Message);
                return null;
            }

            string? problem = Resolve(root, file, data, sites, styles, problems, out StaticDungeonBlueprint? blueprint);
            if (problem != null)
            {
                problems.Add(file + ": " + problem);
                return null;
            }
            return blueprint;
        }

        string? Resolve(string root, string file, StaticDungeonFile? data, Dictionary<int, StaticDungeonSitePool> sites,
            Dictionary<int, DungeonStyleCatalog?> styles, List<string> problems, out StaticDungeonBlueprint? blueprint)
        {
            blueprint = null;
            if (data == null)
                return "the file is empty";
            if (data.SchemaVersion != StaticDungeonFile.CurrentSchemaVersion)
                return "SchemaVersion must be " + StaticDungeonFile.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture);
            if (data.Entrance is not { IdentityInstance: > 0 } entranceRef)
                return "Entrance needs an IdentityType and IdentityInstance";
            if (entranceRef.IdentityType != (uint)IdentityType.ACGEntrance)
                return "Entrance.IdentityType must be " + ((uint)IdentityType.ACGEntrance).ToString(CultureInfo.InvariantCulture) + " (an ACG entrance)";
            if (!_entrances.TryGetByIdentity(entranceRef.IdentityType, entranceRef.IdentityInstance, out MissionEntrancePlacement entrance))
                return "entrance " + entranceRef.IdentityInstance.ToString(CultureInfo.InvariantCulture) + " is not in MissionEntrancePlacements.json";
            if (entranceRef.Playfield is int playfield && playfield != entrance.PlayfieldId)
                return "entrance " + entranceRef.IdentityInstance.ToString(CultureInfo.InvariantCulture) + " stands on playfield "
                    + entrance.PlayfieldId.ToString(CultureInfo.InvariantCulture) + ", not " + playfield.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(data.Instance, "Team", StringComparison.OrdinalIgnoreCase))
                return "Instance must be \"Team\"";
            if (data.RespawnSeconds is <= 0)
                return "RespawnSeconds must be null or above 0";

            // The building, row by row, as the client checks it.
            if (data.Building is not { Rooms: { Count: > 0 } rows } building)
                return "Building needs Rooms";
            if (building.Width is < 1 or > MaxGrid || building.Height is < 1 or > MaxGrid)
                return "Building.Width and Height must be 1-" + MaxGrid.ToString(CultureInfo.InvariantCulture);
            if (building.FloorHeight is < 0 or > 255)
                return "Building.FloorHeight must be 0-255";
            if (building.TemplatePlayfield <= 0)
                return "Building.TemplatePlayfield must name an ACG style playfield";
            if (building.Ambient is not { Length: 3 } ambient || ambient.Any(c => c is < 0 or > 255))
                return "Building.Ambient needs three bytes";
            if (!styles.TryGetValue(building.TemplatePlayfield, out DungeonStyleCatalog? style))
            {
                DungeonStyleCatalog.TryLoad(root, building.TemplatePlayfield, out style);
                styles[building.TemplatePlayfield] = style;
            }
            if (style == null)
                return "style " + building.TemplatePlayfield.ToString(CultureInfo.InvariantCulture) + " has no Rooms.json or metadata under Playfields";
            var roomTemplates = new Dictionary<int, StyleRoomTemplate>();
            foreach (StyleRoomTemplate template in style.Rooms)
                roomTemplates[template.InstanceId] = template;
            var generatorRooms = new List<AcgRoom>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                StaticDungeonRoom? row = rows[i];
                if (row == null || !roomTemplates.ContainsKey(row.Room) || row.Floor < -MaxFloor || row.Floor > MaxFloor
                    || row.X < 0 || row.X > MaxGrid || row.Z < 0 || row.Z > MaxGrid || row.Rotation is < 0 or > 3)
                    return "Building.Rooms[" + i.ToString(CultureInfo.InvariantCulture) + "] fails the client's checks (room in Rooms.json, floor -16..16, grid 0..32, rotation 0..3)";
                generatorRooms.Add(new AcgRoom
                {
                    RoomIndex = (ushort)row.Room, Floor = (sbyte)row.Floor, GridX = (byte)row.X, GridZ = (byte)row.Z, Rotation = (byte)row.Rotation
                });
            }
            var generator = new AcgBuildingGenerator
            {
                Version = AcgBuildingGenerator.CurrentVersion,
                Width = (ushort)building.Width,
                Height = (ushort)building.Height,
                FloorHeight = (ushort)building.FloorHeight,
                TemplatePlayfield = building.TemplatePlayfield,
                AmbientRed = (byte)ambient[0],
                AmbientGreen = (byte)ambient[1],
                AmbientBlue = (byte)ambient[2],
                Rooms = generatorRooms
            };
            var identity = new Identity((IdentityType)BuildingIdentityType, unchecked((int)entranceRef.IdentityInstance));
            var writer = new PacketWriter(64 + generatorRooms.Count * 6);
            writer.WriteIdentity(identity);
            writer.WriteInt32(1);
            generator.Write(writer);
            byte[] payload = writer.ToArray();

            // Doors, lifts and the arrival point.
            if (data.DoorStyle is not int doorStyle || !_layouts.TryGetDoorStyle(doorStyle, out DungeonDoorStyle doors))
                return "DoorStyle " + (data.DoorStyle?.ToString(CultureInfo.InvariantCulture) ?? "(none)") + " is not in DungeonDoorStyles.json";
            if (!_items.TryGet(doors.DoorTemplate, out _) || !_items.TryGet(doors.ExitTemplate, out _))
                return "DoorStyle " + doorStyle.ToString(CultureInfo.InvariantCulture) + " names door templates items.dat lacks";
            if (data.Arrival is not { Position: { Length: 3 } arrival } || !arrival.All(float.IsFinite))
                return "Arrival.Position needs three coordinates";
            AORebirth.Core.Vector.Quaternion arrivalHeading = data.Arrival.Heading is { Length: 4 } h && h.All(float.IsFinite)
                ? new AORebirth.Core.Vector.Quaternion(h[0], h[1], h[2], h[3]) : new AORebirth.Core.Vector.Quaternion(0, 0, 0, 1);
            var transitions = new List<AORebirth.DungeonGenerator.DungeonFloorTransition>();
            foreach (StaticDungeonTransition? transition in data.Transitions ?? [])
            {
                if (transition?.From == null || transition.To == null)
                    return "Transitions need From and To";
                if (!TryEndpoint(transition.From, rows.Count, out var from, out string? endpointProblem)
                    || !TryEndpoint(transition.To, rows.Count, out var to, out endpointProblem))
                    return "Transitions: " + endpointProblem;
                transitions.Add(new AORebirth.DungeonGenerator.DungeonFloorTransition(from!, to!));
            }

            // Behaviour and loot come from the shared dungeon presets, resolved as a mission's would be.
            if (string.IsNullOrWhiteSpace(data.Behaviour))
                return "Behaviour must name a Dungeons/Population.json block";
            int quality = Math.Max(1, data.Chests?.Quality ?? 1);
            var request = new DungeonRequest
            {
                Population = new DungeonPopulationBlock { Preset = data.Behaviour },
                Chests = data.Chests?.Loot is { Length: > 0 } loot ? new DungeonChestBlock { Preset = loot } : null
            };
            ResolvedDungeon? resolved = _layouts.Resolve(request, quality, out string? resolveProblem);
            if (resolved == null)
                return "Behaviour '" + data.Behaviour + "'" + (data.Chests?.Loot != null ? " or Chests.Loot '" + data.Chests.Loot + "'" : string.Empty)
                    + " does not resolve: " + resolveProblem;

            // NPCs on their rooms' district points.
            PlayfieldDistrictsData? districts = ZoneEngine_New.Core.Playfield.AcgDungeonPlayfield.LoadStyleDistricts(root, building.TemplatePlayfield, _logger);
            var npcs = new List<StaticNpcPlacement>();
            var unresolved = new SortedSet<string>(StringComparer.Ordinal);
            int unresolvedCount = 0;
            for (int i = 0; i < (data.Npcs?.Count ?? 0); i++)
            {
                StaticDungeonNpc? npc = data.Npcs![i];
                if (npc == null || npc.Level <= 0)
                    return "Npcs[" + i.ToString(CultureInfo.InvariantCulture) + "] needs a Level above 0";
                if (npc.Room < 0 || npc.Room >= rows.Count)
                    return "Npcs[" + i.ToString(CultureInfo.InvariantCulture) + "].Room is not a Building.Rooms index";
                PlayfieldDistrictEntry? district = DistrictOfZone(districts, rows[npc.Room].Room);
                if (district?.SpawnPoints == null || npc.Point < 0 || npc.Point >= district.SpawnPoints.Length
                    || district.SpawnPoints[npc.Point]?.Position is not { Length: >= 3 })
                    return "Npcs[" + i.ToString(CultureInfo.InvariantCulture) + "].Point is not a spawn point of its room's district";
                if (string.IsNullOrWhiteSpace(npc.Hash) || !_gameData.HasMobTemplate(npc.Hash))
                {
                    unresolvedCount++;
                    unresolved.Add(string.IsNullOrWhiteSpace(npc.Hash) ? (npc.Name ?? "?") + " (MonsterData " + (npc.MonsterData?.ToString(CultureInfo.InvariantCulture) ?? "?") + ")" : npc.Hash);
                    continue;
                }
                npcs.Add(new StaticNpcPlacement(npc.Hash, npc.Level, npc.Room, npc.Point, YawDegrees(npc.Heading)));
            }
            if (unresolvedCount > 0)
                problems.Add(file + ": " + unresolvedCount.ToString(CultureInfo.InvariantCulture) + " NPCs have no NpcTemplates entry and do not spawn: "
                    + string.Join(", ", unresolved));

            StaticChestPlan? chests = null;
            if (data.Chests is { Templates: { Count: > 0 } chestTemplates } chestData)
            {
                if (chestData.Quality <= 0 || chestData.LockDifficulty < 0 || chestData.TrapChancePercent is < 0 or > 100)
                    return "Chests needs Quality above 0, LockDifficulty 0 or more and TrapChancePercent 0-100";
                foreach (int template in chestTemplates.Concat(chestData.Traps ?? []))
                    if (!_items.TryGet(template, out _))
                        return "chest or trap template " + template.ToString(CultureInfo.InvariantCulture) + " is not in items.dat";
                // A block with no LootTable rows still places its chests; they spawn empty.
                IReadOnlyList<MobItemTableEntry> lootTable = resolved.Chests.LootTable ?? [];
                chests = new StaticChestPlan(chestTemplates.ToArray(), chestData.Quality, lootTable.ToArray(), chestData.LockDifficulty,
                    chestData.TrapChancePercent, (chestData.Traps ?? []).ToArray());
            }
            // Mines and controllers are recorded, not spawned yet (no client message for them in the protocol library): a
            // bad entry is reported and dropped rather than keeping the dungeon out.
            foreach (int template in data.Mines?.Templates ?? [])
                if (!_items.TryGet(template, out _))
                    problems.Add(file + ": mine template " + template.ToString(CultureInfo.InvariantCulture) + " is not in items.dat");
            var controllers = new List<StaticDungeonController>();
            foreach (StaticDungeonController? controller in data.Controllers ?? [])
            {
                if (controller == null || !_items.TryGet(controller.Template, out _) || controller.Position is not { Length: 3 }
                    || controller.Room < 0 || controller.Room >= rows.Count)
                {
                    problems.Add(file + ": a controller needs an items.dat Template, a Position and a Building.Rooms index; "
                        + (controller?.Name ?? "?") + " (template " + (controller?.Template.ToString(CultureInfo.InvariantCulture) ?? "?") + ") dropped");
                    continue;
                }
                controllers.Add(controller);
            }
            StaticTurretPlan? turrets = null;
            if (data.Turrets is { Level: > 0 } turretData)
            {
                if (!string.IsNullOrWhiteSpace(turretData.Hash) && _gameData.HasMobTemplate(turretData.Hash))
                    turrets = new StaticTurretPlan(turretData.Hash, turretData.Level);
                else
                    problems.Add(file + ": the turret " + (turretData.Name ?? turretData.Hash ?? "?") + " has no NpcTemplates entry and does not spawn");
            }

            var layout = new DungeonLayout(generator, identity, payload, new Vector3(arrival[0], arrival[1], arrival[2]),
                unchecked((int)entranceRef.IdentityInstance), doors)
            {
                FloorTransitions = transitions.AsReadOnly(),
                SpawnHeading = arrivalHeading,
                Population = resolved.Population,
                MobLoot = resolved.Population.Loot?.Where(MobItemTableEntry.IsWellFormed).ToArray() ?? []
            };
            sites.TryGetValue(building.TemplatePlayfield, out StaticDungeonSitePool? pool);
            if (pool == null)
                problems.Add(file + ": no Dungeons/Sites/" + building.TemplatePlayfield.ToString(CultureInfo.InvariantCulture)
                    + ".json site pool; its instances get no chests or turrets");
            blueprint = new StaticDungeonBlueprint(file, string.IsNullOrWhiteSpace(data.Name) ? entrance.DisplayName : data.Name, entrance, layout,
                quality, data.RespawnSeconds, npcs.AsReadOnly(), unresolvedCount, chests, turrets, data.Mines, controllers.AsReadOnly(), pool);
            return null;
        }

        static bool TryEndpoint(StaticDungeonTransitionEndpoint endpoint, int rooms, out AORebirth.DungeonGenerator.DungeonTransitionEndpoint? result, out string? problem)
        {
            result = null;
            problem = null;
            if (endpoint.Room < 0 || endpoint.Room >= rooms)
            {
                problem = "endpoint Room is not a Building.Rooms index";
                return false;
            }
            if (endpoint.Position is not { Length: 3 } p || !p.All(float.IsFinite))
            {
                problem = "endpoint Position needs three coordinates";
                return false;
            }
            if (endpoint.Platform <= 0 || endpoint.Button <= 0)
            {
                problem = "endpoint needs Platform and Button item templates";
                return false;
            }
            result = new AORebirth.DungeonGenerator.DungeonTransitionEndpoint(endpoint.Room, new System.Numerics.Vector3(p[0], p[1], p[2]),
                endpoint.Platform, endpoint.Button);
            return true;
        }

        /// <summary>The district a room template (zone) maps to in the style's Districts.json.</summary>
        internal static PlayfieldDistrictEntry? DistrictOfZone(PlayfieldDistrictsData? districts, int zone)
        {
            if (districts?.Districts == null)
                return null;
            int districtIndex = districts.ZoneToDistrictMap is { } map && zone >= 0 && zone < map.Length ? map[zone] : zone;
            foreach (PlayfieldDistrictEntry district in districts.Districts)
                if (district != null && district.DistrictIndex == districtIndex)
                    return district;
            return null;
        }

        /// <summary>The yaw, in whole degrees, of an XYZW heading turning about Y (the way CharacterMotor writes headings).</summary>
        internal static int YawDegrees(float[]? heading)
        {
            if (heading is not { Length: 4 } || !heading.All(float.IsFinite))
                return 0;
            double yaw = 2.0 * Math.Atan2(heading[1], heading[3]);
            int degrees = (int)Math.Round(yaw * 180.0 / Math.PI, MidpointRounding.AwayFromZero);
            return ((degrees % 360) + 360) % 360;
        }
    }

    /// <summary>GameData/Dungeons/Static and Dungeons/Sites, swapped inside <see cref="StaticDungeonCatalog"/>.</summary>
    public sealed class StaticDungeonsReload(StaticDungeonCatalog catalog) : IReloadableGameData
    {
        public string Name => StaticDungeonCatalog.ReloadName;

        public IReadOnlyList<string> Files => catalog.Files;

        public GameDataReloadResult Reload() => catalog.Reload();
    }
}
