namespace AORebirth.Tools.SweepCheckpointDaoValidation
{
    using System;
    using System.Globalization;
    using System.IO;
    using MySqlConnector;

    /// <summary>
    /// Real-MySQL validation of the two persistence paths the dungeon expiry sweep and character checkpoints
    /// depend on, against the actual persistence sources in a disposable MySQL container:
    /// <see cref="ValidateArtifactRetirement"/> and <see cref="ValidateConcurrentCheckpoints"/>.
    /// </summary>
    internal static partial class Program
    {
        private const string AcknowledgementEnvironment = "AO_REBIRTH_ALLOW_DISPOSABLE_SWEEP_CHECKPOINT_DAO_VALIDATION";

        private static int checks;

        private static int Main(string[] args)
        {
            if (args.Length != 1 || !string.Equals(args[0], "--run-disposable", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("REFUSED: exact --run-disposable argument required.");
                return 2;
            }

            if (!string.Equals(Environment.GetEnvironmentVariable(AcknowledgementEnvironment), "1", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("REFUSED: " + AcknowledgementEnvironment + "=1 is required.");
                return 2;
            }

            DisposableMySql disposable = null;
            try
            {
                disposable = DisposableMySql.Create();
                using (MySqlConnection connection = disposable.WaitForReady())
                {
                    ApplySchemas(connection, Directory.GetCurrentDirectory());
                    SeedCharacters(connection);
                }

                ValidateSweepAndCheckpoint(disposable.ApplicationConnectionString);
                Console.WriteLine("SWEEP_CHECKPOINT_DAO_TEST_MODE=ISOLATED_PRODUCTION_SOURCES");
                Console.WriteLine("SWEEP_CHECKPOINT_DAO_CHECKS=" + checks.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("SWEEP_CHECKPOINT_DAO_MYSQL_INTEGRATION=PASS");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("SWEEP_CHECKPOINT_DAO_MYSQL_INTEGRATION=FAIL");
                Console.Error.WriteLine("ERROR=" + exception.GetType().Name + ":" + exception.Message);
                return 1;
            }
            finally
            {
                if (disposable != null)
                    disposable.Dispose();
            }
        }

        private static void ApplySchemas(MySqlConnection connection, string root)
        {
            string directory = Path.Combine(root, "AORebirth", "Libraries", "Source", "AORebirth.Database", "SqlTables");
            foreach (string name in new[] { "characters.sql", "stats.sql", "item_instances.sql", "characterquests.sql", "generatedquests.sql", "questdungeonkeys.sql" })
            {
                using (var command = new MySqlCommand(File.ReadAllText(Path.Combine(directory, name)), connection))
                    command.ExecuteNonQuery();
            }
        }

        private static void SeedCharacters(MySqlConnection connection)
        {
            foreach (int id in new[] { ArtifactOwner, ArtifactOtherCharacter })
            {
                using (var command = new MySqlCommand(
                           "INSERT INTO characters (Id, Username, Name, FirstName, LastName, playfield, X, Y, Z, HeadingX, HeadingY, HeadingZ, HeadingW) "
                           + "VALUES (@Id, 'validation_account', @Name, '', '', 1, 0, 0, 0, 0, 0, 0, 1)", connection))
                {
                    command.Parameters.AddWithValue("@Id", id);
                    command.Parameters.AddWithValue("@Name", "Validation" + id.ToString(CultureInfo.InvariantCulture));
                    command.ExecuteNonQuery();
                }
            }
        }

        private static MySqlConnection Open(string connectionString)
        {
            var connection = new MySqlConnection(connectionString);
            connection.Open();
            return connection;
        }

        private static void Require(bool condition, string code)
        {
            checks++;
            if (!condition)
                throw new InvalidOperationException(code);
            Console.WriteLine("PASS " + code);
        }

        private static void Expect<T>(Action action, string code) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                Require(true, code);
                return;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(code + " threw " + exception.GetType().Name + " instead of " + typeof(T).Name);
            }
            throw new InvalidOperationException(code + " did not throw " + typeof(T).Name);
        }

        private static long Scalar(string connectionString, string sql)
        {
            using (MySqlConnection connection = Open(connectionString))
            using (var command = new MySqlCommand(sql, connection))
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }
}
