namespace ZoneEngine_New.Core.Playfield;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using AORebirth.Core.GameData;

using AORebirth.Interfaces.Persistence.Shops;

using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

using Utility.GameData.Missions;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Metrics;
using ZoneEngine_New.Core.Mobs;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.Quests.Dungeons;
using ZoneEngine_New.Core.Trade;
using ZoneEngine_New.Core.WorldSimulation;

/// <summary>
/// A quest's ACG dungeon, generated from the quest's seed when someone enters. It holds no persistent world
/// state: when released it is simply built again from the same seed, doors included. Its doors are placed from
/// the layout's room connections, and its NPC spawn points from the style's district spawn points, moved and
/// turned into each placed room. A kill-target quest's target is placed at a spawn point in one of the rooms farthest
/// from the entrance.
/// </summary>
public sealed class QuestDungeonPlayfield : Playfield
{
    PlayfieldWorldSimulation? _simulation;
    AORebirth.World.Collision.DungeonWorldLayout? _chestDungeon;
    bool _chestsReady;
    Exception? _chestPopulationFailure;
    /// <summary>District spawn points (room, point index) the population or the kill target took; chests avoid them.</summary>
    readonly HashSet<(int Room, int Point)> _usedPoints = new();
    readonly MissionItemObjectiveBinding? _objectiveItem;
    readonly MissionNpcObjectiveBinding? _objectiveNpc;
    readonly bool _spawnNpcObjective;
    readonly Func<Player, MissionObjectiveItem, bool>? _collectObjective;

    public QuestDungeonPlayfield(int playfieldId, string questId, DungeonLayout layout, MissionEntrancePlacement entrance,
        string? targetHash, int quality, IZoneLogger logger, IMessageRouter router, PlayfieldManager manager, PlayerHydrator hydrator, IGameData data,
        IItemBuilder items, HashItemMinter hashItems, IInventoryRepository inventory, IItemInstanceIdAllocator ids,
        InventoryMoveService moves, InventoryFlushService flush, TradeService trades, CharacterSnapshotService snapshot,
        IPlayfieldMetricsRegistry metrics, IShopDao shopDao, MissionItemObjectiveBinding? objectiveItem = null,
        Func<Player, MissionObjectiveItem, bool>? collectObjective = null, MissionNpcObjectiveBinding? npcObjective = null,
        DungeonKills? kills = null, bool spawnNpcObjective = true, MissionChestPopulation? chestPopulation = null)
        : base(new Identity { Type = IdentityType.Playfield, Instance = playfieldId }, logger, router,
            manager, hydrator, data, items, hashItems, inventory, ids, moves, flush, trades, snapshot, metrics, shopDao)
    {
        QuestId = questId ?? throw new ArgumentNullException(nameof(questId));
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
        Entrance = entrance ?? throw new ArgumentNullException(nameof(entrance));
        TargetHash = string.IsNullOrWhiteSpace(targetHash) ? null : targetHash;
        Quality = Math.Max(0, quality);
        if (objectiveItem != null && (!objectiveItem.IsValid
            || !string.Equals(objectiveItem.QuestId, QuestId, StringComparison.Ordinal) || collectObjective == null))
            throw new ArgumentException("Invalid objective for this quest dungeon.", nameof(objectiveItem));
        _objectiveItem = objectiveItem;
        _collectObjective = collectObjective;
        if (npcObjective != null && (!npcObjective.IsValid
            || !string.Equals(npcObjective.QuestId, QuestId, StringComparison.Ordinal)))
            throw new ArgumentException("Invalid NPC objective for this quest dungeon.", nameof(npcObjective));
        _objectiveNpc = npcObjective;
        _spawnNpcObjective = spawnNpcObjective;
        Kills = kills;
        ChestPopulation = chestPopulation;
    }

    public string QuestId { get; }

    public DungeonLayout Layout { get; }

    /// <summary>The population's kill ledger; null for a dungeon with no quest run to record against (tests).</summary>
    public DungeonKills? Kills { get; }

    /// <summary>Writes the kill ledger's checkpoint; the dungeon service sets it, and the dungeon calls it when it unloads.</summary>
    public Action<DungeonKills>? KillsCheckpoint { get; set; }

