namespace ZoneEngine_New.Core.Metrics
{
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using ZoneEngine_New.Core.Commands;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Network;

    /// <summary>Bits of <see cref="CharacterStat.DebugFlags"/>; each one opts a GM into a debug channel.</summary>
    [Flags]
    public enum PlayerDebugFlags
    {
        None = 0,

        /// <summary>Slow and stalled playfield ticks, as chat.</summary>
        Watchdog = 1 << 0,

        /// <summary>Playfield ticks that threw, as chat.</summary>
        Tick = 1 << 1
    }

    /// <summary>
    /// Sends debug notices to online players subscribed to their channel: watchdog (slow ticks, stalls, recoveries) to
    /// <see cref="PlayerDebugFlags.Watchdog"/>, tick failures to <see cref="PlayerDebugFlags.Tick"/>. Subscribers and
    /// their channels are kept here, not read from stats at publish time: notices come from tick and watcher threads
    /// that do not own the subscriber's stats.
    /// </summary>
    internal static class WatchdogFeed
    {
        /// <summary>At most one slow-tick or tick-failure notice per playfield per interval; the rest are counted.</summary>
        const double NoticeIntervalSeconds = 2.0;

        static readonly ConcurrentDictionary<int, Subscriber> Subscribers = new();
        static readonly ConcurrentDictionary<int, Throttle> SlowThrottleByPlayfield = new();
        static readonly ConcurrentDictionary<int, Throttle> FailureThrottleByPlayfield = new();

        public static bool HasSubscribers => !Subscribers.IsEmpty;

        public static bool IsSubscribed(Player player) => Subscribers.TryGetValue(player.Identity.Instance, out Subscriber? current)
            && ReferenceEquals(current.Player, player);

        /// <summary>Matches the subscription to the player's stored flags. Call on the player's owning thread.</summary>
        public static void Sync(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);
            var flags = (PlayerDebugFlags)player.Stats.GetOrZero(CharacterStat.DebugFlags)
                & (PlayerDebugFlags.Watchdog | PlayerDebugFlags.Tick);
            if (flags != PlayerDebugFlags.None)
                Subscribers[player.Identity.Instance] = new Subscriber(player, flags);
            else
                Remove(player);
        }

