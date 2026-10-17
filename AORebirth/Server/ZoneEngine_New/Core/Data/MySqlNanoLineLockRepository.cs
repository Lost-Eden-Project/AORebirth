namespace ZoneEngine_New.Core.Data
{
    using System.Collections.Generic;
    using System.Linq;
    using AORebirth.Interfaces.Persistence.Characters;
    using static SharedCharacterPersistence;

    /// <summary>Reads <c>characternanolinelocks</c> for login; the flush writer replaces the set after a lock.</summary>
    public sealed class MySqlNanoLineLockRepository : INanoLineLockRepository
    {
        readonly ICharacterPersistenceDao _persistence;

        public MySqlNanoLineLockRepository(ICharacterPersistenceDao? persistence = null)
            => _persistence = persistence ?? Create();

        public IReadOnlyList<SkillLockRecord> GetForCharacter(int characterId)
        {
            if (characterId <= 0)
                return [];

            return Run(() => _persistence.LoadNanoLineLocks(characterId)
                .Select(v => new SkillLockRecord { StatId = v.NanoStrain, ExpiresAtUtcTicks = v.ExpiresAtUtcTicks })
                .ToArray());
        }

        public void Save(int characterId, IReadOnlyList<SkillLockRecord> locks)
            => Run(() => _persistence.SaveNanoLineLocks(characterId, locks
                .Select(v => new PersistedNanoLineLockData { NanoStrain = v.StatId, ExpiresAtUtcTicks = v.ExpiresAtUtcTicks })
                .ToArray()));
    }
}
