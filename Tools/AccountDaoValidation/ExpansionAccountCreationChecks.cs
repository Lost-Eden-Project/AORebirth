namespace AORebirth.Tools.AccountDaoValidation
{
    using System;
    using System.IO;
    using System.Reflection;
    using AORebirth.AccountBroker;
    using AORebirth.Core.Encryption;
    using AORebirth.Database.Dao;
    using AORebirth.Enums;
    using Dapper;
    using MySqlConnector;

    internal static partial class Program
    {
        private static void ExpansionAccountCreationChecks(MySqlConnection connection, string databaseSource)
        {
            // This fixture owns the database and its explicit connection factory.
            // Registration runs unchanged production broker source, never an HTTP service.
            connection.Execute(File.ReadAllText(Path.Combine(databaseSource, "SqlTables", "aorebirth_identity.sql")));
            var broker = new AccountBrokerService(() => new MySqlConnection(application));
            var request = new CreateAccountRequest
            {
                IdempotencyKey = "all-expansion-registration-fixture",
                Username = "AllExpansionRegistration",
                Password = "Expansion-Fixture-Password-123!",
                Email = "registration@example.test",
                FirstName = "Expansion",
                LastName = "Fixture"
            };
            AccountProvisioningResult created = broker.CreateGameAccount(request);
            var registered = NewDao(application).LoadByUsername(request.Username);
            Require(created.CreatedGameAccount && registered.Expansions == (int)Expansions.All
                && registered.Expansions == 2047, "registration-grants-all-eleven-expansions");
            Require(registered.Flags == 0 && registered.AccountFlags == 0 && registered.GmLevel == 0
                && registered.AllowedCharacters == 6 && registered.Email == request.Email
                && registered.FirstName == request.FirstName && registered.LastName == request.LastName
                && PasswordHash.ValidatePassword(request.Password, registered.PasswordHash),
                "registration-preserves-non-entitlement-policy");
            AccountProvisioningResult repeated = broker.CreateGameAccount(request);
            Require(!repeated.CreatedGameAccount && repeated.GameAccountId == created.GameAccountId
                && NewDao(application).LoadByUsername(request.Username).Expansions == (int)Expansions.All,
                "registration-idempotent-retry-retains-all-expansions");

            string loginPath = Path.Combine(Directory.GetCurrentDirectory(), "AORebirth", "Built", "Debug", "LoginEngine.dll");
            Require(File.Exists(loginPath), "console-test-requires-approved-windows-login-build");
            Assembly login = Assembly.LoadFrom(loginPath);
            Type program = login.GetType("LoginEngine.Program", true);
            MethodInfo create = program.GetMethod("CreateAccountData", BindingFlags.Static | BindingFlags.NonPublic);
            Require(create != null, "console-actual-account-construction-method-found");
            int index = 0;
            foreach (string requestedExpansion in new[] { "0", "127", "2047" })
            {
                string username = "AllExpansionConsole" + index++;
                string[] arguments = { "adduser", username, "Console-Fixture-Password-123!", "8", requestedExpansion,
                    "511", "console@example.test", "ConsoleFirst", "ConsoleLast" };
                object actual = create.Invoke(null, new object[] { arguments });
                // The fixture links the same DAO sources into its own assembly.
                // Copy the actual console DTO across that assembly boundary, then
                // execute the unchanged production LoginDataDao write adapter.
                var account = new DBLoginData();
                foreach (PropertyInfo property in typeof(DBLoginData).GetProperties())
                    if (property.CanWrite)
                        property.SetValue(account, actual.GetType().GetProperty(property.Name).GetValue(actual));
                Require(account.Expansions == (int)Expansions.All && account.Username == username
                    && account.GM == 511 && account.Flags == 0 && account.AccountFlags == 0
                    && account.AllowedCharacters == 8 && account.Email == arguments[6]
                    && account.FirstName == arguments[7] && account.LastName == arguments[8]
                    && PasswordHash.ValidatePassword(arguments[2], account.Password),
                    "console-construction-all-expansions-other-inputs-preserved-" + requestedExpansion);
                LoginDataDao.WriteLoginData(account, NewDao(application));
                var persisted = NewDao(application).LoadByUsername(username);
                Require(persisted.Expansions == 2047 && persisted.GmLevel == account.GM
                    && persisted.Flags == account.Flags && persisted.AccountFlags == account.AccountFlags
                    && persisted.AllowedCharacters == account.AllowedCharacters && persisted.Email == account.Email
                    && persisted.FirstName == account.FirstName && persisted.LastName == account.LastName
                    && persisted.PasswordHash == account.Password,
                    "console-account-persisted-all-expansions-other-fields-preserved-" + requestedExpansion);
            }
            Console.WriteLine("FUTURE_REGISTRATION_EXPANSIONS=2047 REGISTRATION_RETRY=PASS "
                + "CONSOLE_EXPANSIONS=2047 CONSOLE_REQUESTED_VALUES=0,127,2047 OTHER_FIELDS_EXACT=PASS");
        }
    }
}
