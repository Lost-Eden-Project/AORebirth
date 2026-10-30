namespace ZoneEngine_New.Core.Modules
{
    using System;
    using System.Diagnostics;
    using System.Threading;

    using AORebirth.Modules;

    /// <summary>
    /// The module clock (module API 1.6): <see cref="Stopwatch.GetTimestamp"/>, the clock message samples are stamped
    /// with. UTC conversions use one offset taken when the zone started, so they agree with each other.
    /// </summary>
    public sealed class ModuleClock : IZoneClock
    {
        public static readonly ModuleClock Instance = new();

        readonly long _originTimestamp;
        readonly DateTime _originUtc;

        ModuleClock()
        {
            _originTimestamp = Stopwatch.GetTimestamp();
            _originUtc = DateTime.UtcNow;
        }

        public long Now => Stopwatch.GetTimestamp();

        public long Frequency => Stopwatch.Frequency;

        public DateTime ToUtc(long timestamp)
            => _originUtc + TimeSpan.FromSeconds((timestamp - _originTimestamp) / (double)Stopwatch.Frequency);

        public long FromUtc(DateTime utc)
            => _originTimestamp + (long)((utc.ToUniversalTime() - _originUtc).TotalSeconds * Stopwatch.Frequency);

        /// <summary>A timestamp <paramref name="delay"/> from now.</summary>
        public static long After(TimeSpan delay) => Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);

        /// <summary>A timestamp at <paramref name="utc"/> (an engine deadline kept as wall time).</summary>
        public static long At(DateTime utc) => Instance.FromUtc(utc);
    }

    /// <summary>
    /// The client message the playfield tick is handling (module API 1.6): <see cref="Current"/> is its receive timestamp
    /// while its handler runs on this thread, and 0 otherwise. Events a message causes carry it as RequestTimestamp.
    /// </summary>
    public static class ModuleRequest
    {
        [ThreadStatic]
        static long _current;

        /// <summary>The receive timestamp of the message being handled on this thread, or 0.</summary>
        public static long Current => _current;

        /// <summary>Marks <paramref name="received"/> as the message being handled until the returned scope ends.</summary>
        public static Scope Begin(long received) => new(Interlocked.Exchange(ref _current, received));

        public readonly struct Scope : IDisposable
        {
            readonly long _previous;

            internal Scope(long previous) => _previous = previous;

            public void Dispose() => _current = _previous;
        }
    }
}
