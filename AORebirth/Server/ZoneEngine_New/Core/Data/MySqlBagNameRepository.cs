namespace ZoneEngine_New.Core.Data
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using AORebirth.Interfaces.Persistence.Characters;
    using ZoneEngine_New.Core.Logging;
    using static SharedCharacterPersistence;

    /// <summary>
    /// Reads <c>characterbagnames</c> for login; the flush writer replaces the set after a rename. A read that fails
    /// (the table not migrated yet, a database hiccup) logs and loads no names rather than refusing the login: bag
    /// names are cosmetic.
    /// </summary>
    public sealed class MySqlBagNameRepository : IBagNameRepository
    {
        readonly ICharacterPersistenceDao _persistence;
        readonly IZoneLogger? _logger;

        public MySqlBagNameRepository(IZoneLogger? logger = null, ICharacterPersistenceDao? persistence = null)
        {
            _logger = logger;
            _persistence = persistence ?? Create();
        }

        public IReadOnlyList<BagNameRecord> GetForCharacter(int characterId)
        {
            if (characterId <= 0)
                return [];

            try
            {
                return Run(() => _persistence.LoadBagNames(characterId)
                    .Select(v => new BagNameRecord(v.BagInstanceId, v.Name))
                    .ToArray());
            }
            catch (Exception exception)
            {
                _logger?.Error(exception, "Bag names could not be read; the character's bags load unnamed char=" + characterId);
                return [];
            }
        }

        public void Save(int characterId, IReadOnlyList<BagNameRecord> names)
            => Run(() => _persistence.SaveBagNames(characterId, names
                .Select(v => new PersistedBagNameData { BagInstanceId = v.BagInstanceId, Name = v.Name })
                .ToArray()));
    }
}
