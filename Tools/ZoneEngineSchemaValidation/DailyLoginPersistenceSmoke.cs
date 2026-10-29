using System.Globalization;
using AORebirth.Database.Domain.Characters;
using AORebirth.Database.Migrations;
using AORebirth.Interfaces.Persistence.Characters;
using MySqlConnector;
using SmokeLounge.AOtomation.Messaging.GameData;

/// <summary>Uses the existing isolated real-MySQL fixture; faults decorate transport, never storage.</summary>
static class DailyLoginPersistenceSmoke
{
    public static void Validate(DisposableSchemaDatabase fixture, MySqlConnection connection)
    {
        Require(connection.Database == DisposableSchemaDatabase.DatabaseName, "daily-fixture-target");
        FixtureSql.Execute(connection, "CREATE TABLE login (Id INT NOT NULL PRIMARY KEY,Username VARCHAR(32) NOT NULL,UNIQUE KEY username (Username)) ENGINE=InnoDB; "
            + "CREATE TABLE characters (Id INT NOT NULL PRIMARY KEY,Username VARCHAR(32) NOT NULL,Online SMALLINT NOT NULL) ENGINE=InnoDB");
        string root = ConnectedAcceptanceSmoke.RepositoryRoot();
        string rollback = File.ReadAllText(Path.Combine(root,
            "AORebirth/Libraries/Source/AORebirth.Database/Rollbacks/20261029_daily_login_claims_empty_only.sql"));
        FixtureSql.Execute(connection, File.ReadAllText(Path.Combine(root, "AORebirth/Libraries/Source/AORebirth.Database/SqlTables/item_instances.sql")));
        string migration = MigrationCommand.ReadScript("20261029_daily_login_claims.sql");
        FixtureSql.Execute(connection, migration);
        string migrated = Fingerprint();
        FixtureSql.Execute(connection, migration);
        Require(Fingerprint() == migrated, "daily-migration-repeat-no-change");
        Require(FixtureSql.Scalar(connection, "SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='dailyloginclaims' AND NON_UNIQUE=0 AND INDEX_NAME='UX_dailyloginclaims_account_day'") == 2,
            "daily-account-day-unique-index");
        FixtureSql.Execute(connection, "CREATE TABLE schema_migrations (MigrationName VARCHAR(191) NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
            + "INSERT INTO schema_migrations VALUES ('20261029_daily_login_claims.sql'),('unrelated_migration.sql')");
        RunRollback();
        Require(FixtureSql.Scalar(connection, "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='dailyloginclaims'") == 0
            && FixtureSql.Scalar(connection, "SELECT COUNT(*) FROM schema_migrations WHERE MigrationName='20261029_daily_login_claims.sql'") == 0
            && FixtureSql.Scalar(connection, "SELECT COUNT(*) FROM schema_migrations WHERE MigrationName='unrelated_migration.sql'") == 1,
            "daily-empty-schema-rollback-scoped");
        FixtureSql.Execute(connection, migration);
        FixtureSql.Execute(connection, "INSERT INTO schema_migrations VALUES ('20261029_daily_login_claims.sql')");
        FixtureSql.Execute(connection, "INSERT INTO login (Id,Username) VALUES (12001,'dailyA'),(12002,'dailyB'); "
            + "INSERT INTO characters (Id,Username,Online) VALUES (12101,'dailyA',1),(12102,'dailyA',1),(12201,'dailyB',1),(12202,'dailyB',0)");

        DateTime day = new(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
        var dao = new MySqlCharacterPersistenceDao(() => fixture.Open());
        const string legacy = "{\"Taken\":[1,2],\"LastGrantedUtc\":\"2026-10-28\",\"ClaimedCount\":2}";
        var imported = dao.ImportDailyLoginState("DAILYA", 12101, legacy, day.AddDays(-1), day.Ticks);
        Require(imported.IsLegacyImport && imported.AccountId == 12001 && imported.StateJson == legacy
            && imported.ClaimDateUtc == day.AddDays(-1), "daily-legacy-progress-imported");
        Require(dao.ImportDailyLoginState("dailyA", 12102, "{\"Taken\":[]}", null, day.Ticks).StateJson == legacy
            && FixtureSql.Scalar(connection, "SELECT COUNT(*) FROM dailyloginclaims WHERE AccountId=12001") == 1,
            "daily-stale-file-cannot-reset-import");
        Require(FixtureSql.Scalar(connection, "SELECT COUNT(*) FROM item_instances") == 0, "daily-import-never-regrants");
        string beforeWrongOwner = Fingerprint();
        RequireThrows<InvalidOperationException>(() => dao.ImportDailyLoginState("dailyA", 12201, "{}", null, day.Ticks));
        Require(Fingerprint() == beforeWrongOwner, "daily-import-wrong-owner-no-write");

        var grant = Request("dailyA", 12101, "claim_a_1", imported.ClaimToken, day, 15001, 64);
        grant.Inserts.Add(Row(12101, 15002, 65));
        var applied = dao.CommitDailyLoginClaim(grant);
        Require(applied.Status == DailyLoginClaimCommitStatus.Applied && applied.Snapshot.StateJson == grant.StateJson
            && applied.Snapshot.ResultJson == grant.ResultJson && Rows(15001, 15002) == 2, "daily-complete-grant-and-receipt");
        string afterGrant = Fingerprint();
        var oldToken = Request("dailyA", 12102, grant.ClaimToken, "stale", day.AddDays(1), 15003, 64);
        Require(dao.CommitDailyLoginClaim(oldToken).Status == DailyLoginClaimCommitStatus.AlreadyApplied
            && dao.FindDailyLoginClaim("dailyA", grant.ClaimToken).ResultJson == grant.ResultJson
            && dao.FindDailyLoginClaim("dailyB", grant.ClaimToken) == null && Fingerprint() == afterGrant,
            "daily-token-replay-before-date-gate-no-duplicate");
        Require(dao.CommitDailyLoginClaim(Request("dailyA", 12102, "claim_a_alt", grant.ClaimToken, day, 15003, 64)).Status
            == DailyLoginClaimCommitStatus.AlreadyClaimedToday && Fingerprint() == afterGrant, "daily-alternate-character-same-day");
        Require(dao.CommitDailyLoginClaim(Request("dailyA", 12101, "claim_a_stale", imported.ClaimToken, day.AddDays(1), 15003, 66)).Status
            == DailyLoginClaimCommitStatus.StateChanged && Fingerprint() == afterGrant, "daily-stale-state-cas-no-write");
        RequireThrows<InvalidOperationException>(() => dao.CommitDailyLoginClaim(Request("dailyA", 12201, "wrong_owner", grant.ClaimToken, day.AddDays(1), 15003, 64)));
        Require(Fingerprint() == afterGrant, "daily-commit-wrong-owner-no-write");
        var foreign = dao.CommitDailyLoginClaim(Request("dailyB", 12201, grant.ClaimToken, null!, day, 15003, 64));
        Require(foreign.Status == DailyLoginClaimCommitStatus.TokenOwnedByAnotherAccount && foreign.Snapshot == null
            && Fingerprint() == afterGrant, "daily-foreign-token-no-state-disclosure");

        var todayImport = dao.ImportDailyLoginState("dailyB", 12201, "{\"Taken\":[1],\"LastGrantedUtc\":\"2026-10-29\"}", day, day.Ticks);
        string importedToday = Fingerprint();
        Require(dao.CommitDailyLoginClaim(Request("dailyB", 12201, "claim_imported_today", todayImport.ClaimToken, day, 15003, 64)).Status
            == DailyLoginClaimCommitStatus.AlreadyClaimedToday && Fingerprint() == importedToday, "daily-import-today-no-regrant");
        RequireThrows<InvalidOperationException>(() => dao.ImportDailyLoginState("dailyB", 12202, "{}", null, day.Ticks));

        using (var barrier = new Barrier(2))
        {
            var concurrent = new[] { 12101, 12102 }.Select((character, i) => Task.Run(() =>
            {
                Require(barrier.SignalAndWait(TimeSpan.FromSeconds(15)), "daily-concurrent-barrier-timeout");
                return new MySqlCharacterPersistenceDao(() => fixture.Open()).CommitDailyLoginClaim(
                    Request("dailyA", character, "concurrent_" + i, grant.ClaimToken, day.AddDays(1), 15100 + i, character == 12101 ? 66 : 64));
            })).ToArray();
            Require(Task.WaitAll(concurrent, TimeSpan.FromSeconds(30)), "daily-concurrent-transaction-timeout");
            Require(concurrent.Count(t => t.Result.Status == DailyLoginClaimCommitStatus.Applied) == 1
                && concurrent.Count(t => t.Result.Status == DailyLoginClaimCommitStatus.AlreadyClaimedToday) == 1
                && Rows(15100, 15101) == 1, "daily-concurrent-account-grants-once");
        }

        var current = dao.LoadDailyLoginState("dailyA");
        var rollbackRequest = Request("dailyA", 12101, "rollback_grant", current.ClaimToken, day.AddDays(2), 15200, 68);
        rollbackRequest.Inserts.Add(Row(12101, 15201, 69));
        string beforeRollback = Fingerprint();
        var invalidReceipt = Request("dailyA", 12101, "invalid_receipt", current.ClaimToken, day.AddDays(2), 15202, 70);
        invalidReceipt.ResultJson = "not JSON";
        RequireThrows<MySqlException>(() => dao.CommitDailyLoginClaim(invalidReceipt));
        Require(Fingerprint() == beforeRollback, "daily-invalid-receipt-rolls-back-inventory-insert");
        foreach (int after in new[] { 0, 1, 2, 3 })
        {
            var fault = new PersistenceFault { FailAfterWrite = after };
            RequireThrows<IOException>(() => new MySqlCharacterPersistenceDao(() => new FaultConnection(fixture.Open(), fault)).CommitDailyLoginClaim(rollbackRequest));
            Require(fault.RollbackCalls == 1 && fault.CommitCalls == 0 && Fingerprint() == beforeRollback,
                "daily-partial-write-rollback-" + after);
        }
        var commitBefore = new PersistenceFault { CommitFault = CommitFault.Before };
        RequireThrows<CharacterPersistenceCommitOutcomeUnknownException>(() =>
            new MySqlCharacterPersistenceDao(() => new FaultConnection(fixture.Open(), commitBefore)).CommitDailyLoginClaim(rollbackRequest));
        Require(Fingerprint() == beforeRollback && commitBefore.RollbackCalls == 0
            && dao.FindDailyLoginClaim("dailyA", rollbackRequest.ClaimToken) == null, "daily-before-commit-unknown-old-state");
        var commitAfter = new PersistenceFault { CommitFault = CommitFault.After };
        RequireThrows<CharacterPersistenceCommitOutcomeUnknownException>(() =>
            new MySqlCharacterPersistenceDao(() => new FaultConnection(fixture.Open(), commitAfter)).CommitDailyLoginClaim(rollbackRequest));
        var reopened = new MySqlCharacterPersistenceDao(() => fixture.Open());
        Require(Rows(15200, 15201) == 2 && commitAfter.RollbackCalls == 0
            && reopened.FindDailyLoginClaim("dailyA", rollbackRequest.ClaimToken).ResultJson == rollbackRequest.ResultJson,
            "daily-lost-ack-reopen-has-whole-grant-and-receipt");
        string afterLostAck = Fingerprint();
        Require(reopened.CommitDailyLoginClaim(rollbackRequest).Status == DailyLoginClaimCommitStatus.AlreadyApplied
            && Fingerprint() == afterLostAck, "daily-restart-retry-no-regrant");
        Require(reopened.ImportDailyLoginState("dailyA", 12102, "{\"Taken\":[]}", null, day.AddDays(3).Ticks).StateJson == rollbackRequest.StateJson,
            "daily-legacy-reconciliation-never-overwrites-new-state");
        string populated = Fingerprint();
        bool rollbackRefused = false;
        try { RunRollback(); }
        catch (MySqlException exception) when (exception.SqlState == "45000") { rollbackRefused = true; }
        Require(rollbackRefused && Fingerprint() == populated, "daily-populated-schema-rollback-refused-with-data-preserved");
        Console.WriteLine("DAILY_LOGIN_REAL_MYSQL=PASS MIGRATION_RERUN=PASS EMPTY_SCHEMA_ROLLBACK=PASS POPULATED_ROLLBACK=REFUSED_PRESERVED LEGACY_IMPORT_ONCE=PASS ACCOUNT_OWNER=PASS UTC_ACCOUNT_DAY_UNIQUE=PASS CONCURRENT_CHARACTERS=PASS PARTIAL_WRITE_ROLLBACKS=4 LOST_ACK_RECOVERY=PASS RESTART_REPLAY=PASS");

        long Rows(params int[] ids) => FixtureSql.Scalar(connection, "SELECT COUNT(*) FROM item_instances WHERE InstanceId IN ("
            + string.Join(',', ids.Select(id => id.ToString(CultureInfo.InvariantCulture))) + ")");
        string Fingerprint()
        {
            using var reopenedConnection = fixture.Open();
            return FixtureSql.Fingerprint(reopenedConnection, includeAutoIncrementCounters: false);
        }
        void RunRollback()
        {
            // DELIMITER is a mysql CLI directive. The connector receives each exact stored-program statement.
            string source = string.Join('\n', rollback.Split('\n').Where(line => !line.TrimStart().StartsWith("DELIMITER", StringComparison.Ordinal)));
            try
            {
                foreach (string statement in source.Split("$$", StringSplitOptions.RemoveEmptyEntries))
                    if (!string.IsNullOrWhiteSpace(statement)) FixtureSql.Execute(connection, statement);
            }
            finally
            {
                FixtureSql.Execute(connection, "DROP PROCEDURE IF EXISTS aorebirth_daily_login_20261029_empty_rollback");
            }
        }
    }

    static DailyLoginClaimCommitData Request(string account, int character, string token, string expected, DateTime day, int id, int slot)
        => new()
        {
            AccountUsername = account, CharacterId = character, ClaimToken = token, ExpectedStateToken = expected,
            ClaimDateUtc = day, NowUtcTicks = day.AddHours(12).Ticks,
            StateJson = "{\"Taken\":[1,2,3],\"Receipt\":\"" + token + "\"}", ResultJson = "{\"ok\":true,\"claimToken\":\"" + token + "\"}",
            Inserts = new List<PersistedItemData> { Row(character, id, slot) }
        };

    static PersistedItemData Row(int character, int id, int slot) => new()
    {
        InstanceId = id, ContainerType = (int)IdentityType.Inventory, ContainerInstance = character, ContainerPlacement = slot,
        ItemType = (int)IdentityType.None, LowId = 43384, HighId = 43384, Quality = 1, StackCount = 5, Source = 1
    };

    static void RequireThrows<T>(Action operation) where T : Exception
    {
        try { operation(); }
        catch (T) { return; }
        throw new FixtureFailure("daily-expected-exception-" + typeof(T).Name);
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new FixtureFailure(message);
    }
}
