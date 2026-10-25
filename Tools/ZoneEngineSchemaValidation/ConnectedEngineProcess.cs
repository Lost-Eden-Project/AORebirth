using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

/// <summary>Owns only an explicitly supplied binary, fixture environment, loopback listener and shutdown file.</summary>
sealed class ConnectedEngineProcess : IDisposable
{
    readonly Process process;
    readonly string shutdown;
    readonly StringBuilder output = new();
    readonly DisposableSchemaDatabase fixture;
    readonly string role;
    public int Id => process.Id;
    public string BinarySha256 { get; }
    public bool UsedProvenShutdownBaseline { get; private set; }
    public int OutputMark { get { lock (output) return output.Length; } }

    public bool HasPlayfieldAssignment(int playfield)
    {
        string marker = "Assigned playfield " + playfield.ToString(System.Globalization.CultureInfo.InvariantCulture) + " to ";
        lock (output)
            return output.ToString().Contains(marker, StringComparison.Ordinal);
    }

    public void WaitForPlayfieldAssignment(int playfield)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (HasPlayfieldAssignment(playfield))
                return;
            if (process.HasExited)
                throw new FixtureFailure("chest-world-exited-before-dungeon-assignment");
            Thread.Sleep(20);
        }
        throw new FixtureFailure("chest-world-dungeon-assignment-timeout");
    }

    public void WaitForChestPopulation(string quest, int expected)
    {
        string pattern = "Mission chests loaded quest=" + System.Text.RegularExpressions.Regex.Escape(quest) + @" count=(\d+)(?:\s|$)";
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            string text;
            lock (output) text = output.ToString();
            var match = System.Text.RegularExpressions.Regex.Match(text, pattern);
            if (match.Success)
            {
                if (int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) != expected)
                    throw new FixtureFailure("chest-instantiated-log-count-disagrees-with-durable-seal");
                return;
            }
            if (process.HasExited)
                throw new FixtureFailure("chest-engine-exited-before-population-log");
            Thread.Sleep(20);
        }
        throw new FixtureFailure("chest-instantiated-count-log-timeout");
    }

    public string ChestPlanMetrics(string quest, int expectedGenerated)
    {
        string text;
        lock (output) text = output.ToString();
        var match = System.Text.RegularExpressions.Regex.Match(text,
            "Mission chest population planned quest=" + System.Text.RegularExpressions.Regex.Escape(quest)
            + @" [^\r\n]* totalCells=(\d+) placementSeed=(-?\d+) eligibleRooms=(\d+) requested=(\d+) generated=(\d+) candidates=(\d+)");
        if (!match.Success)
            throw new FixtureFailure("chest-initial-population-plan-metrics-missing");
        if (int.Parse(match.Groups[5].Value, System.Globalization.CultureInfo.InvariantCulture) != expectedGenerated)
            throw new FixtureFailure("chest-generated-plan-count-disagrees-with-durable-seal");
        return "TOTAL_CELLS=" + match.Groups[1].Value + " PLACEMENT_SEED=" + match.Groups[2].Value
            + " ELIGIBLE_ROOMS=" + match.Groups[3].Value + " REQUESTED=" + match.Groups[4].Value
            + " GENERATED=" + match.Groups[5].Value + " CANDIDATES=" + match.Groups[6].Value;
    }

    public void WaitForChestRetirement(int containerInstance)
    {
        string marker = "Despawned lootable Chest id=" + containerInstance + " playfield=";
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            lock (output)
            {
                if (output.ToString().Contains(marker, StringComparison.Ordinal))
                    return;
            }
            if (process.HasExited)
                throw new FixtureFailure("chest-engine-exited-before-empty-retirement");
            Thread.Sleep(20);
        }
        throw new FixtureFailure("chest-empty-retirement-log-timeout");
    }

    public FixturePosition WaitForSnapshot(int after)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            string text;
            lock (output) text = output.ToString(after, output.Length - after);
            var match = System.Text.RegularExpressions.Regex.Match(text,
                @"Character snapshot character=9901 playfield=4582 pos=\(([^,]+),([^,]+),([^\)]+)\)");
            if (match.Success)
                return new FixturePosition(Parse(1), Parse(2), Parse(3));
            Thread.Sleep(20);
            float Parse(int group) => float.Parse(match.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        throw new FixtureFailure("position-runtime-snapshot-log-timeout");
    }
    public ConnectedEngineProcess(string assembly, bool login, DisposableSchemaDatabase fixture, string? gameDataRoot = null, bool world = false)
    {
        this.fixture = fixture;
        role = world ? "world" : login ? "login" : "zone";
        assembly = Path.GetFullPath(assembly);
        BinarySha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly)));
        shutdown = Path.Combine(fixture.DirectoryPath, "connected-stop-" + Guid.NewGuid().ToString("N"));
        bool managed = Path.GetExtension(assembly).Equals(".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(managed ? "dotnet" : assembly) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(assembly)! };
        FixtureEnvironment.ClearInheritedRuntimeSettings(start);
        if (world && fixture.WorldPort == 0)
            throw new FixtureFailure("connected-world-requires-fixture-owned-endpoint");
        if (managed) start.ArgumentList.Add(assembly);
        start.ArgumentList.Add(login ? "/headless" : "--headless");
        start.ArgumentList.Add(login ? "/shutdown-file" : "--shutdown-file");
        start.ArgumentList.Add(shutdown);
        start.Environment["AO_REBIRTH_MYSQL_CONNECTION"] = fixture.ConnectionString;
        start.Environment["AO_REBIRTH_CONFIG_PATH"] = fixture.ConfigPath;
        start.Environment["AO_REBIRTH_BIND_MODE"] = "Loopback";
        start.Environment["AO_REBIRTH_EXPECTED_DATABASE"] = DisposableSchemaDatabase.DatabaseName;
        start.Environment["AO_REBIRTH_REQUIRED_SQL_TYPE"] = "MySql";
        start.Environment["AO_REBIRTH_SESSION_OWNERSHIP_DIR"] = Path.Combine(fixture.DirectoryPath, "sessions");
        if (fixture.WorldPort != 0)
        {
            start.Environment["AO_REBIRTH_WORLD_TOKEN"] = fixture.WorldToken;
        }
        if (gameDataRoot != null)
        {
            if (!Directory.Exists(gameDataRoot)) throw new FixtureFailure("connected-gamedata-root-missing");
            start.Environment["AO_REBIRTH_GAMEDATA_PATH"] = Path.GetFullPath(gameDataRoot);
        }
        process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try
        {
            var elapsed = Stopwatch.StartNew();
            int port = world ? fixture.WorldPort : login ? fixture.LoginPort : fixture.ZonePort;
            while (elapsed.Elapsed < TimeSpan.FromSeconds(60))
            {
                if (process.HasExited) throw new FixtureFailure("connected-engine-exited-before-ready-" + (world ? "world" : login ? "login" : "zone"));
                bool marker;
                lock (output) marker = world
                    ? output.ToString().Contains("WORLDENGINE_READY") && output.ToString().Contains("Recovery window closed; World is authoritative")
                    : login ? output.ToString().Contains("Starting LoginEngine in headless mode.")
                    : output.ToString().Contains("ZONEENGINE_NEW_READY");
                if (marker)
                {
                    try { using var socket = new TcpClient(); socket.Connect(IPAddress.Loopback, port); return; }
                    catch (SocketException) { }
                }
                Thread.Sleep(100);
            }
            throw new FixtureFailure("connected-engine-readiness-timeout-" + (world ? "world" : login ? "login" : "zone"));
        }
        catch { Dispose(); throw; }
    }
    public void Stop(bool allowProvenChestShutdownBaseline = false)
    {
        File.WriteAllText(shutdown, "stop");
        if (!process.WaitForExit(30000)) throw new FixtureFailure("connected-clean-shutdown-timeout");
        process.WaitForExit();
        string text;
        lock (output) text = output.ToString();
        string[] failures = System.Text.RegularExpressions.Regex.Matches(text,
                @"(?m)^ZONEENGINE_NEW_SHUTDOWN_FAILED type=([^\r\n]*)\r?$")
            .Select(match => match.Groups[1].Value).ToArray();
        Console.WriteLine("CONNECTED_ENGINE_STOP ROLE=" + role + " PID=" + process.Id
            + " BINARY_SHA256=" + BinarySha256 + " EXIT_CODE=" + process.ExitCode
            + " SHUTDOWN_FAILURE_TYPES=" + (failures.Length == 0 ? "NONE" : string.Join(",", failures)));
        if (process.ExitCode == 0)
            return;
        if (allowProvenChestShutdownBaseline && role == "zone" && process.ExitCode == 3
            && failures.SequenceEqual(new[] { "NotSupportedException", "ObjectDisposedException" }))
        {
            UsedProvenShutdownBaseline = true;
            Console.WriteLine("CONNECTED_ENGINE_STOP_ACCEPTANCE=PASS_WITH_PROVEN_BASELINE_EXCEPTIONS"
                + " BASELINE_SOURCE_SHA=4f66c4c20d0e6f5f6e300082d81482944b028680");
            return;
        }
        throw new FixtureFailure("connected-clean-shutdown-exit-" + process.ExitCode);
    }
    public void Dispose()
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
        string text;
        lock (output) text = output.ToString();
        // Persist local diagnostics without ephemeral credentials.
        string secret = new MySqlConnector.MySqlConnectionStringBuilder(fixture.ConnectionString).Password;
        text = text.Replace(fixture.ConnectionString, "<fixture-connection>").Replace(secret, "<fixture-secret>");
        if (!string.IsNullOrEmpty(fixture.WorldToken))
        {
            text = text.Replace(fixture.WorldToken, "<fixture-world-secret>");
        }
        string logDirectory = Path.Combine(ConnectedAcceptanceSmoke.RepositoryRoot(), "build-verify");
        Directory.CreateDirectory(logDirectory);
        File.WriteAllText(Path.Combine(logDirectory, "connected-engine-" + process.Id + ".log"), text);
        process.Dispose();
        if (File.Exists(shutdown)) File.Delete(shutdown);
    }
}
