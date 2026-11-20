namespace ZoneEngine_New.Core.Characters
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using AORebirth.Database.Dao;

    using ZoneEngine_New.Core.World;

    using LostEden.Protocol.GameData;

    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Playfield;

    using Quaternion = AORebirth.Core.Vector.Quaternion;
    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// Durable writes of the in-memory character aggregate. Logout/despawn commits location + base stats
    /// synchronously; while online, dirty and periodic base-stat checkpoints are captured on the playfield tick and
    /// written in the character's write-behind lane (<see cref="ZoneEngine_New.Core.Inventory.InventoryFlushService"/>),
    /// so a checkpoint lands in order with the reward and trade transactions that also set stats.
    /// </summary>
    public sealed class CharacterSnapshotService : IDisposable
    {
        /// <summary>Quiet period after the last <see cref="CharacterSaveState.MarkDirty"/> before a checkpoint.</summary>
        public const int CoalesceMilliseconds = 2000;

        /// <summary>Continuous changes still checkpoint this long after the first unsaved one.</summary>
        public const int MaxDirtyDelayMilliseconds = 10000;

        /// <summary>Upper bound on progress lost to a crash for changes nobody marked dirty.</summary>
        public const int CheckpointIntervalMilliseconds = 60000;

        private readonly ICharacterRepository _characters;
        private readonly IStatRepository _stats;
        private readonly IZoneLogger _logger;
        private readonly WorldLink? _world;
        private volatile bool _disposed;

        public CharacterSnapshotService(
            ICharacterRepository characters,
            IStatRepository stats,
            IZoneLogger logger,
            WorldLink? world = null)
        {
            _world = world;
            ArgumentNullException.ThrowIfNull(characters);
            ArgumentNullException.ThrowIfNull(stats);
            ArgumentNullException.ThrowIfNull(logger);

            _characters = characters;
            _stats = stats;
            _logger = logger;
        }

        /// <summary>
        /// The logout save. <paramref name="relocate"/> stores the character there instead of where it stands (a death
        /// settled on logout: <see cref="Player.SettleDeathForLogout"/>), keeping its heading.
        /// </summary>
        public void Commit(Player player, (int Playfield, Vector3 Landing)? relocate = null)
        {
            ArgumentNullException.ThrowIfNull(player);

            lock (player.PersistenceGate)
            {
                if (player.IsPersistenceQuarantined)
                    throw new InvalidOperationException("A quarantined player must be reloaded before persistence.");
                RequireLease(player);
                long sequence = player.SaveState.NextSequence();
                try
                {
                    if (CommitCore(player, relocate) is { } committed)
                        player.SaveState.MarkCommitted(sequence, committed.Stats, committed.Location);
                }
                catch (DatabaseCommitOutcomeUnknownException)
                {
                    player.QuarantinePersistence();
                    player.Session?.Close();
                    throw;
                }
            }
        }

        /// <summary>
        /// The save before a move to another zone instance: everything a logout saves, with the character placed at
        /// its destination so the instance that reloads it puts it there. Like a logout it clears Online; the
        /// destination sets it again when the character spawns.
        /// </summary>
        public void CommitForTransfer(Player player, int destinationPlayfield, Vector3 landing, Quaternion heading)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(destinationPlayfield);
            lock (player.PersistenceGate)
            {
                if (player.IsPersistenceQuarantined)
                    throw new InvalidOperationException("A quarantined player must be reloaded before persistence.");
                RequireLease(player);
                long sequence = player.SaveState.NextSequence();
                var record = new CharacterRecord
                {
                    Id = player.Identity.Instance,
                    Playfield = destinationPlayfield,
                    X = landing.xf,
                    Y = landing.yf,
                    Z = landing.zf,
                    HeadingW = heading.wf,
                    HeadingX = heading.xf,
                    HeadingY = heading.yf,
                    HeadingZ = heading.zf
                };
                List<StatRecord> stats = CaptureBaseStats(player);
                try
                {
                    _characters.SaveSnapshot(record, online: 0, stats);
                    player.SaveState.MarkCommitted(sequence, stats, record);
                }
                catch (DatabaseCommitOutcomeUnknownException)
                {
                    player.QuarantinePersistence();
                    player.Session?.Close();
                    throw;
                }

                _logger.Info(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Character transfer snapshot character={0} destination={1} pos=({2},{3},{4}) stats={5}",
                        record.Id,
                        destinationPlayfield,
                        landing.xf,
                        landing.yf,
                        landing.zf,
                        stats.Count));
            }
        }

        /// <summary>
        /// Stores <paramref name="stats"/> as the character's base values ahead of a reward transaction that expects
        /// exactly them: the values the tick held when it decided the reward. Runs in the write-behind lane, without
        /// the player's gate across the write. Fails unless this instance still owns the online character.
        /// </summary>
        internal void SyncStats(Player player, IReadOnlyList<StatRecord> stats)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(stats);
            if (player.IsPersistenceQuarantined)
                throw new InvalidOperationException("A quarantined player must be reloaded before persistence.");
            RequireLease(player);
            if (stats.Count == 0)
                return;
            if (!_characters.SaveOnlineCheckpoint(player.Identity.Instance, null, stats))
                throw new InvalidOperationException("Reward stat sync requires the current online character owner.");
            // No sequence of its own: it records what storage now holds, and never retires a checkpoint.
            lock (player.PersistenceGate)
                player.SaveState.MarkCommitted(0, stats, location: null);
        }

        /// <summary>Puts Online back after a transfer that did not happen (the transfer save cleared it).</summary>
        public void RestoreOnline(int characterId) => _characters.SetOnline(characterId);

        /// <summary>Flushes the live aggregate before an atomic reward projection without releasing online ownership.</summary>
        public void CommitOnline(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);
            lock (player.PersistenceGate)
            {
                if (player.IsPersistenceQuarantined)
                    throw new InvalidOperationException("A quarantined player must be reloaded before persistence.");
                RequireLease(player);
                long sequence = player.SaveState.NextSequence();
                var location = CaptureResumableLocation(player);
                var stats = CaptureBaseStats(player);
                try
                {
                    if (!_characters.SaveOnlineCheckpoint(player.Identity.Instance, location, stats))
                        throw new InvalidOperationException("Reward checkpoint requires the current online character owner.");
                    player.SaveState.MarkCommitted(sequence, stats, location);
                }
                catch (DatabaseCommitOutcomeUnknownException)
                {
                    player.QuarantinePersistence();
                    player.Session?.Close();
                    throw;
                }
            }
        }

        private (CharacterRecord Location, List<StatRecord> Stats)? CommitCore(Player player, (int Playfield, Vector3 Landing)? relocate)
        {
            int characterId = player.Identity.Instance;
            if (characterId <= 0)
                return null;

            Playfield? playfield = player.Playfield;
            if (playfield == null)
            {
                _logger.Warn(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Character snapshot skipped character={0}: no playfield",
                        characterId));
                return null;
            }

            int playfieldId = playfield.Identity.Instance;
            if (playfieldId <= 0)
            {
                _logger.Warn(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Character snapshot skipped character={0}: playfield id {1}",
                        characterId,
                        playfieldId));
                return null;
            }

            Vector3 position = player.Position;
            if (relocate is { } destination && destination.Playfield > 0)
            {
                playfieldId = destination.Playfield;
                position = destination.Landing;
            }

            CharacterRecord record = CaptureLocation(player, characterId, playfieldId, position);
            List<StatRecord> stats = CaptureBaseStats(player);
            _characters.SaveSnapshot(record, online: 0, stats);

            _logger.Info(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Character snapshot character={0} playfield={1} pos=({2},{3},{4}) stats={5}",
                    characterId,
                    playfieldId,
                    position.xf,
                    position.yf,
                    position.zf,
                    stats.Count));
            return (record, stats);
        }

        /// <summary>
        /// Queues a checkpoint (location + base stats) in the player's write-behind lane when the player's save is
        /// due. Must run on the owning playfield tick: state is copied here because the lane may not read the live
        /// aggregate. In the lane it lands after every reward or trade transaction queued before it and before every
        /// one queued after, so a checkpoint never overwrites stats a later transaction set.
        /// </summary>
        public void CheckpointIfDue(Player player, ZoneEngine_New.Core.Inventory.InventoryFlushService lane)
            => CheckpointIfDue(player, lane, Environment.TickCount64);

        internal void CheckpointIfDue(Player player, ZoneEngine_New.Core.Inventory.InventoryFlushService lane, long nowMs)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(lane);
            if (_disposed || player.IsPersistenceQuarantined || player.Identity.Instance <= 0)
                return;
            if (!player.SaveState.TakeDue(nowMs, player.Identity.Instance))
                return;

            var checkpoint = new PendingCheckpoint(
                player.SaveState.NextSequence(),
                CaptureResumableLocation(player),
                CaptureBaseStats(player));
            lane.Enqueue(player, () => WriteCheckpoint(player, checkpoint));
        }

        /// <summary>
        /// Null keeps the stored location: a dead player must resume at the respawn point, not the corpse.
        /// Mission worlds are stored as-is; login resolves them to the world or its exterior.
        /// </summary>
        private static CharacterRecord? CaptureResumableLocation(Player player)
        {
            if (player.IsDead || player.IsRespawnPending)
                return null;
            int playfieldId = player.Playfield?.Identity.Instance ?? 0;
            return playfieldId <= 0 ? null : CaptureLocation(player, player.Identity.Instance, playfieldId);
        }

        private static CharacterRecord CaptureLocation(Player player, int characterId, int playfieldId)
            => CaptureLocation(player, characterId, playfieldId, player.Position);

        private static CharacterRecord CaptureLocation(Player player, int characterId, int playfieldId, Vector3 position)
        {
            Quaternion heading = player.Rotation;
            return new CharacterRecord
            {
                Id = characterId,
                Playfield = playfieldId,
                X = position.xf,
                Y = position.yf,
                Z = position.zf,
                HeadingW = heading.wf,
                HeadingX = heading.xf,
                HeadingY = heading.yf,
                HeadingZ = heading.zf
            };
        }

        private static List<StatRecord> CaptureBaseStats(Player player)
        {
            List<StatRecord> stats = [];
            foreach (var entry in player.Stats.GetEntries())
            {
                // Runtime-only: fight bookkeeping, taken pet slots (pets never outlive the session) and the
                // equipped weapon criteria, all rebuilt every session.
                if (StatCollection.IsRuntimeOnly(entry.Stat))
                    continue;

                if (StatCollection.IsUnset(entry.Base))
                    continue;

                stats.Add(
                    new StatRecord
                    {
                        StatId = (int)entry.Stat,
                        StatValue = entry.Base
                    });
            }

            return stats;
        }

        /// <summary>
        /// The queued checkpoint, in the player's lane: the gate only around reading and recording the persisted
        /// baseline, never across the write. A write that cannot be repeated quarantines; any other failure is the
        /// lane's to retry (a checkpoint is idempotent).
        /// </summary>
        private void WriteCheckpoint(Player player, PendingCheckpoint checkpoint)
        {
            CharacterSaveState state = player.SaveState;
            List<StatRecord> changed;
            CharacterRecord? location;
            lock (player.PersistenceGate)
            {
                // A logout snapshot or newer checkpoint already made this capture obsolete.
                if (player.IsPersistenceQuarantined || !state.IsNewerThanCommitted(checkpoint.Sequence))
                    return;
                changed = state.ChangedSincePersisted(checkpoint.Stats);
                location = checkpoint.Location != null && state.LocationChanged(checkpoint.Location)
                    ? checkpoint.Location
                    : null;
            }

            if (!OwnsLease(player.Identity.Instance))
            {
                _logger.Warn(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Character checkpoint skipped character={0}: this instance no longer holds its World lease",
                        player.Identity.Instance));
                return;
            }

            if (changed.Count > 0 || location != null)
            {
                bool written;
                try
                {
                    written = _characters.SaveOnlineCheckpoint(player.Identity.Instance, location, changed);
                }
                catch (DatabaseCommitOutcomeUnknownException)
                {
                    player.QuarantinePersistence();
                    player.Session?.Close();
                    throw;
                }

                if (!written)
                {
                    // Storage says offline while this aggregate is live; leave the baseline so nothing is lost.
                    _logger.Warn(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Character checkpoint skipped character={0}: row is not marked online",
                            player.Identity.Instance));
                    return;
                }
            }

            lock (player.PersistenceGate)
            {
                if (state.IsNewerThanCommitted(checkpoint.Sequence))
                    state.MarkCommitted(checkpoint.Sequence, changed, location);
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }

        private sealed record PendingCheckpoint(long Sequence, CharacterRecord? Location, IReadOnlyList<StatRecord> Stats);

        /// <summary>
        /// Online ownership for a spawned player: a reference on the World lease claimed at admission, and the Online
        /// flag. Disposing the last reference releases the lease to World.
        /// </summary>
        public IDisposable AcquireOnlineOwnership(int characterId)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(characterId);
            IDisposable ownership = _world?.AcquireOwnership(characterId) ?? new UnmanagedOwnership();
            try
            {
                _characters.SetOnline(characterId);
                return ownership;
            }
            catch
            {
                ownership.Dispose();
                throw;
            }
        }

        public void AbandonOnlineOwnership(int characterId, IDisposable ownership)
        {
            ArgumentNullException.ThrowIfNull(ownership);
            ownership.Dispose();
            ClearOnlineIfUnowned(characterId);
        }

        /// <summary>Clears the Online flag unless this instance still holds the character.</summary>
        public void ClearOnlineIfUnowned(int characterId)
        {
            if (_world != null && _world.TokenOf(characterId) != null) return;
            _characters.SetOffline(characterId);
        }

        /// <summary>True while this instance may write the character (always, without a World link).</summary>
        public bool OwnsLease(int characterId) => _world == null || _world.OwnsLease(characterId);

        private void RequireLease(Player player)
        {
            if (!OwnsLease(player.Identity.Instance))
                throw new InvalidOperationException(
                    "Character " + player.Identity.Instance.ToString(CultureInfo.InvariantCulture)
                    + " is not leased to this instance; refusing to save.");
        }

        private sealed class UnmanagedOwnership : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    /// <summary>
    /// Write-behind bookkeeping for one player aggregate. The schedule belongs to the owning playfield
    /// tick; persisted values and the committed sequence are guarded by <see cref="Player.PersistenceGate"/>.
    /// </summary>
    public sealed class CharacterSaveState
    {
        /// <summary>Smaller moves ride along with the next write instead of causing one.</summary>
        public const float MinimumPersistedMove = 1f;

        private readonly Dictionary<int, int> _persisted = new();
        private CharacterRecord? _persistedLocation;
        private long _sequence;
        private long _committedSequence;
        private long _dueAtMs;
        private long _deadlineMs;
        private long _nextCheckpointMs;
        private volatile bool _checkpointRequested;

        /// <summary>Asks for a checkpoint at the next due check, from any thread (a write-behind commit that just landed).</summary>
        public void RequestCheckpoint() => _checkpointRequested = true;

        /// <summary>Requests a coalesced checkpoint. Call from the owning playfield tick.</summary>
        public void MarkDirty()
        {
            long now = Environment.TickCount64;
            if (_dueAtMs == 0)
                _deadlineMs = now + CharacterSnapshotService.MaxDirtyDelayMilliseconds;
            _dueAtMs = Math.Min(now + CharacterSnapshotService.CoalesceMilliseconds, _deadlineMs);
        }

        /// <summary>
        /// High-frequency values that would otherwise checkpoint every few seconds for anyone in combat.
        /// They are still written by the periodic checkpoint and the logout snapshot.
        /// </summary>
        public static bool IsCheckpointOnly(CharacterStat stat) => stat switch
        {
            CharacterStat.Health or CharacterStat.CurrentNano or CharacterStat.MaxHealth
                or CharacterStat.MaxNanoEnergy or CharacterStat.PercentRemainingHealth
                or CharacterStat.PercentRemainingNano or CharacterStat.CurrentNCU
                or CharacterStat.NumberOfFightingOpponents or CharacterStat.SelectedTargetType
                or CharacterStat.CurrentMovementMode or CharacterStat.State or CharacterStat.CurrentState
                or CharacterStat.ActionCategory or CharacterStat.DeadTimer => true,
            _ => false,
        };

        /// <summary>Location and base stats as they exist in storage right after hydration.</summary>
        public void SeedPersisted(CharacterRecord location, IReadOnlyList<StatRecord> stats)
        {
            ArgumentNullException.ThrowIfNull(location);
            ArgumentNullException.ThrowIfNull(stats);
            _persistedLocation = location;
            foreach (StatRecord stat in stats)
                _persisted[stat.StatId] = stat.StatValue;
            // Hydration itself raises base changes; those are not player progress.
            _dueAtMs = 0;
        }

        internal bool TakeDue(long nowMs, int characterId)
        {
            // Spread first checkpoints over the second half of the interval so a mass login does not
            // write in lockstep, without ever exceeding the interval.
            const int half = CharacterSnapshotService.CheckpointIntervalMilliseconds / 2;
            if (_nextCheckpointMs == 0)
                _nextCheckpointMs = nowMs + half + characterId % half;

            bool dirtyDue = (_dueAtMs != 0 && nowMs >= _dueAtMs) || _checkpointRequested;
            if (!dirtyDue && nowMs < _nextCheckpointMs)
                return false;

            _checkpointRequested = false;
            _dueAtMs = 0;
            _nextCheckpointMs = nowMs + CharacterSnapshotService.CheckpointIntervalMilliseconds;
            return true;
        }

        internal long NextSequence() => Interlocked.Increment(ref _sequence);

        internal bool IsNewerThanCommitted(long sequence) => sequence > _committedSequence;

        internal List<StatRecord> ChangedSincePersisted(IReadOnlyList<StatRecord> stats)
        {
            List<StatRecord> changed = [];
            foreach (StatRecord stat in stats)
                if (!_persisted.TryGetValue(stat.StatId, out int value) || value != stat.StatValue)
                    changed.Add(stat);
            return changed;
        }

        internal bool LocationChanged(CharacterRecord location)
        {
            CharacterRecord? stored = _persistedLocation;
            if (stored == null || stored.Playfield != location.Playfield)
                return true;
            float dx = stored.X - location.X, dy = stored.Y - location.Y, dz = stored.Z - location.Z;
            return dx * dx + dy * dy + dz * dz >= MinimumPersistedMove * MinimumPersistedMove;
        }

        internal void MarkCommitted(long sequence, IReadOnlyList<StatRecord> written, CharacterRecord? location)
        {
            if (location != null)
                _persistedLocation = location;
            foreach (StatRecord stat in written)
                _persisted[stat.StatId] = stat.StatValue;
            if (sequence > _committedSequence)
                _committedSequence = sequence;
        }
    }
}
