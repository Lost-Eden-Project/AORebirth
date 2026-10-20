namespace AORebirth.World.Contracts
{
    using System;
    using System.Net.Http;
    using System.Threading;

    using Grpc.Core;
    using Grpc.Net.Client;

    using AORebirth.World.Contracts.V1;

    /// <summary>
    /// One HTTP/2 connection to World plus the typed clients over it. Every call carries the shared token and, once a
    /// registry stream is accepted, the caller's session so World can tell a live instance from a stale process.
    /// </summary>
    public sealed class WorldChannel : IDisposable
    {
        public const string TokenHeader = "x-aorebirth-world-token";

        public const string SessionHeader = "x-aorebirth-session";

        public const string RoleHeader = "x-aorebirth-role";

        public const string TokenVariable = "AO_REBIRTH_WORLD_TOKEN";

        readonly GrpcChannel _channel;
        readonly string _token;
        readonly string _role;
        string _session = string.Empty;

        public WorldChannel(string url, string role)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("World url required.", nameof(url));
            _token = Environment.GetEnvironmentVariable(TokenVariable) ?? string.Empty;
            _role = role ?? string.Empty;
            var handler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay = TimeSpan.FromSeconds(10),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                ConnectTimeout = TimeSpan.FromSeconds(3)
            };
            _channel = GrpcChannel.ForAddress(url, new GrpcChannelOptions
            {
                HttpHandler = handler,
                MaxReceiveMessageSize = 4 * 1024 * 1024
            });
            Url = url;
            Registry = new Registry.RegistryClient(_channel);
            Session = new Session.SessionClient(_channel);
            Leases = new Leases.LeasesClient(_channel);
            Playfields = new Playfields.PlayfieldsClient(_channel);
            Directory = new Directory.DirectoryClient(_channel);
            Teams = new Teams.TeamsClient(_channel);
            Chat = new Chat.ChatClient(_channel);
        }

        public string Url { get; }

        public Registry.RegistryClient Registry { get; }

        public Session.SessionClient Session { get; }

        public Leases.LeasesClient Leases { get; }

        public Playfields.PlayfieldsClient Playfields { get; }

        public Directory.DirectoryClient Directory { get; }

        public Teams.TeamsClient Teams { get; }

        public Chat.ChatClient Chat { get; }

        /// <summary>The registry session World accepted; sent on every later call.</summary>
        public string RegistrySession
        {
            get => Volatile.Read(ref _session);
            set => Volatile.Write(ref _session, value ?? string.Empty);
        }

        /// <summary>Headers for a call: token, role, session.</summary>
        public Metadata Headers()
        {
            var headers = new Metadata();
            if (_token.Length > 0) headers.Add(TokenHeader, _token);
            if (_role.Length > 0) headers.Add(RoleHeader, _role);
            string session = RegistrySession;
            if (session.Length > 0) headers.Add(SessionHeader, session);
            return headers;
        }

        /// <summary>Call options with headers and a deadline.</summary>
        public CallOptions Call(TimeSpan timeout, CancellationToken cancellation = default)
            => new CallOptions(Headers(), DateTime.UtcNow + timeout, cancellation);

        /// <summary>Call options for a long-lived stream: headers, no deadline.</summary>
        public CallOptions Stream(CancellationToken cancellation)
            => new CallOptions(Headers(), null, cancellation);

        public void Dispose() => _channel.Dispose();
    }
}
