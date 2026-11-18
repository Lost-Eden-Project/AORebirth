namespace AORebirth.Database.Schema;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MySqlConnector;

/// <summary>One migration a zone module ships: <c>Modules/&lt;Id&gt;/Migrations/NNNN_name.sql</c>, checked.</summary>
/// <param name="Statements">The script's statements in order, each as written (without the closing ';').</param>
public sealed record ModuleMigration(string ModuleId, string Name, string Sha256, IReadOnlyList<string> Statements);

public enum ModuleSchemaState { Current, MigrationRequired, Incompatible }

/// <summary>How a module's shipped migrations compare with what the database has applied.</summary>
public sealed record ModuleSchemaStatus(string ModuleId, ModuleSchemaState State, string Message, IReadOnlyList<ModuleMigration> Pending)
{
    public bool IsCurrent => State == ModuleSchemaState.Current;
}

/// <summary>A module migration that is misnamed or reaches outside the module's own tables.</summary>
public sealed class ModuleMigrationException(string message) : Exception(message);

/// <summary>
/// Module-owned tables (module API 1.14). A module with an id of letters and digits owns the tables
/// <c>mod_&lt;id in lower case&gt;_*</c> and creates them only through the migrations it ships. Each migration is applied
/// by DatabaseMigrationTool and recorded in <see cref="LedgerTable"/> with its SHA-256, so an edited migration is
/// caught. Read-only like <see cref="DatabaseSchemaReadiness"/>: the only writer is the migration tool.
/// </summary>
public static class ModuleSchema
{
    public const string LedgerTable = "module_schema_migrations";
    public const string MigrationsFolder = "Migrations";
    public const int MaxModuleIdLength = 32;
    const int MaxIdentifierLength = 64;

    static readonly Regex FileNamePattern = new(@"^(?<seq>[0-9]{4})_[a-z0-9_]{1,80}\.sql$", RegexOptions.CultureInvariant);
    static readonly Regex TableNamePattern = new(@"^[a-z0-9_]+$", RegexOptions.CultureInvariant);

