namespace ZoneEngine_New.Core.Modules
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Threading;

    using AORebirth.Modules.Network;

    using ZoneEngine_New.Core.Logging;

    /// <summary>
    /// The module message observers (module API 1.5), by direction and N3 message type. Observers are added during module
    /// Initialize only; the tables are copy-on-write, so the receive and send loops read one reference and never lock.
    /// A message type nobody subscribed to costs one dictionary lookup. Each observer call is isolated and timed, as event
    /// handlers are.
    /// </summary>
    public sealed class ModuleMessageObservers
    {
        /// <summary>Consecutive failures after which an observer is removed.</summary>
        const int MaxConsecutiveFailures = 10;

        /// <summary>An observer slower than this is reported, at most once a minute per observer.</summary>
        static readonly long SlowTicks = Stopwatch.Frequency / 1000 * 2;
        static readonly long SlowReportIntervalTicks = Stopwatch.Frequency * 60;

        static readonly Dictionary<int, Entry[]> Empty = new();

        readonly IZoneLogger _logger;
        readonly object _sync = new();
        Dictionary<int, Entry[]> _inbound = Empty;
        Dictionary<int, Entry[]> _outbound = Empty;

        internal ModuleMessageObservers(IZoneLogger logger)
        {
            _logger = logger;
        }

        public bool HasInbound => Volatile.Read(ref _inbound).Count != 0;

        public bool HasOutbound => Volatile.Read(ref _outbound).Count != 0;

        public bool Wants(MessageDirection direction, int messageType)
            => Table(direction).ContainsKey(messageType);

        /// <summary>Calls every observer of the sample's direction and type, in registration order.</summary>
        public void Publish(in MessageSample sample)
        {
            if (!Table(sample.Direction).TryGetValue(sample.MessageType, out Entry[]? entries))
                return;

            for (int i = 0; i < entries.Length; i++)
            {
                Entry entry = entries[i];
                if (entry.Removed)
                    continue;

                long started = Stopwatch.GetTimestamp();
                try
                {
                    entry.Observer.OnMessage(in sample);
                    if (Volatile.Read(ref entry.Failures) != 0)
                        Volatile.Write(ref entry.Failures, 0);
                }
                catch (Exception exception)
                {
                    int failures = Interlocked.Increment(ref entry.Failures);
                    _logger.Error(exception, string.Format(CultureInfo.InvariantCulture,
                        "Module {0} message observer failed ({1} in a row).", entry.ModuleId, failures));
                    if (failures >= MaxConsecutiveFailures && Remove(entry))
                        _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                            "Module {0} message observer removed after {1} consecutive failures.", entry.ModuleId, failures));
                }

                long elapsed = Stopwatch.GetTimestamp() - started;
                if (elapsed > SlowTicks)
                    ReportSlow(entry, started, elapsed);
            }
        }

        internal void Add(string moduleId, IMessageObserver observer, MessageSubscription subscription)
        {
            ArgumentNullException.ThrowIfNull(observer);
            ArgumentNullException.ThrowIfNull(subscription);
            int[] types = (subscription.MessageTypes ?? Array.Empty<int>()).Distinct().ToArray();
            if (types.Length == 0)
                throw new ArgumentException("A message subscription names at least one message type.");
            if ((subscription.Directions & MessageDirection.Both) == MessageDirection.None)
                throw new ArgumentException("A message subscription names a direction.");

            var entry = new Entry(moduleId, observer);
            lock (_sync)
            {
                if (subscription.Directions.HasFlag(MessageDirection.Inbound))
                    Volatile.Write(ref _inbound, With(_inbound, types, entry));
                if (subscription.Directions.HasFlag(MessageDirection.Outbound))
                    Volatile.Write(ref _outbound, With(_outbound, types, entry));
            }

            _logger.Info(string.Format(CultureInfo.InvariantCulture,
                "Module {0} message observer directions={1} types={2}", moduleId, subscription.Directions,
                string.Join(",", types.Select(type => "0x" + type.ToString("x8", CultureInfo.InvariantCulture)))));
        }

        internal void RemoveModule(string moduleId)
        {
            lock (_sync)
            {
                Volatile.Write(ref _inbound, Without(_inbound, entry => entry.ModuleId == moduleId));
                Volatile.Write(ref _outbound, Without(_outbound, entry => entry.ModuleId == moduleId));
            }
        }

        Dictionary<int, Entry[]> Table(MessageDirection direction)
            => direction == MessageDirection.Outbound ? Volatile.Read(ref _outbound) : Volatile.Read(ref _inbound);

        bool Remove(Entry entry)
        {
            lock (_sync)
            {
                if (entry.Removed)
                    return false;
                entry.Removed = true;
                Volatile.Write(ref _inbound, Without(_inbound, candidate => ReferenceEquals(candidate, entry)));
                Volatile.Write(ref _outbound, Without(_outbound, candidate => ReferenceEquals(candidate, entry)));
                return true;
            }
        }

        static Dictionary<int, Entry[]> With(Dictionary<int, Entry[]> table, int[] types, Entry entry)
        {
            var next = new Dictionary<int, Entry[]>(table);
            foreach (int type in types)
                next[type] = next.TryGetValue(type, out Entry[]? entries) ? [.. entries, entry] : [entry];
            return next;
        }

        static Dictionary<int, Entry[]> Without(Dictionary<int, Entry[]> table, Func<Entry, bool> drop)
        {
            var next = new Dictionary<int, Entry[]>();
            foreach ((int type, Entry[] entries) in table)
            {
                Entry[] kept = entries.Where(entry => !drop(entry)).ToArray();
                if (kept.Length != 0)
                    next[type] = kept;
            }

            return next.Count == 0 ? Empty : next;
        }

        void ReportSlow(Entry entry, long now, long elapsed)
        {
            long last = Volatile.Read(ref entry.LastSlowReport);
            if (last != 0 && now - last < SlowReportIntervalTicks)
                return;
            if (Interlocked.CompareExchange(ref entry.LastSlowReport, now, last) != last)
                return;
            _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                "Module {0} message observer took {1:0.0} ms (limit 2 ms).", entry.ModuleId,
                elapsed * 1000.0 / Stopwatch.Frequency));
        }

        sealed class Entry(string moduleId, IMessageObserver observer)
        {
            public readonly string ModuleId = moduleId;
            public readonly IMessageObserver Observer = observer;
            public int Failures;
            public long LastSlowReport;
            public volatile bool Removed;
        }
    }
}
