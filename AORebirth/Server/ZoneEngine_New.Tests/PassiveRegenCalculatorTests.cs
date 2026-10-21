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
        public void NanoDeltaUsesBodyDevelopmentTrickleAndNanoDeltaStat()
        {
            int delta = PassiveRegenCalculator.ComputeNanoDelta((int)Breed.Atrox, bodyDevelopment: 250, nanoDeltaStat: 7);

            Assert.AreEqual(11, delta);
        }

        [TestMethod]
        public void AtroxWith500BodyDevelopmentHeals9AndRegains7Nano()
        {
            Assert.AreEqual(9, PassiveRegenCalculator.ComputeHealthDelta((int)Breed.Atrox, bodyDevelopment: 500));
            Assert.AreEqual(7, PassiveRegenCalculator.ComputeNanoDelta((int)Breed.Atrox, bodyDevelopment: 500));
        }

        [TestMethod]
        public void NegativeDeltaStatReducesTheTick()
        {
            int delta = PassiveRegenCalculator.ComputeHealthDelta((int)Breed.Opifex, bodyDevelopment: 0, healDeltaStat: -10);

            Assert.AreEqual(-7, delta);
        }
    }
}
