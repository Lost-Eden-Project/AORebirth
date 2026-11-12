namespace ZoneEngine_New.Tests
{
    using System.Collections.Generic;
    using System.Reflection;

    using AORebirth.Core.GameData;

    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Inventory;

    /// <summary>
    /// Bug #136: killing a shop NPC dropped its Cash stat as loot. The family-0 rule only covered neutral merchants;
    /// Clan (1), Omni (2) and Equipment Vendor (87) templates kept paying out.
    /// </summary>
    [TestClass]
    public sealed class VendorCorpseCreditsTests
    {
        [DataTestMethod]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(87)]
        [DataRow(100)]
        public void VendorCorpseHoldsNoCreditsWhateverItsFamily(int family)
        {
            NpcCharacter vendor = Npc(1_000_101, family, cash: 274);
            vendor.AttachShop(new VendingMachine(
                new Identity { Type = IdentityType.VendingMachine, Instance = 77 },
                new ItemTemplate { Id = 296881, Name = "Shop" }));
            Assert.IsNotNull(vendor.Shop);

            Assert.AreEqual(0, CashOf(CorpseOf(vendor)));
        }

        [TestMethod]
        public void PlainNpcCorpseStillHoldsItsCredits()
        {
            NpcCharacter mob = Npc(1_000_102, family: 87, cash: 274);
            Assert.IsNull(mob.Shop);

            Assert.AreEqual(274, CashOf(CorpseOf(mob)));
        }

        [TestMethod]
        public void FamilyZeroNpcCorpseStillHoldsNoCredits()
        {
            NpcCharacter guard = Npc(1_000_103, family: 0, cash: 274);

            Assert.AreEqual(0, CashOf(CorpseOf(guard)));
        }

        static NpcCharacter Npc(int instance, int family, int cash)
        {
            var npc = new NpcCharacter(
                new Identity { Type = IdentityType.CanbeAffected, Instance = instance },
                new StubItemBuilder())
            {
                Name = "Merchant"
            };
            npc.Stats.Set(CharacterStat.NPCFamily, family);
            npc.Stats.Set(CharacterStat.Cash, cash);
            return npc;
        }

        static Corpse CorpseOf(NpcCharacter dead)
            => new(
                new Identity { Type = IdentityType.Corpse, Instance = dead.Identity.Instance + 1 },
                dead,
                new StubGameData(HashItemCatalog.Parse("{}"), allowMissingCatMesh: true));

        static int CashOf(Corpse corpse)
        {
            var stats = (Dictionary<CharacterStat, int>)typeof(Corpse)
                .GetProperty("SourceStats", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(corpse)!;
            return stats.TryGetValue(CharacterStat.Cash, out int cash) ? cash : 0;
        }
    }
}
