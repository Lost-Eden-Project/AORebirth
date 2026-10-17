using System.Data;
using System.Text.RegularExpressions;
using AORebirth.Core.GameData;
using AORebirth.Database.Dao;
using AORebirth.Database.Domain.Characters;
using AORebirth.Interfaces.Persistence.CharacterCreation;
using LoginEngine.CharacterCreation;
using LoginEngine.Packets;
using MySqlConnector;
using SmokeLounge.AOtomation.Messaging.GameData;

// Executes the production LoginEngine creation orchestrator against only the
// harness-owned MySQL. Faults are injected after real statements, not fake writes.
static class CharacterCreationSmoke
{
    const string AccountName = "creation_fixture";

    public static void CreateBaseline(MySqlConnection connection)
    {
        // These empty tables were just created by FixtureSql in this disposable DB.
        // Creation needs the canonical AUTO_INCREMENT and full DAO entity columns.
        FixtureSql.Execute(connection, "DROP TABLE characters; DROP TABLE stats");
        string tables = Path.Combine(RepositoryRoot(),
            "AORebirth", "Libraries", "Source", "AORebirth.Database", "SqlTables");
        foreach (string name in new[] { "characters", "stats", "login", "missionstates", "missionobjectiveprogress",
            "missionflags", "missionobjectiveobservations", "missionaccountflags" })
            FixtureSql.Execute(connection, File.ReadAllText(Path.Combine(tables, name + ".sql")));
        FixtureSql.Execute(connection,
            "INSERT INTO login (Id,CreationDate,Email,FirstName,LastName,Username,Password,Expansions,GM) "
            + "VALUES (1,'2026-01-01','','','','creation_fixture','not-an-authentication-test',127,0)");
    }

