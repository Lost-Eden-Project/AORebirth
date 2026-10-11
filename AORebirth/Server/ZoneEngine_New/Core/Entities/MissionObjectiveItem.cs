namespace ZoneEngine_New.Core.Entities;

using System;
using System.Linq;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.Quests.Dungeons;

/// <summary>A persisted, owner-bound objective collected by ClientGetItem, never a template OnUse spell.</summary>
public sealed class MissionObjectiveItem : WorldItem
{
    readonly Func<Player, MissionObjectiveItem, bool> _collect;
    readonly object _pickupGate = new();
    bool _collected;

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

    public bool TryPickup(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        lock (_pickupGate)
        {
            if (_collected || player.IsDead || player.IsPersistenceQuarantined || player.Session == null
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

            _collected = true;
            try
            {
                var owned = player.Inventory.Inventory.Content.Single(pair => pair.Value.Identity == Identity);
                // Both retained pickup traces acknowledge the original world identity, with a zero target and placement.
                player.Session.Send(new ContainerAddItemMessage
                {
                    Identity = player.Identity, SourceContainer = Identity, Target = Identity.None, TargetPlacement = 0
                });
                var ownedUpdate = (SimpleItemFullUpdateMessage)BuildSpawnMessage();
                ownedUpdate.Identitytype = (int)player.Identity.Type;
                ownedUpdate.Instance = player.Identity.Instance;
                ownedUpdate.Unknown2 = 1;
                ownedUpdate.Unknown3 = checked((byte)owned.Key);
                player.Session.Send(ownedUpdate);
                var locality = dungeon.GetRequiredService<PlayfieldLocality>();
                locality.UnregisterDynel(this, transferredTo: player);
                registry.UnregisterExact(this);
                return true;
            }
            catch (Exception exception)
            {
                // Collection already committed. Recovery must reload its exact identity instead of retrying a grant.
                player.QuarantinePersistence();
                player.Session?.Close();
                dungeon.GetService<IZoneLogger>()?.Error(exception, "Committed objective pickup publication failed; reload required.");
                return false;
            }
        }
    }

    protected override bool DelaysUse => false;

    protected override bool OnUse(Player player) => false;

    protected override GameTuple<CharacterStat, uint>[] BuildStats()
    {
        var stats = base.BuildStats();
        if (Binding.WorldFlags.HasValue)
            foreach (var stat in stats)
                if (stat.Value1 == CharacterStat.Flags)
                    stat.Value2 = Binding.WorldFlags.Value;
        if (Binding.QuestInstanceStat <= 0)
            return stats;
        Array.Resize(ref stats, stats.Length + 1);
        stats[^1] = new() { Value1 = CharacterStat.QuestInstance, Value2 = (uint)Binding.QuestInstanceStat };
        return stats;
    }
}
