namespace ZoneEngine_New.Core.Modules
{
    using System;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Threading;

    using AORebirth.Modules.Events;
    using AORebirth.Modules.World;

    using ZoneEngine_New.Core.Logging;

    /// <summary>
    /// The module visibility observers (module API 1.6), told when a client is told an entity exists and when it stops.
    /// Added during module Initialize only; the list is copy-on-write, so a playfield tick reads one reference and never
    /// locks. With no observer <see cref="HasObservers"/> is false and the locality hook costs one field read. Each call is
    /// isolated and timed, as event handlers are.
    /// </summary>
    public sealed class ModuleVisibilityObservers
    {
        const int MaxConsecutiveFailures = 10;

        static readonly long SlowTicks = Stopwatch.Frequency / 1000 * 2;
        static readonly long SlowReportIntervalTicks = Stopwatch.Frequency * 60;

        readonly IZoneLogger _logger;
        readonly object _sync = new();
        Entry[] _entries = Array.Empty<Entry>();

        internal ModuleVisibilityObservers(IZoneLogger logger)
        {
            _logger = logger;
        }

        public bool HasObservers => Volatile.Read(ref _entries).Length != 0;

        /// <summary>
        /// Tells every observer whose subscription covers <paramref name="entity"/>. <paramref name="isNpc"/>: the entity
        /// is an NPC, which is what an empty subscription asks for. Playfield tick.
        /// </summary>
        public void Publish(int characterId, int playfieldId, EntityRef entity, bool isNpc, bool visible, VisibilityReason reason)
        {
            Entry[] entries = Volatile.Read(ref _entries);
            if (entries.Length == 0)
                return;

            var sample = new VisibilitySample(characterId, playfieldId, entity, visible, reason, Stopwatch.GetTimestamp());
            for (int i = 0; i < entries.Length; i++)
            {
                Entry entry = entries[i];
                if (entry.Removed || !entry.Covers(entity.Type, isNpc))
                    continue;

                long started = Stopwatch.GetTimestamp();
                try
                {
                    entry.Observer.OnVisibility(in sample);
                    if (Volatile.Read(ref entry.Failures) != 0)
                        Volatile.Write(ref entry.Failures, 0);
                }
                catch (Exception exception)
                {
                    int failures = Interlocked.Increment(ref entry.Failures);
                    _logger.Error(exception, string.Format(CultureInfo.InvariantCulture,
                        "Module {0} visibility observer failed ({1} in a row).", entry.ModuleId, failures));
                    if (failures >= MaxConsecutiveFailures && Remove(entry))
                        _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                            "Module {0} visibility observer removed after {1} consecutive failures.", entry.ModuleId, failures));
                }

                long elapsed = Stopwatch.GetTimestamp() - started;
                if (elapsed > SlowTicks)
                    ReportSlow(entry, started, elapsed);
            }
        }

        internal void Add(string moduleId, IVisibilityObserver observer, VisibilitySubscription subscription)
        {
            ArgumentNullException.ThrowIfNull(observer);
            ArgumentNullException.ThrowIfNull(subscription);
            int[] types = (subscription.EntityTypes ?? Array.Empty<int>()).Distinct().ToArray();
            lock (_sync)
                Volatile.Write(ref _entries, [.. _entries, new Entry(moduleId, observer, types)]);

            _logger.Info(string.Format(CultureInfo.InvariantCulture, "Module {0} visibility observer types={1}", moduleId,
                types.Length == 0 ? "npcs" : string.Join(",", types)));
        }

        internal void RemoveModule(string moduleId)
        {
            lock (_sync)
                Volatile.Write(ref _entries, _entries.Where(entry => entry.ModuleId != moduleId).ToArray());
        }

        bool Remove(Entry entry)
        {
            lock (_sync)
            {
                if (entry.Removed)
                    return false;
                entry.Removed = true;
                Volatile.Write(ref _entries, _entries.Where(candidate => !ReferenceEquals(candidate, entry)).ToArray());
                return true;
            }
        }

        void ReportSlow(Entry entry, long now, long elapsed)
        {
            long last = Volatile.Read(ref entry.LastSlowReport);
            if (last != 0 && now - last < SlowReportIntervalTicks)
                return;
            if (Interlocked.CompareExchange(ref entry.LastSlowReport, now, last) != last)
                return;
            _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                "Module {0} visibility observer took {1:0.0} ms on the playfield tick (limit 2 ms).", entry.ModuleId,
                elapsed * 1000.0 / Stopwatch.Frequency));
        }

        sealed class Entry(string moduleId, IVisibilityObserver observer, int[] types)
        {
            public readonly string ModuleId = moduleId;
            public readonly IVisibilityObserver Observer = observer;
            public int Failures;
            public long LastSlowReport;
            public volatile bool Removed;

            /// <summary>An empty subscription means NPCs only.</summary>
            public bool Covers(int entityType, bool isNpc) => types.Length == 0 ? isNpc : Array.IndexOf(types, entityType) >= 0;
        }
    }
}
