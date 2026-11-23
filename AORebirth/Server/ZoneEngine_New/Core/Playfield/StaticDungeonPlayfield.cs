namespace ZoneEngine_New.Core.Playfield;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using AORebirth.Core.GameData;
using AORebirth.Enums;
using AORebirth.Interfaces.Persistence.Shops;

using LostEden.Protocol.GameData;

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
using ZoneEngine_New.Core.Quests.Dungeons.Static;
using ZoneEngine_New.Core.Trade;

using NVector3 = System.Numerics.Vector3;

/// <summary>
/// One instance of a fixed ACG dungeon behind a world door (<see cref="StaticDungeonBlueprint"/>). Its building,
/// doors and lifts are the blueprint's exactly; its monsters stand on the district points the capture had them on,
/// through the spawn system's point machinery so respawns and sleeping cells behave as everywhere else; its chests
/// and turrets are rolled for this instance from the style's site pool, their loot held in memory for the
/// instance's life. When the instance is released its kills and chests go with it.
/// </summary>
public sealed class StaticDungeonPlayfield : AcgDungeonPlayfield
{
    /// <summary>A chest or turret spot this close to a placed NPC, controller or lift is not used.</summary>
    const float SpotClearance = 1f;

    /// <summary>Collision radius a chest gets when its template has no DynelCollision.json sphere.</summary>
    const float FallbackChestRadius = 1f;

    /// <summary>Captured Shadowlands chest Flags (ChestFullUpdate 0x20001821).</summary>
    const uint ChestFlags = 0x20001821;

    readonly int _chestSeed;
    int _npcs;
    int _chests;
    int _turrets;

    public StaticDungeonPlayfield(int playfieldId, StaticDungeonBlueprint blueprint, int chestSeed, IZoneLogger logger, IMessageRouter router,
        PlayfieldManager manager, PlayerHydrator hydrator, IGameData data, IItemBuilder items, HashItemMinter hashItems,
        IInventoryRepository inventory, IItemInstanceIdAllocator ids, InventoryMoveService moves, InventoryFlushService flush,
        TradeService trades, CharacterSnapshotService snapshot, IPlayfieldMetricsRegistry metrics, IShopDao shopDao)
        : base(playfieldId, (blueprint ?? throw new ArgumentNullException(nameof(blueprint))).Layout, blueprint.Entrance, blueprint.Quality,
            logger, router, manager, hydrator, data, items, hashItems, inventory, ids, moves, flush, trades, snapshot, metrics, shopDao)
    {
        Blueprint = blueprint;
        _chestSeed = chestSeed;
    }

    /// <summary>The definition this instance was built from; a catalog reload does not change it.</summary>
    public StaticDungeonBlueprint Blueprint { get; }

    /// <summary>Called when the instance is disposed (released by the sweep or a GM); the service drops its records.</summary>
    public Action<StaticDungeonPlayfield>? Released { get; set; }

    /// <summary>How many NPC points, chests and turret points this build placed.</summary>
    public (int Npcs, int Chests, int Turrets) Placed => (_npcs, _chests, _turrets);

    protected override string Label => "Static dungeon " + Blueprint.Entrance.IdentityInstance.ToString(CultureInfo.InvariantCulture)
        + " (" + Blueprint.Name + ")";

    public override void Build()
    {
        if (IsBuilt) return;
        var locality = GetRequiredService<PlayfieldLocality>();
        AORebirth.World.Collision.DungeonWorldLayout? dungeon = BuildWorld(locality);
        if (dungeon != null)
        {
            SpawnDoors(dungeon.Doors, locality);
            SpawnFloorTransitions(locality);
            var taken = new List<NVector3>();
            PlaceNpcs(dungeon, taken);
            ReserveFixedSpots(taken);
            PlaceSites(dungeon, locality, taken);
        }

        Logger.Info(string.Format(CultureInfo.InvariantCulture,
            "{0} built playfield={1} rooms={2} npcs={3}/{4} chests={5} turrets={6} lifts={7} controllers={8} mines={9} (controllers and mines are not sent: no client message in the protocol library)",
            Label, Identity.Instance, dungeon?.Placements.Count ?? 0, _npcs, Blueprint.Npcs.Count, _chests, _turrets,
            Layout.FloorTransitions.Count, Blueprint.Controllers.Count, Blueprint.Mines?.Templates?.Count ?? 0));
        MarkBuilt();
    }

