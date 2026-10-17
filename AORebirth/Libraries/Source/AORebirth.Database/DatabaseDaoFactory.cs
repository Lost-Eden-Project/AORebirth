namespace AORebirth.Database
{
    using System.Data;
    using AORebirth.Database.Domain.Accounts;
    using AORebirth.Database.Domain.Characters;
    using AORebirth.Database.Domain.Missions;
    using AORebirth.Interfaces.Persistence.Accounts;
    using AORebirth.Interfaces.Persistence.Characters;
    using AORebirth.Interfaces.Persistence.Missions;

    /// <summary>
    /// Explicit construction boundary for domain-oriented database access.
    /// </summary>
    public static class DatabaseDaoFactory
    {
        /// <summary>Creates full gameplay storage without opening a connection.</summary>
        public static ICharacterPersistenceDao CreateCharacterPersistenceDao()
        {
            return new MySqlCharacterPersistenceDao();
        }

        /// <summary>Uses the caller's open connection and transaction without committing or disposing them.</summary>
        public static ICharacterPersistenceDao CreateCharacterPersistenceDao(IDbConnection connection, IDbTransaction transaction)
        {
            return new MySqlCharacterPersistenceDao(connection, transaction);
        }

        /// <summary>Creates the MySQL character DAO lazily; no runtime initialization or connection is opened.</summary>
        public static ICharacterDao CreateCharacterDao()
        {
            return new MySqlCharacterDao();
        }

        /// <summary>
        /// Creates the MySQL account DAO without opening a connection or initializing runtime services.
        /// Unsupported configured providers are rejected before account SQL is executed.
        /// </summary>
        public static IAccountDao CreateAccountDao()
        {
            return new MySqlAccountDao();
        }

        /// <summary>
        /// Creates the configured MySQL mission DAO without opening a connection.
        /// Each operation owns its connection; unsupported configured providers are
        /// rejected before mission SQL is executed. No runtime initialization occurs here.
        /// </summary>
        public static IMissionDao CreateMissionDao()
        {
            return new MySqlMissionDao();
        }
    }
}