    public MissionChestPopulation? ChestPopulation { get; }

    /// <summary>Where the dungeon is entered from, and where its occupants return to.</summary>
    public MissionEntrancePlacement Entrance { get; }

    /// <summary>The NPC hash a kill-target quest wants killed here; null when the quest has no kill target.</summary>
    public string? TargetHash { get; }

    /// <summary>The mission's quality level (0 when unknown); locked doors use it as their lock difficulty.</summary>
    public int Quality { get; }

    public override void Build()
    {
        if (IsBuilt) return;
        var locality = GetRequiredService<PlayfieldLocality>();
        AORebirth.World.Collision.DungeonWorldLayout? dungeon = DungeonPlayfieldBinder.TryBuild(
            GameData.RootPath, Identity.Instance, Layout.Generator, Logger);
        _chestDungeon = dungeon;
        if (dungeon != null)
            locality.ApplyDungeonRooms(dungeon);

        var geometry = DungeonPlayfieldBinder.WithDungeonCollision(Identity.Instance, Geometry, dungeon);
        if (geometry.Collision?.HasCollision == true)
        {
            _simulation = PlayfieldWorldSimulation.Create(
                Identity.Instance, geometry, MetaData, DestinationsCatalog.Instance, GameData, Logger, ItemTemplates);
            RegisterWorldServices(_simulation);
            // Before anything spawns: NPC placement and wandering go through the pathfinder. The bake normally
            // finished while the player travelled from the terminal; this build runs off the tick, so waiting is fine.
            if (dungeon != null)
                Pathfinder = DungeonNavMeshes.TryCreatePathfinder(Identity.Instance, Layout);
        }

        if (dungeon != null)
        {
            PlaceObjective(dungeon);
            SpawnDoors(dungeon.Doors, locality);
            SpawnFloorTransitions(locality);
            SpawnObjectiveNpc(dungeon);
            AddDistrictSpawns(dungeon);
            SpawnObjectiveItem(dungeon, locality);
        }

        EnsureChestPopulation();

        MarkBuilt();
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
        if (_chestDungeon?.SafePosition is not System.Numerics.Vector3 position
            || GetService<PlayfieldLocality>()?.IsOutsideDungeonRooms(landing) != true)
            return false;

        safe = new AORebirth.Core.Vector.Vector3(position.X, position.Y, position.Z);
        return true;
    }

    /// <summary>Called during build and on the existing destination admission tick before visibility activates.</summary>
    internal bool EnsureChestPopulation()
    {
        if (_chestsReady || ChestPopulation == null || IsDisposed)
            return true;
        if (_chestPopulationFailure != null)
            throw new InvalidDataException("The mission chest population requires fresh dungeon reconstruction.", _chestPopulationFailure);
        if (_chestDungeon == null)
            return ChestPopulation.Defer("dungeon-geometry-unavailable");
        try
        {
            _chestsReady = ChestPopulation.Populate(this, _chestDungeon);
            return _chestsReady;
        }
        catch (Exception exception)
        {
            _chestPopulationFailure = exception;
            ChestPopulation.Retire();
            throw;
        }
    }

    static readonly ConcurrentDictionary<(string Root, int Style), PlayfieldDistrictsData?> StyleDistricts = new();

