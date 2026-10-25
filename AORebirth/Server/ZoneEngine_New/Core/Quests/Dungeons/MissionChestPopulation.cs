namespace ZoneEngine_New.Core.Quests.Dungeons;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using AORebirth.Interfaces.Persistence.Missions;
using AORebirth.World.Collision;
using SmokeLounge.AOtomation.Messaging.GameData;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.WorldSimulation;
using Vector3 = System.Numerics.Vector3;

/// <summary>One accepted run's generate-once population, using the existing mission and inventory DAOs.</summary>
public sealed class MissionChestPopulation
{
    readonly IMissionDao _dao;
    readonly int _ownerId;
    readonly string _questId;
    readonly long _assigned;
    readonly string _frozenJson;
    readonly QuestDungeonParameters _parameters;
    readonly MissionChestPolicy? _policy;
    readonly IItemBuilder _items;
    readonly IZoneLogger _logger;
    volatile bool _retired;
    bool _materialized;

    internal string? DeferredReason { get; private set; }

    public MissionChestPopulation(IMissionDao dao, int ownerId, string questId, long assigned,
        string frozenJson, QuestDungeonParameters parameters, MissionChestPolicy? policy,
        IItemBuilder items, IZoneLogger logger)
    {
        ArgumentNullException.ThrowIfNull(dao);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(logger);
        if (ownerId <= 0 || assigned <= 0 || string.IsNullOrWhiteSpace(questId) || string.IsNullOrEmpty(frozenJson))
            throw new ArgumentException("Chest persistence requires an exact accepted mission run.");
        _dao = dao;
        _ownerId = ownerId;
        _questId = questId;
        _assigned = assigned;
        _frozenJson = frozenJson;
        _parameters = parameters;
        _policy = policy?.Clone();
        _items = items;
        _logger = logger;
    }

    public void Retire() => _retired = true;

