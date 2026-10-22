namespace ZoneEngine_New.Tests
{
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using ZoneEngine_New.Core.Helpers;

    [TestClass]
    public sealed class PassiveRegenCalculatorTests
    {
        [TestMethod]
        public void HealthDeltaAddsBodyDevelopmentTrickleAndHealDeltaStat()
        {
            int delta = PassiveRegenCalculator.ComputeHealthDelta((int)Breed.Solitus, bodyDevelopment: 270, healDeltaStat: 100000);

            Assert.AreEqual(100005, delta);
        }

        [TestMethod]
        public void HealthDeltaWithoutAStatIsBreedPlusBodyDevelopment()
        {
            int delta = PassiveRegenCalculator.ComputeHealthDelta((int)Breed.Nanomage, bodyDevelopment: 99);

            Assert.AreEqual(2, delta);
        }

        [TestMethod]
        public void NanoDeltaUsesNanoPoolTrickleAndNanoDeltaStat()
        {
            int delta = PassiveRegenCalculator.ComputeNanoDelta((int)Breed.Atrox, nanoPool: 250, nanoDeltaStat: 7);

            Assert.AreEqual(11, delta);
        }

        [TestMethod]
        public void AtroxWith500SkillHeals9AndRegains7Nano()
        {
            Assert.AreEqual(9, PassiveRegenCalculator.ComputeHealthDelta((int)Breed.Atrox, bodyDevelopment: 500));
            Assert.AreEqual(7, PassiveRegenCalculator.ComputeNanoDelta((int)Breed.Atrox, nanoPool: 500));
        }

        [TestMethod]
        public void SittingGivesOneAndAHalfTimesTheSkillBonusOnly()
        {
            // Solitus base 3, Body Development 400 -> bonus 4 standing, 6 sitting; the stat is not multiplied.
            Assert.AreEqual(3 + 4 + 10, PassiveRegenCalculator.ComputeHealthDelta((int)Breed.Solitus, bodyDevelopment: 400, healDeltaStat: 10));
            Assert.AreEqual(3 + 6 + 10, PassiveRegenCalculator.ComputeHealthDelta((int)Breed.Solitus, bodyDevelopment: 400, healDeltaStat: 10, sitting: true));
            // Nano Pool 300 -> bonus 3 standing, 4 sitting (4.5 rounded down).
            Assert.AreEqual(4 + 3, PassiveRegenCalculator.ComputeNanoDelta((int)Breed.Nanomage, nanoPool: 300));
            Assert.AreEqual(4 + 4, PassiveRegenCalculator.ComputeNanoDelta((int)Breed.Nanomage, nanoPool: 300, sitting: true));
        }

        [TestMethod]
        public void SittingShortensTheHealthTickByOneSecondAndLeavesTheNanoTick()
        {
            Assert.AreEqual(22, PassiveRegenCalculator.ComputeHealthIntervalSeconds(stamina: 210, sitting: false));
            Assert.AreEqual(21, PassiveRegenCalculator.ComputeHealthIntervalSeconds(stamina: 210, sitting: true));
            Assert.AreEqual(2, PassiveRegenCalculator.ComputeHealthIntervalSeconds(stamina: 900, sitting: false));
            Assert.AreEqual(1, PassiveRegenCalculator.ComputeHealthIntervalSeconds(stamina: 900, sitting: true));
            Assert.AreEqual(24, PassiveRegenCalculator.ComputeNanoIntervalSeconds(psychic: 120));
        }

        [TestMethod]
        public void NegativeDeltaStatReducesTheTick()
        {
            int delta = PassiveRegenCalculator.ComputeHealthDelta((int)Breed.Opifex, bodyDevelopment: 0, healDeltaStat: -10);

            Assert.AreEqual(-7, delta);
        }
    }
}
