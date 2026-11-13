namespace ZoneEngine_New.Core.Data
{
    using System.Collections.Generic;

    /// <summary>One personal research tier in progress and the research XP it still needs.</summary>
    public readonly record struct ResearchProgressRecord(int ResearchId, int RemainingXp);

    /// <summary>
    /// One save of a character's research progress: <paramref name="Rows"/> must be in storage with these values and the
    /// <paramref name="Removed"/> tiers must not. Stored tiers in neither list are left alone, unless
    /// <paramref name="ReplaceAll"/> (a GM clear) makes storage exactly <paramref name="Rows"/>.
    /// </summary>
    public sealed record ResearchChanges(IReadOnlyList<ResearchProgressRecord> Rows, IReadOnlyList<int> Removed, bool ReplaceAll);

    /// <summary>Personal research progress (characterresearch). Finished tiers are trained perks, not rows here.</summary>
    public interface IResearchRepository
    {
        IReadOnlyList<ResearchProgressRecord> GetForCharacter(int characterId);

        /// <summary>Stores <paramref name="changes"/>; tiers it does not name stay as stored.</summary>
        void Save(int characterId, ResearchChanges changes);
    }
}
