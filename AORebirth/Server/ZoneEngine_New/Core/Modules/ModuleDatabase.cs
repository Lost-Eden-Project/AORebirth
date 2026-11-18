namespace ZoneEngine_New.Core.Modules
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;

    using AORebirth.Database.Schema;
    using AORebirth.Modules.Data;
    using AORebirth.Modules.World;

    using MySqlConnector;

    /// <summary>
    /// A module's own tables (module API 1.14): <c>mod_&lt;id&gt;_*</c>, created by the migrations the module ships and
    /// DatabaseMigrationTool applies. Connections come from a pool of the module's own (its own connection string, so a
    /// module that leaks connections exhausts only its pool) and are never opened on a playfield tick.
    /// </summary>
    internal sealed class ModuleDatabase : IModuleDatabase
    {
        /// <summary>The most connections one module holds open at once.</summary>
        internal const int MaxPoolSize = 8;

        private readonly string _moduleId;
        private readonly CapabilityGate _gate;
        private readonly string? _connectionString;
        private readonly string? _prefix;

        public ModuleDatabase(string moduleId, CapabilityGate gate, string? serverConnectionString)
        {
            _moduleId = moduleId;
            _gate = gate;
            _prefix = ModuleSchema.IsValidModuleId(moduleId) ? ModuleSchema.TablePrefix(moduleId) : null;
            if (serverConnectionString != null)
                _connectionString = new MySqlConnectionStringBuilder(serverConnectionString)
                {
                    ApplicationName = "zone-module:" + moduleId,
                    MaximumPoolSize = MaxPoolSize,
                    MinimumPoolSize = 0,
                    ConnectionTimeout = 5,
                    DefaultCommandTimeout = 30,
                    AllowLoadLocalInfile = false
                }.ConnectionString;
        }

        public string TablePrefix => _prefix ?? throw NoTables();

        public string Table(string name)
        {
            if (_prefix == null)
                throw NoTables();
            try
            {
                return ModuleSchema.Table(_moduleId, name);
            }
            catch (ModuleMigrationException exception)
            {
                throw new ArgumentException(exception.Message, nameof(name));
            }
        }

        public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default)
        {
            _gate.Require(ModuleCapabilities.Database);
            // Database I/O on a tick stalls every player on that playfield.
            if (Playfield.Playfield.IsOnTickThread)
                throw new InvalidOperationException("Module " + _moduleId + " opened a database connection on a playfield tick; "
                    + "hand database work to another thread.");
            if (_connectionString == null)
                throw new InvalidOperationException("This zone has no database for module " + _moduleId + ".");

            var connection = new MySqlConnection(_connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Before the module initializes: its shipped migrations are scope-checked and every one is applied unchanged, or
        /// the module does not load. A module that ships none needs nothing.
        /// </summary>
        internal static void RequireCurrent(string moduleId, string moduleDirectory, CapabilityGate gate, string? serverConnectionString)
        {
            IReadOnlyList<ModuleMigration> migrations = ModuleSchema.Discover(moduleDirectory, moduleId);
            if (migrations.Count == 0)
                return;
            if (!gate.Allows(ModuleCapabilities.Database))
                throw new InvalidOperationException("Module " + moduleId + " ships database migrations, but Config/Modules.json Capabilities "
                    + "does not grant it '" + ModuleCapabilities.Database + "'.");
            if (serverConnectionString == null)
                throw new InvalidOperationException("Module " + moduleId + " ships database migrations, but this zone has no database.");

            using var connection = new MySqlConnection(new MySqlConnectionStringBuilder(serverConnectionString)
            {
                ConnectionTimeout = 5, DefaultCommandTimeout = 15, AllowLoadLocalInfile = false, Pooling = false
            }.ConnectionString);
            connection.Open();
            ModuleSchemaStatus status = ModuleSchema.Evaluate(moduleId, migrations, ModuleSchema.ReadApplied(connection, moduleId));
            if (!status.IsCurrent)
                throw new InvalidOperationException(status.Message);
        }

        private InvalidOperationException NoTables() => new(string.Format(CultureInfo.InvariantCulture,
            "Module id '{0}' cannot own tables: it must be a letter, then letters and digits, at most {1}.", _moduleId, ModuleSchema.MaxModuleIdLength));
    }
}