    static readonly JsonSerializerOptions DistrictJsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The style playfield's Districts.json maps each template room (zone) to a district whose spawn points are in
    /// the style's world space; each placed room moves its district's points into the dungeon with the room's own
    /// transform, and they become ordinary hash spawn points of this playfield. Each room uses a random share of its
    /// points, at least one, rolled from the dungeon seed so a rebuilt dungeon spawns in the same places. Quest dungeon
    /// NPCs do not respawn once killed.
    /// </summary>
    void AddDistrictSpawns(AORebirth.World.Collision.DungeonWorldLayout dungeon)
    {
        IReadOnlyList<AORebirth.World.Collision.DungeonRoomPlacement> placements = dungeon.Placements;
        if (placements.Count == 0)
            return;

        PlayfieldDistrictsData? districts = LoadStyleDistricts(GameData.RootPath, Layout.Generator.Style, Logger);
        if (districts?.Districts == null)
            return;

        var byIndex = new Dictionary<int, PlayfieldDistrictEntry>();
        foreach (PlayfieldDistrictEntry district in districts.Districts)
        {
            if (district != null)
                byIndex[district.DistrictIndex] = district;
        }

        // Each placed room's district and its usable spawn points.
        var rooms = new List<(AORebirth.World.Collision.DungeonRoomPlacement Placement, PlayfieldDistrictEntry District, List<PlayfieldDistrictSpawnPoint> Points)>();
        foreach (AORebirth.World.Collision.DungeonRoomPlacement placement in placements)
        {
            int zone = placement.Template.InstanceId;
            int districtIndex = districts.ZoneToDistrictMap is { } map && zone >= 0 && zone < map.Length ? map[zone] : zone;
            if (!byIndex.TryGetValue(districtIndex, out PlayfieldDistrictEntry? district))
                continue;

            var points = new List<PlayfieldDistrictSpawnPoint>();
            foreach (PlayfieldDistrictSpawnPoint point in district.SpawnPoints ?? [])
            {
                if (point?.Position is { Length: >= 3 })
                    points.Add(point);
            }

            if (points.Count > 0)
                rooms.Add((placement, district, points));
        }

        var spawns = GetRequiredService<HashSpawnSystem>();
        (int Room, PlayfieldDistrictSpawnPoint Point)? target = _objectiveNpc != null || TargetHash == null
            ? null : PlaceTarget(dungeon, rooms, spawns);

        DungeonPopulationPolicy population = Layout.Population;
        if (!population.IsValid(GameData))
        {
            Logger.Warn("Quest dungeon " + QuestId + ": ordinary NPC policy is unavailable; no placeholder population selected");
            return;
        }

        int added = 0;
        int skipped = 0;
        foreach ((AORebirth.World.Collision.DungeonRoomPlacement placement, PlayfieldDistrictEntry district, List<PlayfieldDistrictSpawnPoint> points) in rooms)
        {
            // The target's point is taken; the target counts as that room's NPC.
            bool holdsFrozenTarget = _objectiveNpc?.RoomIndex == placement.Index;
            bool holdsTarget = holdsFrozenTarget || target is { } held && held.Room == placement.Index;
            var candidates = points.FindAll(point =>
                !(target is { } legacyTarget && legacyTarget.Room == placement.Index && ReferenceEquals(point, legacyTarget.Point))
                && !(holdsFrozenTarget && IsObjectiveNpcPoint(placement, point)));
            if (candidates.Count == 0)
                continue;

            var roll = new Random(unchecked((Layout.Seed * 7919) ^ ((placement.Index + 1) * 104729)));
            roll.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(candidates));
            int wanted = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (roll.Next(100) < population.SpawnPointChancePercent)
                    wanted++;
            }