    internal bool Populate(QuestDungeonPlayfield world, DungeonWorldLayout dungeon, PlayfieldWorldSimulation simulation)
    {
        if (_materialized || _retired)
            return true;
        List<MissionChestSnapshot>? stored = ReadStored();
        bool reconstructed = stored != null;
        if (stored == null)
        {
            if (_policy == null)
                return Defer("policy-unavailable");
            if (_parameters.Quality is < 1 or > 250)
                return Defer("accepted-quality-unavailable");
            int placementSeed = RandomNumberGenerator.GetInt32(int.MaxValue);
            var random = new Random(placementSeed);
            var exclusions = new List<Vector3>();
            if (_parameters.ObjectiveItem is { } objective)
                exclusions.Add(new Vector3(objective.X, objective.Y, objective.Z));
            if (_parameters.ObjectiveNpc is { } npc)
                exclusions.Add(new Vector3(npc.X, npc.Y, npc.Z));
            IGameData data = world.GetRequiredService<IGameData>();
            IItemTemplateCatalog templates = world.GetRequiredService<IItemTemplateCatalog>();
            if (_policy.RoomDensity > 0)
            {
                if (simulation.Surface == null)
                    return Defer("collision-surface-unavailable");
                if (_policy.Placement?.IsValid != true)
                    return Defer("placement-policy-unavailable-or-invalid");
                if (data.PlayerCollisionSphere is not { Radius: > 0, CenterY: > 0 } playerSphere
                    || !float.IsFinite(playerSphere.Radius) || !float.IsFinite(playerSphere.CenterY))
                    return Defer("player-collision-sphere-unavailable-or-invalid");
            }
            if (!MissionChestPlacement.TryGetChestSphere(data, templates, _policy, out var sphere))
                return Defer("chest-collision-sphere-unavailable-or-invalid");
            var loot = new MissionChestLootService(templates, data, _items, _policy);
            IItemInstanceIdAllocator ids = world.GetRequiredService<IItemInstanceIdAllocator>();
            var initial = new List<MissionChestInitialData>();
            var diagnostics = new MissionChestPlacementDiagnostics { CaptureRouteWitnesses = false };
            IReadOnlyList<MissionChestPosition> positions = _policy.RoomDensity == 0 ? []
                : MissionChestPlacement.Plan(dungeon, world.Layout, simulation, data, templates, exclusions,
                    _policy, random, diagnostics);
            foreach (MissionChestPosition point in positions)
            {
                int container = ids.Allocate();
                var snapshot = new MissionChestSnapshot
                {
                    ContainerInstance = container, RoomIndex = point.RoomIndex,
                    X = point.X, Y = point.Y, Z = point.Z, YawDegrees = point.YawDegrees,
                    CollisionRadius = sphere.Radius,
                    SpawnDefinition = _policy.SpawnDefinition
                };
                string json = JsonSerializer.Serialize(snapshot);
                if (json.Length > 1024)
                    throw new InvalidDataException("A frozen chest exceeds the existing mission-flag value limit.");
                IReadOnlyList<Item> contents = loot.Roll(_parameters.Quality, random);
                initial.Add(new MissionChestInitialData
                {
                    ContainerInstance = container, SnapshotJson = json,
                    Items = contents.Select((item, slot) => new MissionItemInstanceData
                    {
                        InstanceId = item.InstanceId, ContainerType = (int)IdentityType.Container,
                        ContainerInstance = container, ContainerPlacement = slot, ItemType = (int)item.Identity.Type,
                        LowId = item.LowId, HighId = item.HighId, Quality = item.Quality,
                        StackCount = item.StackCount, Source = (byte)item.Source
                    }).ToArray()
                });
            }
            _logger.Info("Mission chest population planned " + Context + " totalCells=" + dungeon.Rooms.Count
                + " placementSeed=" + placementSeed
                + " eligibleRooms=" + diagnostics.EligibleRooms + " requested=" + diagnostics.Requested
                + " generated=" + initial.Count + " candidates=" + diagnostics.CandidateCounts.Values.Sum()
                + " rejections=" + string.Join(",", diagnostics.Rejections.OrderBy(pair => pair.Key)
                    .Select(pair => pair.Key + "=" + pair.Value)));

            try
            {
                _dao.Execute(_ownerId, transaction =>
                {
                    if (transaction is not IMissionChestPopulationTransaction chests)
                        throw new InvalidOperationException("Mission DAO chest persistence is unavailable.");
                    return chests.TryInitializeChestPopulation(new MissionChestPopulationRequest
                    {
                        QuestId = _questId, AssignedAtUtcTicks = _assigned,
                        ExpectedAcgBuildingGeneratorJson = _frozenJson, NowUtcTicks = DateTime.UtcNow.Ticks,
                        Chests = initial
                    });
                });
            }
            catch (Exception exception)
            {
                // A commit acknowledgement can be lost. Only the durable seal permits recovery;
                // never insert the same candidates again or expose an uncommitted population.
                stored = ReadStored();
                if (stored == null)
                {
                    _logger.Error(exception, "Mission chest persistence failed without a durable seal " + Context);
                    throw;
                }
                _logger.Warn("Mission chest persistence recovered from its durable seal " + Context);
            }
            stored ??= ReadStored()
                ?? throw new InvalidDataException("The mission chest population did not commit its seal.");
        }
        DeferredReason = null;

        IInventoryRepository inventory = world.GetRequiredService<IInventoryRepository>();
        IItemTemplateCatalog catalog = world.GetRequiredService<IItemTemplateCatalog>();
        var registry = world.GetRequiredService<DynelRegistry>();
        var locality = world.GetRequiredService<PlayfieldLocality>();
        foreach (MissionChestSnapshot snapshot in stored)
        {
            if (snapshot.RoomIndex < 0 || snapshot.RoomIndex >= dungeon.Placements.Count)
                throw new InvalidDataException("A persisted chest names an unavailable frozen room.");
            var chest = new Chest(new Identity { Type = IdentityType.Container, Instance = snapshot.ContainerInstance })
            {
                Playfield = world,
                Position = new AORebirth.Core.Vector.Vector3(snapshot.X, snapshot.Y, snapshot.Z),
                Rotation = Door.Heading(snapshot.YawDegrees),
                SpawnSource = SpawnSource.ContentPlacement,
                SpawnDefinition = snapshot.SpawnDefinition,
                CollisionRadius = snapshot.CollisionRadius,
                AccessValidator = player => !_retired,
                RefreshLoot = target => Refresh(target, inventory, catalog)
            };
            if (!Refresh(chest, inventory, catalog))
                throw new InvalidDataException("The persisted chest contents cannot be reconstructed.");
            // An empty chest is deliberately materialized too; its durable empty rows never reroll.
            if (!registry.TryRegister(chest))
                throw new InvalidDataException("A persisted mission chest identity is already registered.");
            locality.RegisterDynel(chest);
        }
        _materialized = true;
        _logger.Info("Mission chests loaded quest=" + _questId + " count=" + stored.Count + " " + Context
            + " persisted=" + stored.Count + " instantiated=" + stored.Count + " reconstructed=" + reconstructed
            + " floors=" + string.Join(",", stored.GroupBy(snapshot => world.Layout.Generator?.Rooms is { } rooms
                    && snapshot.RoomIndex < rooms.Length ? rooms[snapshot.RoomIndex].Floor.ToString(CultureInfo.InvariantCulture) : "unknown")
                .OrderBy(group => group.Key).Select(group => group.Key + "=" + group.Count())));
        return true;
    }

