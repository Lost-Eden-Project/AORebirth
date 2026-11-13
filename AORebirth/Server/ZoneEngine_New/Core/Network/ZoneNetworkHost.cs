namespace ZoneEngine_New.Core.Network
{
    using System;
    using System.Collections.Concurrent;
    using System.Globalization;
    using System.Net;
    using System.Net.Sockets;
    using System.Threading;
    using System.Threading.Tasks;

    using Utility.Config;

    using AORebirth.Modules.Network;

    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Modules;

    public sealed class ZoneNetworkHost : IAsyncDisposable
    {
        private readonly ZoneMessageCodec _codec;
        private readonly ZoneMessageDispatcher _dispatcher;
        private readonly IZoneLogger _logger;
        private readonly ZoneModuleHost? _modules;
        // Bounds on what unauthenticated peers can hold open. A real client logs in within moments of connecting.
        private const int MaxSessions = 8192;
        private static readonly int MaxSessionsPerAddress = ResolveMaxSessionsPerAddress(32);
        private static readonly TimeSpan LoginDeadline = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);

        private readonly ConcurrentDictionary<Guid, ZoneSession> _sessions = new();
        private readonly ConcurrentDictionary<Guid, long> _acceptedAt = new();
        private Task? _sweepLoop;
        private readonly CancellationTokenSource _cts = new();
        private Socket? _listener;
        private Task? _acceptLoop;
        private volatile bool _started;
        private bool _disposed;

        public ZoneNetworkHost(
            ZoneMessageCodec codec,
            ZoneMessageDispatcher dispatcher,
            IZoneLogger logger,
            ZoneModuleHost? modules = null)
        {
            ArgumentNullException.ThrowIfNull(codec);
            ArgumentNullException.ThrowIfNull(dispatcher);
            ArgumentNullException.ThrowIfNull(logger);

            _codec = codec;
            _dispatcher = dispatcher;
            _logger = logger;
            _modules = modules;
        }

        public void Start()
        {
            if (_started)
            {
                return;
            }

            Config config = ConfigReadWrite.Instance.CurrentConfig;
            int port = ZoneEngine_New.Core.World.ZoneInstanceOptions.CurrentOrNull?.Port ?? config.ZonePort;
            if (port < 1 || port > 65535)
            {
                throw new InvalidOperationException("ZonePort must be between 1 and 65535.");
            }
            // ZoneIP is the advertised redirect address, never a bind-policy override.
            IPAddress address = Utility.Network.EngineBindPolicy.ResolveFromEnvironment().Address;

            _listener = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _listener.Bind(new IPEndPoint(address, port));
            _listener.Listen(backlog: 256);
            _started = true;

            _acceptLoop = AcceptLoopAsync(_cts.Token);
            _sweepLoop = SweepLoopAsync(_cts.Token);

            _logger.Info(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "ZoneNetworkHost listening on {0}:{1}",
                    address,
                    port));
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _cts.CancelAsync().ConfigureAwait(false);

            if (_listener != null)
            {
                try
                {
                    _listener.Close();
                }
                catch
                {
                }
            }

            if (_acceptLoop != null)
            {
                try
                {
                    await _acceptLoop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (_sweepLoop != null)
            {
                try
                {
                    await _sweepLoop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            foreach (ZoneSession session in _sessions.Values)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            _sessions.Clear();
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            Socket listener = _listener ?? throw new InvalidOperationException("Listener not started.");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    Socket socket = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                    if (!HasCapacityFor(socket))
                    {
                        // Refused before a session and its buffers exist.
                        try { socket.Close(0); }
                        catch { }
                        continue;
                    }

                    Guid sessionId = Guid.NewGuid();
                    if (!TryCreateConnectionFilter(sessionId, socket, out IConnectionFilter? filter))
                        continue;

                    ZoneSession session = new ZoneSession(
                        sessionId,
                        socket,
                        _codec,
                        _dispatcher,
                        _logger,
                        filter,
                        _modules?.MessageObservers);

                    _acceptedAt[sessionId] = Environment.TickCount64;
                    _sessions[sessionId] = session;

                    _logger.Info(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Accepted zone connection from {0}",
                            session.RemoteEndPoint()));

                    _ = RunSessionAsync(session, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested || _disposed)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, "AcceptLoop error.");
                }
            }
        }

        /// <summary>A module connection filter for the new socket; a provider failure refuses only this connection.</summary>
        private bool TryCreateConnectionFilter(Guid sessionId, Socket socket, out IConnectionFilter? filter)
        {
            filter = null;
            if (_modules == null)
                return true;

            EndPoint? remote = null;
            try
            {
                remote = socket.RemoteEndPoint;
                filter = _modules.CreateConnectionFilter(sessionId, remote);
                return true;
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Module connection filter refused connection from " + (remote?.ToString() ?? "unknown") + ".");
                try { socket.Close(); }
                catch { }
                return false;
            }
        }

        private async Task RunSessionAsync(ZoneSession session, CancellationToken cancellationToken)
        {
            try
            {
                await session.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sessions.TryRemove(session.Id, out _);
                _acceptedAt.TryRemove(session.Id, out _);
            }
        }

        private bool HasCapacityFor(Socket socket)
        {
            if (_sessions.Count >= MaxSessions)
                return false;

            IPAddress? address;
            try
            {
                address = (socket.RemoteEndPoint as IPEndPoint)?.Address;
            }
            catch (Exception)
            {
                return false;
            }

            if (address == null || IPAddress.IsLoopback(address) || MaxSessionsPerAddress == 0)
                return true;

            int sameAddress = 0;
            foreach (ZoneSession existing in _sessions.Values)
            {
                if (address.Equals(existing.RemoteAddress()) && ++sameAddress >= MaxSessionsPerAddress)
                    return false;
            }

            return true;
        }

        // The same operator override Login and Chat honour (0 = no per-address limit, for a proxy or NAT in front).
        private static int ResolveMaxSessionsPerAddress(int defaultLimit)
        {
            string? configured = Environment.GetEnvironmentVariable("AO_REBIRTH_MAX_CONNECTIONS_PER_ADDRESS");
            return !string.IsNullOrWhiteSpace(configured) && int.TryParse(configured.Trim(), out int limit) && limit >= 0
                ? limit
                : defaultLimit;
        }

        /// <summary>Closes connections that never got as far as a zone login.</summary>
        private async Task SweepLoopAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(SweepInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    long now = Environment.TickCount64;
                    foreach (var accepted in _acceptedAt)
                    {
                        if (now - accepted.Value < (long)LoginDeadline.TotalMilliseconds)
                            continue;

                        // Past the deadline it is either logged in (stop tracking) or idle (drop it).
                        _acceptedAt.TryRemove(accepted.Key, out _);
                        if (_sessions.TryGetValue(accepted.Key, out ZoneSession? session)
                            && session.State == SessionState.Connected)
                        {
                            _logger.Info("Closing zone connection from " + session.RemoteEndPoint() + ": no login within "
                                + (int)LoginDeadline.TotalSeconds + " s.");
                            session.Close();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Zone connection sweep stopped.");
            }
        }
    }
}
