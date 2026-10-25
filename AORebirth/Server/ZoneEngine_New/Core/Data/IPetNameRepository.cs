namespace ZoneEngine_New.Core.Data
{
    using System.Collections.Generic;

    /// <summary>One /pet rename kept for a pet type (characterpetnames).</summary>
    public sealed record PetNameRecord(int PetType, string Name);

    /// <summary>Pet names per pet type (characterpetnames): a summoned pet of that type takes its name.</summary>
    public interface IPetNameRepository
    {
        IReadOnlyList<PetNameRecord> GetForCharacter(int characterId);

        /// <summary>Replaces the character's stored pet names with <paramref name="names"/>.</summary>
        void Save(int characterId, IReadOnlyList<PetNameRecord> names);
    }
}
