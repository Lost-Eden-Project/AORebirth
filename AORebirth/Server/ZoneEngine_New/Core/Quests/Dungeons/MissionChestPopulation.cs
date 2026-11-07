namespace ZoneEngine_New.Core.Quests.Dungeons;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AORebirth.Enums;
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

/// <summary>
/// One accepted mission's chests. Where they stand comes from the layout seed every build; what they hold is rolled
/// once, on the first build, from the mission's chest loot table and kept as ordinary container item rows. The only
/// other durable record is one mission flag listing the chests' container identities in placement order.
/// </summary>
public sealed class MissionChestPopulation
{
    readonly IMissionDao _dao;
    readonly int _ownerId;
    readonly string _questId;
    readonly long _assigned;
    readonly QuestDungeonParameters _parameters;
    readonly MissionChestPolicy? _policy;
    readonly IItemBuilder _items;
    readonly IZoneLogger _logger;
    volatile bool _retired;
    bool _materialized;

    internal string? DeferredReason { get; private set; }

    public MissionChestPopulation(IMissionDao dao, int ownerId, string questId, long assigned,
        QuestDungeonParameters parameters, MissionChestPolicy? policy, IItemBuilder items, IZoneLogger logger)
    {
        ArgumentNullException.ThrowIfNull(dao);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(logger);
        if (ownerId <= 0 || assigned <= 0 || string.IsNullOrWhiteSpace(questId))
            throw new ArgumentException("Chest persistence requires an exact accepted mission run.");
        _dao = dao;
        _ownerId = ownerId;
        _questId = questId;
        _assigned = assigned;
        _parameters = parameters;
        _policy = policy?.Clone();
        _items = items;
        _logger = logger;
    }

    public void Retire() => _retired = true;

    /// <summary>Called from the dungeon's build. False (with <see cref="DeferredReason"/>) means try again on the next arrival.</summary>
    internal bool Populate(QuestDungeonPlayfield world, DungeonWorldLayout dungeon)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(dungeon);
        if (_materialized || _retired)
            return true;
        if (_policy == null)
            return Defer("policy-unavailable");
        if (_parameters.Quality is < 1 or > 250)
            return Defer("accepted-quality-unavailable");
        if (_parameters.ChestLoot is not { Count: > 0 } chestLoot)
            return Defer("chest-loot-table-empty");
        IGameData data = world.GetRequiredService<IGameData>();
        IItemTemplateCatalog templates = world.GetRequiredService<IItemTemplateCatalog>();
        if (!MissionChestPlacement.TryGetChestSphere(data, templates, _policy, out var sphere))
            return Defer("chest-collision-sphere-unavailable-or-invalid");

        // Placement is a function of the seed, so it is the same on every build of this dungeon.
        IReadOnlyList<MissionChestSite> sites = _policy.RoomDensity > 0
            ? MissionChestPlacement.Plan(world.ChestSites(dungeon, _policy), _policy, new Random(unchecked(world.Layout.Seed * 48271)))
            : [];

        int[]? stored = ReadStored();
        if (stored == null)
        {
            HashItemMinter minter = world.GetRequiredService<HashItemMinter>();
            IItemInstanceIdAllocator ids = world.GetRequiredService<IItemInstanceIdAllocator>();
            var initial = new List<MissionChestInitialData>(sites.Count);
            var rolled = new List<(int LowId, int HighId, int Quality)>();
            foreach (MissionChestSite _ in sites)
            {
                int container = ids.Allocate();
                // The chest's item table rolls exactly as an NPC corpse's does, at the mission quality.
                rolled.Clear();
                LootableDynel.RollItemTable(chestLoot, _parameters.Quality, minter, Chest.LootCapacity, 0, rolled);
                var contents = new List<Item>(rolled.Count);
                foreach ((int lowId, int highId, int quality) in rolled)
                    contents.Add(minter.CreateWithNewInstance(lowId, highId, quality, ItemSource.Loot));
                initial.Add(new MissionChestInitialData
                {
                    ContainerInstance = container,
                    Items = contents.Select((item, slot) => new MissionItemInstanceData
                    {
                        InstanceId = item.InstanceId, ContainerType = (int)IdentityType.Container,
                        ContainerInstance = container, ContainerPlacement = slot, ItemType = (int)item.Identity.Type,
                        LowId = item.LowId, HighId = item.HighId, Quality = item.Quality,
                        StackCount = item.StackCount, Source = (byte)item.Source
                    }).ToArray()
                });
            }
            bool fresh = _dao.Execute(_ownerId, transaction =>
                transaction is IMissionChestPopulationTransaction chests
                    ? chests.TryInitializeChestPopulation(new MissionChestPopulationRequest
                    {
                        QuestId = _questId, AssignedAtUtcTicks = _assigned, NowUtcTicks = DateTime.UtcNow.Ticks, Chests = initial
                    })
                    : throw new InvalidOperationException("Mission DAO chest persistence is unavailable."));
            // Another build of this dungeon (another zone instance) may have committed first: use its chests.
            stored = fresh ? initial.Select(chest => chest.ContainerInstance).ToArray()
                : ReadStored() ?? throw new InvalidDataException("The mission chest population did not commit.");
            _logger.Info("Mission chests " + (fresh ? "rolled" : "reused") + " " + Context + " chests=" + stored.Length
                + " items=" + initial.Sum(chest => chest.Items.Count));
        }

