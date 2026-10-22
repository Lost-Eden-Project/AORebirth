namespace AORebirth.Tools.AccountDaoValidation
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using AORebirth.Database.Domain.Characters;
    using Dapper;
    using MySqlConnector;
    using SmokeLounge.AOtomation.Messaging.GameData;

    internal static partial class Program
    {
        private static readonly string[] EntitlementFixtureTables =
        {
            "login", "characters", "stats", "item_instances", "charactersmeshs",
            "charactersuploadednanos", "charactersperks", "characterresearch"
        };

        private static void ExpansionEntitlementMigrationChecks()
        {
            string databaseSource = Path.Combine(Directory.GetCurrentDirectory(), "AORebirth", "Libraries",
                "Source", "AORebirth.Database");
            string migration = File.ReadAllText(Path.Combine(databaseSource, "Migrations",
                "20261021_all_expansion_entitlements.sql"));
            using (MySqlConnection connection = Open(rootConnection))
            {
                foreach (string table in EntitlementFixtureTables.Skip(2))
                    connection.Execute(File.ReadAllText(Path.Combine(databaseSource, "SqlTables", table + ".sql")));

                // Exercise the pre-migration production default without copying any production data.
                connection.Execute("ALTER TABLE login ALTER COLUMN Expansions SET DEFAULT 127");
                SeedEntitlementFixture(connection);
                Require(connection.QuerySingle<int>("SELECT COUNT(*) FROM login") == 106
                    && connection.QuerySingle<int>("SELECT COUNT(*) FROM characters") == 125,
                    "migration-synthetic-account-and-character-counts");
                Require(connection.QuerySingle<int>("SELECT COUNT(*) FROM login WHERE Expansions=127") == 105
                    && connection.QuerySingle<int>("SELECT COUNT(*) FROM login WHERE Expansions=0") == 1,
                    "migration-synthetic-account-entitlement-shape");
                Require(connection.QuerySingle<int>("SELECT COUNT(*) FROM stats WHERE Type=50000 AND StatId=389 AND StatValue=127") == 114
                    && connection.QuerySingle<int>("SELECT COUNT(*) FROM stats WHERE Type=50000 AND StatId=389 AND StatValue=2") == 7,
                    "migration-synthetic-character-entitlement-shape");
                Require(connection.QuerySingle<int>("SELECT COUNT(*) FROM stats s INNER JOIN characters c ON c.Id=s.Instance "
                    + "LEFT JOIN login l ON l.Username=c.Username WHERE l.Id IS NULL AND s.Type=50000 AND s.StatId=389 "
                    + "AND s.StatValue IN (8,16,32,64)") == 4,
                    "migration-synthetic-archived-expansion-stats-present");

                string original = EntitlementSnapshot(connection, false);
                string unaffected = EntitlementSnapshot(connection, true);
                string archived = ArchivedEntitlementSnapshot(connection);

                connection.Execute("DELETE FROM stats WHERE Id=10");
                string missingStat = EntitlementSnapshot(connection, false);
                RequireExpansionMigrationFailure(connection, migration,
                    "Account-linked character is missing persisted expansion stat 389", "missing-stat-preflight");
                Require(missingStat == EntitlementSnapshot(connection, false) && ExpansionDefault(connection) == 127,
                    "migration-missing-stat-fails-before-data-and-default-writes");
                connection.Execute("INSERT INTO stats (Id,Instance,Type,StatId,StatValue) VALUES (10,1,50000,389,127)");
                Require(original == EntitlementSnapshot(connection, false), "migration-missing-stat-fixture-restored");

                // The failing trigger observes the completed account UPDATE inside the same transaction.
                // Its error proves that a later stat-write failure really rolls that UPDATE back.
                connection.Execute("CREATE TRIGGER expansion_entitlement_failure BEFORE UPDATE ON stats FOR EACH ROW "
                    + "BEGIN IF NEW.Type=50000 AND NEW.StatId=389 THEN "
                    + "IF (SELECT COUNT(*) FROM login WHERE Expansions=2047)<>106 THEN "
                    + "SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Fixture did not observe account update'; END IF; "
                    + "SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected expansion-stat failure after account update'; "
                    + "END IF; END");
                RequireExpansionMigrationFailure(connection, migration,
                    "Injected expansion-stat failure after account update", "late-stat-write-failure");
                Require(original == EntitlementSnapshot(connection, false) && ExpansionDefault(connection) == 127,
                    "migration-real-account-and-stat-transaction-rollback");
                connection.Execute("DROP TRIGGER expansion_entitlement_failure");

                ExpansionMigrationCounts first = ExecuteExpansionMigration(connection, migration);
                Require(first.AccountsUpdated == 106 && first.CharactersUpdated == 121,
                    "migration-exact-initial-update-counts");
                Require(connection.QuerySingle<int>("SELECT COUNT(*) FROM login WHERE Expansions=2047") == 106,
                    "migration-all-existing-accounts-2047");
                Require(connection.QuerySingle<int>("SELECT COUNT(*) FROM stats s "
                    + "INNER JOIN characters c ON c.Id=s.Instance INNER JOIN login l ON l.Username=c.Username "
                    + "WHERE s.Type=50000 AND s.StatId=389 AND s.StatValue=2047") == 121,
                    "migration-all-linked-character-expansions-2047");
                Require(unaffected == EntitlementSnapshot(connection, true),
                    "migration-all-other-account-stat-inventory-equipment-progression-fields-exact");
                Require(archived == ArchivedEntitlementSnapshot(connection), "migration-four-archived-characters-exact");
                Require(ExpansionDefault(connection) == 2047, "migration-production-column-default-2047");
                Require(connection.QuerySingle<int>("SELECT COUNT(*) FROM information_schema.ROUTINES "
                    + "WHERE ROUTINE_SCHEMA=DATABASE() AND ROUTINE_NAME='aorebirth_migrate_all_expansion_entitlements'") == 0,
                    "migration-temporary-procedure-removed");

                string completed = EntitlementSnapshot(connection, false);
                ExpansionMigrationCounts repeated = ExecuteExpansionMigration(connection, migration);
                Require(repeated.AccountsUpdated == 0 && repeated.CharactersUpdated == 0
                    && completed == EntitlementSnapshot(connection, false) && ExpansionDefault(connection) == 2047,
                    "migration-rerun-zero-data-changes");

                // Fresh DAO instances reopen physical connections, as hydration does on a new session.
                foreach (int characterId in Enumerable.Range(1, 121))
                {
                    var persistence = new MySqlCharacterPersistenceDao(() => new MySqlConnection(application));
                    Require(persistence.LoadStats(characterId).Single(stat => stat.StatId == (int)CharacterStat.Expansion).StatValue == 2047,
                        "migration-fresh-dao-stat-reload-" + characterId);
                }
                connection.Execute("INSERT INTO login (CreationDate,Email,FirstName,LastName,Username,Password) "
                    + "VALUES ('2026-01-01','','','','default_fixture','synthetic-fixture')");
                Require(connection.QuerySingle<int>("SELECT Expansions FROM login WHERE Username='default_fixture'") == 2047,
                    "migration-new-account-omitted-column-uses-2047-default");

                ExpansionAccountCreationChecks(connection, databaseSource);

                Console.WriteLine("EXPANSION_MIGRATION_ACCOUNTS=106 CHARACTERS=121 ARCHIVED_UNCHANGED=4 "
                    + "OTHER_FIELDS_EXACT=PASS MISSING_STAT_FAIL_CLOSED=PASS LATE_FAILURE_ROLLBACK=PASS "
                    + "RERUN_ACCOUNTS=0 RERUN_CHARACTERS=0 DEFAULT=2047 FRESH_DAO_RELOAD=PASS");
            }
        }

        private static void SeedEntitlementFixture(MySqlConnection connection)
        {
            for (int id = 1; id <= 106; id++)
            {
                connection.Execute("INSERT INTO login (Id,CreationDate,Email,FirstName,LastName,Username,Password,"
                    + "AllowedCharacters,Flags,AccountFlags,Expansions,GM) VALUES "
                    + "(@id,'2026-01-01',@email,@first,@last,@username,@password,@allowed,@flags,@accountFlags,@expansions,@gm)",
                    new { id, email = "synthetic" + id + "@example.test", first = "First" + id, last = "Last" + id,
                        username = FixtureAccountName(id), password = "synthetic-fixture-" + id,
                        allowed = id % 8, flags = id * 3, accountFlags = id * 7,
                        expansions = id == 106 ? 0 : 127, gm = id % 4 });
            }
            for (int id = 1; id <= 125; id++)
            {
                connection.Execute("INSERT INTO characters (Id,Username,Name,FirstName,LastName,Textures0,Textures1,Textures2,"
                    + "Textures3,Textures4,playfield,X,Y,Z,HeadingX,HeadingY,HeadingZ,HeadingW,Online,BuddyList) VALUES "
                    + "(@id,@username,@name,'Synthetic','Fixture',1,2,3,4,5,4582,100.25,200.5,300.75,0,0,0,1,0,'1,2')",
                    new { id, username = id <= 121 ? FixtureAccountName((id - 1) % 106 + 1) : "archived_fixture_" + id,
                        name = "Synthetic" + id });
                if (id <= 121)
                    connection.Execute("INSERT INTO stats (Id,Instance,Type,StatId,StatValue) VALUES (@statRow,@id,50000,389,@value)",
                        new { statRow = id * 10, id, value = id <= 114 ? 127 : 2 });
                else
                    connection.Execute("INSERT INTO stats (Id,Instance,Type,StatId,StatValue) VALUES (@statRow,@id,50000,389,@value)",
                        new { statRow = id * 10, id, value = 8 << (id - 122) });
                // Credits, progression, and same-number stats on a different identity type are untouched.
                connection.Execute("INSERT INTO stats (Id,Instance,Type,StatId,StatValue) VALUES "
                    + "(@cashRow,@id,50000,61,@cash),(@xpRow,@id,50000,52,@xp),"
                    + "(@levelRow,@id,50000,54,@level),(@otherTypeRow,@id,10002,389,17)",
                    new { cashRow = id * 10 + 1, xpRow = id * 10 + 2, levelRow = id * 10 + 3,
                        otherTypeRow = id * 10 + 4, id, cash = id * 1234, xp = id * 5678, level = id % 220 + 1 });
                foreach (var item in new[]
                    { new { type = (int)IdentityType.WeaponPage, placement = 1 },
                      new { type = (int)IdentityType.Inventory, placement = 64 } })
                    connection.Execute("INSERT INTO item_instances (ContainerType,ContainerInstance,ContainerPlacement,ItemType,"
                        + "LowId,HighId,Quality,StackCount,Source) VALUES (@type,@id,@placement,0,43384,43384,1,2,1)",
                        new { id, item.type, item.placement });
                connection.Execute("INSERT INTO charactersmeshs (CharacterId,Playfield,MeshValue1,MeshValue2,MeshValue3) "
                    + "VALUES (@id,4582,1,2,3); INSERT INTO charactersuploadednanos (CharacterId,NanoId) VALUES (@id,37); "
                    + "INSERT INTO charactersperks (CharacterId,PacketId) VALUES (@id,123); "
                    + "INSERT INTO characterresearch (CharacterId,ResearchId,RemainingXp) VALUES (@id,1,123456)", new { id });
            }
        }

        private static string FixtureAccountName(int id) { return "entitlement_fixture_" + id; }

        private static ExpansionMigrationCounts ExecuteExpansionMigration(MySqlConnection connection, string migration)
        {
            // The exact checked-in SQL is sent unchanged as one provider batch, like the existing migration tool.
            return connection.QuerySingle<ExpansionMigrationCounts>(migration, commandTimeout: 60);
        }

        private static void RequireExpansionMigrationFailure(MySqlConnection connection, string migration, string message, string test)
        {
            MySqlException failure = null;
            try { ExecuteExpansionMigration(connection, migration); }
            catch (MySqlException exception) { failure = exception; }
            Require(failure != null && failure.Number == 1644 && failure.Message == message,
                "migration-" + test + "-expected-server-signal");
        }

        private static int ExpansionDefault(MySqlConnection connection)
        {
            return connection.QuerySingle<int>("SELECT COLUMN_DEFAULT FROM information_schema.COLUMNS "
                + "WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='login' AND COLUMN_NAME='Expansions'");
        }

        private static string EntitlementSnapshot(MySqlConnection connection, bool excludeTargets)
        {
            var snapshot = new StringBuilder();
            foreach (string table in EntitlementFixtureTables)
            {
                string[] columns = connection.Query<string>("SELECT COLUMN_NAME FROM information_schema.COLUMNS "
                    + "WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@table ORDER BY ORDINAL_POSITION", new { table })
                    .Where(column => !excludeTargets || table != "login" || column != "Expansions").ToArray();
                string projection = string.Join(",", columns.Select(column => "s.`" + column + "`"));
                string sql = "SELECT " + projection + " FROM `" + table + "` s";
                if (excludeTargets && table == "stats")
                {
                    sql += " WHERE NOT (s.Type=50000 AND s.StatId=389 AND EXISTS (SELECT 1 FROM characters c "
                        + "INNER JOIN login l ON l.Username=c.Username WHERE c.Id=s.Instance))";
                    snapshot.Append(SnapshotEntitlementRows(connection, "SELECT s.Id,s.Instance,s.Type,s.StatId FROM stats s "
                        + "INNER JOIN characters c ON c.Id=s.Instance INNER JOIN login l ON l.Username=c.Username "
                        + "WHERE s.Type=50000 AND s.StatId=389 ORDER BY s.Id"));
                }
                snapshot.Append(table).Append(SnapshotEntitlementRows(connection, sql + " ORDER BY " + projection));
            }
            return snapshot.ToString();
        }

        private static string ArchivedEntitlementSnapshot(MySqlConnection connection)
        {
            Require(connection.QuerySingle<int>("SELECT COUNT(*) FROM characters c LEFT JOIN login l "
                + "ON l.Username=c.Username WHERE l.Id IS NULL") == 4, "migration-four-archived-records-remain");
            return SnapshotEntitlementRows(connection, "SELECT c.* FROM characters c LEFT JOIN login l "
                + "ON l.Username=c.Username WHERE l.Id IS NULL ORDER BY c.Id")
                + SnapshotEntitlementRows(connection, "SELECT s.* FROM stats s INNER JOIN characters c ON c.Id=s.Instance "
                    + "LEFT JOIN login l ON l.Username=c.Username WHERE l.Id IS NULL ORDER BY s.Id");
        }

        private static string SnapshotEntitlementRows(MySqlConnection connection, string sql)
        {
            var result = new StringBuilder();
            using (var command = new MySqlCommand(sql, connection))
            using (MySqlDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    result.Append('[');
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        object value = reader.GetValue(i);
                        string text = value is DateTime ? ((DateTime)value).ToString("O", CultureInfo.InvariantCulture)
                            : Convert.ToString(value, CultureInfo.InvariantCulture);
                        result.Append(value.GetType().FullName).Append(':').Append(text.Length).Append(':').Append(text);
                    }
                    result.Append(']');
                }
            }
            return result.ToString();
        }

        private sealed class ExpansionMigrationCounts
        {
            public long AccountsUpdated { get; set; }
            public long CharactersUpdated { get; set; }
        }
    }
}
