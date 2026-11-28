namespace ZoneEngine_New.Core.Inventory
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;

    /// <summary>
    /// One character's ordered storage work: the database writes the tick has already applied in memory (queued
    /// commits, two-character commits shared with another lane) and a taken dirty flush awaiting a retry. Entries run
    /// strictly in the order they were queued, one at a time, by whichever driver holds <see cref="Driver"/>: a writer
    /// thread of <see cref="InventoryFlushService"/> or a hard flush on the owning tick. A driver never holds the
    /// character's memory gate (<see cref="Entities.Player.PersistenceGate"/>) across database work; it takes the gate
    /// only to snapshot or finalize memory, so a tick that takes the gate waits for memory bookkeeping, never for I/O.
    /// Lock order: a lane's <see cref="Driver"/>, then its owner's gate; never the reverse.
    /// </summary>
    public sealed class PersistenceLane
    {
        public PersistenceLane(int id, object owner)
        {
            ArgumentNullException.ThrowIfNull(owner);
            Id = id;
            Owner = owner;
        }

        /// <summary>Orders two-lane commits (the character id).</summary>
        public int Id { get; }

        /// <summary>The character aggregate this lane writes.</summary>
        public object Owner { get; }

        internal ConcurrentQueue<LaneEntry> Queue { get; } = new();

        /// <summary>Held by the one driver running this lane. Entries are dequeued only by its holder.</summary>
        internal SemaphoreSlim Driver { get; } = new(1, 1);

        /// <summary>
        /// A writer left this lane at the head joint commit because the other lane had not reached it yet; the joint's
        /// completion reschedules the lane. Set and cleared under the joint's own lock.
        /// </summary>
        internal volatile JointCommit? ParkedOn;

        /// <summary>A taken dirty flush whose write failed for a transient reason; retried before anything queued after it.</summary>
        internal object? RetryWrite;

        /// <summary>When the head entry or <see cref="RetryWrite"/> may be attempted again; 0 when it may run now.</summary>
        internal long NextAttemptAtMs;

        /// <summary>True while anything is queued or awaiting a retry (a checkpoint of base stats must wait for it).</summary>
        public bool HasQueuedWork => !Queue.IsEmpty || Volatile.Read(ref RetryWrite) != null;
    }

    internal abstract class LaneEntry
    {
        /// <summary>Failed attempts so far; a transient failure is retried, a terminal one quarantines the owner.</summary>
        public int Attempts;
    }

    /// <summary>Database work for one character, queued by <see cref="InventoryFlushService.Enqueue"/>.</summary>
    internal sealed class ActionEntry : LaneEntry
    {
        public ActionEntry(Action commit)
        {
            ArgumentNullException.ThrowIfNull(commit);
            Commit = commit;
        }

        public Action Commit { get; }
    }

    internal enum JointArrival
    {
        /// <summary>Both lanes have reached the joint: the caller runs it.</summary>
        Run,

        /// <summary>The other lane has not reached the joint yet.</summary>
        Wait,

        /// <summary>Another driver is running it now.</summary>
        Running,

        /// <summary>It has run; the caller drops it from its queue.</summary>
        Done
    }

    /// <summary>
    /// One transaction for two characters (a player trade), queued in both lanes at the same instant. It runs once,
    /// by the driver whose arrival completes the pair, after every entry queued before it in either lane. A writer that
    /// arrives first parks its lane and leaves; a hard flush that arrives first drives the other lane up to the joint
    /// itself. Joints are queued in one global order (both lanes at once, on the tick), so the waits form no cycle.
    /// </summary>
    internal sealed class JointCommit : LaneEntry
    {
        readonly object _sync = new();
        readonly ManualResetEventSlim _completed = new(false);
        bool _lowArrived;
        bool _highArrived;
        bool _running;
        bool _done;

        public JointCommit(PersistenceLane first, PersistenceLane second, Action commit)
        {
            ArgumentNullException.ThrowIfNull(first);
            ArgumentNullException.ThrowIfNull(second);
            ArgumentNullException.ThrowIfNull(commit);
            if (first.Id == second.Id)
                throw new ArgumentException("A joint commit needs two different lanes.", nameof(second));
            Low = first.Id < second.Id ? first : second;
            High = ReferenceEquals(Low, first) ? second : first;
            Commit = commit;
        }

        public PersistenceLane Low { get; }

        public PersistenceLane High { get; }

        public Action Commit { get; }

        public PersistenceLane Other(PersistenceLane lane) => ReferenceEquals(lane, Low) ? High : Low;

        public bool IsDone
        {
            get
            {
                lock (_sync)
                    return _done;
            }
        }

        /// <summary>
        /// <paramref name="lane"/>'s driver stands at this joint. With <paramref name="park"/> (a writer), a lane that
        /// must wait is marked parked under the joint's lock, so a completion that follows sees it and reschedules it.
        /// </summary>
        public JointArrival Arrive(PersistenceLane lane, bool park)
        {
            lock (_sync)
            {
                if (_done)
                    return JointArrival.Done;
                if (ReferenceEquals(lane, Low))
                    _lowArrived = true;
                else if (ReferenceEquals(lane, High))
                    _highArrived = true;
                else
                    throw new ArgumentException("The lane is not part of this joint commit.", nameof(lane));

                if (_running)
                {
                    if (park) lane.ParkedOn = this;
                    return JointArrival.Running;
                }
                if (_lowArrived && _highArrived)
                {
                    _running = true;
                    return JointArrival.Run;
                }
                if (park) lane.ParkedOn = this;
                return JointArrival.Wait;
            }
        }

        /// <summary>Marks the joint run (or abandoned) and returns the lanes it parked, which the caller reschedules.</summary>
        public (PersistenceLane? First, PersistenceLane? Second) Complete()
        {
            (PersistenceLane? First, PersistenceLane? Second) parked;
            lock (_sync)
                parked = Finish();
            _completed.Set();
            return parked;
        }

        /// <summary>
        /// A lane holding this joint was discarded before the joint ran: the joint is marked done without running, so no
        /// lane waits for it, and the lanes it parked are returned for rescheduling. False when it has already run or a
        /// driver is running it now, in which case its own completion settles both lanes.
        /// </summary>
        public bool TryAbandon(out PersistenceLane? first, out PersistenceLane? second)
        {
            first = null;
            second = null;
            lock (_sync)
            {
                if (_done || _running)
                    return false;
                (first, second) = Finish();
            }
            _completed.Set();
            return true;
        }

        (PersistenceLane? First, PersistenceLane? Second) Finish()
        {
            PersistenceLane? first = null, second = null;
            _done = true;
            if (ReferenceEquals(Low.ParkedOn, this)) { Low.ParkedOn = null; first = Low; }
            if (ReferenceEquals(High.ParkedOn, this)) { High.ParkedOn = null; second = High; }
            return (first, second);
        }

        public void WaitCompleted() => _completed.Wait();
    }
}
