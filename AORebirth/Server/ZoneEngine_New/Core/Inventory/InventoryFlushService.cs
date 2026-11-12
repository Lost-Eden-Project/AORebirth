namespace ZoneEngine_New.Core.Inventory
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;

    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// Per-character write-behind for dirty item locations, uploaded nanos and NCU.
    /// Coalesces bursts, writes on a dedicated thread (off playfield tick), hard-flushes on authority boundaries.
    /// Inventory and nano inserts commit in one transaction.
    /// </summary>
    public sealed class InventoryFlushService : IDisposable
    {
        /// <summary>Quiet period after last MarkDirty before an async write runs.</summary>
        public const int CoalesceMilliseconds = 300;

        /// <summary>Retry delay for queued commits the writer skipped because the World lease was not held.</summary>
        const int LeaseRetryMilliseconds = 1000;

        private readonly Lazy<PlayfieldManager> _playfieldManager;
        private readonly ICharacterCoalesceCommit _persist;
        private readonly ITrainedPerkRepository? _trainedPerks;
        private readonly IPerkLockRepository? _perkLocks;
        private readonly IResearchRepository? _research;
        private readonly INanoLineLockRepository? _nanoLineLocks;
        private readonly IPetNameRepository? _petNames;
        private readonly IZoneLogger _logger;
        private readonly ZoneEngine_New.Core.World.WorldLink? _world;
        private readonly object _scheduleGate = new();
        private readonly Dictionary<int, long> _dueAtMs = new();
        private readonly ManualResetEventSlim _wake = new(false);
        private readonly Thread _writer;
        private volatile bool _disposed;

        public InventoryFlushService(
            Lazy<PlayfieldManager> playfieldManager,
            ICharacterCoalesceCommit persist,
            IZoneLogger logger,
            ITrainedPerkRepository? trainedPerks = null,
            IPerkLockRepository? perkLocks = null,
            IResearchRepository? research = null,
            INanoLineLockRepository? nanoLineLocks = null,
            IPetNameRepository? petNames = null,
            ZoneEngine_New.Core.World.WorldLink? world = null)
        {
            ArgumentNullException.ThrowIfNull(playfieldManager);
            ArgumentNullException.ThrowIfNull(persist);
            ArgumentNullException.ThrowIfNull(logger);

            _playfieldManager = playfieldManager;
            _persist = persist;
            _trainedPerks = trainedPerks;
            _perkLocks = perkLocks;
            _research = research;
            _nanoLineLocks = nanoLineLocks;
            _petNames = petNames;
            _logger = logger;
            _world = world;

            _writer = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "InventoryFlushWriter"
            };
            _writer.Start();
        }

        /// <summary>
        /// Schedules a coalesced async flush for this character. Safe to call from the playfield tick.
        /// </summary>
        public void NotifyDirty(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);
            if (_disposed || !player.Inventory.IsHydrated || player.IsPersistenceQuarantined)
                return;

            int characterId = player.Identity.Instance;
            if (characterId <= 0)
                return;

            long due = Environment.TickCount64 + CoalesceMilliseconds;
            lock (_scheduleGate)
            {
                // Queued commits keep their time: a busy inventory must not keep pushing an earned reward back.
                if (!player.PendingCommits.IsEmpty && _dueAtMs.TryGetValue(characterId, out long scheduled))
                    due = Math.Min(due, scheduled);
                _dueAtMs[characterId] = due;
                _wake.Set();
            }
        }

        /// <summary>
        /// Queues database work the tick has already applied in memory, to run off the tick in the order queued, under
        /// the player's gate and lease, before the dirty inventory and ahead of every hard flush (logout, zone change,
        /// trade, mission transaction). It runs after the coalesce period, so the rest of the action that queued it is
        /// done first. A commit that throws quarantines the player and closes the session: the next login loads storage.
        /// </summary>
        public void Enqueue(Player player, Action commit)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(commit);
            if (player.IsPersistenceQuarantined)
                throw new InvalidOperationException("A quarantined player must be reloaded before persistence.");
            int characterId = player.Identity.Instance;
            if (characterId <= 0)
                throw new InvalidOperationException("Only a stored character has commits to queue.");

            player.PendingCommits.Enqueue(commit);
            if (_disposed)
                return; // Shutdown hard-flushes every player, which runs it.

            long due = Environment.TickCount64 + CoalesceMilliseconds;
            lock (_scheduleGate)
            {
                _dueAtMs[characterId] = _dueAtMs.TryGetValue(characterId, out long scheduled) ? Math.Min(scheduled, due) : due;
                _wake.Set();
            }
        }

        /// <summary>Runs the player's queued commits in order. The caller holds the gate and has checked the lease.</summary>
        void RunPendingCommits(Player player)
        {
            while (player.PendingCommits.TryDequeue(out Action? commit))
            {
                try
                {
                    commit();
                }
                catch (Exception exception)
                {
                    // The aggregate already shows what this commit was to store: without it memory and storage disagree.
                    player.QuarantinePersistence();
                    player.PendingCommits.Clear();
                    player.Session?.Close();
                    _logger.Error(
                        exception,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Queued commit failed for character {0}; player persistence quarantined for reload.",
                            player.Identity.Instance));
                    throw;
                }
            }
        }

        /// <summary>
        /// Synchronous durable commit (logout, despawn, transfer, shutdown, and right before a caller's own commit that
        /// depends on it). Cancels a pending coalesce. Throws when this instance no longer holds the character's World
        /// lease: callers rely on an exception to abort, so a save that cannot happen is never reported as done.
        /// </summary>
        public void HardFlush(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);

            int characterId = player.Identity.Instance;
            lock (_scheduleGate)
                _dueAtMs.Remove(characterId);

            FlushCharacter(player, requireLease: true);
        }

        /// <summary>
        /// Trade and snapshot writes share the aggregate's gate with the background writer.
        /// Ordered acquisition prevents opposing player trades from deadlocking. The caller
        /// combines pending dirty entries with the final trade in a single transaction.
        /// </summary>
        public void WithExclusivePlayers(Player first, Player? second, Action action)
        {
            ArgumentNullException.ThrowIfNull(first);
            ArgumentNullException.ThrowIfNull(action);
            Player low = second != null && second.Identity.Instance < first.Identity.Instance ? second : first;
            Player? high = second == null ? null : ReferenceEquals(low, first) ? second : first;
            lock (low.PersistenceGate)
            {
                if (high == null)
                    Run();
                else
                    lock (high.PersistenceGate) Run();
            }

            void Run()
            {
                if (first.IsPersistenceQuarantined || second?.IsPersistenceQuarantined == true)
                    throw new InvalidOperationException("Trade participant persistence is quarantined.");
                if (!OwnsLease(first) || (second != null && !OwnsLease(second)))
                    throw new InvalidOperationException("Trade participant is no longer leased to this zone instance.");
                lock (_scheduleGate)
                {
                    _dueAtMs.Remove(first.Identity.Instance);
                    if (second != null) _dueAtMs.Remove(second.Identity.Instance);
                }
                // Queued rewards are stored before a transaction that takes the dirty inventory or writes stats itself.
                RunPendingCommits(first);
                if (second != null) RunPendingCommits(second);
                action();
            }
        }

        void WriterLoop()
        {
            while (!_disposed)
            {
                int waitMs = Timeout.Infinite;
                List<int> dueIds = [];

                lock (_scheduleGate)
                {
                    if (_dueAtMs.Count > 0)
                    {
                        long now = Environment.TickCount64;
                        long soonest = long.MaxValue;
                        foreach (KeyValuePair<int, long> pair in _dueAtMs)
                        {
                            if (pair.Value <= now)
                                dueIds.Add(pair.Key);
                            else if (pair.Value < soonest)
                                soonest = pair.Value;
                        }

                        foreach (int id in dueIds)
                            _dueAtMs.Remove(id);

                        if (dueIds.Count == 0 && soonest != long.MaxValue)
                        {
                            long delta = soonest - now;
                            waitMs = delta <= 0 ? 0 : (int)Math.Min(delta, int.MaxValue);
                        }
                    }

                    if (dueIds.Count == 0)
                        _wake.Reset();
                }

                if (dueIds.Count == 0)
                {
                    _wake.Wait(waitMs);
                    continue;
                }

                PlayfieldManager playfields = _playfieldManager.Value;
                foreach (int characterId in dueIds)
                {
                    if (_disposed)
                        break;

                    if (!playfields.FindPlayer(characterId, out Player player))
                        continue;

                    try
                    {
                        FlushCharacter(player);
                    }
                    catch (Exception exception)
                    {
                        _logger.Error(
                            exception,
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "InventoryFlushService async flush failed for character {0}",
                                characterId));
                    }
                }
            }
        }

        /// <summary>
        /// True while this instance may write the character: it holds the World lease and the lease deadline has not
        /// passed (always, without a World link). The same rule the stat/location checkpoints apply; a character
        /// another zone instance may own by now is never written from here.
        /// </summary>
        bool OwnsLease(Player player) => _world == null || _world.OwnsLease(player.Identity.Instance);

        /// <param name="requireLease">
        /// Throw when the lease is not owned (a hard flush the caller depends on); false skips with a warning, the
        /// background writer's rule, leaving the dirty state with the aggregate.
        /// </param>
        void FlushCharacter(Player player, bool requireLease = false)
        {
            if (!player.Inventory.IsHydrated && player.PendingCommits.IsEmpty)
                return;

            if (!HasDirtyState(player))
                return;

            lock (player.PersistenceGate)
            {
                if (player.IsPersistenceQuarantined)
                    throw new InvalidOperationException("Inventory persistence is quarantined pending database reconciliation.");
                if (!HasDirtyState(player))
                    return;
                if (!OwnsLease(player))
                {
                    // Nothing is taken: the dirty state stays with the aggregate, which this instance may not write.
                    if (requireLease)
                        throw new InvalidOperationException(
                            "Character " + player.Identity.Instance.ToString(CultureInfo.InvariantCulture)
                            + " is not leased to this instance; refusing to save its inventory.");
                    _logger.Warn(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Inventory flush skipped character={0}: this instance no longer holds its World lease",
                            player.Identity.Instance));
                    // Queued commits hold the player's checkpoints back: try again once the lease may be renewed.
                    if (!player.PendingCommits.IsEmpty && !_disposed)
                        lock (_scheduleGate)
                        {
                            _dueAtMs[player.Identity.Instance] = Environment.TickCount64 + LeaseRetryMilliseconds;
                            _wake.Set();
                        }
                    return;
                }

                // A queued reward inserts its rows before the dirty entries that move them are taken.
                RunPendingCommits(player);
                if (!player.Inventory.IsHydrated)
                    return;

                PlayerInventory.InventoryDirtyFlush? inventory = player.Inventory.TakeDirty();
                int[] nanos = player.DrainDirtyUploadedNanos();
                List<ActiveNanoRecord>? activeNanos = player.TakeDirtyActiveNanos();
                List<SkillLockRecord>? skillLocks = player.SkillLocks.TakeDirty(DateTime.UtcNow);
                int[]? trainedPerks = _trainedPerks == null ? null : player.TrainedPerks.TakeDirty();
                List<SkillLockRecord>? perkLocks = _perkLocks == null ? null : player.PerkLocks.TakeDirty(DateTime.UtcNow);
                ResearchProgressRecord[]? research = _research == null ? null : player.Research.TakeDirty();
                List<SkillLockRecord>? nanoLineLocks = _nanoLineLocks == null ? null : player.NanoLineLocks.TakeDirty(DateTime.UtcNow);
                PetNameRecord[]? petNames = _petNames == null ? null : player.PetNames.TakeDirty();
                if (inventory == null && nanos.Length == 0 && activeNanos == null && skillLocks == null && trainedPerks == null
                    && perkLocks == null && research == null && nanoLineLocks == null && petNames == null)
                    return;

                // The perk, lock, research and pet-name sets are full replacements of their own tables, each in its
                // own transaction, and they share nothing with the inventory batch. They are written first so that no
                // outcome of that batch can take them with it: a bag slot holding a row the server does not know about
                // quarantines the player below, and research the player finished must not be lost to it (bug #292).
                // A failed or uncertain set write is safe to repeat: it only leaves that set dirty for a retry.
                bool perksSaved = SaveReplacementSet(player, trainedPerks,
                    perks => _trainedPerks!.Save(player.Identity.Instance, perks),
                    () => player.TrainedPerks.RestoreDirty(trainedPerks), "trained perks");
                SaveReplacementSet(player, perkLocks, locks => _perkLocks!.Save(player.Identity.Instance, locks),
                    () => player.PerkLocks.RestoreDirty(perkLocks), "perk locks");
                SaveReplacementSet(player, nanoLineLocks, locks => _nanoLineLocks!.Save(player.Identity.Instance, locks),
                    () => player.NanoLineLocks.RestoreDirty(nanoLineLocks), "nano line locks");
                SaveReplacementSet(player, petNames, names => _petNames!.Save(player.Identity.Instance, names),
                    () => player.PetNames.RestoreDirty(petNames), "pet names");
                // A finished research tier leaves its progress row only once it is a stored trained perk: never write
                // the research set ahead of a trained-perk set that failed, or the tier would be lost on a restart.
                if (perksSaved)
                    SaveReplacementSet(player, research, rows => _research!.Save(player.Identity.Instance, rows),
                        () => player.Research.RestoreDirty(research), "research");
                else
                    player.Research.RestoreDirty(research);

                if (inventory == null && nanos.Length == 0 && activeNanos == null && skillLocks == null)
                    return;

                try
                {
                    _persist.Persist(
                        inventory?.Inserts ?? [],
                        inventory?.Updates ?? [],
                        player.Identity.Instance,
                        nanos,
                        activeNanos,
                        skillLocks);
                    inventory?.MarkNewlyPersisted();
                }
                catch (DatabaseCommitOutcomeUnknownException exception)
                {
                    player.QuarantinePersistence();
                    player.Session?.Close();
                    _logger.Error(exception, "Inventory commit outcome is unknown; player persistence quarantined.");
                    throw;
                }
                catch (DatabaseStateConflictException exception)
                {
                    // Nothing was written, but the batch can never succeed: a row the server does not know about
                    // occupies a slot. Requeueing it would fail every later save too, until a logout save failed
                    // and dropped hours of changes. Disconnecting now reloads the character from the database.
                    player.QuarantinePersistence();
                    player.Session?.Close();
                    _logger.Error(
                        exception,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Inventory state for character {0} conflicts with the database; player persistence quarantined for reload.",
                            player.Identity.Instance));
                    throw;
                }
                catch (Exception exception)
                {
                    if (inventory != null)
                        player.Inventory.RestoreDirty(inventory);
                    player.RestoreDirtyUploadedNanos(nanos);
                    player.RestoreDirtyActiveNanos(activeNanos);
                    player.SkillLocks.RestoreDirty(skillLocks);
                    _logger.Error(
                        exception,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "InventoryFlushService flush failed for character {0}",
                            player.Identity.Instance));
                    throw;
                }
            }
        }

        bool SaveReplacementSet<T>(Player player, T? snapshot, Action<T> save, Action restoreDirty, string what)
            where T : class
        {
            if (snapshot == null)
                return true;

            try
            {
                save(snapshot);
                return true;
            }
            catch (Exception exception)
            {
                restoreDirty();
                _logger.Error(
                    exception,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "InventoryFlushService {0} write failed for character {1}; retrying on the next flush",
                        what,
                        player.Identity.Instance));
                NotifyDirty(player);
                return false;
            }
        }

        static bool HasDirtyState(Player player)
            => !player.PendingCommits.IsEmpty || player.Inventory.HasDirtyEntries || player.HasDirtyUploadedNanos || player.HasDirtyActiveNanos
                || player.SkillLocks.IsDirty || player.TrainedPerks.IsDirty || player.PerkLocks.IsDirty
                || player.Research.IsDirty || player.NanoLineLocks.IsDirty || player.PetNames.IsDirty;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            List<int> remaining;
            lock (_scheduleGate)
            {
                remaining = new List<int>(_dueAtMs.Keys);
                _dueAtMs.Clear();
                _wake.Set();
            }

            try
            {
                PlayfieldManager playfields = _playfieldManager.Value;
                foreach (Player player in playfields.SnapshotPlayers())
                {
                    try
                    {
                        HardFlush(player);
                    }
                    catch (Exception exception)
                    {
                        _logger.Error(
                            exception,
                            "InventoryFlushService shutdown flush failed for character "
                            + player.Identity.Instance);
                    }
                }

                // Characters scheduled but already unregistered still need nothing if HardFlush ran on despawn.
                _ = remaining;
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "InventoryFlushService Dispose flush failed");
            }

            _wake.Set();
            if (!_writer.Join(2000))
                _logger.Warn("InventoryFlushService writer did not stop within 2s");

            _wake.Dispose();
        }
    }
}
