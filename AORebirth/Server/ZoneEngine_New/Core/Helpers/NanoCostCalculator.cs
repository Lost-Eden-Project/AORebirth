namespace ZoneEngine_New.Core.Helpers
{
    using System;

    using SmokeLounge.AOtomation.Messaging.GameData;

    /// <summary>
    /// Nano point cost after <c>NPCostModifier</c> (stat 318, "% Add. Nano Cost"): the percent added to the cost, so a
    /// reduction is negative. The client (Gamecode.dll FUN_1004e705, FUN_1004f9c4) pays NanoPoints x (100 + modifier)
    /// / 100, rounded, with the paid share floored by breed so stacked reductions stop at the breed's cap.
    /// </summary>
    public static class NanoCostCalculator
    {
        /// <summary>
        /// Largest share of a nano's cost the breed's modifiers may remove: Solitus and Opifex 50%, Nanomage 55%, Atrox
        /// 45%. Other breeds (NPCs) have no cap in the client, beyond a cost-bearing nano never becoming free.
        /// </summary>
        public static int MaxReductionPercent(Breed breed) => breed switch
        {
            Breed.Solitus or Breed.Opifex => 50,
            Breed.Nanomage => 55,
            Breed.Atrox => 45,
            _ => 100
        };

        public static int Compute(int baseCost, int npCostModifierPercent, Breed breed)
        {
            if (baseCost <= 0)
                return 0;

            int paidPercent = Math.Max(100 + npCostModifierPercent, 100 - MaxReductionPercent(breed));
            if (paidPercent == 100)
                return baseCost;

            int cost = (int)Math.Round(baseCost * (double)paidPercent / 100.0, MidpointRounding.ToEven);
            return Math.Max(1, cost);
        }
    }
}
