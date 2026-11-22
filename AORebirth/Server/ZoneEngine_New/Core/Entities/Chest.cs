namespace ZoneEngine_New.Core.Entities
{
    using System;
    using System.Collections.Generic;

    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages;
    using LostEden.Protocol.Messages.N3Messages;

    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Loot;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Playfield.Locality;

    /// <summary>
    /// Treasure chest dynel. Shares open/loot flow with <see cref="Corpse"/> via <see cref="LootableDynel"/>.
    /// A locked chest cannot be opened until a Break and Enter item unlocks it.
    /// </summary>
    public class Chest : LootableDynel, IBreakAndEnterTarget
    {
        public const int LootCapacity = 20;

        float _collisionRadius;

        public Chest(Identity identity, int lootCapacity = LootCapacity, int lockDifficulty = 0)
            : base(identity, IdentityType.Container, lootCapacity)
        {
            LockDifficulty = Math.Max(0, lockDifficulty);
            IsLocked = LockDifficulty > 0;
        }

        public bool IsLocked { get; private set; }

        public object LootGate { get; } = new();

        /// <summary>Players whose takes from this chest are applied in memory with their durable row change still queued.</summary>
        readonly HashSet<Player> _pendingTakers = new();

        /// <summary>
        /// Records a take applied on the tick whose row relocation is queued with the player's flush
        /// (<see cref="Inventory.InventoryActionService.QueueSharedSourceRelocation"/>). Called under <see cref="LootGate"/>.
        /// </summary>
        public void RecordPendingTake(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);
            lock (LootGate)
                _pendingTakers.Add(player);
        }

        /// <summary>
        /// Writes every queued take before a new opener is shown the stored rows: a reload inside the coalesce window
        /// would otherwise offer an item a player already holds. A hard flush with nothing left to write returns at
        /// once, so this costs a transaction only when the chest is reopened within that window. A quarantined taker is
        /// skipped: storage is already the authority for them. A flush that cannot run keeps the chest closed for this
        /// attempt and the takers recorded, so the next open tries again.
        /// </summary>
        bool SettlePendingTakes()
        {
            Player[] takers;
            lock (LootGate)
            {
                if (_pendingTakers.Count == 0)
                    return true;
                takers = [.. _pendingTakers];
                _pendingTakers.Clear();
            }

            InventoryFlushService? flush = Playfield?.GetService<InventoryFlushService>();
            for (int i = 0; i < takers.Length; i++)
            {
                Player taker = takers[i];
                if (taker.IsPersistenceQuarantined)
                    continue;
                try
                {
                    if (flush == null)
                        throw new InvalidOperationException("No inventory flush service is available to settle chest takes.");
                    flush.HardFlush(taker);
                }
                catch (Exception exception)
                {
                    lock (LootGate)
                        for (int remaining = i; remaining < takers.Length; remaining++)
                            _pendingTakers.Add(takers[remaining]);
                    Playfield?.GetService<Logging.IZoneLogger>()?.Warn(
                        "Chest " + Identity.Instance.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " stays closed: a queued take by character "
                        + taker.Identity.Instance.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " could not be stored before reopening. " + exception.Message);
                    return false;
                }
            }
            return true;
        }

        public float CollisionRadius
        {
            get => _collisionRadius;
            set
            {
                if (!float.IsFinite(value) || value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _collisionRadius = value;
            }
        }

        public override double GetCollisionRadius() => CollisionRadius;

        public ChestSpawnDefinition? SpawnDefinition { get; set; }

        public Func<Player, bool>? AccessValidator { get; set; }

        /// <summary>Reloads authoritative durable contents before each new open, never on a close toggle.</summary>
        public Func<Chest, bool>? RefreshLoot { get; set; }

        /// <summary>Generated mission chests bind the current RK policy; generic containers retain their own rules.</summary>
        public Func<Item, bool>? LootItemValidator { get; set; }

        /// <summary>
        /// Every take path (move, stack join, delete) asks this before an item leaves the chest: an item up for a Need or
        /// Greed roll stays put until the roll closes.
        /// </summary>
        public bool CanDistribute(Item item) => !IsHeldForRoll(item) && PassesLootPolicy(item);

        internal bool PassesLootPolicy(Item item) => LootItemValidator?.Invoke(item) != false;

        /// <summary>Instance ids up for an open Need or Greed roll (<see cref="LootRollService"/>).</summary>
        readonly HashSet<int> _heldForRoll = new();

        /// <summary>Instance ids already rolled for on this chest: a later opener's team does not roll them again.</summary>
        readonly HashSet<int> _rolled = new();

        /// <summary>
        /// Holds a stored item for a roll, keyed by instance id because a reopen rebuilds the chest's items from their rows.
        /// False when it has no row id or was rolled for already.
        /// </summary>
        internal bool TryHoldForRoll(Item item)
        {
            ArgumentNullException.ThrowIfNull(item);
            lock (LootGate)
                return item.InstanceId > 0 && _rolled.Add(item.InstanceId) && _heldForRoll.Add(item.InstanceId);
        }

        internal void ReleaseRollHold(int instanceId)
        {
            lock (LootGate)
                _heldForRoll.Remove(instanceId);
        }

        internal bool IsHeldForRoll(Item item)
        {
            lock (LootGate)
                return _heldForRoll.Contains(item.InstanceId);
        }

        /// <summary>A Need or Greed team member opening the chest puts its items up for rolls.</summary>
        protected override void OnOpened(Player player)
            => Playfield?.GetService<LootRollService>()?.OnChestOpened(this, player);

        /// <summary>Stat 299 a lockable chest must carry for the client to allow a lock pick on it.</summary>
        public int LockDifficulty { get; }

        public override MessageBody BuildSpawnMessage()
        {
            ChestSpawnDefinition definition = SpawnDefinition
                ?? throw new InvalidOperationException("Chest appearance has not been configured.");
            definition.Validate();
            var stats = new GameTuple<Stat, int>[definition.Stats.Length];
            for (int i = 0; i < stats.Length; i++)
                stats[i] = new GameTuple<Stat, int>
                {
                    Value1 = (Stat)definition.Stats[i].StatId,
                    Value2 = unchecked((int)definition.Stats[i].Value)
                };

            // Retail mission-20261011-004128-b8e7bf2e, ordinal 480: positioned common prefix,
            // empty optional blob, lockable marker/list and chest marker; contents are a separate packet.
            return new ChestFullUpdateMessage
            {
                Identity = Identity,
                Unknown = 0,
                Unknown1 = 11,
                OwnerType = 0,
                OwnerInstance = 0,
                Position = (LostEden.Protocol.GameData.Vector3)Position,
                Rotation = (LostEden.Protocol.GameData.Quaternion)Rotation,
                PlayfieldId = Playfield?.ClientPlayfieldId ?? 0,
                StateMachine = new Identity
                {
                    Type = (IdentityType)definition.StateMachineType,
                    Instance = definition.StateMachineInstance
                },
                Unknown5 = definition.HeaderStatBytes,
                Stats = stats,
                UnknownData = [],
                Unknown7 = 2,
                Unknown8 = definition.LockableValue,
                UnknownArray = [],
                Unknown9 = 3
            };
        }

        public override bool TryUse(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);
            // Settled outside the loot gate: a hard flush takes the taker's persistence gate, which the move path takes
            // before this gate. Everything that opens or takes from a chest runs on its playfield's tick thread.
            if (!IsOpen && !SettlePendingTakes())
                return false;
            lock (LootGate)
            {
                if (!CanInteract(player))
                    return false;
                if (!IsOpen && (!CanOpenLoot(player) || RefreshLoot?.Invoke(this) == false))
                    return false;
                return base.TryUse(player);
            }
        }

        protected override bool CanOpenLoot(Player player) => !IsLocked && CanInteract(player);

        public bool CanTransferLoot(Player player)
            => OpenerIdentity == player.Identity && !IsLocked && CanInteract(player);

        public override void Tick(double deltaTime)
        {
            lock (LootGate)
                base.Tick(deltaTime);
        }

        bool CanInteract(Player player)
            => SpawnDefinition != null && Playfield != null && !player.IsDead && !player.IsPersistenceQuarantined
                && player.Session?.State == SessionState.InPlay && ReferenceEquals(player.Session.Player, player)
                && ReferenceEquals(player.Playfield, Playfield) && GetEdgeDistanceTo(player) <= OpenRange
                && Playfield.GetRequiredService<DynelRegistry>().TryGet(Identity, out Dynel? registered)
                && ReferenceEquals(registered, this) && AccessValidator?.Invoke(player) != false;

        /// <summary>A Break and Enter item used on the locked chest: it unlocks (and the client opens it) for the user.</summary>
        public BreakAndEnterResult TryBreakAndEnter(Player player, int pickRating)
        {
            ArgumentNullException.ThrowIfNull(player);
            if (!IsLocked || Playfield == null || player.IsDead || !ReferenceEquals(player.Playfield, Playfield)
                || GetEdgeDistanceTo(player) > OpenRange)
                return BreakAndEnterResult.Refused;

            if (pickRating < LockDifficulty)
            {
                BreakAndEnterActions.SendFailed(this, player);
                return BreakAndEnterResult.Failed;
            }

            IsLocked = false;
            Playfield.GetRequiredService<PlayfieldLocality>().Announce(this,
                BreakAndEnterActions.Result(Identity, player.Identity, BreakAndEnterActions.Unlocked), includeSelf: true);
            return BreakAndEnterResult.Unlocked;
        }
    }
}
