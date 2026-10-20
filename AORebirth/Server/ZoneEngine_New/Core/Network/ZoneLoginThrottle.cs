namespace ZoneEngine_New.Core.Network
{
    using System;
    using System.Collections.Generic;
    using System.Net;

    /// <summary>
    /// Refused zone admissions per remote address. Too many in a short window and that address is refused outright
    /// for a while, before any World call: no cookie probing and no log flooding from one source.
    /// </summary>
    public sealed class ZoneLoginThrottle
    {
        public const int MaxFailures = 10;

        static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
        static readonly TimeSpan Block = TimeSpan.FromSeconds(120);

        /// <summary>Bounds memory under a flood from many addresses.</summary>
        const int MaxTracked = 10_000;

        readonly object _sync = new();
        readonly Dictionary<IPAddress, Entry> _entries = new();
        readonly Func<long> _clock;

        public ZoneLoginThrottle(Func<long>? clock = null)
        {
            _clock = clock ?? (() => Environment.TickCount64);
        }

        public bool IsBlocked(IPAddress? address)
        {
            if (address == null) return false;
            lock (_sync)
                return _entries.TryGetValue(address, out Entry? entry) && _clock() < entry.BlockedUntilMs;
        }

        /// <summary>True when this failure blocked the address.</summary>
        public bool RecordFailure(IPAddress? address)
        {
            if (address == null) return false;
            long now = _clock();
            lock (_sync)
            {
                if (!_entries.TryGetValue(address, out Entry? entry))
                {
                    if (_entries.Count >= MaxTracked) Prune(now);
                    entry = new Entry { WindowStartMs = now };
                    _entries[address] = entry;
                }

                if (now - entry.WindowStartMs > (long)Window.TotalMilliseconds)
                {
                    entry.WindowStartMs = now;
                    entry.Failures = 0;
                }

                if (++entry.Failures < MaxFailures) return false;
                entry.BlockedUntilMs = now + (long)Block.TotalMilliseconds;
                entry.Failures = 0;
                entry.WindowStartMs = now;
                return true;
            }
        }

        /// <summary>A good admission clears the address's count.</summary>
        public void RecordSuccess(IPAddress? address)
        {
            if (address == null) return;
            lock (_sync)
                if (_entries.TryGetValue(address, out Entry? entry) && _clock() >= entry.BlockedUntilMs)
                    _entries.Remove(address);
        }

        void Prune(long now)
        {
            var stale = new List<IPAddress>();
            foreach (KeyValuePair<IPAddress, Entry> pair in _entries)
                if (now >= pair.Value.BlockedUntilMs && now - pair.Value.WindowStartMs > (long)Window.TotalMilliseconds)
                    stale.Add(pair.Key);
            foreach (IPAddress address in stale) _entries.Remove(address);
            // Still full: forget the oldest windows rather than grow without bound.
            if (_entries.Count >= MaxTracked) _entries.Clear();
        }

        sealed class Entry
        {
            public long WindowStartMs;
            public int Failures;
            public long BlockedUntilMs;
        }
    }
}
