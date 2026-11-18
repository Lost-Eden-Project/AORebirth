namespace AORebirth.Database.Migrations;

using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AORebirth.Database.Schema;
using MySqlConnector;

/// <summary>Operator-only writer. No server project references this assembly.</summary>
public static class MigrationCommand
{
    public static int Run(string[] args, Func<string?> connectionProvider, TextWriter output)
    {
        if (!TryParse(args, out string operation, out string? expectedDatabase, out string? modulesDirectory))
        {
            output.WriteLine("USAGE: DatabaseMigrationTool init|status|validate|plan OR migrate --expected-database NAME --acknowledge-backup --acknowledge-engines-stopped, each but init optionally followed by --modules DIR (the zone's Modules folder: its modules' own migrations too). Connection comes only from AO_REBIRTH_MIGRATION_CONNECTION.");
            return 64;
        }
        try
        {
            string? raw = connectionProvider();
            if (string.IsNullOrWhiteSpace(raw)) { output.WriteLine("DATABASE_UNREACHABLE: AO_REBIRTH_MIGRATION_CONNECTION is required."); return 3; }
            var builder = new MySqlConnectionStringBuilder(raw) { AllowLoadLocalInfile = false, AllowUserVariables = true, ConnectionTimeout = 5, DefaultCommandTimeout = 120 };
            if (string.IsNullOrWhiteSpace(builder.Database) || (operation == "migrate" && builder.Database != expectedDatabase))
            { output.WriteLine("REFUSED: exact database acknowledgement does not match the selected database."); return 64; }
             if (operation == "init")
             {
                 using var initConnection = new MySqlConnection(builder.ConnectionString);
                 initConnection.Open();
                 using var initAcquire = new MySqlCommand("SELECT GET_LOCK(CONCAT(DATABASE(),':aorebirth-schema'),0)", initConnection);
                 if (Convert.ToInt32(initAcquire.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                 { output.WriteLine("REFUSED: another schema operator holds the database migration lock."); return 2; }
                 try { Initialize(initConnection, output); }
                 finally
                 {
                     // A failure can leave the connection closed (and the lock gone with it): don't let the release
                     // replace the error that says why.
                     if (initConnection.State == System.Data.ConnectionState.Open)
                     {
                         using var initRelease = new MySqlCommand("SELECT RELEASE_LOCK(CONCAT(DATABASE(),':aorebirth-schema'))", initConnection);
                         initRelease.ExecuteScalar();
                     }
                 }
                 return 0;
             }
            // Every module's shipped migrations are read and scope-checked before anything else: a module migration that
            // reaches outside its own tables refuses the whole run.
            IReadOnlyList<ModuleMigrations> modules = modulesDirectory == null ? Array.Empty<ModuleMigrations>() : DiscoverModules(modulesDirectory);
            var before = DatabaseSchemaReadiness.Check(builder.ConnectionString);
            WriteStatus(output, before);
            if (operation is "status" or "validate")
            {
                bool modulesCurrent = WriteModuleStatus(builder.ConnectionString, modules, output, before);
                return before.IsCurrent && modulesCurrent ? 0 : 2;
            }
            foreach (string name in SchemaContract.MigrationNames)
                output.WriteLine("MIGRATION=" + name + " SHA256=" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ReadScript(name)))).ToLowerInvariant());
            foreach (ModuleMigrations module in modules)
                foreach (ModuleMigration migration in module.Migrations)
                    output.WriteLine("MODULE_MIGRATION=" + module.ModuleId + "/" + migration.Name + " SHA256=" + migration.Sha256);
            output.WriteLine("PLAN: Stop all writers, verify recoverable backup, apply in listed order (server migrations, then each module's). Legacy item tables retained. No automatic reverse migration; legacy inventory becomes stale after NewEngine writes.");
            if (operation == "plan")
            {
                bool modulesUsable = WriteModuleStatus(builder.ConnectionString, modules, output, before, pendingIsFine: true);
                return before.State is SchemaState.DATABASE_UNREACHABLE or SchemaState.SCHEMA_INCOMPATIBLE || !modulesUsable ? 2 : 0;
            }
            if (before.State is not (SchemaState.SCHEMA_CURRENT or SchemaState.SCHEMA_MIGRATION_REQUIRED)) return 2;
            if (before.IsCurrent && WriteModuleStatus(builder.ConnectionString, modules, TextWriter.Null, before)) return 0;

            using var connection = new MySqlConnection(builder.ConnectionString);
            connection.Open();
            using var acquire = new MySqlCommand("SELECT GET_LOCK(CONCAT(DATABASE(),':aorebirth-schema'),0)", connection);
            if (Convert.ToInt32(acquire.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            { output.WriteLine("REFUSED: another migration operator holds the database migration lock."); return 2; }
            SchemaCheckResult after;
            try
            {
                if (!before.IsCurrent) Apply(connection, output);
                after = DatabaseSchemaReadiness.Check(builder.ConnectionString);
                WriteStatus(output, after);
                // Module tables only on a current server schema: the module ledger is one of its tables.
                if (after.IsCurrent)
                    foreach (ModuleMigrations module in modules)
                        ApplyModule(connection, module, output);
            }
            finally
            {
                if (connection.State == System.Data.ConnectionState.Open)
                {
                    using var release = new MySqlCommand("SELECT RELEASE_LOCK(CONCAT(DATABASE(),':aorebirth-schema'))", connection);
                    release.ExecuteScalar();
                }
            }
            bool modulesDone = WriteModuleStatus(builder.ConnectionString, modules, output, after);
            return after.IsCurrent && modulesDone ? 0 : 2;
        }
        catch (MigrationRefusedException exception)
        {
            output.WriteLine("MIGRATION_REFUSED: " + exception.Message);
            return 2;
        }
        catch (MySqlException exception)
        {
            // Server messages can contain user/host/SQL values. Emit only the stable error number.
            output.WriteLine("MIGRATION_FAILED: MySQL error " + exception.Number.ToString(CultureInfo.InvariantCulture) + "; stop and inspect status. The migration ledger records completed steps only. No automatic retry or reverse migration was attempted.");
            return 3;
        }
        catch (Exception exception)
        {
            output.WriteLine("MIGRATION_FAILED: " + exception);
            return 3;
        }
    }

    /// <summary>The arguments, with an optional trailing <c>--modules DIR</c> after any operation but init.</summary>
    public static bool TryParse(string[] args, out string operation, out string? expectedDatabase, out string? modulesDirectory)
    {
        modulesDirectory = null;
        if (args.Length >= 3 && args[^2] == "--modules" && args[0] != "init" && !string.IsNullOrWhiteSpace(args[^1]))
        {
            modulesDirectory = Path.GetFullPath(args[^1]);
            args = args[..^2];
        }
        return TryParse(args, out operation, out expectedDatabase);
    }

    public static bool TryParse(string[] args, out string operation, out string? expectedDatabase)
    {
        operation = args.Length == 0 ? string.Empty : args[0];
        expectedDatabase = null;
        if (args.Length == 1 && operation is "status" or "validate" or "plan" or "init") return true;
        if (args.Length == 5 && operation == "migrate" && args[1] == "--expected-database" && IsIdentifier(args[2]) && args[3] == "--acknowledge-backup" && args[4] == "--acknowledge-engines-stopped")
        { expectedDatabase = args[2]; return true; }
        return false;
    }

    public static string ReadScript(string name)
    {
        if (!SchemaContract.MigrationNames.Contains(name, StringComparer.Ordinal)) throw new MigrationRefusedException("Unknown migration identity.");
        Assembly assembly = typeof(MigrationCommand).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(candidate => candidate.EndsWith("." + name, StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        // Normalize checkout line endings so the plan hash is identical on Windows and Linux.
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> InitScriptNames()
    {
        return typeof(MigrationCommand).Assembly.GetManifestResourceNames()
            .Where(resource => resource.Contains("SqlTables", StringComparison.OrdinalIgnoreCase))
            .Select(resource => Regex.Match(resource, @"(?<name>[A-Za-z0-9_]+\.sql)$"))
            .Where(match => match.Success)
            .Select(match => match.Groups["name"].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string ReadInitScript(string name)
    {
        Assembly assembly = typeof(MigrationCommand).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(candidate => candidate.EndsWith("." + name, StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void Initialize(MySqlConnection connection, TextWriter output)
    {
        var scripts = InitScriptNames().Select(name => new InitScript(name, ReadInitScript(name))).ToArray();
        var tableOwners = scripts
            .SelectMany(script => TablesDeclaredByCreate(script.Sql).Select(table => (table, script.Name)))
            .ToDictionary(item => item.table, item => item.Name, StringComparer.OrdinalIgnoreCase);
        var pending = scripts.ToDictionary(script => script.Name, StringComparer.Ordinal);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var orderedScripts = new List<InitScript>(scripts.Length);

        while (pending.Count != 0)
        {
            InitScript? next = pending.Values
                .OrderBy(script => script.Name, StringComparer.Ordinal)
                .FirstOrDefault(script => ReferencedTables(script.Sql)
                    .Select(table => tableOwners.TryGetValue(table, out string? owner) ? owner : null)
                    .Where(owner => owner is not null)
                    .All(owner => owner == script.Name || completed.Contains(owner!)));
            if (next is null)
                throw new MigrationRefusedException("SQL table dependencies contain a cycle or an unresolved creation order.");

            pending.Remove(next.Name);
            completed.Add(next.Name);
            orderedScripts.Add(next);
        }

        foreach (InitScript script in orderedScripts.AsEnumerable().Reverse())
            foreach (string table in TablesDeclaredByCreate(script.Sql))
                Execute(connection, "DROP TABLE IF EXISTS " + QuoteIdentifier(table));

        // Each script's rows in one commit, without a time limit: the big ones (~100k single-row
        // INSERTs) took one disk flush per row under autocommit, ran past the command timeout on a slow disk, and the
        // cancelled command closed the connection half-way through the scripts. CREATE TABLE still commits by itself.
        Execute(connection, "SET autocommit=0");
        try
        {
            foreach (InitScript next in orderedScripts)
            {
                Execute(connection, next.Sql, timeoutSeconds: 0);
                Execute(connection, "COMMIT");
                output.WriteLine("INITIALIZED=" + next.Name);
            }
        }
        finally
        {
            if (connection.State == System.Data.ConnectionState.Open)
                Execute(connection, "SET autocommit=1");
        }
        Execute(connection, "CREATE TABLE IF NOT EXISTS schema_migrations (MigrationName VARCHAR(255) NOT NULL, AppliedAtUtc DATETIME(6) NOT NULL, PRIMARY KEY (MigrationName)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
    }

    private static IEnumerable<string> TablesDeclaredByCreate(string sql)
    {
        const string pattern = @"(?im)^\s*CREATE\s+TABLE(?:\s+IF\s+NOT\s+EXISTS)?\s+`?(?<table>[A-Za-z0-9_]+)`?";
        return Regex.Matches(sql, pattern).Select(match => match.Groups["table"].Value).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ReferencedTables(string sql)
    {
        const string pattern = @"(?i)\bREFERENCES\s+`?(?<table>[A-Za-z0-9_]+)`?";
        return Regex.Matches(sql, pattern).Select(match => match.Groups["table"].Value).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string QuoteIdentifier(string identifier) => "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";

    private sealed record InitScript(string Name, string Sql);

    private static void Apply(MySqlConnection connection, TextWriter output)
    {
        SchemaSnapshot snapshot = DatabaseSchemaReadiness.ReadSnapshot(connection);
        SchemaCheckResult status = DatabaseSchemaReadiness.Evaluate(snapshot);
        if (status.IsCurrent) return;
        if (status.State != SchemaState.SCHEMA_MIGRATION_REQUIRED) throw new MigrationRefusedException(status.Message);
        var baseline = SchemaContract.Columns.Where(c => !SchemaContract.MigrationOwnedTables.Contains(c.Table, StringComparer.Ordinal));
        if (baseline.Any(required => !snapshot.Columns.Any(actual => Equal(actual.Table, required.Table) && Equal(actual.Column, required.Column))))
            throw new MigrationRefusedException("Governed baseline tables/columns are absent. Provision or restore the baseline separately; this tool never bootstraps unrelated SQL tables.");
        if (!snapshot.Indexes.Any(index => Equal(index.Table, "stats") && index.Unique && Equal(index.Columns, "Type,Instance,StatId")))
            throw new MigrationRefusedException("Baseline stats unique key is absent; reconcile the baseline separately.");
        bool firstPending = !snapshot.AppliedMigrations.Contains(SchemaContract.MigrationNames[0], StringComparer.Ordinal);
        if (firstPending)
        {
            if (!snapshot.TableEngines.ContainsKey("items") || !snapshot.TableEngines.ContainsKey("instanceditems"))
                throw new MigrationRefusedException("Legacy items/instanceditems source tables are absent; review baseline and inventory provenance.");
            if (snapshot.TableEngines.ContainsKey("item_instances") && Scalar(connection, "SELECT COUNT(*) FROM item_instances") != 0)
                throw new MigrationRefusedException("Untracked item_instances is nonempty. Do not mark or skip it automatically: reconcile the complete legacy import and backup before proceeding.");
        }
        Execute(connection, "CREATE TABLE IF NOT EXISTS schema_migrations (MigrationName VARCHAR(255) NOT NULL, AppliedAtUtc DATETIME(6) NOT NULL, PRIMARY KEY (MigrationName)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
        foreach (string name in SchemaContract.MigrationNames)
        {
            if (snapshot.AppliedMigrations.Contains(name, StringComparer.Ordinal)) continue;
            string sql = ReadScript(name);
            if (name == SchemaContract.MigrationNames[0])
            {
                const string end = "ENGINE=InnoDB;";
                int split = sql.IndexOf(end, StringComparison.Ordinal) + end.Length;
                if (split < end.Length) throw new MigrationRefusedException("The governed import DDL boundary changed; review the migration executor.");
                Execute(connection, sql[..split]);
                // MySQL DDL commits independently. Both inventory copies and their ledger row
                // share a transaction, so a failed second copy cannot leave a skipped partial import.
                using var transaction = connection.BeginTransaction();
                Execute(connection, sql[split..], transaction);
                Mark(connection, name, transaction);
                transaction.Commit();
            }
            else
            {
                Execute(connection, sql);
                Mark(connection, name);
            }
            output.WriteLine("APPLIED=" + name);
        }
    }

    private sealed record ModuleMigrations(string ModuleId, IReadOnlyList<ModuleMigration> Migrations);

    // Each module folder with a module.json and a Migrations folder: its id (which must name the folder, as the zone
    // requires) and its migrations, scope-checked.
    private static IReadOnlyList<ModuleMigrations> DiscoverModules(string directory)
    {
        if (!Directory.Exists(directory)) throw new MigrationRefusedException("The --modules folder does not exist.");
        var modules = new List<ModuleMigrations>();
        foreach (string folder in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            string manifest = Path.Combine(folder, "module.json");
            if (!File.Exists(manifest) || !Directory.Exists(Path.Combine(folder, ModuleSchema.MigrationsFolder))) continue;
            string id = ReadModuleId(manifest);
            if (!Equal(id, Path.GetFileName(folder)))
                throw new MigrationRefusedException("Module folder " + Path.GetFileName(folder) + " holds module.json id '" + id + "'.");
            try { modules.Add(new ModuleMigrations(id, ModuleSchema.Discover(folder, id))); }
            catch (ModuleMigrationException exception) { throw new MigrationRefusedException(exception.Message); }
        }
        return modules;
    }

    private static string ReadModuleId(string manifest)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest),
            new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
        foreach (var property in document.RootElement.EnumerateObject())
            if (Equal(property.Name, "Id") && property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                return property.Value.GetString()!;
        throw new MigrationRefusedException(manifest + " names no module Id.");
    }

    // One line per module; true when every module is current (or, with pendingIsFine, at least not incompatible).
    private static bool WriteModuleStatus(string connectionString, IReadOnlyList<ModuleMigrations> modules, TextWriter output,
        SchemaCheckResult server, bool pendingIsFine = false)
    {
        if (modules.Count == 0) return true;
        if (server.State is SchemaState.DATABASE_UNREACHABLE or SchemaState.SCHEMA_INCOMPATIBLE)
        {
            output.WriteLine("MODULES: not checked; the server schema is not usable.");
            return false;
        }
        using var connection = new MySqlConnection(connectionString);
        connection.Open();
        bool usable = true;
        foreach (ModuleMigrations module in modules)
        {
            ModuleSchemaStatus status = ModuleSchema.Evaluate(module.ModuleId, module.Migrations, ModuleSchema.ReadApplied(connection, module.ModuleId));
            output.WriteLine("MODULE " + module.ModuleId + " " + status.State + ": " + status.Message);
            if (status.State == ModuleSchemaState.Incompatible || (!pendingIsFine && !status.IsCurrent)) usable = false;
        }
        return usable;
    }

    // A module's pending migrations in order, each recorded once it ran. One that only changes rows runs in a single
    // transaction with its ledger row; one with DDL cannot (MySQL commits each DDL statement), so as with the server's
    // own migrations a failure part-way leaves it unrecorded for an operator to inspect.
    private static void ApplyModule(MySqlConnection connection, ModuleMigrations module, TextWriter output)
    {
        var applied = ModuleSchema.ReadApplied(connection, module.ModuleId)
            ?? throw new MigrationRefusedException("The module migration ledger is absent after the server migrations.");
        ModuleSchemaStatus status = ModuleSchema.Evaluate(module.ModuleId, module.Migrations, applied);
        if (status.State == ModuleSchemaState.Incompatible) throw new MigrationRefusedException(status.Message);
        foreach (ModuleMigration migration in status.Pending)
        {
            if (migration.Statements.All(IsRowStatement))
            {
                using var transaction = connection.BeginTransaction();
                foreach (string statement in migration.Statements) Execute(connection, statement, transaction);
                MarkModule(connection, migration, transaction);
                transaction.Commit();
            }
            else
            {
                foreach (string statement in migration.Statements) Execute(connection, statement);
                MarkModule(connection, migration);
            }
            output.WriteLine("APPLIED=" + module.ModuleId + "/" + migration.Name);
        }
    }

    private static bool IsRowStatement(string statement)
    {
        string first = statement.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return Equal(first, "INSERT") || Equal(first, "REPLACE") || Equal(first, "UPDATE") || Equal(first, "DELETE");
    }

    private static void MarkModule(MySqlConnection connection, ModuleMigration migration, MySqlTransaction? transaction = null)
    {
        using var command = new MySqlCommand("INSERT INTO " + ModuleSchema.LedgerTable
            + " (ModuleId,MigrationName,Sha256,AppliedAtUtc) VALUES (@module,@name,@sha,UTC_TIMESTAMP(6))", connection, transaction);
        command.Parameters.AddWithValue("@module", migration.ModuleId);
        command.Parameters.AddWithValue("@name", migration.Name);
        command.Parameters.AddWithValue("@sha", migration.Sha256);
        command.ExecuteNonQuery();
    }

    private static bool Equal(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool IsIdentifier(string value) => value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static void WriteStatus(TextWriter output, SchemaCheckResult result) => output.WriteLine(result.State + ": " + result.Message);
    private static long Scalar(MySqlConnection connection, string sql)
    { using var command = new MySqlCommand(sql, connection); return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture); }
    private static void Execute(MySqlConnection connection, string sql, MySqlTransaction? transaction = null, int timeoutSeconds = 120)
    { using var command = new MySqlCommand(sql, connection, transaction); command.CommandTimeout = timeoutSeconds; command.ExecuteNonQuery(); }
    private static void Mark(MySqlConnection connection, string name, MySqlTransaction? transaction = null)
    {
        using var command = new MySqlCommand("INSERT INTO schema_migrations (MigrationName,AppliedAtUtc) VALUES (@name,UTC_TIMESTAMP(6))", connection, transaction);
        command.Parameters.AddWithValue("@name", name);
        command.ExecuteNonQuery();
    }
    private sealed class MigrationRefusedException(string message) : Exception(message);
}
