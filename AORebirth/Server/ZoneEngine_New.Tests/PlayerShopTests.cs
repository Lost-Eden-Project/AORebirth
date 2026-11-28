namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using AORebirth.Enums;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;
using LostEden.Protocol.Messages.N3Messages;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Configuration;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.PlayerShops;
using ZoneEngine_New.Core.Trade;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Player shops on the players' path: a terminal is used, rented, stocked, priced, bought from, emptied, withdrawn from
/// and given up through <see cref="PlayerShopService"/> exactly as the engine runs it (the action message on the tick, the
/// commit in the acting character's write-behind lane, the settle on the playfield tick), against a memory database that
/// refuses what the MySQL rows refuse. Every test ends with storage equal to memory, and fails if an item could be
/// duplicated, lost or reminted, credits made or lost, or a shop emptied or given up behind its owner's back.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PlayerShopTests
{
    const int OwnerId = 101;
    const int BuyerId = 102;
    const int OtherId = 103;
    const int PlayfieldId = 4356;
    const int StartCash = 10_000_000;
    static readonly int TerminalA = unchecked((int)0xC0001104);
    static readonly int TerminalB = unchecked((int)0xC0011104);

    [TestMethod]
    public void UsingAShopForRentOffersItAndRentingTakesTheRentOnce()
    {
        using var rig = new Rig();
        Assert.IsTrue(rig.A.TryUse(rig.Owner));
        Assert.AreEqual(PlayerShopState.ForRent, rig.LastView(rig.Owner).State);
        Assert.AreEqual(rig.Rules.RentCost, rig.LastView(rig.Owner).RentCost);

        Assert.AreEqual(PlayerShopResult.Done, rig.Act(rig.Owner, PlayerShopAction.Rent, price: rig.Rules.RentCost));
        Assert.AreEqual(StartCash - rig.Rules.RentCost, rig.Cash(rig.Owner));
        // Clicking accept again, or a second dialog, never charges twice.
        Assert.AreEqual(PlayerShopResult.NotAvailable, rig.Act(rig.Owner, PlayerShopAction.Rent, price: rig.Rules.RentCost));
        Assert.AreEqual(StartCash - rig.Rules.RentCost, rig.Cash(rig.Owner));

        rig.Store();
        PlayerShop shop = rig.ShopA;
        Assert.IsFalse(shop.Opening);
        Assert.AreEqual(OwnerId, rig.Db.Shops[shop.ShopId].OwnerId);
        Assert.AreEqual(StartCash - rig.Rules.RentCost, rig.Db.Cash[OwnerId]);
        Assert.AreEqual("Owner's Shop", rig.Db.Shops[shop.ShopId].Name);
        Assert.IsTrue(rig.Sent<PlayerShopNameMessage>(rig.Buyer).Any(m => m.Terminal.Instance == TerminalA && m.ShopName == "Owner's Shop"),
            "everyone on the playfield sees the new name");
        rig.AssertStorageMatchesMemory();
    }

    /// <summary>
    /// Live 2026-11-26: the owner's client dropped and logged back in while the character was link-dead. The new client
    /// said hello, but the character still spoke the server's messages from the old connection, so the hello was ignored
    /// and the new client never got the shop names (nor its bag names or loot order).
    /// </summary>
    [TestMethod]
    public void AClientThatLogsBackInWhileLinkDeadIsToldTheShopNamesAfterItsHello()
    {
        using var rig = new Rig();
        rig.RentA();
        var hello = new ZoneEngine_New.Core.MessageHandlers.ClientHelloMessageHandler(new ZoneEngine_New.Core.Teams.TeamService(), rig.Shops);

        rig.Owner.EnterLinkDead(TimeSpan.FromMinutes(1));
        var session = new Session();
        rig.Owner.EnterOnline(session);
        hello.Handle(new ClientHelloMessage { Identity = rig.Owner.Identity }, session);
        Assert.IsTrue(session.Of<PlayerShopNameMessage>().Any(m => m.Terminal.Instance == TerminalA && m.ShopName == "Owner's Shop"
            && m.OwnerName == "Owner"), "the new client labels the owner's booth");

        // A repeated hello on the same connection sends nothing again.
        int names = session.Of<PlayerShopNameMessage>().Count();
        hello.Handle(new ClientHelloMessage { Identity = rig.Owner.Identity }, session);
        Assert.AreEqual(names, session.Of<PlayerShopNameMessage>().Count());
        rig.AssertStorageMatchesMemory();
    }

    [TestMethod]
    public void ACharacterRentsOneShopAndARentedTerminalCannotBeTaken()
    {
        using var rig = new Rig();
        rig.RentA();
        Assert.AreEqual(PlayerShopResult.AlreadyOwnShop, rig.Act(rig.Owner, PlayerShopAction.Rent, price: rig.Rules.RentCost, terminal: rig.B));
        Assert.AreEqual(PlayerShopResult.NotAvailable, rig.Act(rig.Buyer, PlayerShopAction.Rent, price: rig.Rules.RentCost));
        Assert.AreEqual(StartCash, rig.Cash(rig.Buyer));
        // A rent the dialog showed at another price is refused rather than charged at the new one.
        Assert.AreEqual(PlayerShopResult.Changed, rig.Act(rig.Buyer, PlayerShopAction.Rent, price: rig.Rules.RentCost - 1, terminal: rig.B));
        Assert.AreEqual(1, rig.Db.Shops.Count);
        rig.AssertStorageMatchesMemory();
    }

    [TestMethod]
    public void StockingKeepsTheItemsInstanceIdAndTakesItOutOfTheInventory()
    {
        using var rig = new Rig();
        rig.RentA();
        Item sword = rig.Give(rig.Owner, 0x41, 1000);

        Assert.AreEqual(PlayerShopResult.Done, rig.Act(rig.Owner, PlayerShopAction.Stock, slot: 0x41, price: 12_345));
        Assert.IsFalse(rig.Owner.Inventory.Inventory.Content.ContainsKey(0x41));
        Assert.IsTrue(rig.Sent<CharacterActionMessage>(rig.Owner).Any(m => m.Action == CharacterActionType.DeleteItem && m.Target.Instance == 0x41));
        PlayerShopListing listing = rig.ShopA.Listings.Values.Single();
        Assert.AreSame(sword, listing.Item);
        Assert.IsTrue(listing.Pending, "nothing can touch a listing before its stock is stored");

        rig.Store();
        Assert.IsFalse(listing.Pending);
        MemoryDb.Row row = rig.Db.Items[sword.InstanceId];
        Assert.AreEqual((int)IdentityType.PlayerShopInventory, row.Type);
        Assert.AreEqual(rig.ShopA.ShopId, row.Instance);
        Assert.AreEqual(12_345, rig.Db.Prices[sword.InstanceId].Price);
        rig.AssertStorageMatchesMemory();
    }

    [TestMethod]
    public void NoDropItemsBagsAndOtherPlayersCannotStock()
    {
        using var rig = new Rig();
        rig.RentA();
        rig.Give(rig.Owner, 0x41, 1000, flags: (int)ItemFlags.NoDrop);
        rig.Give(rig.Owner, 0x42, 1001, bag: true);
        Item loose = rig.Give(rig.Owner, 0x43, 1002);
        rig.Give(rig.Buyer, 0x41, 1003);

        Assert.AreEqual(PlayerShopResult.ItemRefused, rig.Act(rig.Owner, PlayerShopAction.Stock, slot: 0x41, price: 10));
        Assert.AreEqual(PlayerShopResult.ItemRefused, rig.Act(rig.Owner, PlayerShopAction.Stock, slot: 0x42, price: 10));
        Assert.AreEqual(PlayerShopResult.ItemRefused, rig.Act(rig.Owner, PlayerShopAction.Stock, slot: 0x44, price: 10), "empty slot");
        Assert.AreEqual(PlayerShopResult.NotOwner, rig.Act(rig.Buyer, PlayerShopAction.Stock, slot: 0x41, price: 10));
        Assert.AreEqual(PlayerShopResult.BadPrice, rig.Act(rig.Owner, PlayerShopAction.Stock, slot: 0x43, price: 0));
        Assert.AreEqual(PlayerShopResult.BadPrice, rig.Act(rig.Owner, PlayerShopAction.Stock, slot: 0x43, price: -5));
        Assert.AreEqual(0, rig.ShopA.Count);
        Assert.IsTrue(rig.Owner.Inventory.Inventory.Content.ContainsValue(loose));
        rig.Store();
        rig.AssertStorageMatchesMemory();
    }

    [TestMethod]
    public void AShopHoldsAtMostFiftyItems()
    {
        using var rig = new Rig();
        Assert.AreEqual(50, rig.Rules.MaxItems);
        rig.RentA();
        for (int i = 0; i < 51; i++)
        {
            int slot = rig.Owner.Inventory.Inventory.Offset;
            rig.Give(rig.Owner, slot, 2000 + i);
            PlayerShopResult result = rig.Act(rig.Owner, PlayerShopAction.Stock, slot: slot, price: 100);
            Assert.AreEqual(i < 50 ? PlayerShopResult.Done : PlayerShopResult.ShopFull, result, "item " + (i + 1));
            // The inventory slot is reused at once: its earlier item's move is stored first, as the lane would.
            rig.Store();
        }
        rig.Store();
        Assert.AreEqual(50, rig.ShopA.Count);
        Assert.AreEqual(50, rig.Db.Items.Values.Count(r => r.Type == (int)IdentityType.PlayerShopInventory));
        rig.AssertStorageMatchesMemory();
    }

    [TestMethod]
    public void BuyingMovesTheSameItemToTheBuyerAtTheOwnersPriceWithNoComputerLiteracyDiscount()
    {
        using var rig = new Rig();
        rig.RentA();
        Item sword = rig.Give(rig.Owner, 0x41, 1000);
        rig.Act(rig.Owner, PlayerShopAction.Stock, slot: 0x41, price: 250_000);
        rig.Store();
        rig.Buyer.Stats.Set(CharacterStat.ComputerLiteracy, 3000, StatDetail.Base);

        Assert.IsTrue(rig.A.TryUse(rig.Buyer));
        PlayerShopViewMessage view = rig.LastView(rig.Buyer);
        Assert.AreEqual(PlayerShopState.Customer, view.State);
        Assert.AreEqual(0, view.Balance, "a customer never sees the balance");
        PlayerShopViewItem shown = view.Items.Single();
        Assert.AreEqual(250_000, shown.Price);

        Assert.AreEqual(PlayerShopResult.Done, rig.Act(rig.Buyer, PlayerShopAction.Buy, slot: shown.Slot, item: shown.ItemInstance, price: shown.Price));
        Assert.AreEqual(StartCash - 250_000, rig.Cash(rig.Buyer));
        Assert.AreSame(sword, rig.Buyer.Inventory.Inventory.Content.Values.Single(), "the item is never reminted");
        Assert.AreEqual(0, rig.ShopA.Count);
        Assert.AreEqual(0, rig.ShopA.Balance, "a sale reaches the balance only once stored");

        rig.Store();
        Assert.AreEqual(250_000, rig.ShopA.Balance);
        Assert.AreEqual(250_000, rig.Db.Shops[rig.ShopA.ShopId].Balance);
        Assert.AreEqual((int)IdentityType.Inventory, rig.Db.Items[sword.InstanceId].Type);
        Assert.AreEqual(BuyerId, rig.Db.Items[sword.InstanceId].Instance);
        Assert.IsFalse(rig.Db.Prices.ContainsKey(sword.InstanceId));
        rig.AssertStorageMatchesMemory();
    }

    [TestMethod]
    public void AnItemCannotBeBoughtBeforeItsStockIsStoredOrAtAStalePrice()
    {
        using var rig = new Rig();
        rig.RentA();
        Item sword = rig.Give(rig.Owner, 0x41, 1000);
        rig.Act(rig.Owner, PlayerShopAction.Stock, slot: 0x41, price: 100);
        PlayerShopListing listing = rig.ShopA.Listings.Values.Single();

        // The stock is still queued in the owner's lane: a buyer's sale queued now could store first.
        Assert.AreEqual(PlayerShopResult.Changed, rig.Act(rig.Buyer, PlayerShopAction.Buy, slot: listing.Slot, item: sword.InstanceId, price: 100));
        rig.Store();

        // The owner raises the price while the buyer still sees the old one.
        Assert.AreEqual(PlayerShopResult.Done, rig.Act(rig.Owner, PlayerShopAction.SetPrice, slot: listing.Slot, item: sword.InstanceId, price: 9_000_000));
        Assert.AreEqual(PlayerShopResult.Changed, rig.Act(rig.Buyer, PlayerShopAction.Buy, slot: listing.Slot, item: sword.InstanceId, price: 100));
        rig.Store();
        Assert.AreEqual(PlayerShopResult.Changed, rig.Act(rig.Buyer, PlayerShopAction.Buy, slot: listing.Slot, item: sword.InstanceId, price: 100));
        Assert.AreEqual(StartCash, rig.Cash(rig.Buyer));
        Assert.AreEqual(9_000_000, rig.Db.Prices[sword.InstanceId].Price);
        // Another item's id at that slot, or another slot, is not the item the buyer saw.
        Assert.AreEqual(PlayerShopResult.Changed, rig.Act(rig.Buyer, PlayerShopAction.Buy, slot: listing.Slot, item: sword.InstanceId + 1, price: 9_000_000));
        Assert.AreEqual(PlayerShopResult.Changed, rig.Act(rig.Buyer, PlayerShopAction.Buy, slot: listing.Slot + 1, item: sword.InstanceId, price: 9_000_000));
        rig.AssertStorageMatchesMemory();
    }

    [TestMethod]
    public void TwoBuyersOnTheSameTickGetOneItemOnce()
    {
        using var rig = new Rig();
        rig.RentA();
        Item sword = rig.Give(rig.Owner, 0x41, 1000);
        rig.Act(rig.Owner, PlayerShopAction.Stock, slot: 0x41, price: 500);
        rig.Store();
        PlayerShopListing listing = rig.ShopA.Listings.Values.Single();

        Assert.AreEqual(PlayerShopResult.Done, rig.Act(rig.Buyer, PlayerShopAction.Buy, slot: listing.Slot, item: sword.InstanceId, price: 500));
        Assert.AreEqual(PlayerShopResult.Changed, rig.Act(rig.Other, PlayerShopAction.Buy, slot: listing.Slot, item: sword.InstanceId, price: 500));
        Assert.AreEqual(PlayerShopResult.Changed, rig.Act(rig.Buyer, PlayerShopAction.Buy, slot: listing.Slot, item: sword.InstanceId, price: 500), "replayed");
        // The losing buyer's lane runs first: nothing of theirs was queued.
        rig.Store(rig.Other, rig.Buyer, rig.Owner);
        Assert.AreEqual(StartCash - 500, rig.Cash(rig.Buyer));
        Assert.AreEqual(StartCash, rig.Cash(rig.Other));
        Assert.AreEqual(0, rig.Other.Inventory.Inventory.Content.Count);
        Assert.AreEqual(500, rig.Db.Shops[rig.ShopA.ShopId].Balance);
        rig.AssertStorageMatchesMemory();
    }

    [TestMethod]
    public void TheOwnerCannotBuyAndOnlyTheOwnerManagesTheShop()
    {
        using var rig = new Rig();
        rig.RentA();
        Item sword = rig.Give(rig.Owner, 0x41, 1000);
        rig.Act(rig.Owner, PlayerShopAction.Stock, slot: 0x41, price: 500);
        rig.Store();
        int slot = rig.ShopA.Listings.Keys.Single();

        Assert.AreEqual(PlayerShopResult.NotOwner, rig.Act(rig.Owner, PlayerShopAction.Buy, slot: slot, item: sword.InstanceId, price: 500));
        Assert.AreEqual(PlayerShopResult.NotOwner, rig.Act(rig.Buyer, PlayerShopAction.SetPrice, slot: slot, item: sword.InstanceId, price: 1));
        Assert.AreEqual(PlayerShopResult.NotOwner, rig.Act(rig.Buyer, PlayerShopAction.Unstock, slot: slot, item: sword.InstanceId));
        Assert.AreEqual(PlayerShopResult.NotOwner, rig.Act(rig.Buyer, PlayerShopAction.Withdraw));
        Assert.AreEqual(PlayerShopResult.NotOwner, rig.Act(rig.Buyer, PlayerShopAction.Rename, name: "Mine now"));
        Assert.AreEqual(PlayerShopResult.NotOwner, rig.Act(rig.Buyer, PlayerShopAction.Abandon));
        rig.Store();
        Assert.AreEqual(500, rig.Db.Prices[sword.InstanceId].Price);
        Assert.AreEqual("Owner's Shop", rig.ShopA.Name);
        rig.AssertStorageMatchesMemory();
    }

    [TestMethod]
    public void TakingAnItemBackReturnsTheSameItemToTheOwner()
    {
        using var rig = new Rig();
        rig.RentA();
        Item sword = rig.Give(rig.Owner, 0x41, 1000);
        rig.Act(rig.Owner, PlayerShopAction.Stock, slot: 0x41, price: 500);
        Assert.AreEqual(PlayerShopResult.Changed, rig.Act(rig.Owner, PlayerShopAction.Unstock, slot: 0, item: sword.InstanceId), "stock still queued");
        rig.Store();

        Assert.AreEqual(PlayerShopResult.Done, rig.Act(rig.Owner, PlayerShopAction.Unstock, slot: 0, item: sword.InstanceId));
        Assert.AreSame(sword, rig.Owner.Inventory.Inventory.Content.Values.Single());
        Assert.AreEqual(PlayerShopResult.Changed, rig.Act(rig.Owner, PlayerShopAction.Unstock, slot: 0, item: sword.InstanceId), "replayed");
        rig.Store();
        Assert.AreEqual((int)IdentityType.Inventory, rig.Db.Items[sword.InstanceId].Type);
        Assert.IsFalse(rig.Db.Prices.ContainsKey(sword.InstanceId));
        rig.AssertStorageMatchesMemory();
    }

    [TestMethod]
    public void OnlyStoredSalesCanBeWithdrawnAndNeverTwice()
    {
        using var rig = new Rig();
        rig.RentA();
        Item sword = rig.Give(rig.Owner, 0x41, 1000);
        rig.Act(rig.Owner, PlayerShopAction.Stock, slot: 0x41, price: 40_000);
        rig.Store();
        rig.Act(rig.Buyer, PlayerShopAction.Buy, slot: 0, item: sword.InstanceId, price: 40_000);
        int ownerCash = rig.Cash(rig.Owner);

        Assert.AreEqual(PlayerShopResult.NotEnoughCredits, rig.Act(rig.Owner, PlayerShopAction.Withdraw, price: 40_000), "the sale is not stored yet");
        rig.Store();
        Assert.AreEqual(PlayerShopResult.NotEnoughCredits, rig.Act(rig.Owner, PlayerShopAction.Withdraw, price: 40_001));
        Assert.AreEqual(PlayerShopResult.Done, rig.Act(rig.Owner, PlayerShopAction.Withdraw, price: 15_000));
        Assert.AreEqual(PlayerShopResult.Done, rig.Act(rig.Owner, PlayerShopAction.Withdraw));
        Assert.AreEqual(PlayerShopResult.NotEnoughCredits, rig.Act(rig.Owner, PlayerShopAction.Withdraw));
        Assert.AreEqual(ownerCash + 40_000, rig.Cash(rig.Owner));
        rig.Store();
        Assert.AreEqual(0, rig.Db.Shops[rig.ShopA.ShopId].Balance);
        Assert.AreEqual(ownerCash + 40_000, rig.Db.Cash[OwnerId]);
        rig.AssertStorageMatchesMemory();
    }

    [TestMethod]
    public void AShopIsGivenUpOnlyWhenEmptyOfItemsAndCredits()
    {
        using var rig = new Rig();
        rig.RentA();
        Item sword = rig.Give(rig.Owner, 0x41, 1000);
        rig.Act(rig.Owner, PlayerShopAction.Stock, slot: 0x41, price: 700);
        rig.Store();
        Assert.AreEqual(PlayerShopResult.NotEmpty, rig.Act(rig.Owner, PlayerShopAction.Abandon));

        rig.Act(rig.Buyer, PlayerShopAction.Buy, slot: 0, item: sword.InstanceId, price: 700);
        Assert.AreEqual(PlayerShopResult.NotEmpty, rig.Act(rig.Owner, PlayerShopAction.Abandon), "a sale is still being stored");
        rig.Store();
        Assert.AreEqual(PlayerShopResult.NotEmpty, rig.Act(rig.Owner, PlayerShopAction.Abandon), "the balance holds the sale");
        rig.Act(rig.Owner, PlayerShopAction.Withdraw);
        rig.Store();

        int shopId = rig.ShopA.ShopId;
        Assert.AreEqual(PlayerShopResult.Done, rig.Act(rig.Owner, PlayerShopAction.Abandon));
        Assert.AreEqual(PlayerShopResult.NotAvailable, rig.Act(rig.Buyer, PlayerShopAction.Rent, price: rig.Rules.RentCost), "still being given up");
        rig.Store();
        Assert.IsFalse(rig.Shops.TryGetShop(PlayfieldId, TerminalA, out _));
        Assert.IsFalse(rig.Db.Shops.ContainsKey(shopId));
        Assert.IsTrue(rig.Sent<PlayerShopNameMessage>(rig.Buyer).Any(m => m.Terminal.Instance == TerminalA && m.ShopName.Length == 0));
        // The terminal is for rent again, and its old owner may rent another.
        Assert.AreEqual(PlayerShopResult.Done, rig.Act(rig.Owner, PlayerShopAction.Rent, price: rig.Rules.RentCost, terminal: rig.B));
        rig.Store();
        rig.AssertStorageMatchesMemory();
    }

    [TestMethod]
    public void RenamingTakesOnlyAShowableName()
    {
        using var rig = new Rig();
        rig.RentA();
        Assert.AreEqual(PlayerShopResult.BadName, rig.Act(rig.Owner, PlayerShopAction.Rename, name: "   "));
        Assert.AreEqual(PlayerShopResult.BadName, rig.Act(rig.Owner, PlayerShopAction.Rename, name: new string('x', rig.Rules.MaxNameLength + 1)));
        Assert.AreEqual(PlayerShopResult.BadName, rig.Act(rig.Owner, PlayerShopAction.Rename, name: "Bad\u0001Name"));
        Assert.AreEqual(PlayerShopResult.BadName, rig.Act(rig.Owner, PlayerShopAction.Rename, name: "<font color=red>Free</font>"));
        Assert.AreEqual(PlayerShopResult.Done, rig.Act(rig.Owner, PlayerShopAction.Rename, name: "  Delmus Armoury  "));
        rig.Store();
        Assert.AreEqual("Delmus Armoury", rig.Db.Shops[rig.ShopA.ShopId].Name);
        Assert.IsTrue(rig.Sent<PlayerShopNameMessage>(rig.Other).Any(m => m.ShopName == "Delmus Armoury"));
        rig.AssertStorageMatchesMemory();
    }

    [TestMethod]
    public void AStockClientAFarPlayerAndATraderCannotUseTheShop()
    {
        using var rig = new Rig();
        var stock = rig.Online(104, "Stock", hello: false);
        Assert.IsTrue(rig.A.TryUse(stock));
        Assert.AreEqual(0, rig.Sent<PlayerShopViewMessage>(stock).Count());
        rig.Act(stock, PlayerShopAction.Rent, price: rig.Rules.RentCost);
        Assert.AreEqual(0, rig.Sent<PlayerShopResultMessage>(stock).Count(), "a stock client is never sent the server's own messages");

        rig.Buyer.Position = new Vector3(140, 10, 100);
        Assert.AreEqual(PlayerShopResult.CannotUse, rig.Act(rig.Buyer, PlayerShopAction.Rent, price: rig.Rules.RentCost));
        Assert.AreEqual(StartCash, rig.Cash(rig.Buyer));
        Assert.IsFalse(rig.Shops.TryGetShop(PlayfieldId, TerminalA, out _));
    }

    [TestMethod]
    public void ARestartReadsTheShopBackExactlyAsStored()
    {
        using var rig = new Rig();
        rig.RentA();
        Item a = rig.Give(rig.Owner, 0x41, 1000);
        Item b = rig.Give(rig.Owner, 0x42, 1001, stack: 7);
        rig.Act(rig.Owner, PlayerShopAction.Stock, slot: 0x41, price: 11);
        rig.Act(rig.Owner, PlayerShopAction.Stock, slot: 0x42, price: 22);
        rig.Store();
        rig.Act(rig.Buyer, PlayerShopAction.Buy, slot: 0, item: a.InstanceId, price: 11);
        rig.Act(rig.Owner, PlayerShopAction.Rename, name: "Restarted");
        rig.Store();

        PlayerShopService reloaded = rig.NewService();
        reloaded.Start();
        Assert.IsTrue(reloaded.TryGetShop(PlayfieldId, TerminalA, out PlayerShop shop));
        Assert.AreEqual("Restarted", shop.Name);
        Assert.AreEqual(11, shop.Balance);
        Assert.IsFalse(shop.Locked);
        PlayerShopListing listing = shop.Listings.Values.Single();
        Assert.AreEqual(b.InstanceId, listing.Item.InstanceId);
        Assert.AreEqual(7, listing.Item.StackCount);
        Assert.AreEqual(22, listing.Price);
        Assert.IsTrue(reloaded.TryGetShopOf(OwnerId, out _));
    }

    [TestMethod]
    public void ASaleThatCannotBeStoredLocksTheShopAndDuplicatesNothing()
    {
        using var rig = new Rig();
        rig.RentA();
        Item sword = rig.Give(rig.Owner, 0x41, 1000);
        rig.Act(rig.Owner, PlayerShopAction.Stock, slot: 0x41, price: 900);
        rig.Store();

        rig.Db.Refuse = batch => batch.PlayerShop?.Kind == PlayerShopWriteKind.Sale;
        Assert.AreEqual(PlayerShopResult.Done, rig.Act(rig.Buyer, PlayerShopAction.Buy, slot: 0, item: sword.InstanceId, price: 900));
        Assert.IsTrue(rig.ShopA.Listings.Count == 0, "the sale is applied in memory while its write is pending");
        rig.Store();
        Assert.IsTrue(rig.Buyer.IsPersistenceQuarantined, "the buyer reloads from storage");
        // Storage still holds the item in the shop, priced, and the buyer's credits: nothing was duplicated.
        Assert.AreEqual((int)IdentityType.PlayerShopInventory, rig.Db.Items[sword.InstanceId].Type);
        Assert.AreEqual(900, rig.Db.Prices[sword.InstanceId].Price);
        Assert.AreEqual(StartCash, rig.Db.Cash[BuyerId]);
        Assert.AreEqual(0, rig.Db.Shops[rig.ShopA.ShopId].Balance);
        // LE-33: the quarantined buyer's write locked the shop, and with the buyer's lane run dry the same tick read the
        // shop back from storage and reopened it. The owner's items and credits are not held until a restart.
        Assert.IsFalse(rig.ShopA.Locked, "the shop reopened from storage");
        Assert.IsFalse(rig.ShopA.RecoverFromStorage);
        Assert.AreEqual(0, rig.ShopA.PendingWriteCount);
        Assert.AreEqual(sword.InstanceId, rig.ShopA.Listings.Values.Single().Item.InstanceId);
        Assert.IsFalse(rig.ShopA.Listings.Values.Single().Pending);
        Assert.AreEqual(900, rig.ShopA.Listings.Values.Single().Price);
        Assert.AreEqual(0, rig.ShopA.Balance);
        rig.Db.Refuse = null;
        PlayerShopService reloaded = rig.NewService();
        reloaded.Start();
        Assert.IsTrue(reloaded.TryGetShop(PlayfieldId, TerminalA, out PlayerShop shop));
        Assert.AreEqual(sword.InstanceId, shop.Listings.Values.Single().Item.InstanceId);

        Assert.AreEqual(PlayerShopResult.Done, rig.Act(rig.Other, PlayerShopAction.Buy, slot: 0, item: sword.InstanceId, price: 900));
        rig.Store();
        // The reopened shop re-read its item from storage, so the buyer gets that same row (not the old in-memory object).
        Assert.AreEqual(1, rig.Other.Inventory.Inventory.Content.Values.Count(item => item.InstanceId == sword.InstanceId));
        Assert.AreEqual(900, rig.ShopA.Balance);
        Assert.AreEqual(PlayerShopResult.Done, rig.Act(rig.Owner, PlayerShopAction.Withdraw, price: 900));
        rig.Store();
        rig.AssertStorageMatchesMemory();
    }

    /// <summary>
    /// LE-33: a quarantined write whose rent never stored releases the terminal once the shop is read back; one whose
    /// listings read back inconsistent stays locked (storage itself disagrees, which only a restart with repaired rows ends).
    /// </summary>
    [TestMethod]
    public void AShopWhoseRentNeverStoredIsReleasedWhenReadBack()
    {
        using var rig = new Rig();
        rig.Db.Refuse = batch => batch.PlayerShop?.Kind == PlayerShopWriteKind.Rent;
        Assert.AreEqual(PlayerShopResult.Done, rig.Act(rig.Owner, PlayerShopAction.Rent, price: rig.Rules.RentCost));
        Assert.IsTrue(rig.ShopA.Opening, "the claim is held while the rent's write is pending");
        rig.Store();

        Assert.IsTrue(rig.Owner.IsPersistenceQuarantined);
        Assert.IsFalse(rig.Shops.TryGetShop(PlayfieldId, TerminalA, out _), "the terminal is for rent again once the claim is read back");
        Assert.IsFalse(rig.Shops.TryGetShopOf(OwnerId, out _));
        Assert.IsFalse(rig.Db.Shops.Values.Any(row => row.OwnerId == OwnerId), "storage never held the shop");
        rig.Db.Refuse = null;
        Assert.AreEqual(PlayerShopResult.Done, rig.Act(rig.Other, PlayerShopAction.Rent, price: rig.Rules.RentCost), "someone else can rent it");
        rig.Store();
        Assert.IsTrue(rig.Shops.TryGetShopOf(OtherId, out PlayerShop shop) && !shop.Locked);
    }

    /// <summary>
    /// The lane ordering soak for shops: owner and two buyers act in random order (stock, reprice, take back, buy,
    /// withdraw) with each lane stored at random moments and in random order. However the writes interleave, every item
    /// exists exactly once in storage where memory has it, and the credits add up: what buyers paid is what the shop and
    /// its owner took in.
    /// </summary>
    [TestMethod]
    public void RandomInterleavingsNeverDuplicateOrLoseItemsOrCredits()
    {
        for (int seed = 1; seed <= 12; seed++)
        {
            using var rig = new Rig();
            rig.RentA();
            var random = new Random(seed);
            var items = new List<Item>();
            for (int i = 0; i < 12; i++)
                items.Add(rig.Give(rig.Owner, rig.Owner.Inventory.Inventory.Offset + i, 3000 + i));
            int cashBefore = rig.Cash(rig.Owner) + rig.Cash(rig.Buyer) + rig.Cash(rig.Other);
            Player[] everyone = [rig.Owner, rig.Buyer, rig.Other];

            for (int step = 0; step < 160; step++)
            {
                PlayerShop shop = rig.ShopA;
                PlayerShopListing[] listings = shop.Listings.Values.ToArray();
                PlayerShopListing? pick = listings.Length == 0 ? null : listings[random.Next(listings.Length)];
                switch (random.Next(7))
                {
                    case 0:
                    case 1:
                        int[] carried = rig.Owner.Inventory.Inventory.Content.Keys.ToArray();
                        if (carried.Length > 0)
                            rig.Act(rig.Owner, PlayerShopAction.Stock, slot: carried[random.Next(carried.Length)], price: random.Next(1, 5000));
                        break;
                    case 2:
                        if (pick != null)
                            rig.Act(rig.Owner, PlayerShopAction.SetPrice, slot: pick.Slot, item: pick.Item.InstanceId, price: random.Next(1, 5000));
                        break;
                    case 3:
                        if (pick != null)
                            rig.Act(rig.Owner, PlayerShopAction.Unstock, slot: pick.Slot, item: pick.Item.InstanceId);
                        break;
                    case 4:
                    case 5:
                        if (pick != null)
                        {
                            Player buyer = random.Next(2) == 0 ? rig.Buyer : rig.Other;
                            rig.Act(buyer, PlayerShopAction.Buy, slot: pick.Slot, item: pick.Item.InstanceId, price: pick.Price);
                        }
                        break;
                    default:
                        rig.Act(rig.Owner, PlayerShopAction.Withdraw, price: random.Next(0, 3000));
                        break;
                }

                if (random.Next(3) == 0)
                    rig.Store(everyone.OrderBy(_ => random.Next()).Take(random.Next(1, 4)).ToArray());
            }

            rig.Store();
            rig.AssertStorageMatchesMemory();
            int cashAfter = rig.Cash(rig.Owner) + rig.Cash(rig.Buyer) + rig.Cash(rig.Other);
            Assert.AreEqual(cashBefore, cashAfter + (int)rig.ShopA.Balance, "seed " + seed + ": credits were made or lost");
            int held = everyone.Sum(p => p.Inventory.Inventory.Content.Count) + rig.ShopA.Count;
            Assert.AreEqual(items.Count, held, "seed " + seed + ": items were made or lost");
            Assert.AreEqual(items.Count, items.Select(i => i.InstanceId).Distinct().Count(id => rig.Db.Items.ContainsKey(id)));
        }
    }

    sealed class Rig : IDisposable
    {
        internal readonly Player Owner;
        internal readonly Player Buyer;
        internal readonly Player Other;
        internal readonly PlayerShopTerminal A;
        internal readonly PlayerShopTerminal B;
        internal readonly Logger Log = new();
        internal readonly MemoryDb Db = new();
        internal readonly PlayerShopService Shops;
        internal readonly GameplayPlayerShops Rules;
        readonly Items _items = new();
        readonly Ids _ids = new();
        readonly ZoneEngine_New.Core.Playfield.Playfield _world = Blank<ZoneEngine_New.Core.Playfield.Playfield>();
        readonly DynelRegistry _registry = new();
        readonly Dictionary<int, Player> _players = new();
        readonly InventoryFlushService _flush;
        readonly InventoryActionService _actions;
        readonly TradeService _trades;
        readonly GameplaySettings _settings = new();
        readonly ServiceProvider _services;

        internal Rig()
        {
            Set(_world, "<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = PlayfieldId });
            Set(_world, "<ResourceId>k__BackingField", PlayfieldId);
            Set(_world, "_dynelRegistry", _registry);
            Set(_world, "_logger", Log);

            _flush = new InventoryFlushService(id => _players.TryGetValue(id, out Player? p) ? p : null, () => _players.Values.ToArray(), Db, Log);
            _actions = new InventoryActionService(Db, _flush, _ids, Log);
            var templates = new Templates();
            IGameData gameData = Fake<IGameData>();
            _trades = new TradeService(Log, gameData, templates, new HashItemMinter(gameData, templates, _items), _ids, _flush, Db);
            Rules = _settings.PlayerShops;
            Shops = NewService();
            Shops.Start();

            var locality = new ZoneEngine_New.Core.Playfield.Locality.PlayfieldLocality(PlayfieldId, null);
            _services = new ServiceCollection().AddSingleton(_registry).AddSingleton(locality).AddSingleton(Shops).BuildServiceProvider();
            Set(_world, "_serviceProvider", _services);

            A = Terminal(TerminalA, new Vector3(100, 10, 100));
            B = Terminal(TerminalB, new Vector3(102, 10, 100));
            Owner = Online(OwnerId, "Owner");
            Buyer = Online(BuyerId, "Buyer");
            Other = Online(OtherId, "Other");
            Log.Reset();
        }

        internal PlayerShopService NewService()
            => new(_actions, _trades, new SnapshotRepository(Db), _items, _ids, _settings, Log);

        internal PlayerShop ShopA => Shops.TryGetShop(PlayfieldId, TerminalA, out PlayerShop shop) ? shop : throw new AssertFailedException("No shop at terminal A");

        PlayerShopTerminal Terminal(int instance, Vector3 at)
        {
            var terminal = new PlayerShopTerminal(new Identity { Type = PlayerShopTerminal.TerminalType, Instance = instance },
                new ItemTemplate { Id = 254117, Quality = 1, Name = "Shop for Rent" }) { Playfield = _world, Position = at };
            _registry.Register(terminal);
            return terminal;
        }

        internal Player Online(int id, string name, bool hello = true)
        {
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, Log, _items) { FirstName = name, Name = name };
            player.Inventory.Apply(new CharacterHydrationResult(), id, _items);
            player.Stats.Set(CharacterStat.Level, 200);
            player.Stats.Set(CharacterStat.Health, 100);
            player.Stats.Set(CharacterStat.MaxHealth, 100);
            player.Stats.Set(CharacterStat.Cash, StartCash, StatDetail.Base);
            Db.Cash[id] = StartCash;
            var session = new Session();
            player.EnterOnline(session);
            session.BindPlayer(player);
            player.Playfield = _world;
            player.Position = new Vector3(101, 10, 100);
            _registry.Register(player);
            _players[id] = player;
            if (hello)
                player.OnClientHello();
            return player;
        }

        internal Item Give(Player player, int slot, int lowId, int flags = 0, bool bag = false, int stack = 1)
        {
            Item item = _items.Make(lowId, lowId, 100, ItemSource.Loot, stack, 0, null, flags);
            item.AssignInstanceId(_items.NextInstance());
            if (bag)
                item.Identity = new Identity { Type = IdentityType.Container, Instance = item.InstanceId };
            item.IsPersisted = true;
            Container page = player.Inventory.Inventory;
            Assert.IsTrue(page.Add(slot, item), "slot " + slot + " is free");
            Db.Seed(item.ToRecord(page.Identity, slot, item.StackCount));
            return item;
        }

        internal void RentA()
        {
            Assert.AreEqual(PlayerShopResult.Done, Act(Owner, PlayerShopAction.Rent, price: Rules.RentCost));
            Store();
        }

        internal PlayerShopResult Act(Player player, PlayerShopAction action, int slot = 0, int item = 0, int price = 0, string name = "",
            PlayerShopTerminal? terminal = null)
        {
            var session = (Session)player.Session!;
            int before = session.Of<PlayerShopResultMessage>().Count();
            Shops.Handle(player, new PlayerShopActionMessage
            {
                Identity = player.Identity,
                Terminal = (terminal ?? A).Identity,
                Action = action,
                Slot = slot,
                ItemInstance = item,
                Price = price,
                Name = name
            });
            PlayerShopResultMessage[] results = session.Of<PlayerShopResultMessage>().ToArray();
            return results.Length > before ? results[^1].Result : PlayerShopResult.CannotUse;
        }

        /// <summary>Runs the lanes (all, or the given ones in that order), then the playfield tick's settle.</summary>
        internal void Store(params Player[] lanes)
        {
            foreach (Player player in lanes.Length == 0 ? _players.Values.ToArray() : lanes)
            {
                try
                {
                    _flush.HardFlush(player);
                }
                catch (Exception) when (player.IsPersistenceQuarantined)
                {
                }
            }
            Shops.Tick(_world);
        }

        internal int Cash(Player player) => player.Stats.GetOrZero(CharacterStat.Cash, StatDetail.Base);

        internal PlayerShopViewMessage LastView(Player player) => Sent<PlayerShopViewMessage>(player).Last();

        internal IEnumerable<T> Sent<T>(Player player) where T : MessageBody => ((Session)player.Session!).Of<T>();

        /// <summary>Every item, credit and shop row in storage is what memory holds.</summary>
        internal void AssertStorageMatchesMemory()
        {
            var expected = new Dictionary<int, (int Type, int Instance, int Slot, int Count)>();
            foreach (Player player in _players.Values.Where(p => !p.IsPersistenceQuarantined))
            {
                Assert.AreEqual(Cash(player), Db.Cash[player.Identity.Instance], player.Name + "'s credits");
                Container page = player.Inventory.Inventory;
                foreach ((int slot, Item item) in page.Content)
                    expected.Add(item.InstanceId, ((int)page.Identity.Type, page.Identity.Instance, slot, item.StackCount));
            }
            Assert.AreEqual(Db.Shops.Count, _players.Keys.Count(id => Shops.TryGetShopOf(id, out _)));
            foreach (int owner in _players.Keys)
            {
                if (!Shops.TryGetShopOf(owner, out PlayerShop shop))
                    continue;
                MemoryDb.ShopRow row = Db.Shops[shop.ShopId];
                Assert.AreEqual(shop.OwnerId, row.OwnerId);
                Assert.AreEqual(shop.Name, row.Name);
                Assert.AreEqual(shop.Balance, row.Balance, "shop balance");
                Assert.AreEqual(0, shop.PendingSales);
                foreach (PlayerShopListing listing in shop.Listings.Values)
                {
                    Assert.IsFalse(listing.Pending, "pending; locked=" + shop.Locked + " writes=" + shop.PendingWriteCount + " " + Log.Problems);
                    expected.Add(listing.Item.InstanceId, ((int)IdentityType.PlayerShopInventory, shop.ShopId, listing.Slot, listing.Item.StackCount));
                    Assert.AreEqual(listing.Price, Db.Prices[listing.Item.InstanceId].Price);
                    Assert.AreEqual(shop.ShopId, Db.Prices[listing.Item.InstanceId].ShopId);
                }
            }
            Assert.AreEqual(Db.Prices.Count, expected.Values.Count(v => v.Type == (int)IdentityType.PlayerShopInventory), "a price row without its item");
            foreach ((int id, var at) in expected)
            {
                Assert.IsTrue(Db.Items.TryGetValue(id, out MemoryDb.Row row), "item " + id + " is stored");
                Assert.AreEqual((at.Type, at.Instance, at.Slot, at.Count), (row.Type, row.Instance, row.Slot, row.Count), "item " + id);
            }
            Assert.AreEqual(expected.Count, Db.Items.Count, "storage holds an item memory does not");
        }

        public void Dispose()
        {
            _flush.Dispose();
            _services.Dispose();
        }
    }

    /// <summary>
    /// item_instances, player_shops, player_shop_items and Cash, with the MySQL transaction's rules: a batch applies
    /// whole or not at all, a moved row must exist, an expected source must still be where it was, a slot holds one row,
    /// and a shop write is refused exactly where <c>WritePlayerShop</c> refuses it.
    /// </summary>
    internal sealed class MemoryDb : ICharacterCoalesceCommit, IInventoryMutationPersistence, ITradePersistence
    {
        internal readonly record struct Row(int Type, int Instance, int Slot, int Count, ItemInstanceRecord Record);

        internal sealed record ShopRow(int OwnerId, int PlayfieldId, int TerminalInstance, string Name, long Balance);

        internal Dictionary<int, Row> Items = new();
        internal Dictionary<int, ShopRow> Shops = new();
        internal Dictionary<int, (int ShopId, int Price)> Prices = new();
        internal Dictionary<int, int> Cash = new();
        internal Func<InventoryMutationBatch, bool>? Refuse;
        readonly object _sync = new();

        internal void Seed(ItemInstanceRecord record)
        {
            lock (_sync)
                Items.Add(record.InstanceId, new Row(record.ContainerType, record.ContainerInstance, record.ContainerPlacement, record.StackCount, record));
        }

        public void Persist(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates, int characterId,
            IReadOnlyList<int> uploadedNanoIds, IReadOnlyList<ActiveNanoRecord>? activeNanos, IReadOnlyList<SkillLockRecord>? skillLocks)
            => Apply(items => WriteItems(items, inserts, updates));

        public void Persist(TradePersistenceBatch batch) => throw new AssertFailedException("A player shop never writes a trade batch.");

        public void Persist(InventoryMutationBatch batch)
        {
            if (Refuse?.Invoke(batch) == true)
                throw new InvalidOperationException("The database refused the batch.");
            Apply(items =>
            {
                foreach (ItemInstanceRecord source in batch.ExpectedSources)
                {
                    if (!items.TryGetValue(source.InstanceId, out Row row) || row.Type != source.ContainerType
                        || row.Instance != source.ContainerInstance || row.Slot != source.ContainerPlacement || row.Count != source.StackCount
                        || row.Record.LowId != source.LowId || row.Record.Quality != source.Quality)
                        throw new InvalidOperationException("The shared container item was moved or changed before the transfer could commit.");
                }
                WriteItems(items, batch.Inserts, batch.Locations);
                foreach (StatRecord stat in batch.FinalStats)
                {
                    if (stat.StatId == (int)CharacterStat.Cash)
                        Cash[batch.CharacterId] = stat.StatValue;
                }
                if (batch.PlayerShop is PlayerShopWrite shop)
                    WriteShop(items, batch.CharacterId, shop);
            });
        }

        void Apply(Action<Dictionary<int, Row>> write)
        {
            lock (_sync)
            {
                var items = new Dictionary<int, Row>(Items);
                var shops = new Dictionary<int, ShopRow>(Shops);
                var prices = new Dictionary<int, (int, int)>(Prices);
                var cash = new Dictionary<int, int>(Cash);
                try
                {
                    write(items);
                    if (items.Values.GroupBy(r => (r.Type, r.Instance, r.Slot)).Any(g => g.Count() > 1))
                        throw new InvalidOperationException("Duplicate entry for the unique slot key.");
                    Items = items;
                }
                catch
                {
                    Shops = shops;
                    Prices = prices;
                    Cash = cash;
                    throw;
                }
            }
        }

        static void WriteItems(Dictionary<int, Row> items, IEnumerable<ItemInstanceRecord> inserts, IEnumerable<ItemLocationUpdate> updates)
        {
            foreach (ItemLocationUpdate update in updates)
            {
                if (!items.TryGetValue(update.InstanceId, out Row row))
                    throw new InvalidOperationException("Moved item " + update.InstanceId + " has no row.");
                items[update.InstanceId] = row with
                {
                    Type = update.ContainerType, Instance = update.ContainerInstance, Slot = update.ContainerPlacement,
                    Count = update.StackCount ?? row.Count
                };
            }
            foreach (ItemInstanceRecord insert in inserts)
            {
                if (!items.TryAdd(insert.InstanceId, new Row(insert.ContainerType, insert.ContainerInstance, insert.ContainerPlacement, insert.StackCount, insert)))
                    throw new InvalidOperationException("Duplicate item " + insert.InstanceId);
            }
        }

        void WriteShop(Dictionary<int, Row> items, int characterId, PlayerShopWrite shop)
        {
            if (shop.Kind != PlayerShopWriteKind.Sale && shop.OwnerId != characterId)
                throw new ArgumentException("Only the shop's owner changes it.");
            int shopItems(int id) => items.Values.Count(r => r.Type == (int)IdentityType.PlayerShopInventory && r.Instance == id);
            ShopRow Owned()
            {
                if (!Shops.TryGetValue(shop.ShopId, out ShopRow? row) || row.OwnerId != shop.OwnerId)
                    throw new InvalidOperationException("The shop is not rented by its owner any more.");
                return row;
            }
            switch (shop.Kind)
            {
                case PlayerShopWriteKind.Rent:
                    if (Shops.ContainsKey(shop.ShopId) || Shops.Values.Any(s => s.OwnerId == shop.OwnerId
                        || (s.PlayfieldId == shop.PlayfieldId && s.TerminalInstance == shop.TerminalInstance)))
                        throw new InvalidOperationException("Duplicate entry for player_shops.");
                    Shops[shop.ShopId] = new ShopRow(shop.OwnerId, shop.PlayfieldId, shop.TerminalInstance, shop.Name, 0);
                    return;
                case PlayerShopWriteKind.Rename:
                    Shops[shop.ShopId] = Owned() with { Name = shop.Name };
                    return;
                case PlayerShopWriteKind.Abandon:
                    if (Owned().Balance != 0 || shopItems(shop.ShopId) != 0 || Prices.Values.Any(p => p.ShopId == shop.ShopId))
                        throw new InvalidOperationException("The shop is not empty.");
                    Shops.Remove(shop.ShopId);
                    return;
                case PlayerShopWriteKind.Stock:
                    Owned();
                    if (!items.TryGetValue(shop.ItemInstanceId, out Row stocked) || stocked.Type != (int)IdentityType.PlayerShopInventory
                        || stocked.Instance != shop.ShopId || !Prices.TryAdd(shop.ItemInstanceId, (shop.ShopId, shop.Price))
                        || shopItems(shop.ShopId) > shop.MaxItems || shop.Price <= 0)
                        throw new InvalidOperationException("The stocked item is not in the shop, or it is full.");
                    return;
                case PlayerShopWriteKind.Price:
                    Owned();
                    if (!Prices.TryGetValue(shop.ItemInstanceId, out var priced) || priced.ShopId != shop.ShopId || priced.Price != shop.ExpectedPrice)
                        throw new InvalidOperationException("The item's price changed.");
                    Prices[shop.ItemInstanceId] = (shop.ShopId, shop.Price);
                    return;
                case PlayerShopWriteKind.Unstock:
                    Owned();
                    if (!Prices.TryGetValue(shop.ItemInstanceId, out var held) || held.ShopId != shop.ShopId)
                        throw new InvalidOperationException("The item was not for sale.");
                    Prices.Remove(shop.ItemInstanceId);
                    return;
                case PlayerShopWriteKind.Sale:
                    if (!Shops.TryGetValue(shop.ShopId, out ShopRow? sold) || sold.OwnerId == characterId || sold.OwnerId != shop.OwnerId
                        || sold.Balance + shop.ExpectedPrice > shop.MaxBalance
                        || !Prices.TryGetValue(shop.ItemInstanceId, out var asked) || asked.ShopId != shop.ShopId || asked.Price != shop.ExpectedPrice)
                        throw new InvalidOperationException("The item was not for sale at that price.");
                    Prices.Remove(shop.ItemInstanceId);
                    Shops[shop.ShopId] = sold with { Balance = sold.Balance + shop.ExpectedPrice };
                    return;
                case PlayerShopWriteKind.Withdraw:
                    ShopRow source = Owned();
                    if (shop.Amount <= 0 || source.Balance < shop.Amount)
                        throw new InvalidOperationException("The shop does not hold that many credits.");
                    Shops[shop.ShopId] = source with { Balance = source.Balance - shop.Amount };
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(shop));
            }
        }
    }

    /// <summary>The repository the engine starts from, read from the memory database.</summary>
    sealed class SnapshotRepository(MemoryDb db) : IPlayerShopRepository
    {
        public PlayerShopSnapshot LoadAll()
        {
            PlayerShopRecord[] shops = db.Shops.Select(pair => new PlayerShopRecord(pair.Key, pair.Value.PlayfieldId, pair.Value.TerminalInstance,
                pair.Value.OwnerId, "Owner", pair.Value.Name, pair.Value.Balance)).ToArray();
            ItemInstanceRecord[] items = db.Items.Values.Where(r => r.Type == (int)IdentityType.PlayerShopInventory)
                .Select(r => new ItemInstanceRecord
                {
                    InstanceId = r.Record.InstanceId, ContainerType = r.Type, ContainerInstance = r.Instance, ContainerPlacement = r.Slot,
                    ItemType = r.Record.ItemType, LowId = r.Record.LowId, HighId = r.Record.HighId, Quality = r.Record.Quality,
                    StackCount = r.Count, Source = r.Record.Source
                }).ToArray();
            return new PlayerShopSnapshot(shops, items, db.Prices.ToDictionary(p => p.Key, p => p.Value.Price),
                db.Prices.ToDictionary(p => p.Key, p => p.Value.ShopId));
        }
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
        readonly List<MessageBody> _sent = [];
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public bool TransferToPlayfield(ZoneEngine_New.Core.Playfield.Playfield destination, Vector3 landing) => false;
        public void Send(byte[] packet) { }
        public void Send(MessageBody body) { lock (_sent) _sent.Add(body); }
        public void Send(MessageBody body, int sender, int receiver) => Send(body);
        public void SendInitiateCompression() { }
        public void Close() => State = SessionState.Closed;
        internal IEnumerable<T> Of<T>() where T : MessageBody { lock (_sent) return _sent.OfType<T>().ToArray(); }
    }

    sealed class Items : IItemBuilder
    {
        int _next = 700000;

        internal int NextInstance() => Interlocked.Increment(ref _next);

        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null) => Make(lowId, highId, quality, source, stackCount, instanceId, identity, 0);

        internal Item Make(int lowId, int highId, int quality, ItemSource source, int stackCount, int instanceId, Identity? identity,
            int flags) => new()
            {
                LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount,
                InstanceId = instanceId, Identity = identity ?? Identity.None,
                Definition = new ItemTemplate { Id = lowId, Quality = quality, Flags = flags }
            };

        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
        {
            Item item = Make(lowId, highId, quality, source, stackCount, 0, null, 0);
            item.AssignInstanceId(NextInstance());
            return item;
        }

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item)
        {
            item = Make(row.LowId, row.HighId, row.Quality, row.Source, row.StackCount, row.InstanceId,
                row.ItemType != 0 ? new Identity { Type = (IdentityType)row.ItemType, Instance = row.InstanceId } : Identity.None, 0);
            item.IsPersisted = true;
            return true;
        }
    }

    sealed class Templates : IItemTemplateCatalog
    {
        public bool TryGet(int aoid, out ItemTemplate template)
        {
            template = null!;
            return false;
        }

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

    static void Set(object target, string name, object value) => Field(target.GetType(), name).SetValue(target, value);

    static FieldInfo Field(Type type, string name)
    {
        for (Type? at = type; at != null; at = at.BaseType)
            if (at.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) is FieldInfo field)
                return field;
        throw new MissingFieldException(type.Name, name);
    }
}