        if (stored.Length != sites.Count)
            _logger.Warn("Mission chest count differs from the stored population " + Context + " stored=" + stored.Length
                + " planned=" + sites.Count + "; placing the first " + Math.Min(stored.Length, sites.Count));
        int count = Math.Min(stored.Length, sites.Count);
        IInventoryRepository inventory = world.GetRequiredService<IInventoryRepository>();
        IItemTemplateCatalog catalog = world.GetRequiredService<IItemTemplateCatalog>();
        var registry = world.GetRequiredService<DynelRegistry>();
        var locality = world.GetRequiredService<PlayfieldLocality>();
        var heading = new Random(unchecked(world.Layout.Seed * 31 + 7));
        for (int index = 0; index < count; index++)
        {
            var chest = new Chest(new Identity { Type = IdentityType.Container, Instance = stored[index] })
            {
                Playfield = world,
                Position = sites[index].Position,
                Rotation = Door.Heading(heading.Next(360)),
                SpawnSource = SpawnSource.ContentPlacement,
                SpawnDefinition = _policy.SpawnDefinition,
                CollisionRadius = sphere.Radius,
                AccessValidator = player => !_retired,
                RefreshLoot = target => Refresh(target, inventory, catalog)
            };
            if (!Refresh(chest, inventory, catalog))
                throw new InvalidDataException("The persisted chest contents cannot be reconstructed.");
            // An emptied chest is placed too; its rows are gone and it stays empty.
            if (!registry.TryRegister(chest))
                throw new InvalidDataException("A persisted mission chest identity is already registered.");
            locality.RegisterDynel(chest);
        }
        _materialized = true;
        DeferredReason = null;
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
        + " ql=" + _parameters.Quality + " seed=" + _parameters.Seed;

    /// <summary>The stored chests' container identities in placement order; null when this run has not rolled chests yet.</summary>
    int[]? ReadStored()
    {
        string? value = _dao.Execute(_ownerId, transaction =>
            transaction.GetFlag(new MissionKeyData(_ownerId, _questId), MissionChestPopulationFlags.Key(_assigned))?.Value);
        if (value == null)
            return null;
        if (value.Length == 0)
            return [];
        var ids = new List<int>();
        foreach (string part in value.Split(','))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id <= 0)
                throw new InvalidDataException("The stored mission chest list is invalid.");
            ids.Add(id);
        }
        return ids.ToArray();
    }

    bool Refresh(Chest chest, IInventoryRepository inventory, IItemTemplateCatalog catalog)
    {
        if (_retired)
            return false;
        // This runs under Chest.LootGate for every new opener. An uncertain prior transfer is
        // reconciled from row ownership before another player can be offered the old projection.
        IReadOnlyList<ItemInstanceRecord> rows = inventory.GetContainerItems(chest.Identity.Instance);
        if (rows.Count > Chest.LootCapacity || rows.Any(row => row.ContainerType != (int)IdentityType.Container
                || row.ContainerInstance != chest.Identity.Instance
                || row.ContainerPlacement < 0 || row.ContainerPlacement >= Chest.LootCapacity
                || row.InstanceId <= 0 || row.ItemType <= 0 || row.LowId <= 0 || row.HighId <= 0 || row.StackCount <= 0
                || row.Quality <= 0 || !catalog.TryGet(row.LowId, out _) || !catalog.TryGet(row.HighId, out _))
            || rows.Select(row => row.ContainerPlacement).Distinct().Count() != rows.Count)
            return false;
        // The durable rows are the chest's remaining contents; never delete, compact or replace them here.
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
