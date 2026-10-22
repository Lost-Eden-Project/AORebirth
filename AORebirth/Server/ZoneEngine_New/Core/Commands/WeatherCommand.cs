namespace ZoneEngine_New.Core.Commands
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// <c>.weather</c>: sends a <see cref="WeatherControlMessage"/> to every player on the GM's playfield (or only the
    /// GM with <c>me</c>). The client (Gamecode.dll WeatherControlIIR_t) queues each message as a weather entry centred
    /// on <see cref="WeatherControlMessage.Position"/> with a radius (<see cref="WeatherControlMessage.Range"/>), a fade
    /// in, a duration and a fade out. Each frame it weights every entry by the player's distance to its centre
    /// (1 - (d / radius)^2) and by its fade, normalises when the weights add up past 1, and drops an entry once its
    /// weight is 0. So the weather is placed at the GM with a radius that covers the playfield, and it ends by itself
    /// after its duration: there is no message that removes an entry early.
    /// <para>
    /// The type byte selects which environment slot the intensity lands in (apply 0x100be467) and the frame code
    /// (0x100bea97) renders each slot: 0 rain renderer, 1 grey fog, 2 (every type but 3) overcast darkening, and
    /// types 4-7 the sand, ash, fallout red and fallout green renderers, which draw particles at intensity^3 * 0.45
    /// plus a fog in their own colour. Type 2 has no slot of its own, so it is overcast only; type 3 gets nothing,
    /// so it is the clear sky. Those renderers exist only on playfields whose environment type the client builds
    /// them for (0 and 7-13, the outdoor ones; Gamecode.dll 0x100b2252).
    /// </para>
    /// </summary>
    public sealed class WeatherCommand : IGmCommand
    {
        /// <summary>Type byte to name. Numbers are always accepted, so an unverified name costs nothing to test.</summary>
        static readonly (byte Type, string Name)[] Types =
        {
            (0, "rain"),
            (1, "fog"),
            (2, "cloud"),
            (3, "clear"),
            (4, "sand"),
            (5, "ash"),
            (6, "falloutred"),
            (7, "falloutgreen")
        };

        const byte DefaultIntensity = 60;
        const int DefaultDurationSeconds = 600;
        const float DefaultRadiusMeters = 4000f;
        const short FadeSeconds = 5;

        public string Name => "weather";

        public int RequiredGmLevel => 1;

        public string Usage => ".weather <type|name> [intensity 0-100] [duration s] [radius m] [me] | .weather list";

        public GmCommandHelp Help { get; } = new(
            "Sets the weather around you, for every player on your playfield, through the client's WeatherControl "
            + "message. Types are a number 0-7 or a name; intensity is a percent (default 60); duration is how long "
            + "it stays at full strength (default 600 s); radius is how far from you it reaches (default 4000 m).",
            [
                new(".weather <type|name>", "Weather at default intensity, duration and radius, centred on you."),
                new(".weather <type|name> <intensity>", "Set the intensity percent, 0-100."),
                new(".weather <type|name> <intensity> <duration>", "Also set the seconds at full strength."),
                new(".weather <type|name> <intensity> <duration> <radius>", "Also set the radius in metres."),
                new(".weather ... me", "Send it to yourself only."),
                new(".weather list", "The type numbers and names."),
            ],
            [
                new(".weather rain 80", "Heavy rain for ten minutes."),
                new(".weather 5 40 120 300", "Light ash storm for two minutes within 300 m of you."),
                new(".weather clear 100 600", "Clear sky; it blends with weather already active until that ends."),
            ],
            "The weather is a client effect only and is not remembered across zoning. Each message adds a weather "
            + "entry that fades in over 5 s and out over 5 s after its duration; it cannot be cancelled early, so "
            + "several entries at once blend (zoning clears them). Weather fades with distance from where it was cast. "
            + "Particles scale with intensity cubed, so below about 40% they are barely visible. Rain, sand, ash and "
            + "fallout only render on outdoor playfields, and precipitation needs the client's weather option on.");

        public void Execute(GmCommandContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            string[] args = context.Args;
            if (args.Length == 0)
            {
                context.SendUsage();
                return;
            }

            if (args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                var lines = new List<string>(Types.Length);
                foreach ((byte type, string name) in Types)
                    lines.Add(type.ToString(CultureInfo.InvariantCulture) + "  " + name);
                GmCommandFeedback.SendLines(context.Session, context.Player, lines);
                return;
            }

            if (!TryParseType(args[0], out byte weatherType, out string typeName))
            {
                context.SendUsage("Unknown weather type '" + args[0] + "'. Use a number 0-7 or .weather list.");
                return;
            }

            bool onlyMe = false;
            byte intensity = DefaultIntensity;
            int duration = DefaultDurationSeconds;
            float radius = DefaultRadiusMeters;
            int position = 0;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].Equals("me", StringComparison.OrdinalIgnoreCase))
                {
                    onlyMe = true;
                    continue;
                }

                if (!int.TryParse(args[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 0)
                {
                    context.SendUsage("'" + args[i] + "' is not a number.");
                    return;
                }

                switch (position++)
                {
                    case 0:
                        intensity = (byte)Math.Min(100, value);
                        break;
                    case 1:
                        duration = value;
                        break;
                    case 2:
                        if (value < 1)
                        {
                            context.SendUsage("The radius must be at least 1 metre.");
                            return;
                        }

                        radius = value;
                        break;
                    default:
                        context.SendUsage("Too many arguments.");
                        return;
                }
            }

            Player gm = context.Player;
            if (gm.Playfield is not Playfield playfield)
            {
                GmCommandFeedback.SendError(context.Session, gm, "You are not on a playfield.");
                return;
            }

            var centre = new Vector3(gm.Position.xf, gm.Position.yf, gm.Position.zf);
            int sent = 0;
            if (onlyMe)
            {
                context.Session.Send(Build(gm.Identity, weatherType, intensity, duration, radius, centre));
                sent = 1;
            }
            else
            {
                foreach (Player player in playfield.GetRequiredService<DynelRegistry>().PlayerEntities())
                {
                    if (player.Session == null)
                        continue;
                    player.Session.Send(Build(player.Identity, weatherType, intensity, duration, radius, centre));
                    sent++;
                }
            }

            GmCommandFeedback.Send(
                context.Session,
                gm,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Weather {0} ({1}) intensity {2}% for {3}s within {4:0}m of you, sent to {5} player(s).",
                    typeName,
                    weatherType,
                    intensity,
                    duration,
                    radius,
                    sent));
        }

        static bool TryParseType(string text, out byte type, out string name)
        {
            foreach ((byte candidate, string candidateName) in Types)
            {
                if (text.Equals(candidateName, StringComparison.OrdinalIgnoreCase)
                    || text.Equals(candidate.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                {
                    type = candidate;
                    name = candidateName;
                    return true;
                }
            }

            string? alias = text.ToLowerInvariant() switch
            {
                "clouds" or "cloudy" or "overcast" => "cloud",
                "fogcloud" or "fog_cloud" or "fogncloud" or "mist" => "fog",
                "sandstorm" => "sand",
                "ashstorm" => "ash",
                "red" or "fallout" or "fallout_red" => "falloutred",
                "green" or "fallout_green" => "falloutgreen",
                "none" or "sun" or "sunny" => "clear",
                _ => null
            };
            if (alias != null)
                return TryParseType(alias, out type, out name);

            type = 0;
            name = string.Empty;
            return false;
        }

        /// <summary>
        /// Only the type and intensity drive the look: the client's own renderer for the type supplies the particles
        /// and their tinted fog (sand orange, ash dark grey, fallout red / green, rain blue-grey). The extra fog and
        /// ambient light bytes stay 0 so they do not paint over it, and the raw byte the client reads unscaled is 0.
        /// </summary>
        static WeatherControlMessage Build(Identity recipient, byte weatherType, byte intensity, int durationSeconds, float radius, Vector3 centre)
        {
            return new WeatherControlMessage
            {
                Identity = recipient,
                FadeIn = FadeSeconds,
                Duration = durationSeconds,
                FadeOut = FadeSeconds,
                Range = radius,
                WeatherType = weatherType,
                WeatherIntensity = intensity,
                Wind = 0,
                Clouds = 0,
                Thunderstrikes = 0,
                Tremors = 0,
                TremorPercentage = 0,
                ThunderstrikePercentage = 0,
                AmbientLightRed = 0,
                AmbientLightGreen = 0,
                AmbientLightBlue = 0,
                FogColorRed = 0,
                FogColorGreen = 0,
                FogColorBlue = 0,
                FogDensity = 0,
                Position = centre,
                UnknownSingle = 0
            };
        }
    }
}
