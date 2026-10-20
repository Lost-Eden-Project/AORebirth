namespace WorldEngine
{
    using System;
    using System.IO;
    using System.Net;
    using System.Runtime.InteropServices;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Server.Kestrel.Core;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;

    using Utility.Config;

    using WorldEngine.Services;
    using WorldEngine.State;

    public static class Program
    {
        static readonly ManualResetEventSlim Stop = new(false);

        static int Main(string[] args)
        {
            using PosixSignalRegistration termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Terminate);
            using PosixSignalRegistration hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, Terminate);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; Stop.Set(); };
            WorldOptions options;
            try
            {
                options = WorldOptions.FromConfig(ConfigReadWrite.Instance.CurrentConfig);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("WORLDENGINE_STARTUP_FAILED " + exception.Message);
                return 1;
            }

            if (HasArgument(args, "--validate-startup"))
            {
                Console.WriteLine("WORLDENGINE_STARTUP_VALIDATION_OK");
                return 0;
            }

            WebApplication app = Build(options);
            try
            {
                app.StartAsync().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("WORLDENGINE_STARTUP_FAILED type=" + exception.GetType().Name + " " + exception.Message);
                return 1;
            }

            WorldState state = app.Services.GetRequiredService<WorldState>();
            ILogger log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WorldEngine");
            log.LogInformation("World listening grpc=http://{Address}:{Port} status={Status} epoch={Epoch} token={Token} recovery={Recovery}s",
                options.ListenAddress, options.Port,
                options.StatusPort > 0 ? "http://" + options.ListenAddress + ":" + options.StatusPort + "/status" : "off",
                state.Epoch, options.Token.Length > 0 ? "set" : "none", (int)options.RecoveryWindow.TotalSeconds);
            if (options.Token.Length == 0)
                log.LogWarning("No " + AORebirth.World.Contracts.WorldChannel.TokenVariable + " set: any process on this machine can act as a zone. "
                    + "Set the same token for World and every engine before running for players.");
            NotifyService("READY=1\nSTATUS=WorldEngine ready");
            Console.WriteLine("WORLDENGINE_READY");

            StartShutdownFileWatcher(args);
            if (HasArgument(args, "/headless") || HasArgument(args, "--headless") || Console.IsInputRedirected)
                Stop.Wait();
            else
                CommandLoop();

            NotifyService("STOPPING=1");
            app.StopAsync().GetAwaiter().GetResult();
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return 0;
        }

        static WebApplication Build(WorldOptions options)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(),
                ContentRootPath = AppContext.BaseDirectory,
                ApplicationName = "WorldEngine"
            });
            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole(console =>
            {
                console.SingleLine = true;
                console.TimestampFormat = "HH:mm:ss.fff ";
            });
            builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
            builder.Logging.AddFilter("Grpc", LogLevel.Warning);
            builder.Services.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = true);
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.Listen(options.ListenAddress, options.Port, listen => listen.Protocols = HttpProtocols.Http2);
                if (options.StatusPort > 0)
                    kestrel.Listen(options.ListenAddress, options.StatusPort, listen => listen.Protocols = HttpProtocols.Http1);
                kestrel.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(10);
                kestrel.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(5);
            });

            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton<WorldState>();
            builder.Services.AddSingleton<WorldEngine.Teams.TeamRegistry>();
            builder.Services.AddSingleton<TokenInterceptor>();
            builder.Services.AddHostedService<SweepService>();
            builder.Services.AddGrpc(grpc =>
            {
                grpc.Interceptors.Add<TokenInterceptor>();
                grpc.MaxReceiveMessageSize = 4 * 1024 * 1024;
            });

            WebApplication app = builder.Build();
            app.MapGrpcService<RegistryService>();
            app.MapGrpcService<SessionService>();
            app.MapGrpcService<LeasesService>();
            app.MapGrpcService<PlayfieldsService>();
            app.MapGrpcService<DirectoryService>();
            app.MapGrpcService<TeamsService>();
            app.MapGrpcService<ChatService>();
            if (options.StatusPort > 0)
            {
                var json = new JsonSerializerOptions { WriteIndented = true };
                app.MapGet("/status", (HttpContext http, WorldState state) =>
                {
                    // The status page shows character names: loopback callers only.
                    IPAddress? remote = http.Connection.RemoteIpAddress;
                    if (remote == null || !IPAddress.IsLoopback(remote)) return Results.StatusCode(403);
                    return Results.Json(new { world = state.Snapshot(), teams = http.RequestServices
                        .GetRequiredService<WorldEngine.Teams.TeamRegistry>().Snapshot() }, json);
                });
            }

            return app;
        }

        static void CommandLoop()
        {
            Console.WriteLine("Commands: exit, quit");
            while (!Stop.IsSet)
            {
                string? line = Console.ReadLine();
                if (line == null) { Stop.Wait(); return; }
                string command = line.Trim().ToLowerInvariant();
                if (command is "exit" or "quit") return;
                if (command.Length > 0) Console.WriteLine("Commands: exit, quit");
            }
        }

        static void StartShutdownFileWatcher(string[] args)
        {
            string? file = ArgumentValue(args, "/shutdown-file") ?? ArgumentValue(args, "--shutdown-file");
            if (string.IsNullOrWhiteSpace(file)) return;
            _ = Task.Run(async () =>
            {
                while (!Stop.IsSet)
                {
                    if (File.Exists(file))
                    {
                        Console.WriteLine("Shutdown file requested.");
                        Stop.Set();
                        // The interactive loop may be blocked on the console.
                        Environment.Exit(0);
                    }

                    await Task.Delay(1000).ConfigureAwait(false);
                }
            });
        }

        static void Terminate(PosixSignalContext context)
        {
            context.Cancel = true;
            Stop.Set();
        }

        /// <summary>systemd readiness when running under a notify unit; a no-op elsewhere.</summary>
        static void NotifyService(string state)
        {
            string? socket = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
            if (string.IsNullOrEmpty(socket) || OperatingSystem.IsWindows()) return;
            try
            {
                using var client = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
                    System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Unspecified);
                string path = socket[0] == '@' ? "\0" + socket.Substring(1) : socket;
                client.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
                client.Send(System.Text.Encoding.UTF8.GetBytes(state));
            }
            catch (Exception)
            {
            }
        }

        static bool HasArgument(string[] args, string argument)
            => Array.Exists(args, a => string.Equals(a, argument, StringComparison.OrdinalIgnoreCase));

        static string? ArgumentValue(string[] args, string argument)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], argument, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }
    }

    /// <summary>Expires silent instances, tickets and deletes.</summary>
    sealed class SweepService(WorldState state, WorldEngine.Teams.TeamRegistry teams, ILogger<SweepService> log) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    state.Sweep();
                    teams.MembersGone(state.DrainGone());
                }
                catch (Exception exception) { log.LogError(exception, "World sweep failed"); }
            }
        }
    }
}
