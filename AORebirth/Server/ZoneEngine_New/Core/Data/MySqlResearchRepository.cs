namespace ZoneEngine_New.Core.Data
{
    using System.Collections.Generic;
    using System.Linq;
    using AORebirth.Interfaces.Persistence.Characters;
    using static SharedCharacterPersistence;

    /// <summary>Reads <c>characterresearch</c> for login; the flush writer replaces the set after research XP lands.</summary>
    public sealed class MySqlResearchRepository : IResearchRepository
    {
        readonly ICharacterPersistenceDao _persistence;

        public MySqlResearchRepository(ICharacterPersistenceDao? persistence = null)
            => _persistence = persistence ?? Create();

        public IReadOnlyList<ResearchProgressRecord> GetForCharacter(int characterId)
        {
            if (characterId <= 0)
                return [];

            return Run(() => _persistence.LoadResearch(characterId)
                .Select(v => new ResearchProgressRecord(v.ResearchId, v.RemainingXp))
                .ToArray());
        }

        public void Save(int characterId, ResearchChanges changes)
        {
            PersistedResearchData[] rows = changes.Rows
                .Select(v => new PersistedResearchData { ResearchId = v.ResearchId, RemainingXp = v.RemainingXp })
                .ToArray();
            if (changes.ReplaceAll)
                Run(() => _persistence.SaveResearch(characterId, rows));
            else
                Run(() => _persistence.SaveResearchChanges(characterId, rows, changes.Removed.ToArray()));
        }
    }
}
