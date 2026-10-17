namespace ZoneEngine_New.Core.Data
{
    using System.Collections.Generic;

    /// <summary>
    /// Nano line cooldowns (characternanolinelocks): a nano's local cooldown (stat 254) locks its strain. Records reuse
    /// <see cref="SkillLockRecord"/>: StatId holds the locked nano strain.
    /// </summary>
    public interface INanoLineLockRepository
    {
        IReadOnlyList<SkillLockRecord> GetForCharacter(int characterId);

        /// <summary>Replaces the character's stored nano line locks with <paramref name="locks"/>.</summary>
        void Save(int characterId, IReadOnlyList<SkillLockRecord> locks);
    }
}
