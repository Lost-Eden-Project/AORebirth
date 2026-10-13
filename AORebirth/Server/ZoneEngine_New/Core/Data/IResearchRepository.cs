namespace ZoneEngine_New.Core.Data
{
    using System.Collections.Generic;

    /// <summary>One personal research tier in progress and the research XP it still needs.</summary>
    public readonly record struct ResearchProgressRecord(int ResearchId, int RemainingXp);

    /// <summary>Personal research progress (characterresearch). Finished tiers are trained perks, not rows here.</summary>
    public interface IResearchRepository
    {
        IReadOnlyList<ResearchProgressRecord> GetForCharacter(int characterId);

        /// <summary>Replaces the character's stored research progress with <paramref name="research"/>.</summary>
        void Save(int characterId, IReadOnlyList<ResearchProgressRecord> research);
    }
}