        public static void Remove(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);
            if (Subscribers.TryGetValue(player.Identity.Instance, out Subscriber? current) && ReferenceEquals(current.Player, player))
                Subscribers.TryRemove(new System.Collections.Generic.KeyValuePair<int, Subscriber>(player.Identity.Instance, current));
        }

        /// <summary>A watchdog notice.</summary>
        public static void Publish(string text)
            => Send(PlayerDebugFlags.Watchdog, DiagAoml.Color(DiagAoml.Orange, "[watchdog] ") + text);

        static void Send(PlayerDebugFlags channel, string line)
        {
            if (Subscribers.IsEmpty)
                return;

            foreach (Subscriber subscriber in Subscribers.Values)
            {
                if ((subscriber.Flags & channel) == 0)
                    continue;

                IZoneSession? session = subscriber.Player.Session;
                if (session == null)
                    continue;

                try
                {
                    GmCommandFeedback.Send(session, subscriber.Player, line);
                }
                catch (Exception)
                {
                    // A notice must never break the tick or watcher thread that raised it.
                }
            }
        }

        static bool AnySubscribed(PlayerDebugFlags channel)
        {
            foreach (Subscriber subscriber in Subscribers.Values)
            {
                if ((subscriber.Flags & channel) != 0)
                    return true;
            }

            return false;
        }

        /// <summary>False while this playfield's last notice is too recent; <paramref name="suppressed"/> counts the skipped ones.</summary>
        static bool TryPass(ConcurrentDictionary<int, Throttle> throttles, int playfieldId, out int suppressed)
        {
            Throttle throttle = throttles.GetOrAdd(playfieldId, static _ => new Throttle());
            long now = Stopwatch.GetTimestamp();
            lock (throttle)
            {
                if (throttle.LastSentTicks != 0
                    && now - throttle.LastSentTicks < NoticeIntervalSeconds * Stopwatch.Frequency)
                {
                    throttle.Suppressed++;
                    suppressed = 0;
                    return false;
                }

                suppressed = throttle.Suppressed;
                throttle.Suppressed = 0;
                throttle.LastSentTicks = now;
                return true;
            }
        }

        static string MoreText(int suppressed) => suppressed > 0
            ? DiagAoml.Muted(string.Format(CultureInfo.InvariantCulture, "  +{0} more", suppressed))
            : string.Empty;

        internal static void PublishSlowTick(SlowTick slow)
        {
            if (!AnySubscribed(PlayerDebugFlags.Watchdog) || !TryPass(SlowThrottleByPlayfield, slow.PlayfieldId, out int suppressed))
                return;

            string where = slow.WorstDynel != null
                ? " " + DiagAoml.Name(DiagAoml.Safe(slow.WorstDynel))
                : slow.WorstDetail != 0 ? DiagAoml.Muted(" #" + slow.WorstDetail.ToString(CultureInfo.InvariantCulture)) : string.Empty;
            Publish(
                DiagAoml.Name("pf " + slow.PlayfieldId.ToString(CultureInfo.InvariantCulture)) + " slow tick "
                + DiagAoml.Severity(slow.TickMs, slow.LimitMs * 2, slow.LimitMs * 4, DiagAoml.Ms(slow.TickMs) + "ms")
                + DiagAoml.Muted(" / " + DiagAoml.Ms(slow.LimitMs) + "ms")
                + DiagAoml.Label("  worst ") + slow.WorstStage + where + " "
                + DiagAoml.Severity(slow.WorstStageMs, slow.LimitMs * 0.5, slow.LimitMs, DiagAoml.Ms(slow.WorstStageMs) + "ms")
                + (slow.GcPauseMs > 0
                    ? DiagAoml.Label("  gc ") + DiagAoml.Color(DiagAoml.Red, DiagAoml.Ms(slow.GcPauseMs) + "ms")
                        + DiagAoml.Muted(" gen" + Math.Max(0, slow.GcDeepestGeneration).ToString(CultureInfo.InvariantCulture))
                    : string.Empty)
                + MoreText(suppressed));
        }

        /// <summary>A tick stuck past the stall limit; always red.</summary>
        public static void PublishStall(string text) => Publish(DiagAoml.Color(DiagAoml.Red, text));

        /// <summary>A stalled tick that finally returned.</summary>
        public static void PublishRecovered(string text) => Publish(DiagAoml.Color(DiagAoml.Yellow, text));

        /// <summary>
        /// A playfield tick threw: the rest of that tick was skipped (2026-10-13: a corpse loot overflow left an NPC dead
        /// and unswapped). The exception, its message and the first server frame it came from, to
        /// <see cref="PlayerDebugFlags.Tick"/> subscribers; the full stack is in the server log.
        /// </summary>
        public static void PublishTickFailure(int playfieldId, Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (!AnySubscribed(PlayerDebugFlags.Tick) || !TryPass(FailureThrottleByPlayfield, playfieldId, out int suppressed))
                return;

            Send(
                PlayerDebugFlags.Tick,
                DiagAoml.Color(DiagAoml.Red, "[tick] ")
                + DiagAoml.Name("pf " + playfieldId.ToString(CultureInfo.InvariantCulture)) + " tick failed: "
                + DiagAoml.Color(DiagAoml.Red, DiagAoml.Safe(exception.GetType().Name)) + " "
                + DiagAoml.Safe(exception.Message)
                + DiagAoml.Label("  at ") + DiagAoml.Safe(ServerFrame(exception))
                + MoreText(suppressed));
        }

        /// <summary>The innermost frame in server code (not the runtime), as Type.Method (File.cs:line).</summary>
        static string ServerFrame(Exception exception)
        {
            StackFrame[] frames = new StackTrace(exception, fNeedFileInfo: true).GetFrames();
            foreach (StackFrame frame in frames)
            {
                System.Reflection.MethodBase? method = frame.GetMethod();
                string? ns = method?.DeclaringType?.Namespace;
                if (method == null || ns == null || !ns.StartsWith("ZoneEngine_New", StringComparison.Ordinal))
                    continue;

                string file = frame.GetFileName() is string path ? Path.GetFileName(path) : "?";
                return string.Format(CultureInfo.InvariantCulture, "{0}.{1} ({2}:{3})",
                    method.DeclaringType!.Name, method.Name, file, frame.GetFileLineNumber());
            }

            return frames.Length > 0 && frames[0].GetMethod() is System.Reflection.MethodBase first
                ? (first.DeclaringType?.Name ?? "?") + "." + first.Name
                : "?";
        }

        sealed record Subscriber(Player Player, PlayerDebugFlags Flags);

        sealed class Throttle
        {
            internal long LastSentTicks;

            internal int Suppressed;
        }
    }
}
