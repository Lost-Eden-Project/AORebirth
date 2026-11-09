namespace WorldEngine.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;

    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Routing;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;

    using Utility.Config;

    using WorldEngine.State;

    /// <summary>
    /// Starts zone engines for the admin API with the command configured for each host (World ZoneHosts). World only
    /// starts the process: the new zone joins by registering like any other, so a launch is finished when it shows up
    /// in the zone list. Only validated values (instance name, ports) are put into the configured command.
    /// </summary>
    public sealed class ZoneLauncher(WorldOptions options, WorldState state, ILogger<ZoneLauncher> log)
    {
        static readonly Regex InstanceName = new("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);
        const int MaxOutput = 4096;
        const int MaxRecords = 64;
        static readonly TimeSpan RegisterWithin = TimeSpan.FromMinutes(5);
        static readonly TimeSpan KeepRecords = TimeSpan.FromHours(1);

        readonly object _gate = new();
        readonly Dictionary<string, Launch> _launches = new(StringComparer.OrdinalIgnoreCase);

        public sealed class Launch
        {
            public required string Instance { get; init; }

            public required string Host { get; init; }

            public required int Port { get; init; }

            public int? DebugMcpPort { get; init; }

            public required DateTime StartedUtc { get; init; }

            public required string RequestedBy { get; init; }

            /// <summary>starting, started, failed, exited or timed_out; see <see cref="ZoneLauncher.Status"/> for the reported one.</summary>
            public string State { get; set; } = "starting";

            public int? ExitCode { get; set; }

            public int? ProcessId { get; set; }

            public string Output { get; set; } = string.Empty;

            public string? Error { get; set; }
        }

        public readonly record struct LaunchResult(Launch? Launch, int StatusCode, string? Error);

        public IReadOnlyList<object> Launches()
        {
            lock (_gate)
                return _launches.Values.OrderBy(l => l.StartedUtc).Select(Describe).ToList();
        }

        public object? Find(string instance)
        {
            lock (_gate)
                return _launches.TryGetValue(instance, out Launch? launch) ? Describe(launch) : null;
        }

        public LaunchResult Start(string? hostName, int? requestedPort, string? requestedInstance, string requestedBy)
        {
            if (options.ZoneHosts.Count == 0)
                return Refused(StatusCodes.Status409Conflict, "no ZoneHosts are configured in World's <World><ZoneHosts>");
            ZoneHostSettings? host = string.IsNullOrWhiteSpace(hostName)
                ? options.ZoneHosts.Count == 1 ? options.ZoneHosts[0] : null
                : options.ZoneHosts.FirstOrDefault(h => string.Equals(h.Name, hostName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (host == null)
                return Refused(StatusCodes.Status400BadRequest, string.IsNullOrWhiteSpace(hostName)
                    ? "host is required: " + string.Join(", ", options.ZoneHosts.Select(h => h.Name))
                    : "unknown host " + hostName);

            Launch launch;
            lock (_gate)
            {
                Purge();
                HashSet<int> taken = state.PortsInUse(host.Address.Length > 0 ? host.Address : null);
                foreach (Launch pending in _launches.Values)
                    if (string.Equals(pending.Host, host.Name, StringComparison.OrdinalIgnoreCase) && IsPending(pending)) taken.Add(pending.Port);

                int port;
                if (requestedPort is int asked)
                {
                    if (asked < host.FirstPort || asked > host.LastPort)
                        return Refused(StatusCodes.Status400BadRequest, "port " + asked.ToString(CultureInfo.InvariantCulture) + " is outside "
                            + host.Name + "'s range " + host.FirstPort.ToString(CultureInfo.InvariantCulture) + "-"
                            + host.LastPort.ToString(CultureInfo.InvariantCulture));
                    if (taken.Contains(asked))
                        return Refused(StatusCodes.Status409Conflict, "port " + asked.ToString(CultureInfo.InvariantCulture) + " on " + host.Name + " is in use");
                    port = asked;
                }
                else
                {
                    port = Enumerable.Range(host.FirstPort, host.LastPort - host.FirstPort + 1).FirstOrDefault(p => !taken.Contains(p));
                    if (port == 0) return Refused(StatusCodes.Status409Conflict, "every port in " + host.Name + "'s range is in use");
                }

                string name = string.IsNullOrWhiteSpace(requestedInstance)
                    ? host.Name + "-" + port.ToString(CultureInfo.InvariantCulture)
                    : requestedInstance.Trim();
                if (!InstanceName.IsMatch(name))
                    return Refused(StatusCodes.Status400BadRequest, "instance must be 1-64 letters, digits, '.', '_' or '-'");
                if (state.Zone(name) != null || (_launches.TryGetValue(name, out Launch? existing) && IsPending(existing)))
                    return Refused(StatusCodes.Status409Conflict, "instance " + name + " is already running or starting");

                int? debugMcpPort = host.FirstDebugMcpPort > 0 ? host.FirstDebugMcpPort + (port - host.FirstPort) : null;
                bool wantsMcp = Uses(host.Command) || Uses(host.Arguments) || Uses(host.WorkingDirectory);
                if (wantsMcp && debugMcpPort == null)
                    return Refused(StatusCodes.Status409Conflict, "host " + host.Name + " uses {debugMcpPort} but has no FirstDebugMcpPort");

                if (_launches.Count >= MaxRecords)
                    return Refused(StatusCodes.Status429TooManyRequests, "too many launches in the last hour");
                launch = new Launch
                {
                    Instance = name,
                    Host = host.Name,
                    Port = port,
                    DebugMcpPort = debugMcpPort,
                    StartedUtc = DateTime.UtcNow,
                    RequestedBy = requestedBy
                };
                _launches[name] = launch;
            }

            log.LogWarning("Admin launch of zone {Instance} on {Host} port {Port} by {By}", launch.Instance, host.Name, launch.Port, requestedBy);
            Run(host, launch);
            return new LaunchResult(launch, StatusCodes.Status202Accepted, null);
        }

        static bool Uses(string? template) => template?.Contains("{debugMcpPort}", StringComparison.Ordinal) == true;

        static string Fill(string? template, Launch launch)
            => (template ?? string.Empty)
                .Replace("{instance}", launch.Instance, StringComparison.Ordinal)
                .Replace("{port}", launch.Port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{debugMcpPort}", launch.DebugMcpPort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.Ordinal);

        void Run(ZoneHostSettings host, Launch launch)
        {
            var info = new ProcessStartInfo(Fill(host.Command, launch).Trim())
            {
                Arguments = Fill(host.Arguments, launch),
                WorkingDirectory = Fill(host.WorkingDirectory, launch).Trim(),
                // A detached zone engine on Windows gets its own console window, as the start scripts give it.
                UseShellExecute = host.Detached && OperatingSystem.IsWindows()
            };
            if (!host.Detached)
            {
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.CreateNoWindow = true;
            }

            Process process;
            try
            {
                process = Process.Start(info) ?? throw new InvalidOperationException("the command did not start a process");
            }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    launch.State = "failed";
                    launch.Error = exception.Message;
                }

                log.LogError("Admin launch of zone {Instance} failed to start: {Error}", launch.Instance, exception.Message);
                return;
            }

            lock (_gate)
            {
                launch.ProcessId = SafeId(process);
                launch.State = host.Detached ? "started" : "starting";
            }

            TimeSpan timeout = TimeSpan.FromSeconds(host.TimeoutSeconds > 0 ? host.TimeoutSeconds : 60);
            _ = host.Detached ? WatchDetachedAsync(process, launch) : WaitForCommandAsync(process, launch, timeout);
        }

        /// <summary>The zone engine itself: it is done when it exits, which before registering means it failed.</summary>
        async Task WatchDetachedAsync(Process process, Launch launch)
        {
            using (process)
            {
                try { await process.WaitForExitAsync().ConfigureAwait(false); }
                catch (Exception) { return; }
                lock (_gate)
                {
                    launch.ExitCode = SafeExitCode(process);
                    launch.State = "exited";
                }
            }
        }

        /// <summary>A command that starts the zone elsewhere (systemctl, ssh) and exits: its exit code is the answer.</summary>
        async Task WaitForCommandAsync(Process process, Launch launch, TimeSpan timeout)
        {
            using (process)
            {
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                bool timedOut = false;
                using (var limit = new CancellationTokenSource(timeout))
                {
                    try { await process.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException)
                    {
                        timedOut = true;
                        try { process.Kill(entireProcessTree: true); }
                        catch (Exception) { }
                    }
                }

                // A child that inherited the pipes can keep them open after the command itself exited.
                string output = string.Empty;
                try
                {
                    await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    output = (stdout.Result + stderr.Result).Trim();
                }
                catch (Exception) { }

                lock (_gate)
                {
                    launch.Output = output.Length > MaxOutput ? output.Substring(output.Length - MaxOutput) : output;
                    if (timedOut)
                    {
                        launch.State = "timed_out";
                    }
                    else
                    {
                        launch.ExitCode = SafeExitCode(process);
                        launch.State = launch.ExitCode == 0 ? "started" : "failed";
                    }
                }

                if (launch.State != "started")
                    log.LogError("Admin launch of zone {Instance} {State} exit={Exit}: {Output}", launch.Instance, launch.State,
                        launch.ExitCode, launch.Output);
            }
        }

        bool IsPending(Launch launch)
            => launch.State is "starting" or "started" && DateTime.UtcNow - launch.StartedUtc < RegisterWithin
                && state.Zone(launch.Instance) == null;

        void Purge()
        {
            foreach (Launch old in _launches.Values.Where(l => DateTime.UtcNow - l.StartedUtc > KeepRecords).ToList())
                _launches.Remove(old.Instance);
        }

        /// <summary>What a caller sees: registered once the zone is in World's list, not_registered when it never came.</summary>
        string Status(Launch launch)
        {
            WorldState.ZoneView? zone = state.Zone(launch.Instance);
            if (zone is { Alive: true }) return "registered";
            if (launch.State is "starting" or "started" && DateTime.UtcNow - launch.StartedUtc >= RegisterWithin) return "not_registered";
            return launch.State;
        }

        object Describe(Launch launch) => new
        {
            instance = launch.Instance,
            host = launch.Host,
            port = launch.Port,
            debugMcpPort = launch.DebugMcpPort,
            status = Status(launch),
            startedUtc = launch.StartedUtc,
            requestedBy = launch.RequestedBy,
            processId = launch.ProcessId,
            exitCode = launch.ExitCode,
            error = launch.Error,
            output = launch.Output
        };

        static int? SafeId(Process process)
        {
            try { return process.Id; }
            catch (Exception) { return null; }
        }

        static int? SafeExitCode(Process process)
        {
            try { return process.ExitCode; }
            catch (Exception) { return null; }
        }

        static LaunchResult Refused(int status, string error) => new(null, status, error);
    }

    /// <summary>
    /// World's admin API: list, add, drain and remove zone engines. HTTP/1.1 and JSON on its own port
    /// (<c>&lt;World&gt;&lt;AdminPort&gt;</c>), bound to <c>AdminListenIP</c> (for example this host's WireGuard address), and
    /// every request carries <c>Authorization: Bearer $AO_REBIRTH_WORLD_ADMIN_TOKEN</c>. Plain HTTP: keep it on a private
    /// network or tunnel.
    /// </summary>
    public static class AdminApi
    {
        public sealed record AddZoneRequest(string? Host, int? Port, string? Instance);

        public const int DefaultShutdownSeconds = 60;

        const int MaxMessageLength = 200;

        public static void Map(WebApplication app, WorldOptions options)
        {
            byte[] token = Encoding.UTF8.GetBytes(options.AdminToken);
            ILogger log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WorldAdmin");
            RouteGroupBuilder admin = app.MapGroup("/admin");
            admin.AddEndpointFilter(async (context, next) =>
            {
                HttpContext http = context.HttpContext;
                // Only on the admin listener: never through the gRPC or status ports, whatever the Host header says.
                if (http.Connection.LocalPort != options.AdminPort) return Results.NotFound();
                if (token.Length > 0)
                {
                    const string scheme = "Bearer ";
                    string header = http.Request.Headers.Authorization.ToString();
                    byte[] presented = Encoding.UTF8.GetBytes(header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
                        ? header.Substring(scheme.Length).Trim()
                        : string.Empty);
                    if (!CryptographicOperations.FixedTimeEquals(presented, token))
                    {
                        log.LogWarning("Admin API refused {Method} {Path} from {Remote}: bad or missing token", http.Request.Method,
                            http.Request.Path, http.Connection.RemoteIpAddress);
                        return Results.Unauthorized();
                    }
                }

                return await next(context).ConfigureAwait(false);
            });

            admin.MapGet("/zones", (WorldState state, ZoneLauncher launcher) => Results.Json(new
            {
                zones = state.Zones(),
                launches = launcher.Launches(),
                hosts = options.ZoneHosts.Select(h => new { name = h.Name, address = h.Address, firstPort = h.FirstPort, lastPort = h.LastPort })
            }));

            admin.MapGet("/zones/{name}", (string name, WorldState state, ZoneLauncher launcher) =>
            {
                WorldState.ZoneView? zone = state.Zone(name);
                object? launch = launcher.Find(name);
                return zone == null && launch == null
                    ? Error(StatusCodes.Status404NotFound, "no zone or launch named " + name)
                    : Results.Json(new { zone, launch });
            });

            admin.MapPost("/zones", (AddZoneRequest? request, HttpContext http, ZoneLauncher launcher) =>
            {
                ZoneLauncher.LaunchResult result = launcher.Start(request?.Host, request?.Port, request?.Instance, Caller(http));
                if (result.Launch == null) return Error(result.StatusCode, result.Error ?? "refused");
                return Results.Json(launcher.Find(result.Launch.Instance), statusCode: StatusCodes.Status202Accepted);
            });

            admin.MapPost("/zones/{name}/drain", (string name, HttpContext http, WorldState state)
                => Outcome(state.SetDraining(name, draining: true, Caller(http)), name, state));

            // Back in rotation; also cancels a shutdown World asked for.
            admin.MapPost("/zones/{name}/resume", (string name, HttpContext http, WorldState state) =>
            {
                WorldState.ZoneView? zone = state.Zone(name);
                if (zone == null) return Error(StatusCodes.Status404NotFound, "no zone named " + name);
                return Outcome(zone.ShutdownDueUtc != null
                    ? state.CancelShutdown(name, Caller(http))
                    : state.SetDraining(name, draining: false, Caller(http)), name, state);
            });

            // Removes a zone: out of rotation now, then a countdown to the players, a save and a clean exit.
            admin.MapDelete("/zones/{name}", (string name, int? seconds, string? message, HttpContext http, WorldState state) =>
            {
                int countdown = seconds ?? DefaultShutdownSeconds;
                if (countdown < 1 || countdown > 24 * 60 * 60)
                    return Error(StatusCodes.Status400BadRequest, "seconds must be between 1 and 86400");
                string text = (message ?? string.Empty).Trim();
                if (text.Length > MaxMessageLength)
                    return Error(StatusCodes.Status400BadRequest, "message is longer than " + MaxMessageLength.ToString(CultureInfo.InvariantCulture) + " characters");
                WorldState.AdminOutcome outcome = state.RequestShutdown(name, countdown, text, Caller(http));
                return outcome == WorldState.AdminOutcome.Done
                    ? Results.Json(state.Zone(name), statusCode: StatusCodes.Status202Accepted)
                    : Outcome(outcome, name, state);
            });
        }

        static string Caller(HttpContext http)
            => "admin@" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        static IResult Outcome(WorldState.AdminOutcome outcome, string name, WorldState state) => outcome switch
        {
            WorldState.AdminOutcome.Done => Results.Json(state.Zone(name)),
            WorldState.AdminOutcome.NotConnected => Error(StatusCodes.Status409Conflict, name + " is not connected to World"),
            _ => Error(StatusCodes.Status404NotFound, "no zone named " + name)
        };

        static IResult Error(int status, string error) => Results.Json(new { error }, statusCode: status);
    }
}