    public static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AI_START_HERE.md"))) return directory.FullName;
        throw new FixtureFailure("creation-repository-root-not-found");
    }

    public static void Validate(DisposableSchemaDatabase fixture, MySqlConnection connection)
    {
        string? previousOverride = Environment.GetEnvironmentVariable(GameDataPaths.EnvironmentVariableName);
        try
        {
            // The linked public fixture is copied beside this test executable.
            // The real default path is exercised; private/application GameData is never read.
            Environment.SetEnvironmentVariable(GameDataPaths.EnvironmentVariableName, null);
            NewCharacterStats starter = NewCharacterStats.Current;
            var account = new DBLoginData { Id = 1, Username = AccountName, Expansions = 127, GM = 0 };
            var persistence = new MySqlCharacterPersistenceDao(() => fixture.Open());
            foreach (int breed in new[] { 1, 2, 3, 4 })
            {
                int profession = new[] { 1, 3, 6, 9 }[breed - 1];
                CharacterName character = Character("CreationBreed" + breed, breed, profession);
                var transaction = new PersistenceFault();
                int id = character.CreateNewChar(false, starter, account, Package(profession),
                    () => new FaultConnection(fixture.Open(), transaction));
                Require(id > 0 && transaction.CommitCalls == 1 && transaction.RollbackCalls == 0,
                    "creation-one-commit-breed-" + breed);

                // Each DAO call opens a new physical connection after the creation connection closed.
                var row = persistence.LoadCharacter(id);
                var stats = persistence.LoadStats(id).ToDictionary(stat => stat.StatId, stat => stat.StatValue);
                Require(row != null && row.Name == character.Name && row.Playfield == starter.StartLocation.Playfield
                    && row.X == starter.StartLocation.X && row.Y == starter.StartLocation.Y && row.Z == starter.StartLocation.Z,
                    "creation-location-breed-" + breed);
                Require(stats[(int)CharacterStat.Breed] == breed && stats[(int)CharacterStat.Profession] == profession
                    && stats[(int)CharacterStat.Level] == starter.Stats.Single(stat => stat.Stat == (int)CharacterStat.Level).Value
                    && stats[(int)CharacterStat.Sex] == character.Gender && stats[(int)CharacterStat.Expansion] == account.Expansions,
                    "creation-identity-stats-breed-" + breed);
                foreach (var stat in starter.Stats)
                    Require(stats[stat.Stat] == stat.Value, "creation-authored-stat-" + stat.Stat);
                int[] abilityIds = { 16, 21, 20, 19, 18, 17 };
                int[] abilities = starter.AbilitiesFor(breed).ToLegacyOrder();
                for (int i = 0; i < abilityIds.Length; i++)
                    Require(stats[abilityIds[i]] == abilities[i], "creation-authored-ability-" + abilityIds[i]);
                Require(stats[(int)CharacterStat.Health] > 0 && stats[(int)CharacterStat.Health] == stats[(int)CharacterStat.MaxHealth]
                    && stats[(int)CharacterStat.CurrentNano] > 0 && stats[(int)CharacterStat.CurrentNano] == stats[(int)CharacterStat.MaxNanoEnergy],
                    "creation-current-max-vitals-breed-" + breed);
                var item = persistence.LoadCarriedItems(id).Single();
                Require(item.ContainerType == (int)IdentityType.Inventory && item.ContainerInstance == id
                    && item.ContainerPlacement == 64 && item.LowId == 43384 && item.HighId == 43384
                    && item.Quality == 1 && item.StackCount == 2 && item.Source == 1,
                    "creation-inventory-breed-" + breed);
                Require(persistence.LoadUploadedNanos(id).SequenceEqual(new[] { 37 }), "creation-uploaded-nano-breed-" + breed);
                Require(FixtureSql.Scalar(connection, "SELECT COUNT(*) FROM missionflags WHERE CharacterId=" + id
                    + " AND QuestId='system.new_character_start_area' AND FlagKey='selection' AND `Value`='pending'") == 1,
                    "creation-mandatory-start-marker-breed-" + breed);
            }

            // The existing SL path intentionally has no Rubi-Ka selection marker.
            var slTransaction = new PersistenceFault();
            int slId = Character("CreationShadowlands", 1, 1).CreateNewChar(true, starter, account, Package(1),
                () => new FaultConnection(fixture.Open(), slTransaction));
            Require(slTransaction.CommitCalls == 1 && FixtureSql.Scalar(connection,
                "SELECT COUNT(*) FROM missionflags WHERE CharacterId=" + slId) == 0, "creation-sl-marker-contract");

            string baseline = Fingerprint();
            foreach (string table in new[] { "stats", "missionflags" })
            {
                var fault = new CreationStatementFault(table);
                Exception? failure = null;
                try
                {
                    Character("CreationFault" + table, 1, 1).CreateNewChar(false, starter, account, Package(1),
                        () => new CreationFaultConnection(fixture.Open(), fault));
                }
                catch (InvalidOperationException exception) { failure = exception; }
                Require(failure?.GetBaseException() is IOException && fault.Fired
                    && fault.Transaction.CommitCalls == 0 && fault.Transaction.RollbackCalls == 1,
                    "creation-fault-executed-and-rolled-back-" + table);
                Require(Fingerprint() == baseline, "creation-fault-zero-durable-changes-" + table);
                RequireNoOrphans(connection);
            }

            foreach (bool missingAccount in new[] { false, true })
            {
                bool opened = false;
                Exception? failure = null;
                try
                {
                    Character("CreationInvalid", missingAccount ? 1 : 99, 1).CreateNewChar(false, starter,
                        missingAccount ? null! : account, Package(1), () => { opened = true; return fixture.Open(); });
                }
                catch (InvalidOperationException exception) { failure = exception; }
                Require(failure != null && !opened && Fingerprint() == baseline,
                    missingAccount ? "creation-missing-account-no-write" : "creation-invalid-breed-no-write");
            }
            Console.WriteLine("CHARACTER_CREATION_REAL_MYSQL=PASS PLAYER_BREEDS=4 DEFAULT_GAMEDATA_PATH=PASS INITIAL_STATE_RELOADED=PASS STATS_FAILURE_ROLLBACK=PASS MANDATORY_STATE_FAILURE_ROLLBACK=PASS ZERO_ORPHANS=PASS INVALID_INPUT_NO_CONNECTION=PASS");

            string Fingerprint()
            {
                using var reopened = fixture.Open();
                // Rolled-back AUTO_INCREMENT reservations are allowed; all table rows must remain exact.
                return FixtureSql.Fingerprint(reopened, includeAutoIncrementCounters: false);
            }
        }
        finally { Environment.SetEnvironmentVariable(GameDataPaths.EnvironmentVariableName, previousOverride); }
    }

    static void RequireNoOrphans(MySqlConnection connection)
    {
        Require(FixtureSql.Scalar(connection, "SELECT COUNT(*) FROM stats s LEFT JOIN characters c ON c.Id=s.Instance WHERE s.Type=50000 AND c.Id IS NULL") == 0,
            "creation-orphan-stats");
        Require(FixtureSql.Scalar(connection, "SELECT COUNT(*) FROM item_instances i LEFT JOIN characters c ON c.Id=i.ContainerInstance WHERE c.Id IS NULL") == 0,
            "creation-orphan-items");
        Require(FixtureSql.Scalar(connection, "SELECT COUNT(*) FROM charactersuploadednanos n LEFT JOIN characters c ON c.Id=n.CharacterId WHERE c.Id IS NULL") == 0,
            "creation-orphan-nanos");
        Require(FixtureSql.Scalar(connection, "SELECT COUNT(*) FROM missionflags f LEFT JOIN characters c ON c.Id=f.CharacterId WHERE c.Id IS NULL") == 0,
            "creation-orphan-mandatory-state");
    }

    static CharacterName Character(string name, int breed, int profession) => new CharacterName
    {
        Name = name, AccountName = AccountName, Breed = breed, Profession = profession,
        Gender = 0, HeadMesh = 1, MonsterScale = 100, Fatness = 0
    };
    static CharacterStartProfessionPackage Package(int profession) => new CharacterStartProfessionPackage(profession,
        "fixture", "synthetic transaction fixture", new[] { new CharacterStartPackageItem(64, 43384, 43384, 1, 2) }, new[] { 37 });
    static void Require(bool condition, string code) { if (!condition) throw new FixtureFailure(code); }
}

