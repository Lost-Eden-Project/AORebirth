namespace WorldEngine
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Net;
    using System.Text.RegularExpressions;

    using AORebirth.World.Contracts;

    using Utility.Config;

    public sealed class WorldOptions
    {
        /// <summary>NPC instance ids: slot 0 keeps the single-engine range, each further slot gets its own block.</summary>
        public const int NpcIdBase = 1_000_000;

        public const int NpcIdStride = 0x00800000;

        /// <summary>Bearer token for the admin API. Never stored in Config.xml.</summary>
        public const string AdminTokenVariable = "AO_REBIRTH_WORLD_ADMIN_TOKEN";

        static readonly Regex HostName = new("^[A-Za-z0-9._-]{1,32}$", RegexOptions.CultureInvariant);

        public required IPAddress ListenAddress { get; init; }

        public required int Port { get; init; }

        /// <summary>0 when the status page is off.</summary>
        public required int StatusPort { get; init; }

        public required TimeSpan LeaseTtl { get; init; }

        public required TimeSpan LeaseGrace { get; init; }

        public required TimeSpan Heartbeat { get; init; }

        /// <summary>How long an issued zone ticket stays claimable.</summary>
        public required TimeSpan HandoffLifetime { get; init; }

        public required string Token { get; init; }

        /// <summary>0 when the admin API is off.</summary>
        public int AdminPort { get; init; }

        public IPAddress AdminAddress { get; init; } = IPAddress.Loopback;

        /// <summary>Empty only when the admin API listens on loopback.</summary>
        public string AdminToken { get; init; } = string.Empty;

        public IReadOnlyList<ZoneHostSettings> ZoneHosts { get; init; } = Array.Empty<ZoneHostSettings>();

        /// <summary>After a restart, only re-registrations are served for this long; then World is authoritative.</summary>
        public TimeSpan RecoveryWindow => LeaseTtl + LeaseGrace;

        /// <summary>An instance silent this long has lost everything it owned.</summary>
        public TimeSpan InstanceExpiry => LeaseTtl + LeaseGrace;

        public static WorldOptions FromConfig(Config? config)
        {
            WorldSettings? settings = config?.World;
            string listen = WorldSettings.ResolveListenIP(settings);
            if (!IPAddress.TryParse(listen, out IPAddress? address))
                throw new InvalidOperationException("World ListenIP must be an IP address.");
            int port = WorldSettings.ResolvePort(settings);
            int status = WorldSettings.ResolveStatusPort(settings);
            if (port > 65535 || status > 65535)
                throw new InvalidOperationException("World ports must be between 1 and 65535.");
            string token = Environment.GetEnvironmentVariable(WorldChannel.TokenVariable) ?? string.Empty;
            if (token.Length == 0 && !IPAddress.IsLoopback(address))
                throw new InvalidOperationException(
                    "World listens on " + listen + " but " + WorldChannel.TokenVariable + " is not set; a non-loopback World needs a token.");

            int adminPort = settings?.AdminPort ?? 0;
            IPAddress adminAddress = IPAddress.Loopback;
            string adminToken = Environment.GetEnvironmentVariable(AdminTokenVariable) ?? string.Empty;
            if (adminPort != 0)
            {
                if (adminPort < 0 || adminPort > 65535 || adminPort == port || adminPort == status)
                    throw new InvalidOperationException("World AdminPort must be between 1 and 65535 and differ from Port and StatusPort.");
                string adminListen = WorldSettings.ResolveAdminListenIP(settings);
                if (!IPAddress.TryParse(adminListen, out IPAddress? parsedAdmin))
                    throw new InvalidOperationException("World AdminListenIP must be an IP address.");
                adminAddress = parsedAdmin;
                if (adminToken.Length == 0 && !IPAddress.IsLoopback(adminAddress))
                    throw new InvalidOperationException("World admin API listens on " + adminListen + " but " + AdminTokenVariable
                        + " is not set; a non-loopback admin API needs a token.");
                if (adminToken.Length > 0 && adminToken.Length < 24)
                    throw new InvalidOperationException(AdminTokenVariable + " must be at least 24 characters.");
            }

            return new WorldOptions
            {
                ListenAddress = address,
                Port = port,
                StatusPort = status < 0 ? 0 : status,
                LeaseTtl = TimeSpan.FromSeconds(WorldSettings.ResolveLeaseTtlSeconds(settings)),
                LeaseGrace = TimeSpan.FromSeconds(WorldSettings.ResolveLeaseGraceSeconds(settings)),
                Heartbeat = TimeSpan.FromMilliseconds(WorldSettings.ResolveHeartbeatMilliseconds(settings)),
                HandoffLifetime = TimeSpan.FromSeconds(ResolveHandoffSeconds()),
                Token = token,
                AdminPort = adminPort,
                AdminAddress = adminAddress,
                AdminToken = adminToken,
                ZoneHosts = ValidateHosts(settings?.ZoneHosts)
            };
        }

        /// <summary>Every host the admin API may start zones on, checked once at startup so a launch never meets a bad entry.</summary>
        static IReadOnlyList<ZoneHostSettings> ValidateHosts(List<ZoneHostSettings>? hosts)
        {
            if (hosts == null || hosts.Count == 0) return Array.Empty<ZoneHostSettings>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ZoneHostSettings host in hosts)
            {
                string name = host.Name?.Trim() ?? string.Empty;
                if (!HostName.IsMatch(name) || !names.Add(name))
                    throw new InvalidOperationException("Every World ZoneHost needs a unique Name of 1-32 letters, digits, '.', '_' or '-'.");
                if (host.FirstPort < 1 || host.LastPort > 65535 || host.FirstPort > host.LastPort)
                    throw new InvalidOperationException("ZoneHost " + name + ": FirstPort..LastPort must be a range within 1-65535.");
                if (string.IsNullOrWhiteSpace(host.Command))
                    throw new InvalidOperationException("ZoneHost " + name + ": Command is required.");
                if (!string.IsNullOrWhiteSpace(host.Address) && !IPAddress.TryParse(host.Address.Trim(), out _))
                    throw new InvalidOperationException("ZoneHost " + name + ": Address must be an IP address.");
                if (host.FirstDebugMcpPort < 0 || host.FirstDebugMcpPort + (host.LastPort - host.FirstPort) > 65535)
                    throw new InvalidOperationException("ZoneHost " + name + ": FirstDebugMcpPort leaves the port range.");
                host.Name = name;
                host.Address = host.Address?.Trim() ?? string.Empty;
            }

            return hosts.ToArray();
        }

        /// <summary>The same knob and bounds the file-based handoff store used.</summary>
        static int ResolveHandoffSeconds()
            => int.TryParse(Environment.GetEnvironmentVariable("AO_REBIRTH_LOGIN_HANDOFF_TIMEOUT_SECONDS"),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) && seconds >= 5 && seconds <= 120
                ? seconds
                : 30;
    }
}
