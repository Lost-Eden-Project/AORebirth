namespace ZoneEngine_New.Core.Modules
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    using ZoneEngine_New.Core.Logging;

    /// <summary>
    /// The operator's non-interactive reload trigger, polled once a second while Modules.json HotReload is enabled. The
    /// request file (HotReloadSettings.ResolveRequestFile) holds key=value lines: <c>id</c> (required), <c>source</c>
    /// (folder with the new module.json and assemblies; default the module's own folder), <c>request</c> (echoed back) and
    /// <c>dryrun=1</c>. It is consumed on read. The answer is one line, "&lt;request&gt; &lt;ModuleReloadResult.Describe&gt;",
    /// written atomically to "&lt;request file&gt;.result". Write the request atomically too (a temporary file, then rename).
    /// </summary>
    public sealed class ModuleReloadRequests : IDisposable
    {
        private readonly ZoneModuleHost _host;
        private readonly IZoneLogger _logger;
        private readonly string _requestFile;
        private readonly CancellationTokenSource _stop = new();

        private ModuleReloadRequests(ZoneModuleHost host, IZoneLogger logger, string requestFile)
        {
            _host = host;
            _logger = logger;
            _requestFile = requestFile;
        }

        /// <summary>Starts polling, or returns null while hot reload is off.</summary>
        public static ModuleReloadRequests? Start(ZoneModuleHost host, IZoneLogger logger)
        {
            ArgumentNullException.ThrowIfNull(host);
            ArgumentNullException.ThrowIfNull(logger);
            if (!host.HotReload.Enabled)
                return null;
            string requestFile;
            try
            {
                // Whoever can write this file decides what the zone reloads: its folder must be this account's alone.
                requestFile = host.HotReload.ResolveRequestFile();
                HotReloadSettings.EnsurePrivateDirectory(Path.GetDirectoryName(requestFile)!);
            }
            catch (Exception exception)
            {
                logger.Error("Module reload requests are off: " + exception.Message);
                return null;
            }

            var requests = new ModuleReloadRequests(host, logger, requestFile);
            // A request left from before this process is not for it.
            try
            {
                if (File.Exists(requests._requestFile))
                    File.Delete(requests._requestFile);
            }
            catch (Exception exception)
            {
                logger.Warn("Module reload request file " + requests._requestFile + " could not be cleared: " + exception.GetType().Name);
            }

            logger.Info("Module reload requests: watching " + requests._requestFile);
            _ = Task.Run(requests.PollAsync);
            return requests;
        }

        private async Task PollAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await HandleOnceAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        _logger.Error(exception, "Module reload request failed.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task HandleOnceAsync()
        {
            if (!File.Exists(_requestFile))
                return;
            string text;
            try
            {
                text = await File.ReadAllTextAsync(_requestFile).ConfigureAwait(false);
                File.Delete(_requestFile);
            }
            catch (IOException)
            {
                return; // Still being written; the next poll reads it.
            }

            Dictionary<string, string> values = Parse(text);
            string request = values.TryGetValue("request", out string? given) && given.Length > 0 ? given : "-";
            string line;
            if (!values.TryGetValue("id", out string? id) || id.Length == 0)
            {
                line = "refused:bad_request no id=";
            }
            else
            {
                values.TryGetValue("source", out string? source);
                bool dryRun = values.TryGetValue("dryrun", out string? dry) && dry is "1" or "true" or "yes";
                ModuleReloadResult result = await _host.ReloadAsync(id, string.IsNullOrWhiteSpace(source) ? null : source, dryRun,
                    "request-file").ConfigureAwait(false);
                line = result.Describe();
            }

            string resultFile = _requestFile + ".result";
            string temporary = resultFile + ".tmp";
            await File.WriteAllTextAsync(temporary, request + " " + line + "\n").ConfigureAwait(false);
            File.Move(temporary, resultFile, overwrite: true);
        }

        private static Dictionary<string, string> Parse(string text)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in text.Split('\n'))
            {
                string row = raw.Trim();
                int equals = row.IndexOf('=');
                if (row.Length == 0 || row[0] == '#' || equals <= 0)
                    continue;
                values[row[..equals].Trim()] = row[(equals + 1)..].Trim();
            }

            return values;
        }

        public void Dispose() => _stop.Cancel();
    }
}
