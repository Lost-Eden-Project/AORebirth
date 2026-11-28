namespace ZoneEngine_New.Core.Inventory
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;

    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// Per-character write-behind: every database write for a character runs in that character's
    /// <see cref="PersistenceLane"/>, in order, off the playfield tick. The tick applies a change in memory and queues
    /// its transaction (<see cref="Enqueue"/>, <see cref="EnqueueJoint"/>) or marks state dirty for the coalesced
    /// flush that ends every lane run (<see cref="NotifyDirty"/>). Lanes of different characters run in parallel on a
    /// small pool of writer threads; one character's lane never runs on two threads at once. Authority boundaries
    /// (logout, zone change, shutdown) drain a lane synchronously with <see cref="HardFlush"/>.
    /// A failed transient write is retried in place, so nothing queued behind it runs ahead of it; a terminal failure
    /// (an unknown commit outcome, a row that contradicts memory, a logic error) quarantines the character, whose next
    /// login loads storage.
    /// </summary>
    public sealed class InventoryFlushService : IDisposable
    {
        /// <summary>Quiet period after last MarkDirty before an async write runs.</summary>
        public const int CoalesceMilliseconds = 300;

        /// <summary>Retry delay for lane work the writer skipped because the World lease was not held.</summary>
        const int LeaseRetryMilliseconds = 1000;

        /// <summary>Transient failures of one queued commit before the character is quarantined.</summary>
        const int MaxCommitAttempts = 5;

        /// <summary>Attempts at a two-character commit, paused briefly between them on its writer thread.</summary>
        const int MaxJointAttempts = 3;
        const int JointRetryPauseMilliseconds = 50;

        /// <summary>Joint commits nested through other lanes a hard flush drives before its own may run.</summary>
        const int MaxJointDepth = 16;

        /// <summary>Lanes that run at once; MySQL group commit amortizes their fsyncs.</summary>
        static readonly int WriterThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

        private readonly Func<int, Player?> _findPlayer;
        private readonly Func<IReadOnlyList<Player>> _snapshotPlayers;
        private readonly ICharacterCoalesceCommit _persist;
        private readonly ITrainedPerkRepository? _trainedPerks;
        private readonly IPerkLockRepository? _perkLocks;
        private readonly IResearchRepository? _research;
        private readonly INanoLineLockRepository? _nanoLineLocks;
        private readonly IPetNameRepository? _petNames;
        private readonly IBagNameRepository? _bagNames;
        private readonly IZoneLogger _logger;
        private readonly ZoneEngine_New.Core.World.WorldLink? _world;
        private readonly object _scheduleGate = new();
        private readonly Dictionary<int, long> _dueAtMs = new();
        private readonly ManualResetEventSlim _wake = new(false);
        private readonly BlockingCollection<int> _due = new();
        private readonly Thread _scheduler;
        private readonly Thread[] _workers;
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
            ZoneEngine_New.Core.World.WorldLink? world = null,
            IBagNameRepository? bagNames = null)
            : this(
                id => playfieldManager.Value.FindPlayer(id, out Player player) ? player : null,
                () => playfieldManager.Value.SnapshotPlayers(),
                persist, logger, trainedPerks, perkLocks, research, nanoLineLocks, petNames, world, bagNames)
        {
            ArgumentNullException.ThrowIfNull(playfieldManager);
        }

        /// <summary>The service over any player lookup (a scheduler test runs it without a playfield manager).</summary>
        internal InventoryFlushService(
            Func<int, Player?> findPlayer,
            Func<IReadOnlyList<Player>> snapshotPlayers,
            ICharacterCoalesceCommit persist,
            IZoneLogger logger,
            ITrainedPerkRepository? trainedPerks = null,
            IPerkLockRepository? perkLocks = null,
            IResearchRepository? research = null,
            INanoLineLockRepository? nanoLineLocks = null,
            IPetNameRepository? petNames = null,
            ZoneEngine_New.Core.World.WorldLink? world = null,
            IBagNameRepository? bagNames = null)
        {
            _bagNames = bagNames;
            ArgumentNullException.ThrowIfNull(findPlayer);
            ArgumentNullException.ThrowIfNull(snapshotPlayers);
            ArgumentNullException.ThrowIfNull(persist);
            ArgumentNullException.ThrowIfNull(logger);

            _findPlayer = findPlayer;
            _snapshotPlayers = snapshotPlayers;
            _persist = persist;
            _trainedPerks = trainedPerks;
            _perkLocks = perkLocks;
            _research = research;
            _nanoLineLocks = nanoLineLocks;
            _petNames = petNames;
            _logger = logger;
            _world = world;

            _scheduler = new Thread(SchedulerLoop)
            {
                IsBackground = true,
                Name = "InventoryFlushScheduler"
            };
            _workers = new Thread[WriterThreads];
            for (int i = 0; i < _workers.Length; i++)
            {
                _workers[i] = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "InventoryFlushWriter-" + (i + 1).ToString(CultureInfo.InvariantCulture)
                };
                _workers[i].Start();
            }
            _scheduler.Start();
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

            ScheduleAt(characterId, Environment.TickCount64 + CoalesceMilliseconds);
        }

        /// <summary>
        /// Queues database work the tick has already applied in memory, to run off the tick in the order queued, in
        /// the character's lane: before the dirty inventory and ahead of every hard flush (logout, zone change, trade,
        /// mission transaction). It runs after the coalesce period, so the rest of the action that queued it is done
        /// first. The commit runs without the character's persistence gate; it takes the gate itself for any memory
        /// bookkeeping. A transient failure is retried a few times in place; a terminal failure or the last retry
        /// quarantines the character and closes the session: the next login loads storage.
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

            player.Lane.Queue.Enqueue(new ActionEntry(commit));
            if (_disposed)
                return; // Shutdown hard-flushes every player, which runs it.

            ScheduleAt(characterId, Environment.TickCount64 + CoalesceMilliseconds);
        }

        /// <summary>
        /// Queues one transaction for two characters (a player trade), in both lanes at once: it runs once, after
        /// everything queued before it in either lane, and before everything queued after it in either. Both players
        /// are quarantined when it fails. Call on the tick that owns both players, with both memory changes applied.
        /// </summary>
        public void EnqueueJoint(Player first, Player second, Action commit)
        {
            ArgumentNullException.ThrowIfNull(first);
            ArgumentNullException.ThrowIfNull(second);
            ArgumentNullException.ThrowIfNull(commit);
            if (first.IsPersistenceQuarantined || second.IsPersistenceQuarantined)
                throw new InvalidOperationException("A quarantined player must be reloaded before persistence.");
            if (first.Identity.Instance <= 0 || second.Identity.Instance <= 0)
                throw new InvalidOperationException("Only stored characters have commits to queue.");

            var joint = new JointCommit(first.Lane, second.Lane, commit);
            first.Lane.Queue.Enqueue(joint);
            second.Lane.Queue.Enqueue(joint);
            if (_disposed)
                return;

            long due = Environment.TickCount64 + CoalesceMilliseconds;
            ScheduleAt(first.Identity.Instance, due);
            ScheduleAt(second.Identity.Instance, due);
        }

        /// <summary>
        /// Synchronous durable commit (logout, despawn, transfer, shutdown, and right before a caller's own commit that
        /// depends on it): runs everything in the character's lane, then the dirty flush, on the calling thread.
        /// Throws when this instance no longer holds the character's World lease or a write fails: callers rely on an
        /// exception to abort, so a save that cannot happen is never reported as done. Never call it under the
        /// character's persistence gate: a writer holding the lane takes that gate to finish.
        /// </summary>
        public void HardFlush(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);
            if (Monitor.IsEntered(player.PersistenceGate))
                throw new InvalidOperationException("A hard flush must not run under the player's persistence gate (lane first, then gate).");

            int characterId = player.Identity.Instance;
            lock (_scheduleGate)
                _dueAtMs.Remove(characterId);

            PersistenceLane lane = player.Lane;
            lane.Driver.Wait();
            try
            {
                Drive(player, lane, hard: true, until: null, depth: 0);
            }
            finally
            {
                ReleaseDriver(player, lane);
            }
        }

        /// <summary>
        /// Gives the lane up. Work queued while this driver held it (a writer that found the lane taken left it to
        /// us) gets its own run; a parked lane is woken by its joint commit, a retry by its due time.
        /// </summary>
        void ReleaseDriver(Player player, PersistenceLane lane)
        {
            lane.Driver.Release();
            if (!_disposed && !lane.Queue.IsEmpty && lane.ParkedOn == null && Volatile.Read(ref lane.NextAttemptAtMs) == 0)
                ScheduleAt(player.Identity.Instance, Environment.TickCount64);
        }

        void ScheduleAt(int characterId, long dueAtMs)
        {
            if (_disposed)
                return;
            lock (_scheduleGate)
            {
                _dueAtMs[characterId] = _dueAtMs.TryGetValue(characterId, out long scheduled) ? Math.Min(scheduled, dueAtMs) : dueAtMs;
                _wake.Set();
            }
        }

        void SchedulerLoop()
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

                foreach (int id in dueIds)
                {
                    if (_disposed)
                        return;
                    try
                    {
                        _due.Add(id);
                    }
                    catch (InvalidOperationException)
                    {
                        return; // Adding completed: shutting down.
                    }
                }
            }
        }

        void WorkerLoop()
        {
            try
            {
                foreach (int characterId in _due.GetConsumingEnumerable())
                {
                    if (_disposed)
                        return;
                    if (_findPlayer(characterId) is not Player player)
                        continue;
                    DriveAsWriter(player);
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }

        void DriveAsWriter(Player player)
        {
            PersistenceLane lane = player.Lane;
            // Another driver (a hard flush, or a writer still finishing) owns the lane; it sees everything queued.
            if (!lane.Driver.Wait(0))
                return;
            try
            {
                Drive(player, lane, hard: false, until: null, depth: 0);
            }
            catch (Exception exception)
            {
                _logger.Error(
                    exception,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "InventoryFlushService async flush failed for character {0}",
                        player.Identity.Instance));
            }
            finally
            {
                ReleaseDriver(player, lane);
            }
        }

        /// <summary>
        /// Runs the lane's work in order, then (with no <paramref name="until"/>) the dirty flush. The caller holds the
        /// lane's driver. A hard drive throws on anything it cannot store; a writer drive records the retry and returns.
        /// <paramref name="until"/> stops in front of that joint commit, which the caller runs.
        /// </summary>
        void Drive(Player player, PersistenceLane lane, bool hard, JointCommit? until, int depth)
        {
            if (depth > MaxJointDepth)
                throw new InvalidOperationException("Joint commit chain is too deep to store.");

            int characterId = player.Identity.Instance;
            long now = Environment.TickCount64;
            long nextAttempt = Volatile.Read(ref lane.NextAttemptAtMs);
            if (!hard && nextAttempt > now)
            {
                ScheduleAt(characterId, nextAttempt);
                return;
            }

            if (player.IsPersistenceQuarantined)
            {
                bool hadWork = Discard(lane);
                if (hard && hadWork)
                    throw new InvalidOperationException("Inventory persistence is quarantined pending database reconciliation.");
                return;
            }

            if (!lane.HasQueuedWork && (until != null || !HasDirtyMemoryState(player)))
                return;

            if (!OwnsLease(player))
            {
                // Nothing is taken: the lane and dirty state stay with the aggregate, which this instance may not write.
                if (hard)
                    throw new InvalidOperationException(
                        "Character " + characterId.ToString(CultureInfo.InvariantCulture)
                        + " is not leased to this instance; refusing to save its inventory.");
                _logger.Warn(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Inventory flush skipped character={0}: this instance no longer holds its World lease",
                        characterId));
                ScheduleAt(characterId, now + LeaseRetryMilliseconds);
                return;
            }

            while (true)
            {
                if (Volatile.Read(ref lane.RetryWrite) is PendingWrite retry && !TryExecuteWrite(player, lane, retry, hard))
                    return;

                if (!lane.Queue.TryPeek(out LaneEntry? entry))
                    break;

                if (player.IsPersistenceQuarantined)
                {
                    Discard(lane);
                    if (hard)
                        throw new InvalidOperationException("Inventory persistence is quarantined pending database reconciliation.");
                    return;
                }

                switch (entry)
                {
                    case ActionEntry action:
                        if (!RunAction(player, lane, action, hard))
                            return;
                        break;
                    case JointCommit joint:
                        if (ReferenceEquals(joint, until))
                            return;
                        if (!RunJointFromLane(lane, joint, hard, depth))
                            return;
                        break;
                    default:
                        throw new InvalidOperationException("Unknown persistence lane entry.");
                }
            }

            if (until == null)
                FlushDirty(player, lane, hard);
        }

        /// <summary>True to continue with the next entry; false when the drive must stop (retry scheduled, or quarantined).</summary>
        bool RunAction(Player player, PersistenceLane lane, ActionEntry action, bool hard)
        {
            int characterId = player.Identity.Instance;
            try
            {
                action.Commit();
            }
            catch (Exception exception) when (!IsTerminal(exception) && action.Attempts + 1 < MaxCommitAttempts)
            {
                action.Attempts++;
                long delay = BackoffMilliseconds(action.Attempts);
                _logger.Error(
                    exception,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Queued commit for character {0} failed (attempt {1}); retrying in {2} ms",
                        characterId, action.Attempts, delay));
                if (hard)
                    throw;
                long next = Environment.TickCount64 + delay;
                Volatile.Write(ref lane.NextAttemptAtMs, next);
                ScheduleAt(characterId, next);
                return false;
            }
            catch (Exception exception)
            {
                // The aggregate already shows what this commit was to store: without it memory and storage disagree.
                Quarantine(player, exception, "Queued commit failed for character {0}; player persistence quarantined for reload.");
                Discard(lane);
                if (hard)
                    throw;
                return false;
            }

            Volatile.Write(ref lane.NextAttemptAtMs, 0);
            DequeueHead(lane, action);
            return true;
        }

        /// <summary>The lane's driver stands at <paramref name="joint"/>. True to continue; false when a writer parks.</summary>
        bool RunJointFromLane(PersistenceLane lane, JointCommit joint, bool hard, int depth)
        {
            switch (joint.Arrive(lane, park: !hard))
            {
                case JointArrival.Done:
                    DequeueHead(lane, joint);
                    return true;
                case JointArrival.Running:
                    if (!hard)
                        return false;
                    joint.WaitCompleted();
                    DequeueHead(lane, joint);
                    return true;
                case JointArrival.Run:
                    RunJoint(joint);
                    DequeueHead(lane, joint);
                    return true;
                default:
                    if (!hard)
                        return false; // Parked: the joint's completion reschedules this lane.
                    BringOtherToJoint(lane, joint, depth);
                    DequeueHead(lane, joint);
                    return true;
            }
        }

        /// <summary>
        /// A hard flush cannot park: it drives the other lane up to the joint itself (holding that lane's driver after
        /// its own) and runs the joint. It takes the other driver only while the joint is still open: another driver
        /// may run the joint meanwhile, and a hard flush that holds its lane at a later joint must never be waited for
        /// over one that is already done, or three hard flushes could wait in a ring.
        /// </summary>
        void BringOtherToJoint(PersistenceLane lane, JointCommit joint, int depth)
        {
            PersistenceLane other = joint.Other(lane);
            var otherPlayer = (Player)other.Owner;
            while (!other.Driver.Wait(1))
            {
                if (joint.IsDone)
                    return;
            }
            try
            {
                if (joint.IsDone)
                    return;
                Drive(otherPlayer, other, hard: true, until: joint, depth + 1);
                if (!other.Queue.TryPeek(out LaneEntry? head) || !ReferenceEquals(head, joint))
                    throw new InvalidOperationException(
                        "The other participant's queued work could not be stored ahead of the shared commit.");
                switch (joint.Arrive(other, park: false))
                {
                    case JointArrival.Run:
                        RunJoint(joint);
                        break;
                    case JointArrival.Running:
                        joint.WaitCompleted();
                        break;
                    case JointArrival.Done:
                        break;
                    default:
                        throw new InvalidOperationException("Joint commit arrival is inconsistent: both lanes have arrived.");
                }
                DequeueHead(other, joint);
            }
            finally
            {
                ReleaseDriver(otherPlayer, other);
            }
        }

        /// <summary>Runs a joint whose arrival the caller completed; both players are quarantined when it fails.</summary>
        void RunJoint(JointCommit joint)
        {
            var low = (Player)joint.Low.Owner;
            var high = (Player)joint.High.Owner;
            try
            {
                int attempt = 0;
                while (true)
                {
                    try
                    {
                        joint.Commit();
                        break;
                    }
                    catch (Exception exception) when (!IsTerminal(exception) && ++attempt < MaxJointAttempts)
                    {
                        _logger.Error(
                            exception,
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Joint commit for characters {0} and {1} failed (attempt {2}); retrying",
                                low.Identity.Instance, high.Identity.Instance, attempt));
                        Thread.Sleep(JointRetryPauseMilliseconds * attempt);
                    }
                }
            }
            catch (Exception exception)
            {
                // Both aggregates already show the trade; neither may be written from memory again.
                Quarantine(low, exception, "Joint commit failed for character {0}; player persistence quarantined for reload.");
                Quarantine(high, exception, "Joint commit failed for character {0}; player persistence quarantined for reload.");
            }
            finally
            {
                (PersistenceLane? first, PersistenceLane? second) = joint.Complete();
                long now = Environment.TickCount64;
                if (first != null) ScheduleAt(first.Id, now);
                if (second != null) ScheduleAt(second.Id, now);
            }
        }

        static void DequeueHead(PersistenceLane lane, LaneEntry entry)
        {
            if (lane.Queue.TryPeek(out LaneEntry? head) && ReferenceEquals(head, entry))
                lane.Queue.TryDequeue(out _);
        }

        /// <summary>
        /// Drops everything a quarantined character's lane still holds. A joint commit (a player trade) found in it has
        /// not run and never will: the other participant's memory shows a trade storage will never hold, so that player
        /// is quarantined for a reload and its lane discarded the same way (which follows any further joints it holds),
        /// and a lane parked on the joint is woken so it stops waiting. A joint another driver is running now is left to
        /// that driver, whose completion settles both lanes.
        /// </summary>
        bool Discard(PersistenceLane lane)
        {
            bool hadWork = lane.HasQueuedWork;
            List<PersistenceLane>? abandoned = null;
            while (lane.Queue.TryDequeue(out LaneEntry? entry))
            {
                if (entry is not JointCommit joint || !joint.TryAbandon(out PersistenceLane? first, out PersistenceLane? second))
                    continue;
                PersistenceLane other = joint.Other(lane);
                var otherPlayer = (Player)other.Owner;
                if (!otherPlayer.IsPersistenceQuarantined)
                {
                    Quarantine(otherPlayer, new InvalidOperationException(
                            "The shared commit with character " + ((Player)lane.Owner).Identity.Instance.ToString(CultureInfo.InvariantCulture)
                            + " was discarded before it ran."),
                        "Shared commit abandoned for character {0}; player persistence quarantined for reload.");
                }
                (abandoned ??= []).Add(other);
                long now = Environment.TickCount64;
                if (first != null && !ReferenceEquals(first, lane)) ScheduleAt(first.Id, now);
                if (second != null && !ReferenceEquals(second, lane)) ScheduleAt(second.Id, now);
            }
            Volatile.Write(ref lane.RetryWrite, null);
            Volatile.Write(ref lane.NextAttemptAtMs, 0);
            if (abandoned != null)
                foreach (PersistenceLane other in abandoned)
                    Discard(other);
            return hadWork;
        }

        void Quarantine(Player player, Exception exception, string format)
        {
            player.QuarantinePersistence();
            player.Session?.Close();
            _logger.Error(exception, string.Format(CultureInfo.InvariantCulture, format, player.Identity.Instance));
        }

        /// <summary>
        /// Terminal: the write must not be repeated (its outcome is unknown, or storage contradicts memory) or it is a
        /// logic failure that would fail the same way again. Anything else is treated as transient.
        /// </summary>
        static bool IsTerminal(Exception exception) => exception is DatabaseCommitOutcomeUnknownException
            or DatabaseStateConflictException
            or AORebirth.Interfaces.Persistence.Missions.MissionCommitOutcomeUnknownException
            or InvalidOperationException
            or ArgumentException
            || exception.GetType().Name.EndsWith("OutcomeUnknownException", StringComparison.Ordinal);

        static long BackoffMilliseconds(int attempts) => Math.Min(1000L * attempts, 10000L);

        /// <summary>
        /// True while this instance may write the character: it holds the World lease and the lease deadline has not
        /// passed (always, without a World link). The same rule the stat/location checkpoints apply; a character
        /// another zone instance may own by now is never written from here.
        /// </summary>
        bool OwnsLease(Player player) => _world == null || _world.OwnsLease(player.Identity.Instance);

        /// <summary>
        /// The dirty flush at the end of a lane run. Memory is snapshotted under the gate, with the new rows marked
        /// persisted right away (their insert now precedes everything queued later in this lane), then written without
        /// the gate. A transient failure keeps the taken snapshot as the lane's retry; a terminal one quarantines.
        /// </summary>
        void FlushDirty(Player player, PersistenceLane lane, bool hard)
        {
            PendingWrite write;
            TrainedPerkChanges? trainedPerks;
            List<SkillLockRecord>? perkLocks;
            ResearchChanges? research;
            List<SkillLockRecord>? nanoLineLocks;
            PetNameRecord[]? petNames;
            BagNameRecord[]? bagNames;
            lock (player.PersistenceGate)
            {
                if (player.IsPersistenceQuarantined)
                {
                    if (hard)
                        throw new InvalidOperationException("Inventory persistence is quarantined pending database reconciliation.");
                    return;
                }
                if (!player.Inventory.IsHydrated || !HasDirtyMemoryState(player))
                    return;

                PlayerInventory.InventoryDirtyFlush? inventory = player.Inventory.TakeDirty();
                int[] nanos = player.DrainDirtyUploadedNanos();
                List<ActiveNanoRecord>? activeNanos = player.TakeDirtyActiveNanos();
                List<SkillLockRecord>? skillLocks = player.SkillLocks.TakeDirty(DateTime.UtcNow);
                trainedPerks = _trainedPerks == null ? null : player.TrainedPerks.TakeDirty();
                perkLocks = _perkLocks == null ? null : player.PerkLocks.TakeDirty(DateTime.UtcNow);
                research = _research == null ? null : player.Research.TakeDirty();
                nanoLineLocks = _nanoLineLocks == null ? null : player.NanoLineLocks.TakeDirty(DateTime.UtcNow);
                petNames = _petNames == null ? null : player.PetNames.TakeDirty();
                bagNames = _bagNames == null ? null : player.BagNames.TakeDirty();
                // Rows about to be inserted count as persisted from here on: a retire of one queued after this point
                // writes its graveyard row behind the insert instead of dropping it.
                inventory?.MarkNewlyPersisted();
                write = new PendingWrite(inventory, nanos, activeNanos, skillLocks);
            }

            // The perk, lock, research and pet-name sets are written to their own tables, each in its own
            // transaction, and they share nothing with the inventory batch. Trained perks and research only add
            // what the character has and delete what this session removed: what a login failed to load stays stored. They are written first so that no
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
            SaveReplacementSet(player, bagNames, names => _bagNames!.Save(player.Identity.Instance, names),
                () => player.BagNames.RestoreDirty(bagNames), "bag names");
            // A finished research tier leaves its progress row only once it is a stored trained perk: never write
            // the research set ahead of a trained-perk set that failed, or the tier would be lost on a restart.
            if (perksSaved)
                SaveReplacementSet(player, research, rows => _research!.Save(player.Identity.Instance, rows),
                    () => player.Research.RestoreDirty(research), "research");
            else
                player.Research.RestoreDirty(research);

            if (write.IsEmpty)
                return;

            TryExecuteWrite(player, lane, write, hard);
        }

        /// <summary>True when the write landed; false when the drive must stop (retry scheduled, or quarantined).</summary>
        bool TryExecuteWrite(Player player, PersistenceLane lane, PendingWrite write, bool hard)
        {
            int characterId = player.Identity.Instance;
            try
            {
                _persist.Persist(
                    write.Inventory?.Inserts ?? [],
                    write.Inventory?.Updates ?? [],
                    characterId,
                    write.UploadedNanos,
                    write.ActiveNanos,
                    write.SkillLocks);
            }
            catch (DatabaseCommitOutcomeUnknownException exception)
            {
                Volatile.Write(ref lane.RetryWrite, null);
                Quarantine(player, exception, "Inventory commit outcome is unknown for character {0}; player persistence quarantined.");
                if (hard)
                    throw;
                return false;
            }
            catch (DatabaseStateConflictException exception)
            {
                // Nothing was written, but the batch can never succeed: a row the server does not know about
                // occupies a slot. Retrying it would fail every later save too, until a logout save failed
                // and dropped hours of changes. Disconnecting now reloads the character from the database.
                Volatile.Write(ref lane.RetryWrite, null);
                Quarantine(player, exception,
                    "Inventory state for character {0} conflicts with the database; player persistence quarantined for reload.");
                if (hard)
                    throw;
                return false;
            }
            catch (Exception exception)
            {
                // The taken snapshot stays at the head of the lane: it is written before anything queued after it.
                write.Attempts++;
                long delay = BackoffMilliseconds(write.Attempts);
                Volatile.Write(ref lane.RetryWrite, write);
                long next = Environment.TickCount64 + delay;
                Volatile.Write(ref lane.NextAttemptAtMs, next);
                _logger.Error(
                    exception,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "InventoryFlushService flush failed for character {0} (attempt {1}); retrying in {2} ms",
                        characterId, write.Attempts, delay));
                if (hard)
                    throw;
                ScheduleAt(characterId, next);
                return false;
            }

            Volatile.Write(ref lane.RetryWrite, null);
            Volatile.Write(ref lane.NextAttemptAtMs, 0);
            return true;
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

        static bool HasDirtyMemoryState(Player player)
            => player.Inventory.HasDirtyEntries || player.HasDirtyUploadedNanos || player.HasDirtyActiveNanos
                || player.SkillLocks.IsDirty || player.TrainedPerks.IsDirty || player.PerkLocks.IsDirty
                || player.Research.IsDirty || player.NanoLineLocks.IsDirty || player.PetNames.IsDirty
                || player.BagNames.IsDirty;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            lock (_scheduleGate)
            {
                _dueAtMs.Clear();
                _wake.Set();
            }

            try
            {
                foreach (Player player in _snapshotPlayers())
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
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "InventoryFlushService Dispose flush failed");
            }

            _due.CompleteAdding();
            _wake.Set();
            if (!_scheduler.Join(2000))
                _logger.Warn("InventoryFlushService scheduler did not stop within 2s");
            foreach (Thread worker in _workers)
            {
                if (!worker.Join(2000))
                    _logger.Warn("InventoryFlushService writer did not stop within 2s");
            }

            _wake.Dispose();
            _due.Dispose();
        }

        /// <summary>A dirty flush taken from memory, written as one transaction; kept for a retry when that fails.</summary>
        sealed class PendingWrite
        {
            public PendingWrite(PlayerInventory.InventoryDirtyFlush? inventory, int[] uploadedNanos,
                List<ActiveNanoRecord>? activeNanos, List<SkillLockRecord>? skillLocks)
            {
                Inventory = inventory;
                UploadedNanos = uploadedNanos;
                ActiveNanos = activeNanos;
                SkillLocks = skillLocks;
            }

            public PlayerInventory.InventoryDirtyFlush? Inventory { get; }

            public int[] UploadedNanos { get; }

            public List<ActiveNanoRecord>? ActiveNanos { get; }

            public List<SkillLockRecord>? SkillLocks { get; }

            public int Attempts;

            public bool IsEmpty => Inventory == null && UploadedNanos.Length == 0 && ActiveNanos == null && SkillLocks == null;
        }
    }
}
