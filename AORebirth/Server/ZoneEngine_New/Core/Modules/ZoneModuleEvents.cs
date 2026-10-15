namespace ZoneEngine_New.Core.Modules
{
    using System;
    using System.Diagnostics;
    using System.Globalization;
    using System.Threading;

    using AORebirth.Modules.Events;

    using ZoneEngine_New.Core.Logging;

    /// <summary>
    /// Module event handlers, raised from the playfield tick. Hook points check <see cref="HandlerList{T}.HasHandlers"/>
    /// before building an event, so with no subscribers a hook costs one field read and allocates nothing.
    /// </summary>
    public sealed class ZoneModuleEvents
    {
        public ZoneModuleEvents(IZoneLogger logger)
        {
            NpcKilled = new HandlerList<NpcKilledEvent>("NpcKilled", logger);
            PlayerDied = new HandlerList<PlayerDiedEvent>("PlayerDied", logger);
        }

        public HandlerList<NpcKilledEvent> NpcKilled { get; }

        public HandlerList<PlayerDiedEvent> PlayerDied { get; }

        internal void RemoveModule(string moduleId)
        {
            NpcKilled.RemoveModule(moduleId);
            PlayerDied.RemoveModule(moduleId);
        }

        /// <summary>
        /// Copy-on-write handler array: raising reads one array reference and never locks, so playfields raise
        /// concurrently without contention. Each handler is isolated and timed.
        /// </summary>
        public sealed class HandlerList<T>
        {
            /// <summary>Consecutive failures after which a handler is unsubscribed.</summary>
            const int MaxConsecutiveFailures = 10;

            /// <summary>A handler slower than this is reported, at most once a minute per handler.</summary>
            static readonly long SlowTicks = Stopwatch.Frequency / 1000 * 2;
            static readonly long SlowReportIntervalTicks = Stopwatch.Frequency * 60;

            readonly string _name;
            readonly IZoneLogger _logger;
            readonly object _sync = new();
            Entry[] _entries = Array.Empty<Entry>();

            internal HandlerList(string name, IZoneLogger logger)
            {
                _name = name;
                _logger = logger;
            }

            public bool HasHandlers => Volatile.Read(ref _entries).Length != 0;

            public void Raise(T evt)
            {
                Entry[] entries = Volatile.Read(ref _entries);
                for (int i = 0; i < entries.Length; i++)
                {
                    Entry entry = entries[i];
                    long started = Stopwatch.GetTimestamp();
                    try
                    {
                        entry.Handler(evt);
                        if (Volatile.Read(ref entry.Failures) != 0)
                            Volatile.Write(ref entry.Failures, 0);
                    }
                    catch (Exception exception)
                    {
                        int failures = Interlocked.Increment(ref entry.Failures);
                        _logger.Error(exception, string.Format(CultureInfo.InvariantCulture,
                            "Module {0} {1} handler failed ({2} in a row).", entry.ModuleId, _name, failures));
                        if (failures >= MaxConsecutiveFailures && Remove(entry))
                            _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                                "Module {0} {1} handler unsubscribed after {2} consecutive failures.", entry.ModuleId, _name, failures));
                    }

                    long elapsed = Stopwatch.GetTimestamp() - started;
                    if (elapsed > SlowTicks)
                        ReportSlow(entry, started, elapsed);
                }
            }

            internal void Add(string moduleId, Action<T> handler)
            {
                ArgumentNullException.ThrowIfNull(handler);
                lock (_sync)
                {
                    Entry[] next = new Entry[_entries.Length + 1];
                    Array.Copy(_entries, next, _entries.Length);
                    next[^1] = new Entry(moduleId, handler);
                    Volatile.Write(ref _entries, next);
                }
            }

            internal void Remove(string moduleId, Action<T> handler)
            {
                lock (_sync)
                {
                    for (int i = _entries.Length - 1; i >= 0; i--)
                    {
                        if (_entries[i].ModuleId == moduleId && _entries[i].Handler == handler)
                        {
                            RemoveAt(i);
                            return;
                        }
                    }
                }
            }

            internal void RemoveModule(string moduleId)
            {
                lock (_sync)
                {
                    for (int i = _entries.Length - 1; i >= 0; i--)
                    {
                        if (_entries[i].ModuleId == moduleId)
                            RemoveAt(i);
                    }
                }
            }

            bool Remove(Entry entry)
            {
                lock (_sync)
                {
                    int index = Array.IndexOf(_entries, entry);
                    if (index < 0)
                        return false;
                    RemoveAt(index);
                    return true;
                }
            }

            void RemoveAt(int index)
            {
                Entry[] next = new Entry[_entries.Length - 1];
                Array.Copy(_entries, 0, next, 0, index);
                Array.Copy(_entries, index + 1, next, index, _entries.Length - index - 1);
                Volatile.Write(ref _entries, next);
            }

            void ReportSlow(Entry entry, long now, long elapsed)
            {
                long last = Volatile.Read(ref entry.LastSlowReport);
                if (last != 0 && now - last < SlowReportIntervalTicks)
                    return;
                if (Interlocked.CompareExchange(ref entry.LastSlowReport, now, last) != last)
                    return;
                _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                    "Module {0} {1} handler took {2:0.00} ms on the playfield tick; module handlers must not block.",
                    entry.ModuleId, _name, elapsed * 1000.0 / Stopwatch.Frequency));
            }

            sealed class Entry
            {
                public readonly string ModuleId;
                public readonly Action<T> Handler;
                public int Failures;
                public long LastSlowReport;

                public Entry(string moduleId, Action<T> handler)
                {
                    ModuleId = moduleId;
                    Handler = handler;
                }
            }
        }
    }
}
