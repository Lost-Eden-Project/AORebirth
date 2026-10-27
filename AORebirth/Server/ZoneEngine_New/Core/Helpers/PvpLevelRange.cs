namespace ZoneEngine_New.Core.Helpers
{
    using System;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using ZoneEngine_New.Core.Entities;

    /// <summary>
    /// The levels a player may fight in PvP. Fitted to the retail level table (Nadybot LEVEL_MODULE levels.csv, every
    /// level 1-220): <see cref="Max"/> is the highest level whose <see cref="Min"/> still reaches this level, so the
    /// range is mutual, and capped at the level cap.
    /// </summary>
    public static class PvpLevelRange
    {
        public const int LevelCap = 220;

        /// <summary>The lowest level <paramref name="level"/> may fight.</summary>
        public static int Min(int level)
        {
            if (level <= 5)
                return 1;
            if (level <= 14)
                return level - 4;
            if (level <= 20)
                return level - 5;
            if (level <= 23)
                return level - 6;

            // ceil((4 * level - 6) / 5)
            return (4 * level - 2) / 5;
        }

        /// <summary>The highest level <paramref name="level"/> may fight.</summary>
        public static int Max(int level)
        {
            if (level <= 9)
                return level + 4;
            if (level <= 14)
                return level + 5;
            if (level <= 21)
                return level + 6;

            return Math.Min(LevelCap, (5 * level + 6) / 4);
        }

        /// <summary>Whether two players' levels are within each other's PvP range.</summary>
        public static bool Allows(Character attacker, Character target)
        {
            ArgumentNullException.ThrowIfNull(attacker);
            ArgumentNullException.ThrowIfNull(target);

            int attackerLevel = attacker.Stats.GetOrOne(CharacterStat.Level);
            int targetLevel = target.Stats.GetOrOne(CharacterStat.Level);
            return targetLevel >= Min(attackerLevel) && targetLevel <= Max(attackerLevel);
        }
    }
}
