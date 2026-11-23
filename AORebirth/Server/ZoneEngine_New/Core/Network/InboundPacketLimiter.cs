namespace ZoneEngine_New.Core.Network
{
    using System;
    using System.Diagnostics;

    /// <summary>
    /// What the receive loop does with one inbound packet.
    /// </summary>
    public enum InboundPacketVerdict
    {
        /// <summary>Within budget: decode and dispatch it.</summary>
        Accept,

        /// <summary>Over budget, first drop of this flood: drop it and log once.</summary>
        FirstDrop,

        /// <summary>Over budget: drop it silently.</summary>
        Drop,

        /// <summary>Over budget for <see cref="InboundPacketLimiter.DisconnectAfter"/> straight: close the connection.</summary>
        Disconnect
    }

    /// <summary>
    /// Per-connection budget for client→server packets: a token bucket of <see cref="Burst"/> packets refilled at
    /// <see cref="RefillPerSecond"/>. The busiest normal play (zoning in, mouse-turning while running, a long macro,
    /// fast inventory work) stays far below it; a flood of any message type runs out within a second and its packets
    /// are dropped before they are decoded or queued to the playfield tick. A drop is a request the server never acted
    /// on, so it cannot duplicate or lose anything. A flood that keeps the bucket empty (a drop at least every
    /// <see cref="OverflowResetAfter"/>) for <see cref="DisconnectAfter"/> closes the connection; a single burst such as
    /// a lag spike delivering buffered packets ends long before that and the bucket refills.
    /// Used only by the session's receive loop, so it is not thread-safe.
    /// </summary>
    public sealed class InboundPacketLimiter
    {
        public const int Burst = 500;

        public const int RefillPerSecond = 100;

        public static readonly TimeSpan DisconnectAfter = TimeSpan.FromSeconds(5);

        /// <summary>A flood is over once this long passes without a drop.</summary>
        public static readonly TimeSpan OverflowResetAfter = TimeSpan.FromSeconds(1);

        private readonly long _frequency;
        private readonly long _disconnectTicks;
        private readonly long _resetTicks;

        private double _tokens = Burst;
        private long _lastRefill;
        private bool _started;

        private bool _overflowing;
        private long _overflowSince;
        private long _lastDrop;

        private long _windowStart;
        private int _windowCount;

        public InboundPacketLimiter()
            : this(Stopwatch.Frequency)
        {
        }

        /// <param name="frequency">Timestamp ticks per second (<see cref="Stopwatch.Frequency"/> in the engine).</param>
        public InboundPacketLimiter(long frequency)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
            _frequency = frequency;
            _disconnectTicks = (long)(DisconnectAfter.TotalSeconds * frequency);
            _resetTicks = (long)(OverflowResetAfter.TotalSeconds * frequency);
        }

        /// <summary>Packets dropped since the connection opened.</summary>
        public long Dropped { get; private set; }

        /// <summary>Most packets received in any one-second window since the connection opened.</summary>
        public int PeakPerSecond { get; private set; }

        /// <param name="now">The packet's arrival timestamp, in the ticks given to the constructor.</param>
        public InboundPacketVerdict Consume(long now)
        {
            if (!_started)
            {
                _started = true;
                _lastRefill = now;
                _windowStart = now;
            }

            if (now > _lastRefill)
            {
                _tokens = Math.Min(Burst, _tokens + ((double)(now - _lastRefill) * RefillPerSecond / _frequency));
                _lastRefill = now;
            }

            if (now - _windowStart >= _frequency)
            {
                _windowStart = now;
                _windowCount = 0;
            }

            _windowCount++;
            if (_windowCount > PeakPerSecond)
                PeakPerSecond = _windowCount;

            if (_overflowing && now - _lastDrop >= _resetTicks)
                _overflowing = false;

            if (_tokens >= 1)
            {
                _tokens -= 1;
                return InboundPacketVerdict.Accept;
            }

            Dropped++;
            _lastDrop = now;
            if (!_overflowing)
            {
                _overflowing = true;
                _overflowSince = now;
                return InboundPacketVerdict.FirstDrop;
            }

            return now - _overflowSince >= _disconnectTicks ? InboundPacketVerdict.Disconnect : InboundPacketVerdict.Drop;
        }
    }
}
