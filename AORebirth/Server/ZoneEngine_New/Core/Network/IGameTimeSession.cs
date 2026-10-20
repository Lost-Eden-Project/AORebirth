namespace ZoneEngine_New.Core.Network
{
    using System;
    using System.Globalization;
    using System.Threading;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

    using ZoneEngine_New.Core.Configuration;

    /// <summary>The actual GameTime send anchors client-facing mission expiry clocks.</summary>
    public interface IGameTimeSession
    {
        DateTime? GameTimeSynchronizedAtUtc { get; }

        /// <summary>
        /// The server time the last GameTime gave the client (its last field, an int: GameTime_t::Update sets the
        /// client's server-synced system time to it, Gamecode.dll 0x1000b590). The client's clock reads this plus the
        /// seconds since <see cref="GameTimeSynchronizedAtUtc"/>.
        /// </summary>
        int GameTimeServerSeconds { get; }

        void RecordGameTimeSynchronization(DateTime utcNow, int serverSeconds);
    }

    /// <summary>
    /// The world clock every GameTime send carries, as the client keeps it (Gamecode.dll GameTime_t, ctor 0x1000afdb,
    /// Update 0x1000b590, RunFunction 0x1000b27e): a time of day in game seconds through a 27-hour day that the client
    /// then advances at 15 game seconds per real second, the day period it recomputes from the hour, the day number,
    /// and a server time in seconds it advances in step with real time. One clock for every player: the day and time
    /// of day come from the real time elapsed since Gameplay.json worldClock's epoch, plus the GM shift from .gametime
    /// (<see cref="OffsetGameSeconds"/>, in memory only); the server time is UTC seconds and never shifts, so mission
    /// and perk timers given in it stay right when the day is moved.
    /// </summary>
    public static class GameClock
    {
        public const int SecondsPerHour = 3600;

        public const int HoursPerDay = 27;

        public const int SecondsPerDay = HoursPerDay * SecondsPerHour;

        /// <summary>GameTime_t::GetGameSecondsPerSecond: the client's own clock rate after a GameTime.</summary>
        public const int GameSecondsPerRealSecond = 15;

        static long _offsetGameSeconds;

        /// <summary>Game seconds .gametime has moved the world clock by since startup.</summary>
        public static long OffsetGameSeconds => Interlocked.Read(ref _offsetGameSeconds);

        public static void ShiftGameSeconds(long delta) => Interlocked.Add(ref _offsetGameSeconds, delta);

        public static void ResetOffset() => Interlocked.Exchange(ref _offsetGameSeconds, 0);

        /// <summary>World holds the shift for every zone instance; each takes the value it pushes.</summary>
        public static void SetOffset(long offsetGameSeconds) => Interlocked.Exchange(ref _offsetGameSeconds, offsetGameSeconds);

        public readonly record struct Reading(float TimeOfDay, int DayPeriod, int Day, int ServerSeconds)
        {
            public int Hour => (int)TimeOfDay / SecondsPerHour;

            public int Minute => (int)TimeOfDay % SecondsPerHour / 60;

            public int Second => (int)TimeOfDay % 60;

            /// <summary>"day 189259 16:14:42 (day)".</summary>
            public override string ToString()
                => string.Format(CultureInfo.InvariantCulture, "day {0} {1:00}:{2:00}:{3:00} ({4})",
                    Day, Hour, Minute, Second, DayPeriodName(DayPeriod));
        }

        /// <summary>The world clock at <paramref name="utcNow"/>. Missing or unreadable settings use the defaults.</summary>
        public static Reading Now(GameplayWorldClock? settings, DateTime utcNow)
        {
            settings ??= new GameplayWorldClock();
            if (!settings.TryGetEpoch(out DateTime epoch))
                epoch = DateTime.UnixEpoch;

            long gameSeconds = (long)Math.Floor((utcNow - epoch).TotalSeconds * GameSecondsPerRealSecond)
                + ((long)settings.DayAtEpoch * SecondsPerDay)
                + settings.TimeOfDayAtEpochSeconds
                + OffsetGameSeconds;
            long day = Math.DivRem(gameSeconds, SecondsPerDay, out long timeOfDay);
            if (timeOfDay < 0)
            {
                timeOfDay += SecondsPerDay;
                day--;
            }

            int hour = (int)(timeOfDay / SecondsPerHour);
            return new Reading(timeOfDay, DayPeriodOf(hour), (int)Math.Clamp(day, 0, int.MaxValue), UnixSeconds(utcNow));
        }

        /// <summary>GameTime_t::UpdateDayPeriod: night until 7, dawn until 9, day until 21, dusk until 23, then night.</summary>
        public static int DayPeriodOf(int hour)
            => hour < 7 ? 3 : hour < 9 ? 0 : hour < 21 ? 1 : hour < 23 ? 2 : 3;

        public static string DayPeriodName(int period)
            => period switch { 0 => "dawn", 1 => "day", 2 => "dusk", 3 => "night", _ => "period " + period };

        public static GameTimeMessage BuildMessage(Identity identity, Reading reading)
            => new()
            {
                Identity = identity,
                Unknown1 = reading.TimeOfDay,
                Unknown2 = reading.DayPeriod,
                Unknown3 = reading.Day,
                Unknown4 = WireServerTime(reading.ServerSeconds)
            };

        public static int UnixSeconds(DateTime utc)
            => (int)Math.Clamp((utc - DateTime.UnixEpoch).TotalSeconds, 0, int.MaxValue);

        /// <summary>The int server time carried by a GameTime last field typed as a float.</summary>
        public static int ServerSeconds(float wireValue) => BitConverter.SingleToInt32Bits(wireValue);

        /// <summary>The GameTime last field (typed as a float) that carries <paramref name="serverSeconds"/>.</summary>
        public static float WireServerTime(int serverSeconds) => BitConverter.Int32BitsToSingle(serverSeconds);
    }
}