    internal bool Defer(string reason)
    {
        if (!string.Equals(DeferredReason, reason, StringComparison.Ordinal))
            _logger.Warn("Mission chest population deferred " + Context + " reason=" + reason);
        DeferredReason = reason;
        return false;
    }

    string Context => "quest=" + _questId + " owner=" + _ownerId + " assigned=" + _assigned
        + " ql=" + _parameters.Quality + " style=" + (_parameters.LayoutPlan?.StyleId.ToString(CultureInfo.InvariantCulture) ?? "unknown")
        + " seed=" + _parameters.Seed + " floors=" + (_parameters.LayoutPlan?.FloorCount.ToString(CultureInfo.InvariantCulture) ?? "unknown");

    List<MissionChestSnapshot>? ReadStored()
    {
        IList<MissionFlagData> flags = _dao.ReadCharacter(_ownerId).Flags;
        string prefix = MissionChestPopulationFlags.Prefix(_assigned);
        MissionFlagData[] rows = flags.Where(flag => flag.QuestId == _questId
            && flag.FlagKey.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        MissionFlagData? seal = rows.SingleOrDefault(flag => flag.FlagKey == MissionChestPopulationFlags.Seal(_assigned));
        if (seal == null)
        {
            if (rows.Length != 0)
                throw new InvalidDataException("Mission chest records exist without an atomic population seal.");
            return null;
        }
        if (!int.TryParse(seal.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int count)
            || count < 0 || rows.Length != count + 1)
            throw new InvalidDataException("The mission chest seal and snapshot count disagree.");
        var snapshots = new List<MissionChestSnapshot>(count);
        var identities = new HashSet<int>();
        for (int index = 0; index < count; index++)
        {
            MissionFlagData? row = rows.SingleOrDefault(flag => flag.FlagKey == MissionChestPopulationFlags.Chest(_assigned, index));
            MissionChestSnapshot? snapshot = row == null ? null : JsonSerializer.Deserialize<MissionChestSnapshot>(row.Value);
            if (snapshot == null || snapshot.ContainerInstance <= 0 || !identities.Add(snapshot.ContainerInstance)
                || !float.IsFinite(snapshot.X) || !float.IsFinite(snapshot.Y) || !float.IsFinite(snapshot.Z)
                || !float.IsFinite(snapshot.CollisionRadius) || snapshot.CollisionRadius <= 0
                || snapshot.SpawnDefinition == null)
                throw new InvalidDataException("A frozen mission chest snapshot is invalid.");
            snapshot.SpawnDefinition.Validate();
            snapshots.Add(snapshot);
        }
        return snapshots;
    }

    bool Refresh(Chest chest, IInventoryRepository inventory, IItemTemplateCatalog catalog)
    {
        if (_retired)
            return false;
        // This runs under Chest.LootGate for every new opener. An uncertain prior transfer is
        // reconciled from row ownership before another player can be offered the old projection.
        IReadOnlyList<ItemInstanceRecord> rows = inventory.GetContainerItems(chest.Identity.Instance);
        if (rows.Count > 3 || rows.Any(row => row.ContainerType != (int)IdentityType.Container
                || row.ContainerInstance != chest.Identity.Instance || row.ContainerPlacement is < 0 or > 2
                || row.InstanceId <= 0 || row.ItemType <= 0 || row.LowId <= 0 || row.HighId <= 0 || row.StackCount <= 0
                || row.Quality < Math.Max(1, _parameters.Quality - 5) || row.Quality > Math.Min(250, _parameters.Quality + 5)
                || !catalog.TryGet(row.LowId, out _) || !catalog.TryGet(row.HighId, out _))
            || rows.Select(row => row.ContainerPlacement).Distinct().Count() != rows.Count)
            return false;
        var items = rows.Select(row => (row.ContainerPlacement, Item: _items.Create(row.LowId, row.HighId,
            row.Quality, row.Source, row.StackCount, row.InstanceId,
            new Identity { Type = (IdentityType)row.ItemType, Instance = row.InstanceId }))).ToArray();
        if (items.Where((entry, index) => entry.Item.Quality != rows[index].Quality).Any())
            return false;
        chest.Loot.Content.Clear();
        foreach (var entry in items)
        {
            entry.Item.IsPersisted = true;
            if (!chest.Loot.Add(entry.ContainerPlacement, entry.Item))
                throw new InvalidDataException("A persisted chest item slot changed during reconstruction.");
        }
        return true;
    }
}

/// <summary>Compact immutable per-chest metadata; live remaining contents come exclusively from item_instances.</summary>
public sealed class MissionChestSnapshot
{
    public int ContainerInstance { get; set; }
    public int RoomIndex { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public int YawDegrees { get; set; }
    public float CollisionRadius { get; set; }
    public ChestSpawnDefinition? SpawnDefinition { get; set; }
}
