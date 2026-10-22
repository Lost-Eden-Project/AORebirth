namespace ZoneEngine_New.Core.Helpers
{
    using System;

    using SmokeLounge.AOtomation.Messaging.GameData;

    /// <summary>
    /// Natural health/nano regeneration. Each delta is a breed base, plus a skill bonus of one per 100 points
    /// (Body Development for health, Nano Pool for nano), plus the stored HealDelta / NanoDelta stat (gear, buffs,
    /// explicit sets). Sitting multiplies the skill bonus by 1.5 and shortens the health tick by one second; the
    /// nano tick keeps its standing rate. Non-playable breeds have a 0 breed base. See KNOWN_DECISIONS.md,
    /// "Passive Regeneration Follows The Sitting Rules".
    /// </summary>
    public static class PassiveRegenCalculator
    {
        public const double SittingSkillBonusMultiplier = 1.5;

        public static int ComputeHealthDelta(int breed, int bodyDevelopment, int healDeltaStat = 0, bool sitting = false)
            => BreedHealthBase(breed) + SkillBonus(bodyDevelopment, sitting) + healDeltaStat;

        public static int ComputeNanoDelta(int breed, int nanoPool, int nanoDeltaStat = 0, bool sitting = false)
            => BreedNanoBase(breed) + SkillBonus(nanoPool, sitting) + nanoDeltaStat;

        /// <summary>Standing: 29 - Stamina / 30, at least 2. Sitting: one second less, at least 1.</summary>
        public static double ComputeHealthIntervalSeconds(int stamina, bool sitting)
        {
            int standing = Math.Max(2, 29 - Math.Max(0, stamina) / 30);
            return sitting ? Math.Max(1, standing - 1) : standing;
        }

        /// <summary>28 - 2 * (Psychic / 60), at least 2. Sitting does not change it.</summary>
        public static double ComputeNanoIntervalSeconds(int psychic)
            => Math.Max(2, 28 - (2 * (Math.Max(0, psychic) / 60)));

        /// <summary>One per 100 skill points; sitting gives 1.5 times that, rounded down.</summary>
        public static int SkillBonus(int skill, bool sitting)
        {
            int bonus = Math.Max(0, skill) / 100;
            return sitting ? (int)Math.Floor(bonus * SittingSkillBonusMultiplier) : bonus;
        }

        static int BreedHealthBase(int breed)
        {
            return (Breed)breed switch
            {
                Breed.Solitus => 3,
                Breed.Opifex => 3,
                Breed.Nanomage => 2,
                Breed.Atrox => 4,
                _ => 0
            };
        }

        static int BreedNanoBase(int breed)
        {
            return (Breed)breed switch
            {
                Breed.Solitus => 3,
                Breed.Opifex => 3,
                Breed.Nanomage => 4,
                Breed.Atrox => 2,
                _ => 0
            };
        }
    }
}
