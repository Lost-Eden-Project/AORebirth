namespace ZoneEngine_New.Core.World
{
    using System;
    using System.Globalization;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;

    using Utility.Config;

    /// <summary>
    /// Who this zone engine process is among several: its instance name, the port it serves clients on and the
    /// address it advertises. Arguments win over environment variables, which win over Config.xml, so one build can
    /// run as many instances.
    /// </summary>
    public sealed class ZoneInstanceOptions
    {
        static ZoneInstanceOptions? _current;

        ZoneInstanceOptions(string name, IPAddress advertised, int port, int? debugMcpPort)
        {
            Name = name;
            AdvertisedAddress = advertised;
            Port = port;
            DebugMcpPort = debugMcpPort;
        }

        public string Name { get; }

        /// <summary>The IPv4 address clients are redirected to (ZoneIP).</summary>
        public IPAddress AdvertisedAddress { get; }

        public int Port { get; }

        /// <summary>Overrides DebugMcp Port for this instance; null keeps the configured one.</summary>
        public int? DebugMcpPort { get; }

        public static ZoneInstanceOptions Current
            => _current ?? throw new InvalidOperationException("Zone instance options are not configured.");

        /// <summary>Null until <see cref="Configure"/> runs (tools and tests read Config.xml directly).</summary>
        public static ZoneInstanceOptions? CurrentOrNull => _current;

        public static ZoneInstanceOptions Configure(string[] args)
        {
            Config config = ConfigReadWrite.Instance.CurrentConfig
                ?? throw new InvalidOperationException("Configuration is required.");
            int port = ParsePort(Value(args, "--zone-port", "AO_REBIRTH_ZONE_PORT")) ?? config.ZonePort;
            if (port < 1 || port > ushort.MaxValue) throw new InvalidOperationException("ZonePort must be between 1 and 65535.");
            int? mcp = ParsePort(Value(args, "--debug-mcp-port", "AO_REBIRTH_DEBUG_MCP_PORT"));
            string name = Value(args, "--instance", "AO_REBIRTH_ZONE_INSTANCE")
                ?? (string.IsNullOrWhiteSpace(config.World?.InstanceName) ? null : config.World!.InstanceName.Trim())
                ?? "zone-" + port.ToString(CultureInfo.InvariantCulture);
            if (name.Length > 64 || name.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')))
                throw new InvalidOperationException("Zone instance name must be 1-64 letters, digits, '-', '_' or '.'.");
            _current = new ZoneInstanceOptions(name, ResolveAdvertised(config.ZoneIP), port, mcp);
            return _current;
        }

        static IPAddress ResolveAdvertised(string? host)
        {
            if (string.IsNullOrWhiteSpace(host)) host = "127.0.0.1";
            if (IPAddress.TryParse(host, out IPAddress? parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
                return parsed;
            return Dns.GetHostEntry(host).AddressList.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                ?? throw new InvalidOperationException("ZoneIP did not resolve to an IPv4 address.");
        }

        static string? Value(string[] args, string argument, string variable)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], argument, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(args[i + 1]))
                    return args[i + 1].Trim();
            string? environment = Environment.GetEnvironmentVariable(variable);
            return string.IsNullOrWhiteSpace(environment) ? null : environment.Trim();
        }

        static int? ParsePort(string? value)
        {
            if (value == null) return null;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) || port < 1 || port > ushort.MaxValue)
                throw new InvalidOperationException("Port '" + value + "' must be between 1 and 65535.");
            return port;
        }
    }
}
