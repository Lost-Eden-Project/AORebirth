namespace ZoneEngine_New.Core.Data
{
    using System.Collections.Generic;
    using System.Linq;
    using AORebirth.Interfaces.Persistence.Characters;
    using static SharedCharacterPersistence;

    /// <summary>Reads <c>characterpetnames</c> for login; the flush writer replaces the set after a rename.</summary>
    public sealed class MySqlPetNameRepository : IPetNameRepository
    {
        readonly ICharacterPersistenceDao _persistence;

        public MySqlPetNameRepository(ICharacterPersistenceDao? persistence = null)
            => _persistence = persistence ?? Create();

        public IReadOnlyList<PetNameRecord> GetForCharacter(int characterId)
        {
            if (characterId <= 0)
                return [];

            return Run(() => _persistence.LoadPetNames(characterId)
                .Select(v => new PetNameRecord(v.PetType, v.Name))
                .ToArray());
        }

        public void Save(int characterId, IReadOnlyList<PetNameRecord> names)
            => Run(() => _persistence.SavePetNames(characterId, names
                .Select(v => new PersistedPetNameData { PetType = v.PetType, Name = v.Name })
                .ToArray()));
    }
}
