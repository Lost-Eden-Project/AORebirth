namespace AORebirth.Database.Dao
{
    using System;
    using System.Data;
    using AORebirth.Database.Domain.Missions;

    /// <summary>
    /// Compatibility entry points for existing callers. The shared mission DAO
    /// owns all SQL and preserves the legacy false/null failure behavior.
    /// </summary>
    public static class NewCharacterStartAreaSelectionDao
    {
        public const string PendingState = AORebirth.Interfaces.Persistence.Missions.MissionStartAreaSelectionStates.Pending;

        public const string AreteState = AORebirth.Interfaces.Persistence.Missions.MissionStartAreaSelectionStates.Arete;

        public const string IccShuttleportState = AORebirth.Interfaces.Persistence.Missions.MissionStartAreaSelectionStates.IccShuttleport;

        public static bool MarkPending(int characterId)
        {
            return DatabaseDaoFactory.CreateMissionDao().MarkStartAreaSelectionPending(characterId);
        }

        /// <summary>Writes and verifies the pending selection inside character creation's transaction.</summary>
        public static bool MarkPending(int characterId, IDbConnection connection, IDbTransaction transaction)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            if (!ReferenceEquals(transaction.Connection, connection))
                throw new ArgumentException("The transaction must belong to the supplied connection.");
            return MySqlMissionDao.MarkStartAreaSelectionPending(characterId, connection, transaction);
        }

        public static string GetState(int characterId)
        {
            return DatabaseDaoFactory.CreateMissionDao().GetStartAreaSelectionState(characterId);
        }

        public static bool TryComplete(int characterId, string selectedState)
        {
            return DatabaseDaoFactory.CreateMissionDao().TryCompleteStartAreaSelection(characterId, selectedState);
        }
    }
}
