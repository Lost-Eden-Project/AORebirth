namespace ZoneEngine_New.Core.Data
{
    using AORebirth.Interfaces.Persistence.Characters;
    using static SharedCharacterPersistence;

    /// <summary>Reads <c>organizations</c> through the shared character persistence DAO.</summary>
    public sealed class MySqlOrganizationRepository : IOrganizationRepository
    {
        readonly ICharacterPersistenceDao _persistence;

        public MySqlOrganizationRepository(ICharacterPersistenceDao? persistence = null)
            => _persistence = persistence ?? Create();

        public OrganizationRecord? GetById(int organizationId)
        {
            if (organizationId <= 0)
                return null;

            PersistedOrganizationData? row = Run(() => _persistence.LoadOrganization(organizationId));
            return row == null
                ? null
                : new OrganizationRecord(row.Id, row.Creation, row.Name, row.LeaderId, row.LeaderName,
                    row.GovernmentForm, row.Description, row.Objective, row.History);
        }
    }
}