    // Anywhere in a module migration these end it: other databases, users, routines, files, locks, and SELECT (with it
    // a statement could read or copy another table).
    static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "DATABASE", "SCHEMA", "GRANT", "REVOKE", "USE", "USER", "ROLE", "PROCEDURE", "FUNCTION", "TRIGGER", "EVENT", "VIEW",
        "SERVER", "TABLESPACE", "TEMPORARY", "SELECT", "JOIN", "UNION", "WITH", "CALL", "HANDLER", "PREPARE", "EXECUTE",
        "DEALLOCATE", "LOCK", "UNLOCK", "FLUSH", "KILL", "SHUTDOWN", "INSTALL", "UNINSTALL", "LOAD", "OUTFILE", "DUMPFILE",
        "INFILE", "LOAD_FILE", "SLEEP", "BENCHMARK", "GET_LOCK", "RELEASE_LOCK", "DELIMITER"
    };

    /// <summary>A module id that may own tables: a letter, then letters and digits, at most 32.</summary>
    public static bool IsValidModuleId(string? moduleId)
        => moduleId is { Length: > 0 and <= MaxModuleIdLength } && char.IsAsciiLetter(moduleId[0]) && moduleId.All(char.IsAsciiLetterOrDigit);

    /// <summary><c>mod_&lt;id in lower case&gt;_</c>.</summary>
    public static string TablePrefix(string moduleId)
    {
        if (!IsValidModuleId(moduleId))
            throw new ModuleMigrationException("Module id '" + moduleId + "' cannot own tables: it must be a letter, then letters and digits, at most "
                + MaxModuleIdLength.ToString(CultureInfo.InvariantCulture) + ".");
        return "mod_" + moduleId.ToLowerInvariant() + "_";
    }

    /// <summary>The full name of the module's table <paramref name="name"/>, or throws for a name it may not have.</summary>
    public static string Table(string moduleId, string name)
    {
        string table = TablePrefix(moduleId) + name;
        if (string.IsNullOrEmpty(name) || !TableNamePattern.IsMatch(name) || table.Length > MaxIdentifierLength)
            throw new ModuleMigrationException("Module table name '" + name + "' must be lower-case letters, digits and '_', "
                + "at most " + (MaxIdentifierLength - TablePrefix(moduleId).Length).ToString(CultureInfo.InvariantCulture) + " characters.");
        return table;
    }

    /// <summary>
    /// The migrations in <c>&lt;moduleDirectory&gt;/Migrations</c>, in order, each checked; empty when the folder is absent.
    /// Throws <see cref="ModuleMigrationException"/> for a misnamed file, a repeated sequence number or a statement that
    /// reaches outside the module's tables.
    /// </summary>
    public static IReadOnlyList<ModuleMigration> Discover(string moduleDirectory, string moduleId)
    {
        string folder = Path.Combine(moduleDirectory, MigrationsFolder);
        if (!Directory.Exists(folder))
            return Array.Empty<ModuleMigration>();
        string prefix = TablePrefix(moduleId);

        var migrations = new List<ModuleMigration>();
        var sequences = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            Match match = FileNamePattern.Match(name);
            if (!match.Success)
                throw new ModuleMigrationException("Module " + moduleId + " migration file '" + name
                    + "' is not named NNNN_lower_case_name.sql (only migrations belong in " + MigrationsFolder + ").");
            if (!sequences.Add(match.Groups["seq"].Value))
                throw new ModuleMigrationException("Module " + moduleId + " has two migrations numbered " + match.Groups["seq"].Value + ".");
            // Line endings normalized so the hash is the same on Windows and Linux checkouts.
            string sql = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);
            migrations.Add(new ModuleMigration(moduleId, name, Sha256(sql), Check(moduleId, name, prefix, sql)));
        }
        return migrations;
    }

    /// <summary>
    /// What the ledger says this module has applied (name to SHA-256), or null when the ledger table is not there yet
    /// (the server's own migration that creates it is pending). SELECT only.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ReadApplied(MySqlConnection connection, string moduleId)
    {
        using (var exists = new MySqlCommand("SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@table", connection))
        {
            exists.Parameters.AddWithValue("@table", LedgerTable);
            if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                return null;
        }
        var applied = new Dictionary<string, string>(StringComparer.Ordinal);
        using var command = new MySqlCommand("SELECT MigrationName,Sha256 FROM " + LedgerTable + " WHERE ModuleId=@module", connection);
        command.Parameters.AddWithValue("@module", moduleId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            applied[reader.GetString(0)] = reader.GetString(1);
        return applied;
    }

    /// <summary>The module's state: current, migrations to apply, or a ledger that does not match what it ships.</summary>
    public static ModuleSchemaStatus Evaluate(string moduleId, IReadOnlyList<ModuleMigration> migrations, IReadOnlyDictionary<string, string>? applied)
    {
        if (migrations.Count == 0 && (applied == null || applied.Count == 0))
            return new(moduleId, ModuleSchemaState.Current, "Module " + moduleId + " ships no migrations.", Array.Empty<ModuleMigration>());
        if (applied == null)
            return new(moduleId, ModuleSchemaState.MigrationRequired, "The module migration ledger (" + LedgerTable
                + ") is absent: run DatabaseMigrationTool migrate for the server's migrations first.", migrations);

        var shipped = migrations.ToDictionary(m => m.Name, StringComparer.Ordinal);
        string[] unknown = applied.Keys.Where(name => !shipped.ContainsKey(name)).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length != 0)
            return Incompatible("the database has applied migrations this module no longer ships (" + string.Join(", ", unknown)
                + "); install the module release that matches the database");
        foreach (ModuleMigration migration in migrations)
            if (applied.TryGetValue(migration.Name, out string? sha) && !string.Equals(sha, migration.Sha256, StringComparison.OrdinalIgnoreCase))
                return Incompatible("migration " + migration.Name + " was changed after it was applied; ship a new migration instead of editing one");

        bool gap = false;
        var pending = new List<ModuleMigration>();
        foreach (ModuleMigration migration in migrations)
        {
            if (!applied.ContainsKey(migration.Name))
            {
                gap = true;
                pending.Add(migration);
            }
            else if (gap)
                return Incompatible("migration " + migration.Name + " is applied but an earlier one is not; reconcile the ledger");
        }
        return pending.Count == 0
            ? new(moduleId, ModuleSchemaState.Current, "Module " + moduleId + " tables are current.", Array.Empty<ModuleMigration>())
            : new(moduleId, ModuleSchemaState.MigrationRequired, "Module " + moduleId + " has migrations to apply: "
                + string.Join(", ", pending.Select(m => m.Name)) + ". Run DatabaseMigrationTool plan, then migrate.", pending);

        ModuleSchemaStatus Incompatible(string why)
            => new(moduleId, ModuleSchemaState.Incompatible, "Module " + moduleId + ": " + why + ".", Array.Empty<ModuleMigration>());
    }

    public static string Sha256(string sql) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();

    // ---- The scope check ------------------------------------------------------------------------------------------

    /// <summary>
    /// The statements of <paramref name="sql"/>, or throws when one does anything but create, alter, drop, truncate or
    /// fill the module's own tables. Every table a statement names must be <paramref name="prefix"/> plus lower-case
    /// letters, digits and '_' (lower case so the same name works on Linux, where MySQL table names are case-sensitive);
    /// named constraints and foreign keys stay inside the module too. Unqualified names only: no other database.
    /// </summary>
    internal static IReadOnlyList<string> Check(string moduleId, string file, string prefix, string sql)
    {
        List<Token> tokens = Tokenize(sql, file);
        var statements = new List<string>();
        int start = 0;
        for (int i = 0; i <= tokens.Count; i++)
        {
            if (i < tokens.Count && !tokens[i].Is(";"))
                continue;
            if (i > start)
            {
                List<Token> statement = tokens.GetRange(start, i - start);
                CheckStatement(statement, prefix, moduleId, file, statements.Count + 1);
                statements.Add(sql[statement[0].Start..statement[^1].End]);
            }
            start = i + 1;
        }
        if (statements.Count == 0)
            throw new ModuleMigrationException("Module " + moduleId + " migration " + file + " has no statements.");
        return statements;
    }

    static void CheckStatement(List<Token> t, string prefix, string moduleId, string file, int number)
    {
        void Refuse(string why) => throw new ModuleMigrationException(string.Format(CultureInfo.InvariantCulture,
            "Module {0} migration {1} statement {2}: {3}.", moduleId, file, number, why));

        foreach (Token token in t)
            if (token.Kind == TokenKind.Word && Forbidden.Contains(token.Text))
                Refuse("'" + token.Text.ToUpperInvariant() + "' is not allowed in a module migration");
        for (int i = 0; i + 1 < t.Count; i++)
            if (t[i].IsName && t[i + 1].Is("."))
                Refuse("qualified names (db.table or table.column) are not allowed");

        // The table after position i, skipping IF [NOT] EXISTS; checked to be the module's.
        int TableAt(int i)
        {
            if (i < t.Count && t[i].IsWord("IF"))
            {
                i++;
                if (i < t.Count && t[i].IsWord("NOT")) i++;
                if (i < t.Count && t[i].IsWord("EXISTS")) i++;
                else Refuse("IF must be IF EXISTS or IF NOT EXISTS");
            }
            if (i >= t.Count || !t[i].IsName)
                Refuse("a table name is missing");
            string name = t[i].Text;
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length == prefix.Length
                || !TableNamePattern.IsMatch(name) || name.Length > MaxIdentifierLength)
                Refuse("table '" + name + "' is not one of this module's tables (" + prefix + "<lower_case_name>)");
            return i;
        }

        // The statement's own shape.
        string first = t[0].Kind == TokenKind.Word ? t[0].Text.ToUpperInvariant() : string.Empty;
        string second = t.Count > 1 && t[1].Kind == TokenKind.Word ? t[1].Text.ToUpperInvariant() : string.Empty;
        switch (first)
        {
            case "CREATE" when second == "TABLE":
            {
                int table = TableAt(2);
                if (table + 1 < t.Count && t[table + 1].IsWord("LIKE"))
                    Refuse("CREATE TABLE ... LIKE copies another table");
                break;
            }
            case "CREATE" when second is "INDEX" or "UNIQUE" or "FULLTEXT" or "SPATIAL":
            case "DROP" when second == "INDEX":
            {
                int on = t.FindIndex(x => x.IsWord("ON"));
                if (on < 0) Refuse("an index statement needs ON <table>");
                TableAt(on + 1);
                break;
            }
            case "ALTER" when second == "TABLE":
            case "TRUNCATE" when second == "TABLE":
                TableAt(2);
                break;
            case "DROP" when second == "TABLE":
            {
                int i = TableAt(2) + 1;
                while (i < t.Count && t[i].Is(","))
                    i = TableAt(i + 1) + 1;
                break;
            }
            case "INSERT":
            case "REPLACE":
            {
                int i = 1;
                if (i < t.Count && t[i].IsWord("IGNORE")) i++;
                if (i >= t.Count || !t[i].IsWord("INTO")) Refuse(first + " must be " + first + " INTO <table>");
                int table = TableAt(i + 1);
                if (table + 1 >= t.Count || !(t[table + 1].Is("(") || t[table + 1].IsWord("VALUES") || t[table + 1].IsWord("VALUE")))
                    Refuse(first + " INTO <table> must be followed by a column list or VALUES");
                break;
            }
            case "UPDATE":
            {
                int table = TableAt(1);
                if (table + 1 >= t.Count || !t[table + 1].IsWord("SET"))
                    Refuse("UPDATE <table> SET only (one table, no alias)");
                break;
            }
            case "DELETE":
            {
                if (t.Count < 2 || !t[1].IsWord("FROM")) Refuse("DELETE must be DELETE FROM <table>");
                int table = TableAt(2);
                if (table + 1 < t.Count && !(t[table + 1].IsWord("WHERE") || t[table + 1].IsWord("ORDER") || t[table + 1].IsWord("LIMIT")))
                    Refuse("DELETE FROM <table> [WHERE ...] only (one table)");
                break;
            }
            default:
                Refuse("only CREATE/ALTER/DROP/TRUNCATE TABLE, CREATE/DROP INDEX, INSERT, REPLACE, UPDATE and DELETE are allowed");
                break;
        }

        // Wherever else a table can be named inside a statement.
        for (int i = 2; i < t.Count; i++)
        {
            if (t[i].IsWord("REFERENCES") || t[i].IsWord("INTO") || t[i].IsWord("FROM") || t[i].IsWord("TABLE"))
                TableAt(i + 1);
            // ALTER TABLE ... RENAME [TO|AS] <table> names a table; RENAME COLUMN/INDEX/KEY does not.
            if (t[i].IsWord("RENAME") && i + 1 < t.Count
                && !(t[i + 1].IsWord("COLUMN") || t[i + 1].IsWord("INDEX") || t[i + 1].IsWord("KEY")))
                TableAt(t[i + 1].IsWord("TO") || t[i + 1].IsWord("AS") ? i + 2 : i + 1);
            // Constraint names are unique across the whole database in MySQL: keep them in the module's namespace.
            if (t[i].IsWord("CONSTRAINT") && i + 1 < t.Count && t[i + 1].IsName
                && !(t[i + 1].IsWord("PRIMARY") || t[i + 1].IsWord("UNIQUE") || t[i + 1].IsWord("FOREIGN") || t[i + 1].IsWord("CHECK"))
                && !t[i + 1].Text.StartsWith(prefix, StringComparison.Ordinal))
                Refuse("constraint '" + t[i + 1].Text + "' must be named " + prefix + "<name>");
        }
    }

    enum TokenKind { Word, Quoted, Text, Number, Symbol }

    readonly record struct Token(TokenKind Kind, string Text, int Start, int End)
    {
        public bool IsName => Kind is TokenKind.Word or TokenKind.Quoted;

        public bool Is(string symbol) => Kind == TokenKind.Symbol && Text == symbol;

        public bool IsWord(string word) => Kind == TokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
    }

    // MySQL lexing as far as the check needs: comments dropped, strings and `names` kept whole. Executable /*! */ and
    // optimizer /*+ */ comments are refused: MySQL runs what they hold.
    static List<Token> Tokenize(string sql, string file)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '#' || (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-' && (i + 2 == sql.Length || char.IsWhiteSpace(sql[i + 2]))))
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                if (i + 2 < sql.Length && sql[i + 2] is '!' or '+')
                    throw new ModuleMigrationException("Module migration " + file + ": executable /*! */ and /*+ */ comments are not allowed.");
                int close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) throw new ModuleMigrationException("Module migration " + file + ": a /* comment is not closed.");
                i = close + 2;
                continue;
            }
            if (c is '\'' or '"' or '`')
            {
                int start = i++;
                var text = new StringBuilder();
                while (true)
                {
                    if (i >= sql.Length) throw new ModuleMigrationException("Module migration " + file + ": a quoted value is not closed.");
                    if (c != '`' && sql[i] == '\\' && i + 1 < sql.Length) { text.Append(sql[i + 1]); i += 2; continue; }
                    if (sql[i] == c)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == c) { text.Append(c); i += 2; continue; }
                        i++;
                        break;
                    }
                    text.Append(sql[i++]);
                }
                tokens.Add(new Token(c == '`' ? TokenKind.Quoted : TokenKind.Text, text.ToString(), start, i));
                continue;
            }
            if (char.IsAsciiDigit(c))
            {
                int start = i;
                while (i < sql.Length && (char.IsAsciiLetterOrDigit(sql[i]) || sql[i] is '.' or '_')) i++;
                tokens.Add(new Token(TokenKind.Number, sql[start..i], start, i));
                continue;
            }
            if (char.IsAsciiLetter(c) || c is '_' or '$' || c > 127)
            {
                int start = i;
                while (i < sql.Length && (char.IsAsciiLetterOrDigit(sql[i]) || sql[i] is '_' or '$' || sql[i] > 127)) i++;
                tokens.Add(new Token(TokenKind.Word, sql[start..i], start, i));
                continue;
            }
            tokens.Add(new Token(TokenKind.Symbol, c.ToString(), i, i + 1));
            i++;
        }
        return tokens;
    }
}
