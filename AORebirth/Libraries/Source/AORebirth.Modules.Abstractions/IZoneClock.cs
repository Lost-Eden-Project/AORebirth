namespace AORebirth.Modules
{
    using System;

    /// <summary>
    /// The one clock every module timestamp uses (module API 1.6): <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>,
    /// the clock <see cref="Network.MessageSample.Timestamp"/> and every event's <c>Timestamp</c> are on. Timing between a
    /// message and an event is a subtraction divided by <see cref="Frequency"/>.
    /// </summary>
    public interface IZoneClock
    {
        /// <summary><see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> now.</summary>
        long Now { get; }

        /// <summary><see cref="System.Diagnostics.Stopwatch.Frequency"/>: ticks per second.</summary>
        long Frequency { get; }

        /// <summary>
        /// The UTC time of <paramref name="timestamp"/>, through one offset taken at startup. <see cref="ToUtc"/> and
        /// <see cref="FromUtc"/> agree with each other but drift from wall time by the system clock's adjustments: use them
        /// for intervals, not calendar dates.
        /// </summary>
        DateTime ToUtc(long timestamp);

        /// <summary>The timestamp of <paramref name="utc"/>, the inverse of <see cref="ToUtc"/>.</summary>
        long FromUtc(DateTime utc);
    }
}