    /// <summary>
    /// Every captured monster on its room's district point, as a placed spawn point: the hash at its level, facing as
    /// captured, with the Behaviour block applied, respawning after <see cref="StaticDungeonBlueprint.RespawnSeconds"/>
    /// or never.
    /// </summary>
    void PlaceNpcs(AORebirth.World.Collision.DungeonWorldLayout dungeon, List<NVector3> taken)
    {
        if (Blueprint.Npcs.Count == 0)
            return;
        PlayfieldDistrictsData? districts = LoadStyleDistricts(GameData.RootPath, Layout.Generator.TemplatePlayfield, Logger);
        if (districts == null)
            return;
        var spawns = GetRequiredService<HashSpawnSystem>();
        DungeonPopulationBlock population = Layout.Population;
        int skipped = 0;
        foreach (StaticNpcPlacement npc in Blueprint.Npcs)
        {
            if (npc.Room >= dungeon.Placements.Count)
            {
                skipped++;
                continue;
            }
            AORebirth.World.Collision.DungeonRoomPlacement placement = dungeon.Placements[npc.Room];
            PlayfieldDistrictEntry? district = DistrictOf(districts, placement);
            if (district?.SpawnPoints == null || npc.Point >= district.SpawnPoints.Length
                || district.SpawnPoints[npc.Point]?.Position is not { Length: >= 3 } raw)
            {
                skipped++;
                continue;
            }
            NVector3 placed = placement.TransformTemplatePoint(new NVector3(raw[0], raw[1], raw[2]));
            if (spawns.AddPlacedPoint(npc.Hash, new AORebirth.Core.Vector.Vector3(placed.X, placed.Y, placed.Z), npc.HeadingDegrees,
                    Blueprint.RespawnSeconds, npc.Level, population.AssistanceRadius ?? 0, population.ApplyTo))
            {
                taken.Add(placed);
                _npcs++;
            }
            else
                skipped++;
        }
        if (skipped > 0)
            Logger.Warn(Label + ": " + skipped.ToString(CultureInfo.InvariantCulture) + " NPCs could not be placed (unspawnable hash or point outside the grid)");
    }

    /// <summary>Lift platforms and controller spots are taken too: nothing from the pool stands on them.</summary>
    void ReserveFixedSpots(List<NVector3> taken)
    {
        foreach (var transition in Layout.FloorTransitions)
        {
            taken.Add(transition.From.Position);
            taken.Add(transition.To.Position);
        }
        foreach (StaticDungeonController controller in Blueprint.Controllers)
            if (controller.Position is { Length: 3 } p)
                taken.Add(new NVector3(p[0], p[1], p[2]));
    }

    /// <summary>
    /// Chests, then turrets, on spots from the style's site pool: for each placed room, its template's spot list and
    /// per-room average roll how many (the whole part, plus one more at the fraction as a chance, capped at the
    /// spots), on distinct random spots that nothing placed already stands on. A new seed every instance.
    /// </summary>
    void PlaceSites(AORebirth.World.Collision.DungeonWorldLayout dungeon, PlayfieldLocality locality, List<NVector3> taken)
    {
        StaticDungeonSitePool? pool = Blueprint.Sites;
        if (pool == null)
            return;
        var random = new Random(_chestSeed);
        StaticChestPlan? chests = Blueprint.Chests;
        ChestSpawnDefinition[]? appearances = chests == null ? null : Array.ConvertAll(chests.Templates.ToArray(), template => ChestAppearance(template, chests.Quality));
        var registry = GetRequiredService<DynelRegistry>();
        var minter = GetRequiredService<HashItemMinter>();
        var ids = GetRequiredService<IItemInstanceIdAllocator>();
        var spawns = GetRequiredService<HashSpawnSystem>();
        var rolled = new List<(int LowId, int HighId, int Quality)>();

        foreach (AORebirth.World.Collision.DungeonRoomPlacement placement in dungeon.Placements)
        {
            if (!pool.Rooms.TryGetValue(placement.Template.Name, out StaticDungeonRoomSites? room))
                continue;
            int turn = (placement.Facing - placement.Template.TemplateFacing) & 3;
            if (chests != null && appearances is { Length: > 0 } && room.Chests != null)
            {
                foreach (StaticDungeonSpot spot in PickSpots(room.Chests, random, placement, taken))
                {
                    NVector3 world = SpotWorld(placement, spot);
                    ChestSpawnDefinition appearance = appearances[random.Next(appearances.Length)];
                    var chest = new Chest(new Identity { Type = IdentityType.Container, Instance = ids.Allocate() }, Chest.LootCapacity, chests.LockDifficulty)
                    {
                        Playfield = this,
                        Position = new AORebirth.Core.Vector.Vector3(world.X, world.Y, world.Z),
                        Rotation = Door.Heading(spot.Heading != null ? (StaticDungeonCatalog.YawDegrees(spot.Heading) + turn * 90) % 360 : random.Next(360)),
                        SpawnSource = SpawnSource.ContentPlacement,
                        SpawnDefinition = appearance,
                        CollisionRadius = ChestRadius(appearance)
                    };
                    // The chest's item table rolls exactly as an NPC corpse's does, at the dungeon's chest quality; its
                    // items live in memory for this instance and become rows only when a player takes them.
                    rolled.Clear();
                    LootableDynel.RollItemTable(chests.LootTable, chests.Quality, minter, Chest.LootCapacity, 0, rolled);
                    for (int slot = 0; slot < rolled.Count; slot++)
                    {
                        (int lowId, int highId, int quality) = rolled[slot];
                        if (!chest.Loot.Add(slot, minter.CreateWithNewInstance(lowId, highId, quality, ItemSource.Loot)))
                            break;
                    }
                    if (!registry.TryRegister(chest))
                        throw new InvalidOperationException("A static dungeon chest identity is already registered.");
                    locality.RegisterDynel(chest);
                    taken.Add(world);
                    _chests++;
                }
            }

            if (Blueprint.Turrets is { } turrets && room.Turrets != null)
            {
                foreach (StaticDungeonSpot spot in PickSpots(room.Turrets, random, placement, taken))
                {
                    NVector3 world = SpotWorld(placement, spot);
                    int heading = spot.Heading != null ? (StaticDungeonCatalog.YawDegrees(spot.Heading) + turn * 90) % 360 : random.Next(360);
                    if (spawns.AddPlacedPoint(turrets.Hash, new AORebirth.Core.Vector.Vector3(world.X, world.Y, world.Z), heading,
                            respawnSeconds: null, turrets.Level, Layout.Population.AssistanceRadius ?? 0, Layout.Population.ApplyTo))
                    {
                        taken.Add(world);
                        _turrets++;
                    }
                }
            }
        }
    }

