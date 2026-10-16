namespace ZoneEngine_New.Core.Commands
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// <c>.gametime</c>: reads or moves the shared Rubi-Ka clock (<see cref="GameClock"/>). A move shifts the clock for
    /// the whole zone engine and resends GameTime to every player online, each on its own playfield tick. The shift is
    /// kept in memory only: a restart goes back to Gameplay.json worldClock.
    /// </summary>
    public sealed class GameTimeCommand : IGmCommand
    {
        readonly Lazy<PlayfieldManager> _playfields;

        public GameTimeCommand(Lazy<PlayfieldManager> playfields)
        {
            ArgumentNullException.ThrowIfNull(playfields);
            _playfields = playfields;
        }

        public string Name => "gametime";

        public int RequiredGmLevel => 1;

        public string Usage => ".gametime [set <hh:mm[:ss]> | set <day> <hh:mm[:ss]> | inc <n>[h|m|d] | dec <n>[h|m|d] | reset]";

        public GmCommandHelp Help { get; } = new(
            "Shows or moves the world clock for everyone online. Rubi-Ka runs a 27-hour day at 15 game seconds per real second "
            + "(1 h 48 min real per day): dawn 07-09, day 09-21, dusk 21-23, night otherwise.",
            [
                new(".gametime", "The clock now, and how far it has been moved."),
                new(".gametime set <hh:mm[:ss]>", "Set the time of day (0-26 hours), keeping the day."),
                new(".gametime set <day> <hh:mm[:ss]>", "Set the day number and the time of day."),
                new(".gametime inc <n>[h|m|d]", "Move the clock forward by n hours (default), minutes or days. Decimals allowed."),
                new(".gametime dec <n>[h|m|d]", "Move the clock back by the same."),
                new(".gametime reset", "Drop every move: back to the clock Gameplay.json worldClock gives."),
            ],
            [
                new(".gametime set 22:30", "Dusk, right now, for everyone."),
                new(".gametime inc 6", "Six game hours later."),
                new(".gametime dec 30m", "Half a game hour earlier."),
            ],
            "Moves are kept in memory only; a zone engine restart returns to the configured clock. Mission and perk "
            + "timers are not affected: they run on real time, which never moves.");

        public void Execute(GmCommandContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            string[] args = context.Args;
            if (args.Length == 0)
            {
                Report(context, "World clock");
                return;
            }

            long shift;
            switch (args[0].ToLowerInvariant())
            {
                case "set":
                    if (!TryParseSet(args, WorldClockSettings(context), out shift))
                    {
                        context.SendUsage("Give a time as hh:mm or hh:mm:ss (hours 0-26), optionally after a day number.");
                        return;
                    }

                    break;
                case "inc":
                case "dec":
                    if (args.Length != 2 || !TryParseSpan(args[1], out shift))
                    {
                        context.SendUsage("Give an amount: hours, or a number followed by h, m or d.");
                        return;
                    }

                    if (args[0].Equals("dec", StringComparison.OrdinalIgnoreCase))
                        shift = -shift;
                    break;
                case "reset":
                    shift = -GameClock.OffsetGameSeconds;
                    break;
                default:
                    context.SendUsage();
                    return;
            }

            GameClock.ShiftGameSeconds(shift);
            int sent = ResendToEveryone();
            Report(context, string.Format(CultureInfo.InvariantCulture, "World clock moved; GameTime sent to {0} player(s)", sent));
        }

        /// <summary>The game seconds to move so the clock reads the requested day and time of day.</summary>
        static bool TryParseSet(string[] args, ZoneEngine_New.Core.Configuration.GameplayWorldClock? settings, out long shift)
        {
            shift = 0;
            GameClock.Reading now = GameClock.Now(settings, DateTime.UtcNow);
            long day = now.Day;
            string time;
            if (args.Length == 2)
            {
                time = args[1];
            }
            else if (args.Length == 3 && long.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out day))
            {
                time = args[2];
            }
            else
            {
                return false;
            }

            if (!TryParseTimeOfDay(time, out int timeOfDay))
                return false;

            shift = ((day - now.Day) * GameClock.SecondsPerDay) + (timeOfDay - (long)now.TimeOfDay);
            return true;
        }

        static bool TryParseTimeOfDay(string text, out int seconds)
        {
            seconds = 0;
            string[] parts = text.Split(':');
            if (parts.Length is not (2 or 3)
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int hour)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minute)
                || hour >= GameClock.HoursPerDay || minute >= 60)
                return false;

            int second = 0;
            if (parts.Length == 3 && (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out second) || second >= 60))
                return false;

            seconds = (hour * GameClock.SecondsPerHour) + (minute * 60) + second;
            return true;
        }

        /// <summary>"6", "6h", "30m" or "1.5d" as game seconds.</summary>
        static bool TryParseSpan(string text, out long seconds)
        {
            seconds = 0;
            int unitSeconds = GameClock.SecondsPerHour;
            char unit = char.ToLowerInvariant(text[^1]);
            if (unit is 'h' or 'm' or 'd')
            {
                unitSeconds = unit switch { 'm' => 60, 'd' => GameClock.SecondsPerDay, _ => GameClock.SecondsPerHour };
                text = text[..^1];
            }

            if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double amount)
                || !double.IsFinite(amount) || amount <= 0 || amount * unitSeconds > int.MaxValue)
                return false;

            seconds = (long)Math.Round(amount * unitSeconds);
            return true;
        }

        /// <summary>A fresh GameTime to every player online, each sent from its own playfield's tick.</summary>
        int ResendToEveryone()
        {
            int sent = 0;
            foreach (Player player in _playfields.Value.SnapshotPlayers())
            {
                if (player.Playfield is not Playfield playfield || player.Session == null)
                    continue;

                playfield.DispatchPlayerProjection(player, () =>
                {
                    if (player.Session != null && player.Playfield is Playfield current)
                        current.GetRequiredService<SpawnService>().SendGameTime(player.Session, player);
                });
                sent++;
            }

            return sent;
        }

        static ZoneEngine_New.Core.Configuration.GameplayWorldClock? WorldClockSettings(GmCommandContext context)
            => context.Player.Playfield?.GetService<ZoneEngine_New.Core.Configuration.GameplaySettings>()?.WorldClock;

        static void Report(GmCommandContext context, string title)
        {
            GameClock.Reading reading = GameClock.Now(WorldClockSettings(context), DateTime.UtcNow);
            long offset = GameClock.OffsetGameSeconds;
            var lines = new List<string>
            {
                title + ": " + reading,
                string.Format(CultureInfo.InvariantCulture, "Moved by {0}{1:0.##} game hours since startup; real time x{2}.",
                    offset < 0 ? "-" : "+", Math.Abs(offset) / (double)GameClock.SecondsPerHour, GameClock.GameSecondsPerRealSecond)
            };
            GmCommandFeedback.SendLines(context.Session, context.Player, lines);
        }
    }
}
