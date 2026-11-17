namespace ZoneEngine_New.Core.Modules
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Threading;

    using AORebirth.Modules.Network;

    using ZoneEngine_New.Core.Logging;

    /// <summary>
    /// The inbound N3 message types modules handle (module API 1.12): types the engine has no reader for, each owned by
    /// one module. A connection asks <see cref="Wants"/> on its receive thread for every N3 packet whose type the engine
    /// does not read; the playfield tick then calls <see cref="Handle"/> through
    /// <see cref="MessageHandlers.ModuleRawMessageHandler"/>. Registration happens while modules initialize, before any
    /// connection exists; the table is swapped whole, so readers never lock.
    /// </summary>
    public sealed class ModuleMessageHandlers
    {
        const int MaxConsecutiveFailures = 10;

        static readonly long SlowTicks = Stopwatch.Frequency / 1000 * 2;
        static readonly long SlowReportIntervalTicks = Stopwatch.Frequency * 60;

        readonly IZoneLogger _logger;
        readonly object _sync = new();
        Dictionary<int, Entry> _handlers = new();

        internal ModuleMessageHandlers(IZoneLogger logger)
        {
            _logger = logger;
        }

        public int Count => Volatile.Read(ref _handlers).Count;

        /// <summary>Whether a module handles <paramref name="messageType"/>; one dictionary lookup, no lock.</summary>
        public bool Wants(int messageType) => Volatile.Read(ref _handlers).ContainsKey(messageType);

        /// <summary>The module that handles <paramref name="messageType"/>, for logs; null when none.</summary>
        public string? OwnerOf(int messageType)
            => Volatile.Read(ref _handlers).TryGetValue(messageType, out Entry? entry) ? entry.ModuleId : null;

        /// <summary>Runs the type's handler on the caller's (playfield tick) thread. False when no module handles the type any more.</summary>
        public bool Handle(in ModuleMessage message)
        {
            if (!Volatile.Read(ref _handlers).TryGetValue(message.MessageType, out Entry? entry) || entry.Removed)
                return false;

            long started = Stopwatch.GetTimestamp();
            try
            {
                entry.Handler.Handle(in message);
                if (Volatile.Read(ref entry.Failures) != 0)
                    Volatile.Write(ref entry.Failures, 0);
            }
            catch (Exception exception)
            {
                int failures = Interlocked.Increment(ref entry.Failures);
                _logger.Error(exception, string.Format(CultureInfo.InvariantCulture,
                    "Module {0} message handler for 0x{1:x8} failed ({2} in a row).", entry.ModuleId, message.MessageType, failures));
                if (failures >= MaxConsecutiveFailures && Remove(entry))
                    _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                        "Module {0} message handler for 0x{1:x8} removed after {2} consecutive failures.",
                        entry.ModuleId, message.MessageType, failures));
            }

            long elapsed = Stopwatch.GetTimestamp() - started;
            if (elapsed > SlowTicks)
                ReportSlow(entry, started, elapsed);
            return true;
        }

        internal void Add(string moduleId, int messageType, IModuleMessageHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (_sync)
            {
                if (_handlers.TryGetValue(messageType, out Entry? owner))
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                        "Message type 0x{0:x8} is already handled by module {1}.", messageType, owner.ModuleId));
                Volatile.Write(ref _handlers, new Dictionary<int, Entry>(_handlers)
                {
                    [messageType] = new Entry(moduleId, messageType, handler)
                });
            }

            _logger.Info(string.Format(CultureInfo.InvariantCulture,
                "Module {0} handles inbound message type 0x{1:x8}", moduleId, messageType));
        }

        internal void RemoveModule(string moduleId)
        {
            lock (_sync)
            {
                var kept = new Dictionary<int, Entry>();
                foreach ((int type, Entry entry) in _handlers)
                {
                    if (string.Equals(entry.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase))
                        entry.Removed = true;
                    else
                        kept[type] = entry;
                }
                Volatile.Write(ref _handlers, kept);
            }
        }

        bool Remove(Entry entry)
        {
            lock (_sync)
            {
                if (entry.Removed)
                    return false;
                entry.Removed = true;
                var kept = new Dictionary<int, Entry>(_handlers);
                kept.Remove(entry.MessageType);
                Volatile.Write(ref _handlers, kept);
                return true;
            }
        }

        void ReportSlow(Entry entry, long started, long elapsed)
        {
            long last = Volatile.Read(ref entry.LastSlowReport);
            if (last != 0 && started - last < SlowReportIntervalTicks)
                return;
            if (Interlocked.CompareExchange(ref entry.LastSlowReport, started, last) != last)
                return;
            _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                "Module {0} message handler for 0x{1:x8} took {2:0.0} ms (reported at most once a minute).",
                entry.ModuleId, entry.MessageType, elapsed * 1000.0 / Stopwatch.Frequency));
        }

        sealed class Entry(string moduleId, int messageType, IModuleMessageHandler handler)
        {
            public readonly string ModuleId = moduleId;
            public readonly int MessageType = messageType;
            public readonly IModuleMessageHandler Handler = handler;
            public int Failures;
            public long LastSlowReport;
            public volatile bool Removed;
        }
    }
}