sealed class CreationStatementFault(string table)
{
    public readonly PersistenceFault Transaction = new();
    public bool Fired;
    public void AfterStatement(string sql)
    {
        if (Fired || !Regex.IsMatch(sql.Replace("`", string.Empty), @"\bINSERT\s+INTO\s+" + table + @"\b", RegexOptions.IgnoreCase)) return;
        Fired = true;
        throw new IOException("Injected failure after real creation statement.");
    }
}

sealed class CreationFaultConnection(IDbConnection inner, CreationStatementFault fault) : IDbConnection
{
    public string ConnectionString { get => inner.ConnectionString; set => inner.ConnectionString = value; }
    public int ConnectionTimeout => inner.ConnectionTimeout;
    public string Database => inner.Database;
    public ConnectionState State => inner.State;
    public void Open() => inner.Open();
    public void Close() => inner.Close();
    public void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);
    public IDbTransaction BeginTransaction() => new FaultTransaction(this, inner.BeginTransaction(), fault.Transaction);
    public IDbTransaction BeginTransaction(IsolationLevel il) => new FaultTransaction(this, inner.BeginTransaction(il), fault.Transaction);
    public IDbCommand CreateCommand() => new CreationFaultCommand(inner.CreateCommand(), fault);
    public void Dispose() => inner.Dispose();
}

sealed class CreationFaultCommand(IDbCommand inner, CreationStatementFault fault) : IDbCommand
{
    public string CommandText { get => inner.CommandText; set => inner.CommandText = value; }
    public int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
    public CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
    public IDbConnection? Connection { get => inner.Connection; set => inner.Connection = value; }
    public IDataParameterCollection Parameters => inner.Parameters;
    public IDbTransaction? Transaction { get => inner.Transaction; set => inner.Transaction = value is FaultTransaction wrapped ? wrapped.Inner : value; }
    public UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
    public void Cancel() => inner.Cancel();
    public IDbDataParameter CreateParameter() => inner.CreateParameter();
    public int ExecuteNonQuery() { int result = inner.ExecuteNonQuery(); fault.AfterStatement(CommandText); return result; }
    public object? ExecuteScalar() { object? result = inner.ExecuteScalar(); fault.AfterStatement(CommandText); return result; }
    public IDataReader ExecuteReader() => ExecuteReader(CommandBehavior.Default);
    public IDataReader ExecuteReader(CommandBehavior behavior)
    {
        IDataReader reader = inner.ExecuteReader(behavior);
        try { fault.AfterStatement(CommandText); return reader; }
        catch { reader.Dispose(); throw; }
    }
    public void Prepare() => inner.Prepare();
    public void Dispose() => inner.Dispose();
}
