namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using AORebirth.Core.GameData;
using AORebirth.Enums;

using LostEden.Protocol;
using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;
using LostEden.Protocol.Messages.N3Messages;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Chat;
using ZoneEngine_New.Core.Cities;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Organizations;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Trade;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Org cities on the player's path: the city controller's window, buying a district, building and demolishing houses,
/// and the advantages a district gives. The purchase and build run through <see cref="CityService"/> exactly as the
/// engine runs them (the tick, the district's queue, the player's write-behind lane), against an in-memory store with the
/// MySQL store's rules, and fail if credits or a house item could be spent twice, lost, or taken for a refused request.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class CityPlotTests
{
    const int Playfield = 585;
    const int OrganizationId = 4242;
    const int BuyerId = 61001;
    const int MemberId = 61002;
    const int HeadquartersTemplate = 6946817;
    const int GridTemplate = 6946818;
    const int HeadquartersItem = 254195;
    const int GridItem = 254135;
    const int CafeTemplate = 6946828;
    const int CafeItem = 254183;
    const long Price = 50_000_000;

    // ------------------------------------------------------------------------------------------------ footprints, rules

    [TestMethod]
    public void RotationSwapsTheSidesAndKeepsEveryCoveredTile()
    {
        HouseFootprint footprint = HouseFootprint.FromBits(3, 2, [0b0001_0111]); // row 0: x 0,1,2; row 1: x 1
        foreach (int degrees in new[] { 0, 90, 180, 270 })
        {
            HouseFootprint turned = footprint.Rotated(degrees);
            bool swap = degrees is 90 or 270;
            Assert.AreEqual(swap ? 2 : 3, turned.TilesX, "width at " + degrees);
            Assert.AreEqual(swap ? 3 : 2, turned.TilesZ, "depth at " + degrees);
            Assert.AreEqual(footprint.OccupiedCount, turned.OccupiedCount, "covered tiles at " + degrees);
        }

        Assert.IsFalse(HouseFootprint.IsValidRotation(45));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => footprint.Rotated(45));
    }

    [TestMethod]
    public void HousesOverlapOnlyWhereBothCoverATile()
    {
        HouseFootprint l = HouseFootprint.FromBits(2, 2, [0b0111]); // (1,1) open
        HouseFootprint dot = HouseFootprint.Solid(1, 1);
        Assert.IsTrue(CityPlacement.Intersects(l, 0, 0, dot, 0, 0));
        Assert.IsFalse(CityPlacement.Intersects(l, 0, 0, dot, 1, 1), "the open tile of the L takes another house");
        Assert.IsFalse(CityPlacement.Intersects(l, 0, 0, dot, 2, 0));
    }

    [TestMethod]
    public void PlacementFollowsTheClientsRules()
    {
        CityControllerDefinition controller = Controller(out _);
        HouseTemplateDefinition hq = Template(HeadquartersTemplate, HouseType.Headquarters, 4, 4);
        HouseTemplateDefinition grid = Template(GridTemplate, HouseType.Grid, 2, 2);
        HouseTemplateDefinition market = Template(6946900, HouseType.Market, 2, 2);
        var templates = new Dictionary<int, HouseTemplateDefinition> { [hq.Id] = hq, [grid.Id] = grid, [market.Id] = market };
        HouseTemplateDefinition? Of(int id) => templates.TryGetValue(id, out HouseTemplateDefinition? t) ? t : null;
        var none = new List<CityHouse>();

        Assert.AreEqual(BuildRefusal.NeedsHeadquarters, CityPlacement.Check(controller, grid, 0, 0, 0, none, Of, 40, false));
        Assert.AreEqual(BuildRefusal.None, CityPlacement.Check(controller, hq, 0, 0, 0, none, Of, 40, false));
        Assert.AreEqual(BuildRefusal.BadRotation, CityPlacement.Check(controller, hq, 0, 0, 45, none, Of, 40, false));
        Assert.AreEqual(BuildRefusal.MarketNotAllowed, CityPlacement.Check(controller, market, 0, 0, 0, [House(1, hq.Id, 0, 0)], Of, 40, false));
        // The district is tiles 0..39 x 0..39; tile (5, 5) of zone 0 is kept clear.
        Assert.AreEqual(BuildRefusal.OutsideDistrict, CityPlacement.Check(controller, hq, 38, 0, 0, none, Of, 40, false));
        Assert.AreEqual(BuildRefusal.OutsideDistrict, CityPlacement.Check(controller, hq, 0, 38, 0, none, Of, 40, false));
        Assert.AreEqual(BuildRefusal.OutsideDistrict, CityPlacement.Check(controller, hq, -1, 0, 0, none, Of, 40, false));
        Assert.AreEqual(BuildRefusal.NotBuildable, CityPlacement.Check(controller, hq, 3, 3, 0, none, Of, 40, false));
        Assert.AreEqual(BuildRefusal.None, CityPlacement.Check(controller, hq, 9, 2, 0, none, Of, 40, false), "a house may span two zones");

        List<CityHouse> withHq = [House(1, hq.Id, 0, 0)];
        Assert.AreEqual(BuildRefusal.OneOfTypeOnly, CityPlacement.Check(controller, hq, 10, 0, 0, withHq, Of, 40, false));
        Assert.AreEqual(BuildRefusal.Overlaps, CityPlacement.Check(controller, grid, 3, 3, 0, withHq, Of, 40, false));
        Assert.AreEqual(BuildRefusal.None, CityPlacement.Check(controller, grid, 4, 0, 0, withHq, Of, 40, false));
        Assert.AreEqual(BuildRefusal.TooManyHouses, CityPlacement.Check(controller, grid, 4, 0, 0, withHq, Of, 1, false));
        // A headquarters being demolished no longer carries the district.
        List<CityHouse> demolishing = [House(1, hq.Id, 0, 0) with { DemolishAtUtc = DateTime.UtcNow }];
        Assert.AreEqual(BuildRefusal.OneOfTypeOnly, CityPlacement.Check(controller, hq, 10, 0, 0, demolishing, Of, 40, false));
        Assert.AreEqual(BuildRefusal.NeedsHeadquarters, CityPlacement.Check(controller, grid, 4, 0, 0, demolishing, Of, 40, false));
    }

    [TestMethod]
    public void AdvantageHashesReadAsTheirFourCharacters()
    {
        Assert.AreEqual("GIHO", CityService.HashText(1195984975));
        Assert.AreEqual("SIPB", CityService.HashText(1397313602));
        Assert.IsNull(CityService.HashText(0));
    }

    [TestMethod]
    public void TheExportedCityDataLoadsWithTheClientsIdentities()
    {
        string root = GameDataPaths.ResolveRuntimeRoot();
        var catalog = new CityCatalog(root);
        if (!catalog.Enabled || !catalog.TryGetPlayfield(Playfield, out CityPlayfieldDefinition aegean))
        {
            Assert.IsTrue(catalog.LoadProblems.Count == 0 || catalog.Rules.DisabledReason != "not loaded",
                "The city data failed to load: " + string.Join("; ", catalog.LoadProblems));
            Assert.Inconclusive("The runtime GameData at " + root + " has no city data (" + catalog.Rules.DisabledReason + ").");
            return;
        }

        Assert.AreEqual(0, catalog.LoadProblems.Count, string.Join("; ", catalog.LoadProblems));
        Assert.AreEqual(10, aegean.Controllers.Count);
        foreach (CityControllerDefinition controller in aegean.Controllers)
        {
            Assert.AreEqual(0xC000 | controller.Index, controller.CityInstance);
            Assert.AreEqual(unchecked((int)(0xC0000000u | ((uint)controller.Index << 16) | Playfield)), controller.DynelInstance);
            Assert.IsTrue(controller.BuildableTiles > 0 && controller.PurchaseCost > 0, "controller " + controller.Index);
        }

        Assert.IsTrue(catalog.TryGetTemplate(HeadquartersTemplate, out HouseTemplateDefinition largeHq));
        Assert.AreEqual(HouseType.Headquarters, largeHq.Type);
        Assert.AreEqual(20, largeHq.Footprint.TilesX);
        Assert.IsTrue(catalog.TryGetTemplate(GridTemplate, out HouseTemplateDefinition grid));
        Assert.AreEqual(HouseType.Grid, grid.Type);
    }

    // ------------------------------------------------------------------------------------------------ advantages

    [TestMethod]
    public void CityAdvantagesAreOneBonusLayerThatNeverStacks()
    {
        var items = new Items();
        var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = BuyerId }, new Logger(), items);
        player.Inventory.Apply(new CharacterHydrationResult(), BuyerId, items);
        player.Stats.Set(CharacterStat.Level, 200);
        player.Stats.Set(CharacterStat.ComputerLiteracy, 100);
        player.Rebase();
        int before = player.Stats.Get(CharacterStat.ComputerLiteracy);

        ItemTemplate advantage = ScalingModify(254387, CharacterStat.ComputerLiteracy, 54);
        player.SetCityAdvantages([advantage]);
        Assert.AreEqual(before + 54, player.Stats.Get(CharacterStat.ComputerLiteracy));
        player.Rebase();
        player.SetCityAdvantages([advantage]);
        player.Rebase();
        Assert.AreEqual(before + 54, player.Stats.Get(CharacterStat.ComputerLiteracy), "a rebase or the same set again adds nothing");
        player.SetCityAdvantages([]);
        Assert.AreEqual(before, player.Stats.Get(CharacterStat.ComputerLiteracy), "losing the district takes the advantage away");
    }

    // ------------------------------------------------------------------------------------------------ purchase

    [TestMethod]
    public void BuyingTakesThePriceOnceFromTheOrganizationBankAndGivesItTheDistrict()
    {
        using var rig = new Rig();
        rig.OpenWindow(rig.Buyer);
        AOTransportSignalMessage open = rig.Signals(rig.Buyer, CitySignal.OpenGuiSession).Last();
        Assert.AreEqual(CityService.BuyWindow, open.Int1);
        Assert.AreEqual(CityService.LeaseFull, open.Int2, "the organization's leader, with the price in the bank, may buy");
        Assert.AreEqual(ClientCityId(0xC000), open.City, "the CityID the client builds for controller 0");

        long cash = rig.Cash(rig.Buyer);
        long bank = rig.Store.Bank;
        // Two clicks in one tick: the second finds the district already bought.
        rig.OnTick(() =>
        {
            rig.Cities.Buy(rig.Buyer, 0xC000);
            rig.Cities.Buy(rig.Buyer, 0xC000);
        });
        rig.Settle();

        Assert.AreEqual(bank - Price, rig.Store.Bank, "the price is taken from the bank once");
        Assert.AreEqual(cash, rig.Cash(rig.Buyer), "the leader's own credits are untouched");
        Assert.AreEqual(cash, rig.StoredCash(rig.Buyer));
        Assert.AreEqual(bank - Price, rig.CachedBank(), "the organization shows its new balance at once");
        CityPlotState plot = rig.Store.Load(Playfield, 0);
        Assert.AreEqual(OrganizationId, plot.OrganizationId);
        Assert.IsFalse(plot.ClaimPending);
        Assert.IsNotNull(plot.NextBillAtUtc);
        Assert.IsTrue(rig.Store.Claims is 1 or 2, "the second click is refused by the store or never sent");
        Assert.AreEqual(0, rig.Cities.RequestsInFlight(rig.Buyer));
        // The window reopens as the district's terminal.
        AOTransportSignalMessage terminal = rig.Signals(rig.Buyer, CitySignal.OpenGuiSession).Last();
        Assert.AreEqual(CityService.TerminalWindow, terminal.Int1);
        Assert.AreEqual(CityService.LeaseFull, terminal.Int2);
        Assert.AreEqual(CityService.BuildAllowed, terminal.Int3);
        Assert.AreEqual(string.Empty, rig.Log.Problems);
    }

    /// <summary>
    /// The CityID_t the client registers its city under (Gamecode LoadPlayfieldCityData 101224a0) and compares a window
    /// signal against (city.dll 1000a59f): the controller (0xC419, instance), the playfield proxy's +0xc (PlayfieldAnarchyF
    /// SG, 0 on an ordinary playfield) and the playfield's model id (the proxy's identity, PlayfieldAnarchyF PlayfieldId1).
    /// </summary>
    static CityId ClientCityId(int cityInstance)
        => new(new Identity((IdentityType)CityService.CityIdentityType, cityInstance), 0,
            new Identity(IdentityType.PlayfieldProxy, Playfield));

    [TestMethod]
    public void TheWindowSignalsNameTheCityAsTheClientRegistersIt()
    {
        // Live defect: the playfield identity and the int were swapped, so the client dropped the window and nothing opened.
        using var rig = new Rig();
        rig.OpenWindow(rig.Member);
        Assert.AreEqual(ClientCityId(0xC000), rig.Signals(rig.Member, CitySignal.OpenGuiSession).Last().City);
        rig.OnTick(() => rig.Cities.CloseWindow(rig.Member, 0xC000));
        Assert.AreEqual(ClientCityId(0xC000), rig.Signals(rig.Member, CitySignal.CloseGuiSession).Last().City);
    }

    [TestMethod]
    public void TheServersPricesReachTheClientBeforeTheWindowOpens()
    {
        // Team request 29: the Lost Eden client shows only the prices the server sends (signal 1000) and keeps Purchase
        // disabled until they arrive, so a window without them could never be bought from.
        using var rig = new Rig();
        rig.OpenWindow(rig.Buyer);
        List<AOTransportSignalMessage> signals = ((Session)rig.Buyer.Session!).Sent.OfType<AOTransportSignalMessage>().ToList();
        int prices = signals.FindIndex(s => (int)s.Signal == CityService.AreaPricesSignal);
        int open = signals.FindIndex(s => s.Signal == CitySignal.OpenGuiSession);
        Assert.IsTrue(prices >= 0, "the prices are sent");
        Assert.IsTrue(prices < open, "before the window opens");

        // The bytes the client reads: the signal id, the CityId of signal 5, the purchase cost, the area upkeep.
        var writer = new PacketWriter();
        signals[prices].Write(writer);
        byte[] bytes = writer.ToArray();
        var reader = new SpanReader(bytes);
        reader.ReadInt32();
        reader.ReadIdentity();
        reader.ReadByte();
        Assert.AreEqual(CityService.AreaPricesSignal, reader.ReadInt32());
        Assert.AreEqual(ClientCityId(0xC000), CityId.Read(ref reader));
        Assert.AreEqual((int)Price, reader.ReadInt32(), "the purchase cost the server charges");
        Assert.AreEqual(1_000_000, reader.ReadInt32(), "the district's area upkeep");
        Assert.AreEqual(0, reader.Remaining);

        // And a purchase is charged that same price.
        long bank = rig.Store.Bank;
        rig.OnTick(() => rig.Cities.Buy(rig.Buyer, 0xC000));
        rig.Settle();
        Assert.AreEqual(bank - Price, rig.Store.Bank);
    }

    [TestMethod]
    public void AMemberWhoIsNotTheLeaderCannotBuyAndKeepsTheirCredits()
    {
        using var rig = new Rig();
        rig.OpenWindow(rig.Member);
        AOTransportSignalMessage open = rig.Signals(rig.Member, CitySignal.OpenGuiSession).Last();
        Assert.AreEqual(CityService.BuyWindow, open.Int1);
        Assert.AreEqual(CityService.LeaseLimited, open.Int2, "the purchase is greyed out for a member who is not the leader");

        long bank = rig.Store.Bank;
        // A crafted request past the greyed-out button: refused on the tick, nothing reaches the store.
        rig.OnTick(() => rig.Cities.Buy(rig.Member, 0xC000));
        rig.Settle();
        Assert.AreEqual(bank, rig.Store.Bank);
        Assert.AreEqual(0, rig.Store.Claims);
        Assert.IsFalse(rig.Store.Load(Playfield, 0).IsOwned);
    }

    [TestMethod]
    public void ALeaderWhoLosesLeadershipBeforeTheClaimIsRefusedByTheStore()
    {
        using var rig = new Rig();
        rig.OpenWindow(rig.Buyer);
        long bank = rig.Store.Bank;
        // The tick still thinks the buyer leads; the store, which holds the organization's lock, knows it does not.
        rig.Store.Leader = MemberId;
        rig.OnTick(() => rig.Cities.Buy(rig.Buyer, 0xC000));
        rig.Settle();
        Assert.AreEqual(1, rig.Store.Claims);
        Assert.AreEqual(bank, rig.Store.Bank, "nothing is taken for a purchase the store refuses");
        Assert.IsFalse(rig.Store.Load(Playfield, 0).IsOwned);
    }

    [TestMethod]
    public void ACraftedPurchaseWithoutTheWindowOpenDoesNothing()
    {
        using var rig = new Rig();
        long bank = rig.Store.Bank;
        rig.Inbound(rig.Buyer, () => rig.Handle(rig.Buyer, new ClientRequestBuyMessage { CityInstance = 0xC000 }));
        rig.Settle();
        Assert.AreEqual(bank, rig.Store.Bank);
        Assert.AreEqual(0, rig.Store.Claims);
        Assert.IsFalse(rig.Store.Load(Playfield, 0).IsOwned);
    }

    [TestMethod]
    public void APurchaseTheStoreRefusesTakesNothingFromTheBank()
    {
        using var rig = new Rig();
        rig.Store.SellToSomeoneElse(Playfield, 0, 9999);
        rig.OpenWindow(rig.Buyer);
        long bank = rig.Store.Bank;
        rig.OnTick(() => rig.Cities.Buy(rig.Buyer, 0xC000));
        rig.Settle();
        Assert.AreEqual(bank, rig.Store.Bank);
        Assert.AreEqual(9999, rig.Store.Load(Playfield, 0).OrganizationId);
    }

    [TestMethod]
    public void ABankShortOfThePriceGreysThePurchaseOutWhateverTheLeaderCarries()
    {
        using var rig = new Rig();
        rig.SetBank(Price - 1);
        rig.OpenWindow(rig.Buyer);
        Assert.AreEqual(CityService.LeaseLimited, rig.Signals(rig.Buyer, CitySignal.OpenGuiSession).Last().Int2,
            "the leader carries 900M, but the bank pays");
        // A crafted click past the greyed-out button: refused on the tick, nothing reaches the store.
        rig.OnTick(() => rig.Cities.Buy(rig.Buyer, 0xC000));
        rig.Settle();
        Assert.AreEqual(0, rig.Store.Claims);
        Assert.AreEqual(Price - 1, rig.Store.Bank);
        Assert.IsFalse(rig.Store.Load(Playfield, 0).IsOwned);
    }

    [TestMethod]
    public void ABankEmptiedBeforeThePurchaseCommitsBuysNothing()
    {
        // The tick's balance is a cache: a withdrawal elsewhere between the click and the transaction leaves the bank short.
        using var rig = new Rig();
        rig.OpenWindow(rig.Buyer);
        rig.Store.Bank = Price - 1;
        long cash = rig.Cash(rig.Buyer);
        rig.OnTick(() => rig.Cities.Buy(rig.Buyer, 0xC000));
        rig.Settle();
        Assert.AreEqual(1, rig.Store.Claims);
        Assert.AreEqual(Price - 1, rig.Store.Bank, "the bank never goes below zero");
        Assert.IsFalse(rig.Store.Load(Playfield, 0).IsOwned, "and the district stays on sale");
        Assert.AreEqual(cash, rig.Cash(rig.Buyer), "the leader is not charged instead");
        Assert.AreEqual(Price - 1, rig.CachedBank(), "the stale balance is reloaded");
        Assert.AreEqual(0, rig.Cities.RequestsInFlight(rig.Buyer));
    }

    [TestMethod]
    public void ManyClicksBuyTheDistrictOnceAndChargeTheBankOnce()
    {
        // Duplication check: every click the request limit lets through reaches the store; only the first one buys.
        using var rig = new Rig();
        rig.OpenWindow(rig.Buyer);
        long bank = rig.Store.Bank;
        rig.OnTick(() =>
        {
            for (int i = 0; i < 20; i++)
                rig.Cities.Buy(rig.Buyer, 0xC000);
        });
        rig.Settle();
        Assert.AreEqual(bank - Price, rig.Store.Bank);
        Assert.AreEqual(OrganizationId, rig.Store.Load(Playfield, 0).OrganizationId);
        Assert.AreEqual(0, rig.Cities.RequestsInFlight(rig.Buyer));
    }

    // ------------------------------------------------------------------------------------------------ build

    [TestMethod]
    public void BuildingTakesTheHouseItemOnceAndTheHouseStands()
    {
        using var rig = new Rig();
        rig.GiveDistrict();
        Item hq = rig.GiveItem(rig.Buyer, HeadquartersItem, HeadquartersTemplate);
        rig.OpenWindow(rig.Buyer);

        var build = new ClientRequestBuildMessage
        {
            CityInstance = 0xC000, Template = new Identity((IdentityType)CityService.HouseTemplateRecordType, HeadquartersTemplate),
            Tile = new CityTilePos(10, 0), Rotation = 0, Item = rig.SlotOf(rig.Buyer, hq)
        };
        // The same item twice, as a replayed or doubled request.
        rig.OnTick(() =>
        {
            rig.Cities.Build(rig.Buyer, build);
            rig.Cities.Build(rig.Buyer, build);
        });
        rig.Settle();

        CityPlotState plot = rig.Store.Load(Playfield, 0);
        Assert.AreEqual(1, plot.Houses.Count, "one house from one item");
        Assert.IsTrue(plot.Houses[0].Standing);
        Assert.IsFalse(rig.Buyer.Inventory.Inventory.Content.Values.Any(i => i.InstanceId == hq.InstanceId), "the item is gone from memory");
        Assert.AreEqual(1, rig.Persistence.Retired(hq.InstanceId), "and retired in storage once");
        Assert.IsTrue(rig.Signals(rig.Buyer, CitySignal.HouseAdded).Any(s => s.House.Template.Instance == HeadquartersTemplate),
            "the playfield is told the house stands");
        Assert.AreEqual(string.Empty, rig.Log.Problems);
    }

    [TestMethod]
    public void OneItemSentForTwoTilesBuildsOneHouse()
    {
        using var rig = new Rig();
        rig.GiveDistrict();
        Item hq = rig.GiveItem(rig.Buyer, HeadquartersItem, HeadquartersTemplate);
        Item cafe = rig.GiveItem(rig.Buyer, CafeItem, CafeTemplate);
        rig.OpenWindow(rig.Buyer);
        rig.OnTick(() => rig.Cities.Build(rig.Buyer, rig.BuildMessage(hq, HeadquartersTemplate, 10, 0)));
        rig.Settle();
        // Both requests pass the tick and both reserve their tiles; only the first finds the item to take.
        rig.OnTick(() =>
        {
            rig.Cities.Build(rig.Buyer, rig.BuildMessage(cafe, CafeTemplate, 30, 0));
            rig.Cities.Build(rig.Buyer, rig.BuildMessage(cafe, CafeTemplate, 30, 10));
        });
        rig.Settle();
        CityPlotState plot = rig.Store.Load(Playfield, 0);
        Assert.AreEqual(2, plot.Houses.Count, "the headquarters and one cafe");
        Assert.IsTrue(plot.Houses.All(h => h.Standing), "the second reservation is given up, not left pending");
        Assert.AreEqual(1, rig.Persistence.Retired(cafe.InstanceId));
        Assert.AreEqual(3, rig.Store.Reservations, "the headquarters and both cafe requests reserved tiles");
    }

    [TestMethod]
    public void AStackOfHouseItemsBuildsNothing()
    {
        using var rig = new Rig();
        rig.GiveDistrict();
        Item hq = rig.GiveItem(rig.Buyer, HeadquartersItem, HeadquartersTemplate);
        hq.StackCount = 2;
        rig.OpenWindow(rig.Buyer);
        rig.OnTick(() => rig.Cities.Build(rig.Buyer, rig.BuildMessage(hq, HeadquartersTemplate, 10, 0)));
        rig.Settle();
        Assert.AreEqual(0, rig.Store.Load(Playfield, 0).Houses.Count);
        Assert.AreEqual(2, hq.StackCount);
        Assert.AreEqual(0, rig.Persistence.Retired(hq.InstanceId));
    }

    [TestMethod]
    public void AHouseTheStoreRefusesKeepsItsItem()
    {
        using var rig = new Rig();
        rig.GiveDistrict();
        Item hq = rig.GiveItem(rig.Buyer, HeadquartersItem, HeadquartersTemplate);
        rig.OpenWindow(rig.Buyer);
        rig.Store.RefuseHouses = true;
        rig.OnTick(() => rig.Cities.Build(rig.Buyer, rig.BuildMessage(hq, HeadquartersTemplate, 10, 0)));
        rig.Settle();
        Assert.IsTrue(rig.Buyer.Inventory.Inventory.Content.Values.Contains(hq));
        Assert.AreEqual(0, rig.Persistence.Retired(hq.InstanceId));
        Assert.AreEqual(0, rig.Store.Load(Playfield, 0).Houses.Count);
    }

    [TestMethod]
    public void AnItemMovedBeforeItIsTakenGivesTheReservationUp()
    {
        using var rig = new Rig();
        rig.GiveDistrict();
        Item hq = rig.GiveItem(rig.Buyer, HeadquartersItem, HeadquartersTemplate);
        rig.OpenWindow(rig.Buyer);
        rig.OnTick(() => rig.Cities.Build(rig.Buyer, rig.BuildMessage(hq, HeadquartersTemplate, 10, 0)));
        rig.Cities.WhenIdle().Wait();
        // Reserved; before the builder's tick takes it, the item leaves that slot.
        rig.OnTick(() => hq.Locked = true);
        rig.Settle();
        Assert.IsTrue(rig.Buyer.Inventory.Inventory.Content.Values.Contains(hq), "the item is kept");
        Assert.AreEqual(0, rig.Persistence.Retired(hq.InstanceId));
        Assert.AreEqual(0, rig.Store.Load(Playfield, 0).Houses.Count, "the pending house is given up");
    }

    [TestMethod]
    public void AMemberBelowTheManagingRanksCannotBuild()
    {
        using var rig = new Rig();
        rig.GiveDistrict();
        Item hq = rig.GiveItem(rig.Member, HeadquartersItem, HeadquartersTemplate);
        rig.OpenWindow(rig.Member);
        AOTransportSignalMessage terminal = rig.Signals(rig.Member, CitySignal.OpenGuiSession).Last();
        Assert.AreEqual(CityService.BuildDenied, terminal.Int3, "the window's build controls are off");
        rig.OnTick(() => rig.Cities.Build(rig.Member, rig.BuildMessage(hq, HeadquartersTemplate, 10, 0)));
        rig.Settle();
        Assert.IsTrue(rig.Member.Inventory.Inventory.Content.Values.Contains(hq));
        Assert.AreEqual(0, rig.Store.Load(Playfield, 0).Houses.Count);
    }

    [TestMethod]
    public void OnlyOneMemberHoldsTheFullTerminal()
    {
        using var rig = new Rig();
        rig.GiveDistrict();
        rig.OpenWindow(rig.Buyer);
        rig.OpenWindow(rig.Member);
        Assert.AreEqual(CityService.LeaseReadOnly, rig.Signals(rig.Member, CitySignal.OpenGuiSession).Last().Int2);
        rig.OnTick(() => rig.Cities.CloseWindow(rig.Buyer, 0xC000));
        Assert.IsTrue(rig.Signals(rig.Buyer, CitySignal.CloseGuiSession).Any(), "the client closes only on signal 7");
        rig.OpenWindow(rig.Member);
        Assert.AreEqual(CityService.LeaseFull, rig.Signals(rig.Member, CitySignal.OpenGuiSession).Last().Int2);
    }

    [TestMethod]
    public void DemolishingTheHeadquartersTakesEveryHouseAndTheItemsDoNotComeBack()
    {
        using var rig = new Rig();
        rig.GiveDistrict();
        Item hq = rig.GiveItem(rig.Buyer, HeadquartersItem, HeadquartersTemplate);
        Item grid = rig.GiveItem(rig.Buyer, GridItem, GridTemplate);
        rig.OpenWindow(rig.Buyer);
        rig.OnTick(() => rig.Cities.Build(rig.Buyer, rig.BuildMessage(hq, HeadquartersTemplate, 10, 0)));
        rig.Settle();
        rig.OnTick(() => rig.Cities.Build(rig.Buyer, rig.BuildMessage(grid, GridTemplate, 30, 0)));
        rig.Settle();
        Assert.AreEqual(2, rig.Store.Load(Playfield, 0).StandingHouses.Count());

        rig.OnTick(() => rig.Cities.Demolish(rig.Buyer, 0xC000, new CityTilePos(10, 0)));
        rig.Settle();
        CityPlotState demolishing = rig.Store.Load(Playfield, 0);
        Assert.IsTrue(demolishing.Houses.All(h => h.DemolishAtUtc != null), "every house goes with the headquarters");
        Assert.AreEqual(2, rig.Signals(rig.Buyer, CitySignal.HouseDemolishStarted).Count());

        rig.Store.Advance(TimeSpan.FromMinutes(3));
        rig.Cities.SweepDueNow();
        rig.Settle();
        Assert.AreEqual(0, rig.Store.Load(Playfield, 0).Houses.Count);
        Assert.AreEqual(2, rig.Signals(rig.Buyer, CitySignal.HouseRemoved).Count());
        Assert.IsFalse(rig.Buyer.Inventory.Inventory.Content.Values.Any(i => i.InstanceId == hq.InstanceId || i.InstanceId == grid.InstanceId));
    }

    [TestMethod]
    public void UpkeepComesFromTheBankAndAnEmptyBankLosesTheCity()
    {
        using var rig = new Rig();
        rig.GiveDistrict();
        rig.Store.Bank = 10;
        rig.Store.Advance(TimeSpan.FromDays(31));
        rig.Cities.SweepDueNow();
        rig.Settle();
        Assert.IsFalse(rig.Store.Load(Playfield, 0).IsOwned, "unpaid upkeep demolishes the city");
        Assert.AreEqual(10, rig.Store.Bank, "nothing is taken from a bank that cannot pay");
    }

    // ------------------------------------------------------------------------------------------------ a player who leaves

    [TestMethod]
    public void ABuyerWhoLeavesRightAfterTheClickStillBuysOnceAndNothingIsLeftPending()
    {
        // The purchase needs nothing more from the buyer's tick once it is sent: leaving first neither strands a pending
        // claim (blocking the district) nor leaks the request (blocking the buyer), and the bank pays exactly once.
        using var rig = new Rig();
        long bank = rig.Store.Bank;
        long cash = rig.Cash(rig.Buyer);
        rig.OpenWindow(rig.Buyer);
        rig.OnTick(() =>
        {
            rig.Cities.Buy(rig.Buyer, 0xC000);
            rig.Leave(rig.Buyer);
        });
        rig.Settle();
        CityPlotState plot = rig.Store.Load(Playfield, 0);
        Assert.AreEqual(OrganizationId, plot.OrganizationId);
        Assert.IsFalse(plot.ClaimPending);
        Assert.AreEqual(bank - Price, rig.Store.Bank);
        Assert.AreEqual(cash, rig.Cash(rig.Buyer));
        Assert.AreEqual(0, rig.Cities.RequestsInFlight(rig.Buyer));
        Assert.AreEqual(0, rig.Cities.TickStepsWaiting);
    }

    [TestMethod]
    public void ABuyerDroppedFromThePlayfieldMidPurchaseStillBuysOnce()
    {
        using var rig = new Rig();
        long bank = rig.Store.Bank;
        rig.OpenWindow(rig.Buyer);
        rig.OnTick(() =>
        {
            rig.Cities.Buy(rig.Buyer, 0xC000);
            // Logged out while the request waits: whatever the tick would have shown the buyer is dropped.
            rig.Detach(rig.Buyer);
        });
        rig.Settle();
        rig.Cities.SyncNow();
        rig.Settle();
        Assert.AreEqual(OrganizationId, rig.Store.Load(Playfield, 0).OrganizationId);
        Assert.AreEqual(bank - Price, rig.Store.Bank);
        Assert.AreEqual(0, rig.Cities.RequestsInFlight(rig.Buyer));
        Assert.AreEqual(0, rig.Cities.TickStepsWaiting);
    }

    [TestMethod]
    public void ABuilderWhoLeavesBeforeTheItemIsTakenKeepsItAndTheTilesAreFreed()
    {
        using var rig = new Rig();
        rig.GiveDistrict();
        Item hq = rig.GiveItem(rig.Buyer, HeadquartersItem, HeadquartersTemplate);
        rig.OpenWindow(rig.Buyer);
        rig.OnTick(() => rig.Cities.Build(rig.Buyer, rig.BuildMessage(hq, HeadquartersTemplate, 10, 0)));
        rig.Cities.WhenIdle().Wait();
        Assert.AreEqual(1, rig.Store.Load(Playfield, 0).Houses.Count, "the tiles are reserved");
        rig.Leave(rig.Buyer);
        rig.Settle();
        Assert.IsTrue(rig.Buyer.Inventory.Inventory.Content.Values.Contains(hq), "the item is kept");
        Assert.AreEqual(0, rig.Persistence.Retired(hq.InstanceId));
        Assert.AreEqual(0, rig.Store.Load(Playfield, 0).Houses.Count, "the reservation is given up");
        Assert.AreEqual(0, rig.Cities.RequestsInFlight(rig.Buyer));
    }

    // ------------------------------------------------------------------------------------------------ the right item

    [TestMethod]
    public void AnItemForAnotherHouseBuildsNothing()
    {
        using var rig = new Rig();
        rig.GiveDistrict();
        // A grid house item sent as the headquarters the client asked to place.
        Item grid = rig.GiveItem(rig.Buyer, GridItem, GridTemplate);
        rig.OpenWindow(rig.Buyer);
        rig.OnTick(() => rig.Cities.Build(rig.Buyer, rig.BuildMessage(grid, HeadquartersTemplate, 10, 0)));
        rig.Settle();
        Assert.AreEqual(0, rig.Store.Reservations, "refused before any tile is reserved");
        Assert.IsTrue(rig.Buyer.Inventory.Inventory.Content.Values.Contains(grid));
        Assert.AreEqual(0, rig.Persistence.Retired(grid.InstanceId));
        Assert.AreEqual(0, rig.Store.Load(Playfield, 0).Houses.Count);
    }

    [TestMethod]
    public void AnItemThatIsNoHouseBuildsNothing()
    {
        using var rig = new Rig();
        rig.GiveDistrict();
        // Like a Broken City Building: the building's look, no house template.
        Item broken = rig.GiveItem(rig.Buyer, 254153, template: 0);
        rig.OpenWindow(rig.Buyer);
        rig.OnTick(() => rig.Cities.Build(rig.Buyer, rig.BuildMessage(broken, HeadquartersTemplate, 10, 0)));
        rig.Settle();
        Assert.AreEqual(0, rig.Store.Reservations);
        Assert.IsTrue(rig.Buyer.Inventory.Inventory.Content.Values.Contains(broken));
        Assert.AreEqual(0, rig.Persistence.Retired(broken.InstanceId));
    }

    [TestMethod]
    public void AnotherSidesHouseBuildsNothingAndAnAnySideHouseBuilds()
    {
        using var rig = new Rig();
        rig.GiveDistrict();
        Item omni = rig.GiveItem(rig.Buyer, 254197, HeadquartersTemplate, side: 2);
        rig.OpenWindow(rig.Buyer);
        rig.OnTick(() => rig.Cities.Build(rig.Buyer, rig.BuildMessage(omni, HeadquartersTemplate, 10, 0)));
        rig.Settle();
        Assert.AreEqual(0, rig.Store.Reservations, "a neutral builder may not build an Omni house");
        Assert.IsTrue(rig.Buyer.Inventory.Inventory.Content.Values.Contains(omni));
        Assert.AreEqual(0, rig.Persistence.Retired(omni.InstanceId));

        Item any = rig.GiveItem(rig.Buyer, HeadquartersItem, HeadquartersTemplate, side: CityService.AnySide);
        rig.OnTick(() => rig.Cities.Build(rig.Buyer, rig.BuildMessage(any, HeadquartersTemplate, 10, 0)));
        rig.Settle();
        Assert.AreEqual(1, rig.Store.Load(Playfield, 0).StandingHouses.Count(), "an any-side house builds");
        Assert.AreEqual(1, rig.Persistence.Retired(any.InstanceId));
        Assert.IsTrue(rig.Buyer.Inventory.Inventory.Content.Values.Contains(omni), "the refused item is still kept");
    }

    // ------------------------------------------------------------------------------------------------ advantages

    [TestMethod]
    public void AMemberWhoLeavesTheOrganizationLosesItsCityAdvantages()
    {
        using var rig = new Rig();
        rig.Templates.Known[HeadquartersItem] = new ItemTemplate
        {
            Id = HeadquartersItem, Quality = 1, Stats = { [CharacterStat.AdvantageHash1] = 0x4749484F }
        };
        rig.Templates.Known[254387] = new ItemTemplate { Id = 254387, Quality = 300 };
        rig.Hashes["GIHO"] = new HashInstance("GIHO", [254387]);
        rig.GiveDistrict();
        Item hq = rig.GiveItem(rig.Buyer, HeadquartersItem, HeadquartersTemplate);
        rig.OpenWindow(rig.Buyer);
        rig.OnTick(() => rig.Cities.Build(rig.Buyer, rig.BuildMessage(hq, HeadquartersTemplate, 10, 0)));
        rig.Settle();
        Assert.AreEqual(1, rig.Member.CityAdvantages.Count, "every member has the headquarters' advantage");
        Assert.AreEqual(1, rig.Buyer.CityAdvantages.Count);

        rig.RemoveFromOrganization(rig.Member);
        rig.Settle();
        Assert.IsNull(rig.Member.Organization);
        Assert.AreEqual(0, rig.Member.CityAdvantages.Count, "the advantage leaves with the member");
        Assert.AreEqual(1, rig.Buyer.CityAdvantages.Count, "the others keep it");
    }

    // ------------------------------------------------------------------------------------------------ helpers

    static CityControllerDefinition Controller(out CityPlayfieldDefinition playfield)
    {
        var open = new byte[13];
        Array.Fill(open, (byte)0xFF);
        var withHole = (byte[])open.Clone();
        int hole = 5 * 10 + 5;
        withHole[hole >> 3] &= (byte)~(1 << (hole & 7));
        var controller = new CityControllerDefinition(Playfield, 0, unchecked((int)(0xC0000000u | Playfield)), 1, 1, Price, 1_000_000,
            100, 100, Enumerable.Range(0, 4).SelectMany(row => Enumerable.Range(0, 4).Select(col =>
                new CityZone(row * 10 + col, col * 10, row * 10, row == 0 && col == 0 ? withHole : open))).ToList());
        playfield = new CityPlayfieldDefinition(Playfield, "Aegean", 100, 100, [controller]);
        return controller;
    }

    static HouseTemplateDefinition Template(int id, HouseType type, int x, int z)
        => new(id, type, 1_000_000, HouseFootprint.Solid(x, z));

    static CityHouse House(long id, int template, int x, int z)
        => new(id, template, x, z, 0, BuyerId, false, DateTime.UtcNow, null, 0, 0, 1);

    static ItemTemplate ScalingModify(int id, CharacterStat stat, int value) => new()
    {
        Id = id,
        Quality = 300,
        SpellList =
        {
            [EventType.OnUse] =
            [
                new ItemSpell
                {
                    FunctionType = (int)FunctionType.ScalingModify, Target = 2, Arguments = [(int)stat, value], Requirements = []
                }
            ]
        }
    };

    sealed class Rig : IDisposable
    {
        internal readonly Player Buyer;
        internal readonly Player Member;
        internal readonly Logger Log = new();
        internal readonly MemoryCityStore Store;
        internal readonly Persistence Persistence = new();
        internal readonly CityService Cities;
        internal readonly int Form;
        internal readonly Templates Templates = new();
        internal readonly ConcurrentDictionary<string, HashInstance> Hashes = new();
        readonly OrganizationRules _rules;
        readonly OrgRow _leaderRow;
        List<OrgMemberRow> _members;
        readonly ZoneEngine_New.Core.Playfield.Playfield _world = Blank<ZoneEngine_New.Core.Playfield.Playfield>();
        readonly PlayfieldManager _manager = Blank<PlayfieldManager>();
        readonly InventoryFlushService _flush;
        readonly ServiceProvider _services;
        readonly Characters _characters = new();
        readonly OrganizationService _organizations;
        readonly CityController _controller;
        int _nextInstance = 900000;

        internal Rig()
        {
            string root = GameDataPaths.ResolveRuntimeRoot();
            OrganizationRules rules = OrganizationRules.Load(root, Log);
            if (!rules.Enabled)
                Assert.Inconclusive("Organizations are disabled in the runtime GameData at " + root + ": " + rules.DisabledReason);
            Form = rules.Forms.Values.First(f => !f.SingleRank && f.LowestRank >= 2).Id;
            Log.Reset();

            var items = new Items();
            Controller(out CityPlayfieldDefinition playfieldDefinition);
            var catalog = new CityCatalog(new CityRules(), [playfieldDefinition],
                [Template(HeadquartersTemplate, HouseType.Headquarters, 20, 20), Template(GridTemplate, HouseType.Grid, 5, 5),
                    Template(CafeTemplate, HouseType.Normal, 5, 5)]);
            Store = new MemoryCityStore(catalog, rules, Form);
            IGameData gameData = Fake<IGameData>((method, args) => method.Name switch
            {
                "get_Organizations" => rules,
                "get_RootPath" => root,
                nameof(IGameData.TryGetHashInstance) => TryGetHash(args!),
                _ => throw new AssertFailedException("Unexpected game data call " + method.Name)
            });

            Buyer = Online(BuyerId, "Buyer", items);
            Member = Online(MemberId, "Member", items);
            foreach (Player player in new[] { Buyer, Member })
                player.Organization = new PlayerOrganization(OrganizationId, "Lost Eden", "Member");

            var registry = new DynelRegistry();
            Set(_manager, "_sync", new Lock());
            Set(_manager, "_playersByCharacterId", new Dictionary<int, Player> { [BuyerId] = Buyer, [MemberId] = Member });
            Set(_manager, "_playfields", new Dictionary<int, ZoneEngine_New.Core.Playfield.Playfield> { [Playfield] = _world });
            Set(_world, "<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = Playfield });
            Set(_world, "<ResourceId>k__BackingField", Playfield);
            Set(_world, "_tickSync", new Lock());
            Set(_world, "_lifecycleSync", new Lock());
            Set(_world, "_inbound", new PlayfieldInboundQueue());
            Set(_world, "_dynelRegistry", registry);
            Set(_world, "_logger", Log);
            Set(_world, "_playfieldManager", _manager);
            Set(_world, "<Geometry>k__BackingField", new PlayfieldGeometryData());

            _flush = new InventoryFlushService(id => id == BuyerId ? Buyer : id == MemberId ? Member : null, () => [Buyer, Member], Persistence, Log);
            var ids = new Ids();
            var actions = new InventoryActionService(Persistence, _flush, ids, Log);
            Templates templates = Templates;
            var trades = new TradeService(Log, gameData, templates, new HashItemMinter(gameData, templates, items), ids, _flush, Persistence);
            // The organization's row as stored: its bank is the city store's, which a purchase and upkeep charge.
            IOrganizationStore organizationStore = Fake<IOrganizationStore>((method, _) => method.Name switch
            {
                nameof(IOrganizationStore.Load) => ((OrgRow, IReadOnlyList<OrgMemberRow>)?)(StoredOrganization(), _members.ToArray()),
                _ => throw new AssertFailedException("Unexpected organization store call " + method.Name)
            });
            _organizations = new OrganizationService(organizationStore, gameData, new Lazy<PlayfieldManager>(() => _manager),
                Fake<IChatEngineLink>(), Log);
            _leaderRow = new OrgRow(OrganizationId, "Lost Eden", Form, BuyerId, string.Empty, string.Empty, string.Empty, 0, 0);
            _rules = rules;
            GoverningForm form = rules.FormOf(Form);
            _members =
            [
                new(BuyerId, "Buyer", 0, DateTime.UtcNow),
                new(MemberId, "Member", form.LowestRank, DateTime.UtcNow)
            ];
            CacheOrganization();

            Cities = new CityService(Store, catalog, gameData, items, templates, actions, _flush, trades, _organizations,
                new Lazy<PlayfieldManager>(() => _manager), Log);
            var locality = new ZoneEngine_New.Core.Playfield.Locality.PlayfieldLocality(Playfield, null);
            _services = new ServiceCollection().AddSingleton(registry).AddSingleton(locality).AddSingleton(Cities).BuildServiceProvider();
            Set(_world, "_serviceProvider", _services);

            _controller = new CityController(new Identity(IdentityType.CityController, unchecked((int)(0xC0000000u | Playfield))),
                new ItemTemplate { Id = 249767, Quality = 1 }) { Playfield = _world, Position = new Vector3(100, 10, 100) };
            registry.Register(_controller);
            foreach (Player player in new[] { Buyer, Member })
            {
                player.Playfield = _world;
                player.Position = new Vector3(101, 10, 100);
                registry.Register(player);
            }

            Cities.Start(timers: false);
            Log.Reset();
        }

        Player Online(int id, string name, Items items)
        {
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, Log, items) { FirstName = name, Name = name };
            player.Inventory.Apply(new CharacterHydrationResult(), id, items);
            player.Stats.Set(CharacterStat.Level, 200);
            player.Stats.Set(CharacterStat.Health, 100);
            player.Stats.Set(CharacterStat.MaxHealth, 100);
            player.Stats.Set(CharacterStat.Cash, 900_000_000, StatDetail.Base);
            _characters.Stored[id] = 900_000_000;
            var session = new Session();
            player.EnterOnline(session);
            session.BindPlayer(player);
            return player;
        }

        internal void GiveDistrict() => Store.Give(Playfield, 0, OrganizationId, BuyerId, Cities);

        object TryGetHash(object?[] args)
        {
            if (args[0] is string hash && Hashes.TryGetValue(hash, out HashInstance? family))
            {
                args[1] = family;
                return true;
            }

            return false;
        }

        /// <summary>The player logs out (or another login replaces it) while a step of its request waits on its tick.</summary>
        internal void Leave(Player player)
        {
            lock ((Lock)Get(_manager, "_sync"))
                ((Dictionary<int, Player>)Get(_manager, "_playersByCharacterId")).Remove(player.Identity.Instance);
        }

        internal void Return(Player player)
        {
            lock ((Lock)Get(_manager, "_sync"))
                ((Dictionary<int, Player>)Get(_manager, "_playersByCharacterId"))[player.Identity.Instance] = player;
        }

        /// <summary>The player is out of every playfield (logged out mid-queue): a step queued for it is dropped unrun.</summary>
        internal void Detach(Player player) => player.Playfield = null;

        /// <summary>The member leaves the organization: the cache loses it and its tick reconciles, as a leave or a kick does.</summary>
        internal void RemoveFromOrganization(Player player)
        {
            _members = _members.Where(m => m.CharacterId != player.Identity.Instance).ToList();
            CacheOrganization();
            MethodInfo reconcile = typeof(OrganizationService).GetMethod("ReconcileOnTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
            OnTick(() => reconcile.Invoke(_organizations, [player, OrganizationId]));
        }

        OrgRow StoredOrganization() => _leaderRow with { Bank = Store.Bank, Version = Store.OrganizationVersion };

        /// <summary>Puts the stored organization into this instance's cache, as a reload does.</summary>
        void CacheOrganization()
            => ((ConcurrentDictionary<int, OrgState>)Get(_organizations, "_cache"))[OrganizationId] = OrgState.From(StoredOrganization(), _members, _rules);

        /// <summary>The bank as stored and as this instance shows it.</summary>
        internal void SetBank(long bank)
        {
            Store.Bank = bank;
            CacheOrganization();
        }

        /// <summary>The bank this instance shows, as the purchase window and /org bank read it.</summary>
        internal long CachedBank() => _organizations.TryGetBank(OrganizationId, out long bank) ? bank : -1;

        internal Item GiveItem(Player player, int itemId, int template, int side = 0)
        {
            var item = new Item
            {
                LowId = itemId, HighId = itemId, Quality = 1, StackCount = 1, Identity = Identity.None,
                Definition = new ItemTemplate { Id = itemId, Quality = 1, Stats = { [CharacterStat.HouseTemplate] = template, [CharacterStat.Side] = side } }
            };
            item.AssignInstanceId(Interlocked.Increment(ref _nextInstance));
            item.IsPersisted = true;
            Container page = player.Inventory.Inventory;
            Assert.IsTrue(page.Add(page.FindFreeSlot(), item));
            return item;
        }

        internal Identity SlotOf(Player player, Item item)
            => new(IdentityType.Inventory, player.Inventory.Inventory.Content.Single(entry => ReferenceEquals(entry.Value, item)).Key);

        internal ClientRequestBuildMessage BuildMessage(Item item, int template, int x, int z) => new()
        {
            CityInstance = 0xC000, Template = new Identity((IdentityType)CityService.HouseTemplateRecordType, template),
            Tile = new CityTilePos(x, z), Rotation = 0, Item = SlotOf(item == null ? Buyer : Owner(item), item!)
        };

        Player Owner(Item item) => Member.Inventory.Inventory.Content.Values.Contains(item) ? Member : Buyer;

        internal void OpenWindow(Player player) => OnTick(() => Assert.IsTrue(_controller.TryUse(player), "the controller opens"));

        internal void Handle(Player player, ClientRequestBuyMessage message)
            => new ZoneEngine_New.Core.MessageHandlers.ClientRequestBuyMessageHandler(Cities).Handle(message, player.Session!);

        internal long Cash(Player player) => player.Stats.GetOrZero(CharacterStat.Cash, StatDetail.Base);

        internal long StoredCash(Player player) => _characters.Stored[player.Identity.Instance];

        internal IEnumerable<AOTransportSignalMessage> Signals(Player player, CitySignal signal)
            => ((Session)player.Session!).Sent.OfType<AOTransportSignalMessage>().Where(s => s.Signal == signal);

        internal void OnTick(Action work)
        {
            lock ((Lock)Get(_world, "_tickSync"))
                work();
        }

        internal void Inbound(Player player, Action work)
            => Assert.IsTrue(_world.TryEnqueue(new PlayerProjectionInboundItem { Player = player, Projection = work }));

        void Drain() => OnTick(() => ((PlayfieldInboundQueue)Get(_world, "_inbound")).Drain(null!, null!, _world));

        /// <summary>Runs the district queues, the ticks and both lanes until nothing more happens.</summary>
        internal void Settle()
        {
            for (int i = 0; i < 20; i++)
            {
                Assert.IsTrue(Cities.WhenIdle().Wait(TimeSpan.FromSeconds(10)), "district queues finish");
                Drain();
                _flush.HardFlush(Buyer);
                _flush.HardFlush(Member);
                Assert.IsTrue(Cities.WhenIdle().Wait(TimeSpan.FromSeconds(10)), "district queues finish");
                Drain();
            }
        }

        public void Dispose()
        {
            Cities.Dispose();
            _organizations.Dispose();
            _flush.Dispose();
            _services.Dispose();
        }
    }

    /// <summary>
    /// The MySQL store's rules in memory: one lock for the row locks, a version per district, pending claims and houses
    /// that only a confirmation makes stand. Failure switches model what the database or a crash could do.
    /// </summary>
    sealed class MemoryCityStore(CityCatalog catalog, OrganizationRules rules, int governingForm) : ICityStore
    {
        readonly object _sync = new();
        readonly Dictionary<(int, int), CityPlotState> _plots = new();
        long _nextHouse;
        DateTime _now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

        long _bank = 1_000_000_000;

        internal int Claims;
        internal int Reservations;
        internal bool RefuseHouses;

        /// <summary>The organization's bank; every change moves the organization's version, as the database's does.</summary>
        internal long Bank
        {
            get { lock (_sync) return _bank; }
            set
            {
                lock (_sync)
                {
                    _bank = value;
                    OrganizationVersion++;
                }
            }
        }

        internal long OrganizationVersion { get; private set; } = 1;
        /// <summary>The organization's stored leader, as the claim reads it under the organization's lock.</summary>
        internal int Leader = BuyerId;

        internal void Advance(TimeSpan by)
        {
            lock (_sync)
                _now += by;
        }

        CityPlotState Get((int, int) key) => _plots.TryGetValue(key, out CityPlotState? state) ? state : new CityPlotState(key.Item1, key.Item2);

        static CityPlotState With(CityPlotState s, int? org = -1, long? ownership = null, bool? pending = null, DateTime? bill = null,
            DateTime? empty = null, bool clearEmpty = false, DateTime? hq = null, IReadOnlyList<CityHouse>? houses = null, int? cloak = null,
            DateTime? cloakAt = null, bool release = false)
            => release
                ? new CityPlotState(s.PlayfieldId, s.ControllerIndex, null, string.Empty, s.OwnershipId, false, null, null, null, null, null, null,
                    0, null, 1f, s.LockoutOrganizationId, s.LockoutUntilUtc, s.Version + 1, [])
                : new CityPlotState(s.PlayfieldId, s.ControllerIndex, org == -1 ? s.OrganizationId : org, org == -1 ? s.OrganizationName : "Lost Eden",
                    ownership ?? s.OwnershipId, pending ?? s.ClaimPending, s.PurchasedBy, s.PurchasedAtUtc, bill ?? s.NextBillAtUtc,
                    s.UpkeepWarnedForUtc, clearEmpty ? null : empty ?? s.EmptySinceUtc, hq ?? s.HeadquartersBuiltAtUtc, cloak ?? s.CloakState,
                    cloakAt ?? s.CloakToggledAtUtc, s.ControllerCharge, s.LockoutOrganizationId, s.LockoutUntilUtc, s.Version + 1, houses ?? s.Houses);

        internal void SellToSomeoneElse(int pf, int idx, int organizationId)
        {
            lock (_sync)
                _plots[(pf, idx)] = With(Get((pf, idx)), org: organizationId, ownership: 1, pending: false, bill: _now.AddDays(30));
        }

        internal void Give(int pf, int idx, int organizationId, int buyer, CityService cities)
        {
            lock (_sync)
                _plots[(pf, idx)] = With(Get((pf, idx)), org: organizationId, ownership: 1, pending: false, bill: _now.AddDays(30), empty: _now);
            cities.SyncNow();
        }

        bool Manages(int organizationId, int characterId)
        {
            if (organizationId != OrganizationId)
                return false;
            GoverningForm form = rules.FormOf(governingForm);
            bool leader = characterId == BuyerId;
            int rank = rules.EffectiveRank(governingForm, leader ? 0 : form.LowestRank, leader);
            return catalog.Rules.CanManage(form, rank, leader);
        }

        public IReadOnlyList<CityPlotState> LoadAll() { lock (_sync) return _plots.Values.ToArray(); }

        public CityPlotState Load(int playfieldId, int controllerIndex) { lock (_sync) return Get((playfieldId, controllerIndex)); }

        public IReadOnlyDictionary<(int PlayfieldId, int ControllerIndex), long> Versions()
        {
            lock (_sync)
                return _plots.ToDictionary(p => p.Key, p => p.Value.Version);
        }

        public CityClaimResult Claim(int playfieldId, int controllerIndex, int organizationId, int characterId, long price, float startingCharge,
            TimeSpan billingPeriod)
        {
            lock (_sync)
            {
                Claims++;
                if (organizationId != OrganizationId)
                    return new CityClaimResult(CityClaimStatus.NotMember, 0, null);
                if (characterId != Leader)
                    return new CityClaimResult(CityClaimStatus.NotLeader, 0, null);
                if (_plots.Values.Any(p => p.OrganizationId == organizationId))
                    return new CityClaimResult(CityClaimStatus.OrganizationOwnsCity, 0, null);
                CityPlotState plot = Get((playfieldId, controllerIndex));
                if (plot.IsOwned)
                    return new CityClaimResult(CityClaimStatus.AlreadyOwned, 0, null);
                if (_bank < price)
                    return new CityClaimResult(CityClaimStatus.NotEnoughInBank, 0, null, _bank);
                Bank = _bank - price;
                CityPlotState bought = With(plot, org: organizationId, ownership: plot.OwnershipId + 1, pending: false,
                    bill: _now + billingPeriod, empty: _now, houses: []);
                _plots[bought.Key] = bought;
                return new CityClaimResult(CityClaimStatus.Claimed, bought.OwnershipId, bought, _bank);
            }
        }

        public CityWriteResult ReserveHouse(CityControllerDefinition controller, CityCatalog cities, int organizationId, int characterId,
            HouseTemplateDefinition template, int tileX, int tileZ, int rotation, int itemLowId, int itemHighId, int itemQuality,
            CityManagePermission canManage)
        {
            lock (_sync)
            {
                CityPlotState plot = Get((controller.PlayfieldId, controller.Index));
                if (plot.OrganizationId != organizationId || plot.ClaimPending)
                    return CityWriteResult.Fail(CityWriteStatus.NotOwner);
                if (!Manages(organizationId, characterId))
                    return CityWriteResult.Fail(CityWriteStatus.NoPermission);
                if (RefuseHouses)
                    return CityWriteResult.Fail(CityWriteStatus.Refused, BuildRefusal.Overlaps);
                Reservations++;
                BuildRefusal refusal = CityPlacement.Check(controller, template, tileX, tileZ, rotation, plot.Houses,
                    id => cities.TryGetTemplate(id, out HouseTemplateDefinition t) ? t : null, cities.Rules.MaxHousesPerPlot, false);
                if (refusal != BuildRefusal.None)
                    return CityWriteResult.Fail(CityWriteStatus.Refused, refusal);
                var house = new CityHouse(++_nextHouse, template.Id, tileX, tileZ, rotation, characterId, true, _now, null, itemLowId, itemHighId, itemQuality);
                CityPlotState next = With(plot, houses: plot.Houses.Append(house).ToArray());
                _plots[next.Key] = next;
                return new CityWriteResult(CityWriteStatus.Done, BuildRefusal.None, house.HouseId, next);
            }
        }

        public bool ConfirmHouse(int playfieldId, int controllerIndex, long houseId, int organizationId, bool headquarters)
        {
            lock (_sync)
            {
                CityPlotState plot = Get((playfieldId, controllerIndex));
                CityHouse? house = plot.Houses.FirstOrDefault(h => h.HouseId == houseId);
                if (plot.OrganizationId != organizationId || house == null)
                    return false;
                if (house.Pending)
                    _plots[plot.Key] = With(plot, clearEmpty: true, hq: headquarters ? plot.HeadquartersBuiltAtUtc ?? _now : null,
                        houses: plot.Houses.Select(h => h.HouseId == houseId ? h with { Pending = false } : h).ToArray());
                return true;
            }
        }

        public CityPlotState AbandonHouse(int playfieldId, int controllerIndex, long houseId)
        {
            lock (_sync)
            {
                CityPlotState plot = Get((playfieldId, controllerIndex));
                _plots[plot.Key] = plot = With(plot, houses: plot.Houses.Where(h => !(h.HouseId == houseId && h.Pending)).ToArray());
                return plot;
            }
        }

        public CityWriteResult StartDemolish(CityControllerDefinition controller, CityCatalog cities, int organizationId, int characterId,
            int tileX, int tileZ, CityManagePermission canManage)
        {
            lock (_sync)
            {
                CityPlotState plot = Get((controller.PlayfieldId, controller.Index));
                if (plot.OrganizationId != organizationId || !Manages(organizationId, characterId))
                    return CityWriteResult.Fail(CityWriteStatus.NoPermission);
                CityHouse? house = CityPlacement.HouseAt(plot.Houses, tileX, tileZ);
                if (house == null || house.Pending || house.DemolishAtUtc != null)
                    return CityWriteResult.Fail(CityWriteStatus.NoHouse);
                DateTime at = _now + cities.Rules.Demolish;
                bool hq = cities.TryGetTemplate(house.TemplateId, out HouseTemplateDefinition t) && t.Type == HouseType.Headquarters;
                CityPlotState next = With(plot, houses: plot.Houses
                    .Select(h => (hq || h.HouseId == house.HouseId) && h.DemolishAtUtc == null ? h with { DemolishAtUtc = at } : h).ToArray());
                _plots[next.Key] = next;
                return new CityWriteResult(CityWriteStatus.Done, BuildRefusal.None, house.HouseId, next);
            }
        }

        public CityWriteResult ToggleCloak(CityControllerDefinition controller, CityCatalog cities, int organizationId, int characterId,
            CityManagePermission canManage) => CityWriteResult.Fail(CityWriteStatus.NoCloakingDevice);

        public IReadOnlyList<(int PlayfieldId, int ControllerIndex)> DuePlots(CityRules cityRules)
        {
            lock (_sync)
                return _plots.Values.Where(p => p.IsOwned).Select(p => p.Key).ToArray();
        }

        public (IReadOnlyList<CityDueEvent> Events, CityPlotState State) ProcessDue(CityControllerDefinition controller, CityCatalog cities)
        {
            lock (_sync)
            {
                var events = new List<CityDueEvent>();
                CityPlotState plot = Get((controller.PlayfieldId, controller.Index));
                if (!plot.IsActive)
                    return (events, plot);
                CityHouse[] remaining = plot.Houses.Where(h => h.DemolishAtUtc is not { } at || at > _now).ToArray();
                if (remaining.Length != plot.Houses.Count)
                    _plots[plot.Key] = plot = With(plot, houses: remaining, empty: remaining.Length == 0 ? _now : null);
                if (plot.NextBillAtUtc is { } bill && bill <= _now)
                {
                    long upkeep = plot.Upkeep(cities, controller);
                    if (Bank >= upkeep)
                    {
                        Bank -= upkeep;
                        _plots[plot.Key] = plot = With(plot, bill: bill + cities.Rules.BillingPeriod);
                        events.Add(new CityDueEvent(CityDueEventKind.UpkeepPaid, plot.OrganizationId!.Value, upkeep, Bank, 30, 0));
                    }
                    else
                    {
                        events.Add(new CityDueEvent(CityDueEventKind.DemolishedForUpkeep, plot.OrganizationId!.Value, upkeep, Bank, 0, 0));
                        _plots[plot.Key] = plot = With(plot, release: true);
                    }
                }

                return (events, plot);
            }
        }
    }

    /// <summary>The character rows the payment's SyncStats stores, by character.</summary>
    sealed class Characters : ICharacterRepository
    {
        internal readonly ConcurrentDictionary<int, long> Stored = new();

        public bool SaveOnlineCheckpoint(int characterId, CharacterRecord? location, IReadOnlyList<StatRecord> stats)
        {
            foreach (StatRecord stat in stats)
                if (stat.StatId == (int)CharacterStat.Cash)
                    Stored[characterId] = stat.StatValue;
            return true;
        }

        public CharacterRecord? GetById(int characterId) => throw new NotSupportedException();
        public void SetOnline(int characterId) => throw new NotSupportedException();
        public void SetOffline(int characterId) => throw new NotSupportedException();
        public void SaveLocation(CharacterRecord character, int online) => throw new NotSupportedException();
        public void SaveSnapshot(CharacterRecord character, int online, IReadOnlyList<StatRecord> stats) => throw new NotSupportedException();
    }

    sealed class Logger : IZoneLogger
    {
        readonly object _sync = new();
        string _problems = string.Empty;

        internal string Problems { get { lock (_sync) return _problems; } }

        internal void Reset() { lock (_sync) _problems = string.Empty; }

        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { lock (_sync) _problems += "WARN " + message + Environment.NewLine; }
        public void Error(string message) { lock (_sync) _problems += "ERROR " + message + Environment.NewLine; }
        public void Error(Exception exception, string message) { lock (_sync) _problems += "ERROR " + message + " " + exception + Environment.NewLine; }
        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }

    sealed class Session : IZoneSession
    {
        internal readonly List<MessageBody> Sent = [];
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public bool TransferToPlayfield(ZoneEngine_New.Core.Playfield.Playfield destination, Vector3 landing) => false;
        public void Send(byte[] packet) { }
        public void Send(MessageBody body) { lock (Sent) Sent.Add(body); }
        public void Send(MessageBody body, int sender, int receiver) => Send(body);
        public void SendInitiateCompression() { }
        public void Close() => State = SessionState.Closed;
    }

    /// <summary>Records the inventory batches; every row an item retire writes is counted.</summary>
    sealed class Persistence : ICharacterCoalesceCommit, IInventoryMutationPersistence, ITradePersistence
    {
        readonly List<InventoryMutationBatch> _batches = [];

        internal int Retired(int instanceId)
        {
            lock (_batches)
                return _batches.SelectMany(b => b.Locations).Count(l => l.InstanceId == instanceId && l.ContainerType == (int)IdentityType.None);
        }

        public void Persist(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates, int characterId,
            IReadOnlyList<int> uploadedNanoIds, IReadOnlyList<ActiveNanoRecord>? activeNanos, IReadOnlyList<SkillLockRecord>? skillLocks) { }

        public void Persist(InventoryMutationBatch batch) { lock (_batches) _batches.Add(batch); }

        public void Persist(TradePersistenceBatch batch) { }
    }

    sealed class Items : IItemBuilder
    {
        int _next = 700000;

        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null) => new()
            {
                LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount,
                InstanceId = instanceId, Identity = identity ?? Identity.None, Definition = CreateTemplate(lowId, highId, quality)
            };

        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
        {
            Item item = Create(lowId, highId, quality, source, stackCount);
            item.AssignInstanceId(++_next);
            return item;
        }

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }

    sealed class Templates : IItemTemplateCatalog
    {
        internal readonly ConcurrentDictionary<int, ItemTemplate> Known = new();

        public bool TryGet(int aoid, out ItemTemplate template) => Known.TryGetValue(aoid, out template!);

        public ItemTemplate Require(int aoid) => throw new KeyNotFoundException(aoid.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    sealed class Ids : IItemInstanceIdAllocator
    {
        int _next = 800000;
        public int Allocate() => Interlocked.Increment(ref _next);
    }

    public class FakeProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?>? Handler;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Handler != null ? Handler(targetMethod!, args) : throw new AssertFailedException("Unexpected call " + targetMethod!.Name);
    }

    static T Fake<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        T proxy = DispatchProxy.Create<T, FakeProxy>();
        ((FakeProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    static T Blank<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    static object Get(object target, string name) => Field(target.GetType(), name).GetValue(target)!;

    static void Set(object target, string name, object value) => Field(target.GetType(), name).SetValue(target, value);

    static FieldInfo Field(Type type, string name)
    {
        for (Type? at = type; at != null; at = at.BaseType)
            if (at.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) is FieldInfo field)
                return field;
        throw new MissingFieldException(type.Name, name);
    }
}
