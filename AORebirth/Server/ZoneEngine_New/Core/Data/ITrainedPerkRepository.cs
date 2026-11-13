namespace ZoneEngine_New.Core.Data
{
    using System.Collections.Generic;

    /// <summary>
    /// One save of a character's trained perks: <paramref name="Trained"/> must be in storage and <paramref name="Removed"/>
    /// must not. Perks in storage that are in neither list are left alone, unless <paramref name="ReplaceAll"/> (a GM
    /// replace) makes storage exactly <paramref name="Trained"/>.
    /// </summary>
    public sealed record TrainedPerkChanges(IReadOnlyList<int> Trained, IReadOnlyList<int> Removed, bool ReplaceAll);

    /// <summary>Trained perk ids (charactersperks).</summary>
    public interface ITrainedPerkRepository
    {
        IReadOnlyList<int> GetForCharacter(int characterId);

        /// <summary>Stores <paramref name="changes"/>; perks it does not name stay as stored.</summary>
        void Save(int characterId, TrainedPerkChanges changes);
    }
}
