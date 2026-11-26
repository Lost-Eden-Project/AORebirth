namespace ZoneEngine_New.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Inventory.Dat;
    using ZoneEngine_New.Core.Trade;

    /// <summary>
    /// items.dat ShopInventory lines above QL 255 (the extended form the City Buildings shops use) and the retail rule
    /// that a child outside the line's band stocks at its nearest QL. Evidence: the 2026-11-26 retail capture, ShopUpdate
    /// for Neutral City Buildings (2CS6, band 300-300) sold every building at QL 300, the Tax Refund Office (QL 200) at 200
    /// and the AO Logo Sculpture (QL 1) at 1; Social City Buildings (7UC2, 300-300) sold the Bronto Hot Dog Stand at 300
    /// and its QL 1 statues, benches and teleporters at 1.
    /// </summary>
    [TestClass]
    public sealed class ShopInventoryExtendedBandTests
    {
        const string CatalogJson =
            """
            {
                "CITY": { "Children": [ "BLDG", "TAXO", "LOGO" ] },
                "BLDG": { "Templates": [254136] },
                "TAXO": { "Templates": [296421] },
                "LOGO": { "Templates": [297200] },
                "ORGO": { "Templates": [83877] },
                "KATA": { "Children": [ "KFAR", "KSAM" ] },
                "KFAR": { "Templates": [159040, 159041] },
                "KSAM": { "Templates": [159048] }
            }
            """;

        [TestMethod]
        public void ExtendedLineDecodesItsBandAmountAndRegen()
        {
            // 2CS6 as the stock items.dat carries it: both quality bytes 0, Amount 44 (0x2C), Regen 1, Extra 0x0101012C.
            ShopInventoryEntry entry = Map(new DatShopItem
            {
                Hash = Hash("2CS6"), MinQl = 0, MaxQl = 0, Amount = 44, Regen = 1, Extra = 0x0101012C,
                Interval = 60, Unknown1 = 1, Chance = 100
            });

            Assert.AreEqual("2CS6", entry.Hash);
            Assert.AreEqual(300, entry.MinQl);
            Assert.AreEqual(300, entry.MaxQl);
            Assert.AreEqual(1, entry.Amount);
            Assert.AreEqual(1, entry.Regen);
            Assert.AreEqual(60, entry.Interval);
            Assert.AreEqual(100u, entry.Chance);
        }

        [TestMethod]
        public void ExtendedLineWithAWideBandDecodesBothEdges()
        {
            // 1-300, Amount 5, Regen 1: min 0x0001 in Amount/Regen, Extra = 0x0105 << 16 | 0x012C.
            ShopInventoryEntry entry = Map(new DatShopItem
            {
                Hash = Hash("KATA"), Amount = 1, Regen = 0, Extra = 0x0105012C, Chance = 100
            });

            Assert.AreEqual(1, entry.MinQl);
            Assert.AreEqual(300, entry.MaxQl);
            Assert.AreEqual(5, entry.Amount);
            Assert.AreEqual(1, entry.Regen);
        }

        [TestMethod]
        public void ZeroBandExtendedLineKeepsItsAmount()
        {
            // ORGO at the Organization registration shop (81799): really 0-0, Amount 5, Regen 1 (Extra 0x01050000).
            ShopInventoryEntry entry = Map(new DatShopItem { Hash = Hash("ORGO"), Extra = 0x01050000, Chance = 100 });

            Assert.AreEqual(0, entry.MinQl);
            Assert.AreEqual(0, entry.MaxQl);
            Assert.AreEqual(5, entry.Amount);
            Assert.AreEqual(1, entry.Regen);
        }

        [TestMethod]
        public void OrdinaryLineIsReadAsStored()
        {
            ShopInventoryEntry entry = Map(new DatShopItem
            {
                Hash = Hash("CRMC"), MinQl = 10, MaxQl = 10, Amount = 1, Regen = 1, Extra = null, Interval = 60, Chance = 100
            });

            Assert.AreEqual(10, entry.MinQl);
            Assert.AreEqual(10, entry.MaxQl);
            Assert.AreEqual(1, entry.Amount);
            Assert.AreEqual(1, entry.Regen);
        }

        [TestMethod]
        public void ExtendedLineStocksEveryChildAtItsNearestQl()
        {
            // Neutral City Buildings: the 300-300 line over QL 300 buildings, the QL 200 Tax Refund Office and the QL 1
            // AO Logo Sculpture stocks all three, each at its own QL.
            ShopStock stock = new();
            stock.EnsureFreshFromShopInventory(
                Lines(new DatShopItem { Hash = Hash("CITY"), Amount = 44, Regen = 1, Extra = 0x0101012C, Chance = 100 }),
                Minter(),
                new Random(7));

            Dictionary<int, int> byItem = stock.Slots.ToDictionary(slot => slot.LowId, slot => slot.Quality);
            Assert.AreEqual(3, byItem.Count, "every child of the 300-300 line is stocked once");
            Assert.AreEqual(300, byItem[254136]);
            Assert.AreEqual(200, byItem[296421]);
            Assert.AreEqual(1, byItem[297200]);
        }

        [TestMethod]
        public void ZeroBandLineStocksTheChildAtItsLowestQl()
        {
            // The 0-0 ORGO line sells the QL 10 Monarchy government chip at 10; read as Amount 0 it stocked nothing.
            ShopStock stock = new();
            stock.EnsureFreshFromShopInventory(
                Lines(new DatShopItem { Hash = Hash("ORGO"), Extra = 0x01050000, Chance = 100 }),
                Minter(),
                new Random(3));

            Assert.AreEqual(1, stock.Slots.Count);
            Assert.AreEqual(83877, stock.Slots[0].LowId);
            Assert.AreEqual(10, stock.Slots[0].Quality);
        }

        [TestMethod]
        public void ChildReachingTheBandStillRollsInsideIt()
        {
            // The unchanged rule: a QL 1-49 family and a QL 200 family under a 1-200 line roll inside the band and
            // inside the family picked.
            ShopStock stock = new();
            stock.EnsureFreshFromShopInventory(
                Lines(new DatShopItem { Hash = Hash("KATA"), MinQl = 1, MaxQl = 200, Amount = 1, Regen = 1, Chance = 100 }),
                Minter(),
                new Random(11));

            Assert.AreEqual(2, stock.Slots.Count);
            foreach (ShopStockSlot slot in stock.Slots)
            {
                if (slot.LowId == 159048)
                    Assert.AreEqual(200, slot.Quality);
                else
                    Assert.IsTrue(slot.Quality >= 1 && slot.Quality <= 49, "Farmer katana rolled QL " + slot.Quality);
            }
        }

        [TestMethod]
        public void ChildAboveTheBandStocksAtItsLowestQl()
        {
            // A band below every family: the family nearest the band stocks at its lowest QL.
            ShopStock stock = new();
            stock.EnsureFreshFromShopInventory(
                Lines(new DatShopItem { Hash = Hash("KSAM"), MinQl = 10, MaxQl = 20, Amount = 1, Regen = 1, Chance = 100 }),
                Minter(),
                new Random(5));

            Assert.AreEqual(1, stock.Slots.Count);
            Assert.AreEqual(200, stock.Slots[0].Quality);
        }

        static ShopInventoryEntry Map(DatShopItem line)
        {
            IReadOnlyList<ShopInventoryEntry> lines = Lines(line);
            Assert.AreEqual(1, lines.Count);
            return lines[0];
        }

        static IReadOnlyList<ShopInventoryEntry> Lines(params DatShopItem[] lines)
        {
            DatItemTemplate dat = new()
            {
                ID = 295892,
                Rdb = new DatRdbTemplate { ShopInventory = new List<DatShopItem>(lines) }
            };
            return DatItemMapper.ToTemplate(dat, "Neutral City Buildings").ShopInventory;
        }

        static uint Hash(string text)
            => ((uint)text[0] << 24) | ((uint)text[1] << 16) | ((uint)text[2] << 8) | text[3];

        static HashItemMinter Minter()
            => new(
                new StubGameData(HashItemCatalog.Parse(CatalogJson)),
                new StubCatalog()
                    .Add(254136, 300)
                    .Add(296421, 200)
                    .Add(297200, 1)
                    .Add(83877, 10)
                    .Add(159040, 1)
                    .Add(159041, 49)
                    .Add(159048, 200),
                new StubItemBuilder());
    }
}