            if (!holdsTarget)
                wanted = Math.Max(1, wanted);
            int placedInRoom = 0;
            foreach (PlayfieldDistrictSpawnPoint point in candidates)
            {
                if (placedInRoom >= wanted)
                    break;

                float[] position = point.Position;
                System.Numerics.Vector3 placed = placement.TransformTemplatePoint(
                    new System.Numerics.Vector3(position[0], position[1], position[2]));
                string hash = population.RegularNpcHashes[roll.Next(population.RegularNpcHashes.Length)];
                // A quest's spread rolls each spawn's own level; without one the whole population is at the quality.
                int populationLevel = population.RollLevel(roll, Quality);
                string pointKey = "r" + placement.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "p" + Array.IndexOf(district.SpawnPoints!, point).ToString(System.Globalization.CultureInfo.InvariantCulture);
                // With a kill ledger the point's branches are fixed now, so each gets a slot: a killed slot never spawns
                // again, and a respawn after the cell slept rebinds the new actor to its slot.
                MobTemplate[]? branches = null;
                int firstSlot = Kills?.Total ?? 0;
                if (Kills != null)
                {
                    branches = ResolveBranches(hash, Math.Max(1, populationLevel));
                    if (branches.Length == 0)
                    {
                        skipped++;
                        continue;
                    }
                    for (int branch = 0; branch < branches.Length; branch++)
                        Kills.RegisterSlot(DungeonKills.Slot(pointKey, branch));
                }
                DungeonKills? kills = Kills;
                if (spawns.AddPlacedDistrictPoint(hash, district,
                        new AORebirth.Core.Vector.Vector3(placed.X, placed.Y, placed.Z), point.Radius,
                        useDistrictLevels: false, respawns: false, requestedLevel: populationLevel > 0 ? populationLevel : null,
                        frozenTemplates: branches,
                        onBranchSpawned: kills == null ? null : (actor, branch) => kills.Bind(actor, DungeonKills.Slot(pointKey, branch)),
                        isBranchKilled: kills == null ? null : branch => kills.IsKilled(DungeonKills.Slot(pointKey, branch))))
                {
                    _usedPoints.Add((placement.Index, Array.IndexOf(district.SpawnPoints!, point)));
                    placedInRoom++;
                    added++;
                }
                else
                {
                    Kills?.RollbackSlots(firstSlot);
                    skipped++;
                }
            }
        }

        Logger.Info("Quest dungeon " + QuestId + " spawn points=" + added + " skipped=" + skipped
            + " requestedNpcLevel=" + Quality + (population.LevelSpreadPercent > 0 ? "±" + population.LevelSpreadPercent + "%" : string.Empty)
            + " families=" + string.Join(",", population.RegularNpcHashes));
    }

    /// <summary>
    /// The objective's room and point come from the layout seed, so the accepted binding carries only the NPC or item
    /// and its identity; the same seed always lands it in the same place. A binding that already holds a position
    /// (one accepted before this derivation) keeps it.
    /// </summary>
    void PlaceObjective(AORebirth.World.Collision.DungeonWorldLayout dungeon)
    {
        MissionNpcObjectiveBinding? npc = _objectiveNpc;
        MissionItemObjectiveBinding? item = _objectiveItem is { IsSourceItem: false } placed ? placed : null;
        if ((npc == null || npc.RoomIndex > 0) && (item == null || item.RoomIndex > 0))
            return;
        if (!DungeonObjectivePlacement.TrySelect(dungeon, GameData.RootPath, Layout, Logger,
                out int room, out AORebirth.Core.Vector.Vector3 point))
            throw new InvalidDataException("The dungeon has no reachable objective room.");
        if (npc is { RoomIndex: <= 0 })
        {
            npc.RoomIndex = room;
            npc.X = point.xf; npc.Y = point.yf; npc.Z = point.zf;
        }
        if (item is { RoomIndex: <= 0 })
        {
            item.RoomIndex = room;
            item.X = point.xf; item.Y = point.yf; item.Z = point.zf;
        }
    }

    /// <summary>
    /// Where chests may stand: every district spawn point in a chest-eligible room that the population, the kill target
    /// and the objective left free. Same order on every build, so the seeded chest plan is stable.
    /// </summary>
    internal IReadOnlyList<MissionChestSite> ChestSites(AORebirth.World.Collision.DungeonWorldLayout dungeon, MissionChestPolicy policy)
    {
        var sites = new List<MissionChestSite>();
        HashSet<int> eligible = MissionChestPlacement.EligibleRooms(dungeon, Layout, GameData.RootPath, policy);
        PlayfieldDistrictsData? districts = eligible.Count == 0 ? null : LoadStyleDistricts(GameData.RootPath, Layout.Generator.Style, Logger);
        if (districts?.Districts == null)
            return sites;
        var byIndex = new Dictionary<int, PlayfieldDistrictEntry>();
        foreach (PlayfieldDistrictEntry district in districts.Districts)
            if (district != null)
                byIndex[district.DistrictIndex] = district;
        foreach (AORebirth.World.Collision.DungeonRoomPlacement placement in dungeon.Placements)
        {
            if (!eligible.Contains(placement.Index))
                continue;
            int zone = placement.Template.InstanceId;
            int districtIndex = districts.ZoneToDistrictMap is { } map && zone >= 0 && zone < map.Length ? map[zone] : zone;
            if (!byIndex.TryGetValue(districtIndex, out PlayfieldDistrictEntry? district) || district.SpawnPoints == null)
                continue;
            for (int index = 0; index < district.SpawnPoints.Length; index++)
            {
                PlayfieldDistrictSpawnPoint? point = district.SpawnPoints[index];
                if (point?.Position is not { Length: >= 3 } raw || _usedPoints.Contains((placement.Index, index)))
                    continue;
                System.Numerics.Vector3 placed = placement.TransformTemplatePoint(new System.Numerics.Vector3(raw[0], raw[1], raw[2]));
                if (!float.IsFinite(placed.X) || !float.IsFinite(placed.Y) || !float.IsFinite(placed.Z) || IsObjectivePoint(placed))
                    continue;
                sites.Add(new MissionChestSite(placement.Index, new AORebirth.Core.Vector.Vector3(placed.X, placed.Y, placed.Z)));
            }
        }
        return sites;
    }

    bool IsObjectivePoint(System.Numerics.Vector3 placed)
        => (_objectiveNpc != null && placed.X == _objectiveNpc.X && placed.Y == _objectiveNpc.Y && placed.Z == _objectiveNpc.Z)
            || (_objectiveItem != null && placed.X == _objectiveItem.X && placed.Y == _objectiveItem.Y && placed.Z == _objectiveItem.Z);

    /// <summary>The templates a district point spawns for <paramref name="hash"/> at <paramref name="level"/>: the point's branches.</summary>
    MobTemplate[] ResolveBranches(string hash, int level)
    {
        var resolved = new List<MobTemplate>();
        GameData.CollectMobSpawns(hash, level, resolved);
        return resolved.Where(template => !template.UnresolvedPlaceholder && NpcTemplateValidation.CanSpawn(template)).ToArray();
    }

    bool IsObjectiveNpcPoint(AORebirth.World.Collision.DungeonRoomPlacement placement, PlayfieldDistrictSpawnPoint point)
    {
        if (_objectiveNpc == null)
            return false;
        System.Numerics.Vector3 placed = placement.TransformTemplatePoint(
            new System.Numerics.Vector3(point.Position[0], point.Position[1], point.Position[2]));
        return placed.X == _objectiveNpc.X && placed.Y == _objectiveNpc.Y && placed.Z == _objectiveNpc.Z;
    }

    void SpawnObjectiveNpc(AORebirth.World.Collision.DungeonWorldLayout dungeon)
    {
        if (_objectiveNpc == null || !_spawnNpcObjective || Kills?.IsKilled(DungeonKills.ObjectiveSlot) == true)
            return;
        if (_objectiveNpc.RoomIndex >= dungeon.Placements.Count
            || DungeonObjectivePlacement.RoomDistances(dungeon, Layout)[_objectiveNpc.RoomIndex] <= 0)
            throw new InvalidDataException("Accepted NPC objective room is unavailable.");
        if (!GameData.TryResolveMobTemplate(_objectiveNpc.NpcHash, _objectiveNpc.Quality, out MobTemplate template)
            || template.UnresolvedPlaceholder || !NpcTemplateValidation.CanSpawn(template)
            || !string.Equals(template.Hash, _objectiveNpc.NpcHash, StringComparison.Ordinal)
            || (_objectiveNpc.MissionType == 0 && template.Has(NpcFeature.NoCombat)))
            throw new InvalidDataException("Accepted NPC objective template is unavailable.");

        int? objectiveScale = _objectiveNpc.ScalePercent;
        NpcCharacter npc = GetRequiredService<SpawnService>().SpawnMob(template,
            new AORebirth.Core.Vector.Vector3(_objectiveNpc.X, _objectiveNpc.Y, _objectiveNpc.Z),
            new AORebirth.Core.Vector.Quaternion(0, 0, 0, 1), _objectiveNpc.Quality,
            SpawnSource.HashSpawn, spawnHash: _objectiveNpc.NpcHash,
            configure: actor =>
            {
                // The policy percent scales the template's own size (Master of Time and Space is 600, a human 100);
                // it is not an absolute Scale, or a large boss would shrink to a fifth of its catalog size.
                if (objectiveScale is int scale)
                {
                    int baseScale = actor.Stats.TryGetValue(CharacterStat.Scale, out int held) && !StatCollection.IsUnset(held)
                        ? Math.Max(1, held) : 100;
                    actor.Stats.Set(CharacterStat.Scale, Math.Max(1, (int)((long)baseScale * scale / 100)));
                }
            },
            fixedIdentity: _objectiveNpc.Identity);
        Kills?.Bind(npc, DungeonKills.ObjectiveSlot);
        Logger.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "Quest dungeon {0} objective NPC {1}:{2} hash={3} in room {4} at ({5:0.#}, {6:0.#}, {7:0.#}) scale={8}",
            QuestId, (int)npc.Identity.Type, npc.Identity.Instance, _objectiveNpc.NpcHash,
            _objectiveNpc.RoomIndex, npc.Position.xf, npc.Position.yf, npc.Position.zf,
            npc.Stats.GetOrZero(CharacterStat.Scale)));
    }

    /// <summary>
    /// Places the kill target at a spawn point of a room as far from the entrance (in doors) as the layout goes,
    /// picked from the seed so a rebuilt dungeon puts it in the same place. Only rooms reachable from the entrance
    /// count. Falls back to nearer rooms when the farthest have no spawnable point. The target spawns once.
    /// </summary>
    (int Room, PlayfieldDistrictSpawnPoint Point)? PlaceTarget(AORebirth.World.Collision.DungeonWorldLayout dungeon,
        List<(AORebirth.World.Collision.DungeonRoomPlacement Placement, PlayfieldDistrictEntry District, List<PlayfieldDistrictSpawnPoint> Points)> rooms,
        HashSpawnSystem spawns)
    {
        int[] distance = DungeonObjectivePlacement.RoomDistances(dungeon, Layout);

        var roll = new Random(unchecked(Layout.Seed * 31337));
        var byDistance = new List<int>();
        for (int i = 0; i < rooms.Count; i++)
        {
            if (distance[rooms[i].Placement.Index] > 0)
                byDistance.Add(i);
        }

        // Farthest first; rooms at the same distance in a seeded order.
        var order = byDistance.Select(i => (Index: i, Tie: roll.Next())).OrderByDescending(pair => distance[rooms[pair.Index].Placement.Index])
            .ThenBy(pair => pair.Tie).Select(pair => pair.Index).ToList();
        foreach (int i in order)
        {
            (AORebirth.World.Collision.DungeonRoomPlacement placement, PlayfieldDistrictEntry district, List<PlayfieldDistrictSpawnPoint> points) = rooms[i];
            var shuffled = new List<PlayfieldDistrictSpawnPoint>(points);
            roll.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(shuffled));
            foreach (PlayfieldDistrictSpawnPoint point in shuffled)
            {
                System.Numerics.Vector3 placed = placement.TransformTemplatePoint(
                    new System.Numerics.Vector3(point.Position[0], point.Position[1], point.Position[2]));
                if (!spawns.AddPlacedDistrictPoint(TargetHash!, district, new AORebirth.Core.Vector.Vector3(placed.X, placed.Y, placed.Z),
                        point.Radius, useDistrictLevels: false, respawns: false))
                    continue;
                _usedPoints.Add((placement.Index, Array.IndexOf(district.SpawnPoints!, point)));

                Logger.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Quest dungeon {0} kill target {1} in room {2} ({3}), {4} doors from the entrance, at ({5:0.#}, {6:0.#}, {7:0.#})",
                    QuestId, TargetHash, placement.Index, placement.Template.Name, distance[placement.Index], placed.X, placed.Y, placed.Z));
                return (placement.Index, point);
            }
        }

        Logger.Warn("Quest dungeon " + QuestId + ": kill target " + TargetHash + " could not be placed (unspawnable hash or no reachable spawn point)");
        return null;
    }

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

    void SpawnObjectiveItem(AORebirth.World.Collision.DungeonWorldLayout dungeon, PlayfieldLocality locality)
    {
        if (_objectiveItem == null)
            return;
        if (_objectiveItem.RoomIndex >= dungeon.Placements.Count
            || DungeonObjectivePlacement.RoomDistances(dungeon, Layout)[_objectiveItem.RoomIndex] <= 0)
            throw new InvalidDataException("Accepted objective room is unavailable.");
        var item = new MissionObjectiveItem(_objectiveItem,
            GetRequiredService<IItemBuilder>().CreateTemplate(_objectiveItem.LowId, _objectiveItem.HighId, _objectiveItem.Quality),
            _collectObjective!)
        {
            Playfield = this,
            Position = new AORebirth.Core.Vector.Vector3(_objectiveItem.X, _objectiveItem.Y, _objectiveItem.Z),
            Rotation = new AORebirth.Core.Vector.Quaternion(0, 0, 0, 1)
        };
        if (!GetRequiredService<DynelRegistry>().TryRegister(item))
            throw new InvalidDataException("Accepted objective identity is already registered.");
        locality.RegisterDynel(item);
    }

    void SpawnFloorTransitions(PlayfieldLocality locality)
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
    /// </summary>
    void SpawnDoors(IReadOnlyList<AORebirth.World.Collision.DungeonDoorPlacement> placements, PlayfieldLocality locality)
    {
        DungeonDoorStyle? style = Layout.Doors;
        if (style == null || placements.Count == 0)
            return;

        var registry = GetRequiredService<DynelRegistry>();
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
            if (placement.IsExit)
                flags |= Door.OpenFlag;
            else
            {
                // Same seed and door index as the generator (and its viewer), so both agree on which doors are locked.
                var roll = new Random(unchecked((Layout.Seed * 397) ^ (i + 1)));
                if (AORebirth.DungeonGenerator.DungeonDoorLocks.IsLocked(Layout.Seed, i, style.LockedChancePercent))
                    flags |= Door.LockedFlag;
                else if (roll.Next(100) < style.OpenChancePercent)
                    flags |= Door.OpenFlag;
            }

            var door = new Door(new Identity { Type = IdentityType.Door, Instance = FirstDoorInstance + i }, templateId, flags,
                (placement.Room << 16) | (placement.LinkedRoom & 0xFFFF), lockDifficulty: Math.Max(1, Quality))
            {
                Playfield = this,
                Position = new AORebirth.Core.Vector.Vector3(placement.Position.X, placement.Position.Y, placement.Position.Z),
                Rotation = Door.Heading(placement.YawDegrees)
            };
            registry.Register(door);
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
            Logger.Warn("Quest dungeon " + Identity.Instance + " has no world simulation; its exit door has no walk-out");
            return;
        }

        var entrancePosition = new AORebirth.Core.Vector.Vector3(Entrance.LocalX, Entrance.LocalY, Entrance.LocalZ);
        // Raw placement components are WXYZ: exact-identity comparisons against the current Dynels reader
        // match all 2,242 catalog rows. The engine's Quaternion constructor takes XYZW.
        var entranceHeading = new AORebirth.Core.Vector.Quaternion(
            Entrance.RotationComponent1, Entrance.RotationComponent2, Entrance.RotationComponent3, Entrance.RotationComponent0);
        AORebirth.Core.Vector.Vector3 landing = PortalDoorLandingResolver.LandingInFront(
            entrancePosition, entranceHeading, PortalDoorLandingResolver.ExitDoorClearance);
        _simulation.RegisterDungeonExit(exit.Position.xf, exit.Position.yf, exit.Position.zf, exit.Identity.Instance,
            Entrance.PlayfieldId, landing, entranceHeading, exit.TemplateId, exit.Rotation);
    }

    protected override void OnDispose()
    {
        if (Kills != null)
            KillsCheckpoint?.Invoke(Kills);
        ChestPopulation?.Retire();
        _simulation?.Dispose();
        _simulation = null;
        base.OnDispose();
    }

    /// <summary>The captured ACG PlayfieldAnarchyF shape: building identity plus the generator the client builds from.</summary>
    public override PlayfieldAnarchyFMessage CreatePlayfieldAnarchyFMessage(Vector3 coordinates) => new()
    {
        Identity = new Identity { Type = IdentityType.Playfield2, Instance = Identity.Instance },
        CharacterCoordinates = coordinates,
        PlayfieldId1 = Layout.Generator.Identity,
        Unknown3 = 0,
        Unknown4 = 0,
        PlayfieldId2 = new Identity { Type = IdentityType.Playfield2, Instance = Identity.Instance },
        GeneratorPayload = Layout.GeneratorPayload,
        PlayfieldX = -1,
        PlayfieldZ = -1
    };
}
