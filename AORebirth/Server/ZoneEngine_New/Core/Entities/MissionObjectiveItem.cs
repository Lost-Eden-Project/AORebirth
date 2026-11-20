namespace ZoneEngine_New.Core.Entities;

using System;
using System.Linq;
using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages.N3Messages;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.Quests.Dungeons;

/// <summary>
/// A persisted, owner-bound mission item, never a template OnUse spell. Collecting it is two steps: the collect or
/// earn callback queues the mission transaction in the player's write-behind lane and returns true, and the quest
/// service calls <see cref="FinishPickup"/> or <see cref="FinishFind"/> on this playfield's tick once it has landed.
/// </summary>
public sealed class MissionObjectiveItem : WorldItem
{
    readonly Func<Player, MissionObjectiveItem, bool> _collect;
    readonly object _pickupGate = new();
    bool _collected;
    bool _pending;

    public MissionObjectiveItem(MissionItemObjectiveBinding binding, ItemTemplate template,
        Func<Player, MissionObjectiveItem, bool> collect)
        : base(binding?.Identity ?? throw new ArgumentNullException(nameof(binding)), template,
            binding.LowId, binding.HighId, binding.Quality)
    {
        if (!binding.IsValid)
            throw new ArgumentException("Invalid mission objective binding.", nameof(binding));
        Binding = binding;
        _collect = collect ?? throw new ArgumentNullException(nameof(collect));
    }

    public MissionItemObjectiveBinding Binding { get; }

    /// <summary>True when the collection was queued; <see cref="FinishPickup"/> then publishes or releases it.</summary>
    public bool TryPickup(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        lock (_pickupGate)
        {
            if (Binding.IsSourceItem || (MissionRollType)Binding.MissionType is not (MissionRollType.FindItem or MissionRollType.FindItemReturn)
                || _collected || _pending || player.IsDead || player.IsPersistenceQuarantined || player.Session == null
                || player.Identity.Instance != Binding.OwnerId || Playfield is not QuestDungeonPlayfield dungeon
                || !ReferenceEquals(player.Playfield, dungeon)
                || !string.Equals(dungeon.QuestId, Binding.QuestId, StringComparison.Ordinal)
                || GetEdgeDistanceTo(player) > LootableDynel.OpenRange)
                return false;

            var registry = dungeon.GetRequiredService<DynelRegistry>();
            if (!registry.TryGet(Identity, out Dynel? registered) || !ReferenceEquals(registered, this))
                return false;
            if (!_collect(player, this))
                return false;

            _pending = true;
            return true;
        }
    }

    /// <summary>
    /// The queued collection landed (<paramref name="collected"/>) or did not: the item leaves the world for the
    /// player's inventory, or is free to be picked up again. On this playfield's tick.
    /// </summary>
    public void FinishPickup(Player player, bool collected)
    {
        ArgumentNullException.ThrowIfNull(player);
        lock (_pickupGate)
        {
            if (!_pending)
                return;
            _pending = false;
            if (!collected || Playfield is not QuestDungeonPlayfield dungeon)
                return;

            _collected = true;
            var registry = dungeon.GetRequiredService<DynelRegistry>();
            try
            {
                var owned = player.Inventory.Inventory.Content.Single(pair => pair.Value.Identity == Identity);
                Network.IZoneSession session = player.Session
                    ?? throw new InvalidOperationException("The collector is no longer connected.");
                // Both retained pickup traces acknowledge the original world identity, with a zero target and placement.
                session.Send(new ContainerAddItem
                {
                    Identity = player.Identity, Source = Identity, Target = Identity.None, Slot = 0
                });
                var ownedUpdate = (SimpleItemFullUpdateMessage)BuildSpawnMessage();
                ownedUpdate.OwnerType = (int)player.Identity.Type;
                ownedUpdate.OwnerInstance = player.Identity.Instance;
                // The two bytes after the state machine: the inventory id (1), then the slot.
                ownedUpdate.Unknown2 = (short)((1 << 8) | checked((byte)owned.Key));
                session.Send(ownedUpdate);
                var locality = dungeon.GetRequiredService<PlayfieldLocality>();
                locality.UnregisterDynel(this, transferredTo: player);
                registry.UnregisterExact(this);
            }
            catch (Exception exception)
            {
                // Collection already committed. Recovery must reload its exact identity instead of retrying a grant.
                player.QuarantinePersistence();
                player.Session?.Close();
                dungeon.GetService<IZoneLogger>()?.Error(exception, "Committed objective pickup publication failed; reload required.");
            }
        }
    }

    /// <summary>
    /// Find Item: the owner selected this item (LookAt). <paramref name="earn"/> queues the mission's earn and returns
    /// true; once it lands (<see cref="FinishFind"/>) the item leaves the world for everyone; it is never picked up.
    /// Return an item still needs <see cref="TryPickup"/>.
    /// </summary>
    public bool TryFindByTarget(Player player, Func<Player, MissionObjectiveItem, bool> earn)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(earn);
        lock (_pickupGate)
        {
            if (Binding.IsSourceItem || (MissionRollType)Binding.MissionType != MissionRollType.FindItem
                || _collected || _pending || player.IsDead || player.Identity.Instance != Binding.OwnerId
                || Playfield is not QuestDungeonPlayfield dungeon || !ReferenceEquals(player.Playfield, dungeon)
                || !string.Equals(dungeon.QuestId, Binding.QuestId, StringComparison.Ordinal))
                return false;

            var registry = dungeon.GetRequiredService<DynelRegistry>();
            if (!registry.TryGet(Identity, out Dynel? registered) || !ReferenceEquals(registered, this) || !earn(player, this))
                return false;

            _pending = true;
            return true;
        }
    }

    /// <summary>The queued Find Item earn landed (<paramref name="earned"/>) or did not. On this playfield's tick.</summary>
    public void FinishFind(bool earned)
    {
        lock (_pickupGate)
        {
            if (!_pending)
                return;
            _pending = false;
            if (!earned || Playfield is not QuestDungeonPlayfield dungeon)
                return;

            _collected = true;
            dungeon.GetRequiredService<PlayfieldLocality>().UnregisterDynel(this);
            dungeon.GetRequiredService<DynelRegistry>().UnregisterExact(this);
        }
    }

    protected override bool DelaysUse => false;

    protected override bool OnUse(Player player) => false;

    protected override GameTuple<Stat, int>[] BuildStats()
    {
        var stats = base.BuildStats();
        if (Binding.WorldFlags.HasValue)
            for (int i = 0; i < stats.Length; i++)
                if (stats[i].Value1 == (Stat)CharacterStat.Flags)
                    stats[i].Value2 = unchecked((int)Binding.WorldFlags.Value);
        if (Binding.QuestInstanceStat > 0)
        {
            Array.Resize(ref stats, stats.Length + 1);
            stats[^1] = new((Stat)CharacterStat.QuestInstance, Binding.QuestInstanceStat);
        }
        // Static dynel Scale is a percentage. Keep this world-only override out of
        // the parented inventory update built after the committed pickup.
        if (!_collected && Binding.WorldScalePercent is int scale)
        {
            Array.Resize(ref stats, stats.Length + 1);
            stats[^1] = new((Stat)CharacterStat.Scale, scale);
        }
        return stats;
    }
}
