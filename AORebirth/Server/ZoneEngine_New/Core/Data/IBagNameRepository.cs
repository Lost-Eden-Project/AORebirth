namespace ZoneEngine_New.Core.Data
{
    using System.Collections.Generic;

    /// <summary>One name a player gave their own bag (characterbagnames), keyed by the bag's item instance.</summary>
    public sealed record BagNameRecord(int BagInstanceId, string Name);

    /// <summary>Bag names per character (characterbagnames).</summary>
    public interface IBagNameRepository
    {
        IReadOnlyList<BagNameRecord> GetForCharacter(int characterId);

        /// <summary>Replaces the character's stored bag names with <paramref name="names"/>.</summary>
        void Save(int characterId, IReadOnlyList<BagNameRecord> names);
    }
}