    /// <summary>The spots of <paramref name="sites"/> this room gets this time: the rolled count, distinct, clear of what stands already.</summary>
    static List<StaticDungeonSpot> PickSpots(StaticDungeonSiteKind sites, Random random, AORebirth.World.Collision.DungeonRoomPlacement placement,
        List<NVector3> taken)
    {
        var picked = new List<StaticDungeonSpot>();
        if (sites.Spots is not { Count: > 0 } spots || sites.PerRoom <= 0)
            return picked;
        int wanted = (int)Math.Floor(sites.PerRoom);
        if (random.NextDouble() < sites.PerRoom - wanted)
            wanted++;
        wanted = Math.Min(wanted, spots.Count);
        if (wanted == 0)
            return picked;
        var order = new List<StaticDungeonSpot>(spots);
        random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(order));
        foreach (StaticDungeonSpot spot in order)
        {
            if (picked.Count >= wanted)
                break;
            NVector3 world = SpotWorld(placement, spot);
            bool clear = true;
            foreach (NVector3 other in taken)
            {
                if (NVector3.DistanceSquared(world, other) < SpotClearance * SpotClearance)
                {
                    clear = false;
                    break;
                }
            }
            if (clear)
                picked.Add(spot);
        }
        return picked;
    }

    /// <summary>A pool spot (template X and Z, height above the floor) in this placed room.</summary>
    static NVector3 SpotWorld(AORebirth.World.Collision.DungeonRoomPlacement placement, StaticDungeonSpot spot)
    {
        float[] p = spot.Position!;
        return placement.TransformTemplatePoint(new NVector3(p[0], placement.Template.TemplatePos.Y + p[1], p[2]));
    }

    /// <summary>The captured Shadowlands chest's ChestFullUpdate shape with this template and QL.</summary>
    internal static ChestSpawnDefinition ChestAppearance(int template, int quality)
    {
        ChestSpawnDefinition appearance = ChestSpawnDefinition.MissionChest();
        appearance.StateMachineInstance = 0;
        appearance.Stats =
        [
            new() { StatId = (int)CharacterStat.Flags, Value = ChestFlags },
            new() { StatId = (int)CharacterStat.StaticInstance, Value = (uint)template },
            new() { StatId = (int)CharacterStat.ACGItemLevel, Value = (uint)Math.Max(1, quality) },
            new() { StatId = (int)CharacterStat.ACGItemTemplateID, Value = (uint)template },
            new() { StatId = (int)CharacterStat.ACGItemTemplateID2, Value = (uint)template },
            new() { StatId = (int)CharacterStat.MultipleCount, Value = 1 }
        ];
        return appearance;
    }

    float ChestRadius(ChestSpawnDefinition appearance)
        => MissionChestPlacement.TryGetChestSphere(GameData, ItemTemplates, appearance, out var sphere) ? sphere.Radius : FallbackChestRadius;

    protected override void OnDispose()
    {
        base.OnDispose();
        Released?.Invoke(this);
    }
}
