namespace ZoneEngine_New.Tests
{
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using ZoneEngine_New.Core.Helpers;

    [TestClass]
    public sealed class NanoCostCalculatorTests
    {
        [TestMethod]
        public void NoModifierLeavesTheTemplateCost()
        {
            Assert.AreEqual(250, NanoCostCalculator.Compute(250, npCostModifierPercent: 0, Breed.Solitus));
        }

        [TestMethod]
        public void NegativeModifierRemovesItsPercentage()
        {
            Assert.AreEqual(200, NanoCostCalculator.Compute(250, npCostModifierPercent: -20, Breed.Solitus));
        }

        [TestMethod]
        public void PositiveModifierAddsItsPercentage()
        {
            Assert.AreEqual(300, NanoCostCalculator.Compute(250, npCostModifierPercent: 20, Breed.Solitus));
        }

        [TestMethod]
        public void StackedReductionsStopAtTheBreedCap()
        {
            Assert.AreEqual(100, NanoCostCalculator.Compute(200, npCostModifierPercent: -90, Breed.Solitus));
            Assert.AreEqual(100, NanoCostCalculator.Compute(200, npCostModifierPercent: -90, Breed.Opifex));
            Assert.AreEqual(90, NanoCostCalculator.Compute(200, npCostModifierPercent: -90, Breed.Nanomage));
            Assert.AreEqual(110, NanoCostCalculator.Compute(200, npCostModifierPercent: -90, Breed.Atrox));
        }

        [TestMethod]
        public void ACostBearingNanoNeverBecomesFree()
        {
            Assert.AreEqual(1, NanoCostCalculator.Compute(1, npCostModifierPercent: -50, Breed.Solitus));
        }

        [TestMethod]
        public void FreeNanosStayFree()
        {
            Assert.AreEqual(0, NanoCostCalculator.Compute(0, npCostModifierPercent: -50, Breed.Solitus));
        }
    }
}
