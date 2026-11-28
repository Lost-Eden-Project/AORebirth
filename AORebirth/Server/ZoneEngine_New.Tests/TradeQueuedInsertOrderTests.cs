namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;

using AORebirth.Enums;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;
using LostEden.Protocol.Messages.N3Messages;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Trade;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// A trade's queued commit (Knubot hand-in, shop sale) takes the player's dirty inventory when it runs in the lane. A
/// quest reward the same tick pays is published before its insert runs: it counts as persisted and its move into a
/// slot is dirty, while its insert is queued behind the trade. Live v0.13.6 wrote that move as an update of a row that
/// did not exist yet ("UpdateLocations found no row"), quarantined the character and closed the session on every
/// quest trade-in (Antonio Stacklund MWTD, Elsa Oosta IIG3, Drake Rodriguez EQ8Z). The reward here is queued exactly as
/// <c>QuestService.CompleteTemplate</c> queues it: grant plan, commit enqueued, published before the commit, marked
/// stored by the commit. The store refuses what MySQL refuses: an update of a missing row, a second insert of one.
/// </summary>
[TestClass]
public sealed class TradeQueuedInsertOrderTests
{
    const int CharacterId = 1105;
    const int PartnerId = 1106;
    const int ChestId = 777000;
    const int NpcId = 1035686;

    [TestMethod]
    public void KnubotHandInAheadOfAQueuedQuestRewardStoresBoth()
    {
        using var rig = new Rig();
        Item belt = rig.Carried(51222001, persisted: true);
        TradeSession knubot = rig.OpenKnubot();
        rig.OfferToKnubot(knubot, belt);

        Assert.IsTrue(rig.Trades.TryCommitKnubot(rig.Player, rig.Npc, 0));
        Item reward = rig.QueueQuestReward();

        rig.Flush.HardFlush(rig.Player);

        rig.AssertHealthy();
        rig.Store.AssertRow(belt, (int)rig.Npc.Identity.Type, NpcId, belt.InstanceId);
        rig.AssertStoredWhereHeld(reward);
    }

    [TestMethod]
    public void HandingInARewardWhoseInsertIsStillQueuedInsertsThenRetiresIt()
    {
        using var rig = new Rig();
        Item first = rig.QueueQuestReward();
        TradeSession knubot = rig.OpenKnubot();
        rig.OfferToKnubot(knubot, first);

        Assert.IsTrue(rig.Trades.TryCommitKnubot(rig.Player, rig.Npc, 0));
        Item next = rig.QueueQuestReward();

        rig.Flush.HardFlush(rig.Player);

        rig.AssertHealthy();
        rig.Store.AssertRow(first, (int)rig.Npc.Identity.Type, NpcId, first.InstanceId);
        rig.AssertStoredWhereHeld(next);
    }

    [TestMethod]
    public void HandingInAFreshlyLootedItemNeverWritesItsRow()
    {
        using var rig = new Rig();
        Item looted = rig.Carried(51222002, persisted: false);
        TradeSession knubot = rig.OpenKnubot();
        rig.OfferToKnubot(knubot, looted);

        Assert.IsTrue(rig.Trades.TryCommitKnubot(rig.Player, rig.Npc, 0));
        Item reward = rig.QueueQuestReward();

        rig.Flush.HardFlush(rig.Player);

        rig.AssertHealthy();
        Assert.IsFalse(rig.Store.Has(looted.InstanceId), "A handed-in item that was never stored gets no row.");
        rig.AssertStoredWhereHeld(reward);
    }

    [TestMethod]
    public void SellingAFreshlyLootedItemAheadOfAQueuedRewardStoresTheReward()
    {
        using var rig = new Rig();
        Item looted = rig.Carried(51222003, persisted: false);
        TradeSession shop = rig.OpenShop();
        rig.OfferToShop(shop, looted);

        rig.CommitShop(shop);
        Assert.AreEqual(0, shop.InitiatorOffer.Count, "the sale went through");
        Item reward = rig.QueueQuestReward();

        rig.Flush.HardFlush(rig.Player);

        rig.AssertHealthy();
        Assert.IsFalse(rig.Store.Has(looted.InstanceId), "A sold item that was never stored gets no row.");
        rig.AssertStoredWhereHeld(reward);
    }

    [TestMethod]
    public void SellingARewardWhoseInsertIsStillQueuedInsertsThenRetiresIt()
    {
        using var rig = new Rig();
        Item reward = rig.QueueQuestReward();
        TradeSession shop = rig.OpenShop();
        rig.OfferToShop(shop, reward);

        rig.CommitShop(shop);
        Assert.AreEqual(0, shop.InitiatorOffer.Count, "the sale went through");

        rig.Flush.HardFlush(rig.Player);

        rig.AssertHealthy();
        rig.Store.AssertRow(reward, (int)shop.Machine!.Identity.Type, shop.Machine.Identity.Instance, reward.InstanceId);
    }

    [TestMethod]
    public void PlayerTradeAheadOfAQueuedQuestRewardStoresBoth()
    {
        using var rig = new Rig();
        Item given = rig.Carried(rig.Player, 51222004, persisted: true);
        Item returned = rig.Carried(rig.Partner, 51222005, persisted: false);

        rig.TradeWithPartner(given, returned);
        // Both lanes hold the joint commit; each side's reward is queued behind it.
        Item reward = rig.QueueQuestReward(rig.Player);
        Item partnerReward = rig.QueueQuestReward(rig.Partner);

        rig.Flush.HardFlush(rig.Player);
        rig.Flush.HardFlush(rig.Partner);

        rig.AssertHealthy();
        rig.AssertStoredWhereHeld(rig.Partner, given);
        rig.AssertStoredWhereHeld(rig.Player, returned);
        rig.AssertStoredWhereHeld(rig.Player, reward);
        rig.AssertStoredWhereHeld(rig.Partner, partnerReward);
    }

    [TestMethod]
    public void TradingARewardWhoseInsertIsStillQueuedStoresItWithTheReceiver()
    {
        using var rig = new Rig();
        Item reward = rig.QueueQuestReward(rig.Player);

        rig.TradeWithPartner(reward, null);
        Item next = rig.QueueQuestReward(rig.Partner);

        rig.Flush.HardFlush(rig.Partner);
        rig.Flush.HardFlush(rig.Player);

        rig.AssertHealthy();
        rig.AssertStoredWhereHeld(rig.Partner, reward);
        rig.AssertStoredWhereHeld(rig.Partner, next);
    }

    [TestMethod]
    public void InventoryActionAheadOfAQueuedQuestRewardStoresBoth()
    {
        using var rig = new Rig();
        Item looted = rig.Carried(rig.Player, 51222006, persisted: false);
        Item kept = rig.Carried(rig.Player, 51222007, persisted: true);

        Assert.IsTrue(rig.ActionMove(rig.Player, kept), "the move applied");
        Assert.IsTrue(rig.Delete(rig.Player, looted), "the delete applied");
        Item reward = rig.QueueQuestReward(rig.Player);

        rig.Flush.HardFlush(rig.Player);

        rig.AssertHealthy();
        Assert.IsFalse(rig.Store.Has(looted.InstanceId), "A deleted item that was never stored gets no row.");
        rig.AssertStoredWhereHeld(rig.Player, kept);
        rig.AssertStoredWhereHeld(rig.Player, reward);
    }

    [TestMethod]
    public void DeletingARewardWhoseInsertIsStillQueuedInsertsThenRetiresIt()
    {
        using var rig = new Rig();
        Item reward = rig.QueueQuestReward(rig.Player);
        Assert.IsTrue(rig.Delete(rig.Player, reward), "the delete applied");
        Item next = rig.QueueQuestReward(rig.Player);

        rig.Flush.HardFlush(rig.Player);

        rig.AssertHealthy();
        rig.Store.AssertRow(reward, (int)IdentityType.None, CharacterId, reward.InstanceId);
        rig.AssertStoredWhereHeld(rig.Player, next);
    }

    // The mirror case. A commit planned while an item was never stored drops it (a hand-in, sale or delete). If a lane
    // action queued ahead of that commit took the item's pending insert, the row was written. The commit then wrote
    // nothing for it, so the row stayed on the player's page: the item came back on the next login after it was handed
    // in, sold (credits paid) or traded away (a duplicate insert).

    [TestMethod]
    public void HandingInAFreshlyLootedItemBehindAnotherQueuedActionLeavesNoRow()
    {
        using var rig = new Rig();
        Item kept = rig.Carried(rig.Player, 51222008, persisted: true);
        Item looted = rig.Carried(rig.Player, 51222009, persisted: false);
        Assert.IsTrue(rig.ActionMove(rig.Player, kept), "the move applied");
        TradeSession knubot = rig.OpenKnubot();
        rig.OfferToKnubot(knubot, looted);
        Assert.IsTrue(rig.Trades.TryCommitKnubot(rig.Player, rig.Npc, 0));

        rig.Flush.HardFlush(rig.Player);

        rig.AssertHealthy();
        Assert.IsFalse(rig.Store.Has(looted.InstanceId), "A handed-in item that was never stored gets no row.");
        rig.AssertStorageMatchesMemory();
    }

    [TestMethod]
    public void MovingAFreshlyLootedItemTwiceBehindAnotherQueuedActionStoresItWhereItIs()
    {
        using var rig = new Rig();
        Item kept = rig.Carried(rig.Player, 51222016, persisted: true);
        Item looted = rig.Carried(rig.Player, 51222017, persisted: false);
        rig.DirtyMove(rig.Player, looted);
        Assert.IsTrue(rig.ActionMove(rig.Player, kept), "the first move applied");
        Assert.IsTrue(rig.ActionMove(rig.Player, looted), "the second move applied");

        rig.Flush.HardFlush(rig.Player);

        rig.AssertHealthy();
        rig.AssertStorageMatchesMemory();
    }

    /// <summary>
    /// A sale queued ahead of an inventory action used to take the dirty inventory and store the item's newest move.
    /// The action then wrote the older place it had planned on top of it, so storage held the item where it no longer was.
    /// </summary>
    [TestMethod]
    public void AMoveMadeAfterAQueuedActionIsNotOverwrittenByThatActionsOlderPlace()
    {
        using var rig = new Rig();
        Item sold = rig.Carried(rig.Player, 51222018, persisted: true);
        Item moved = rig.Carried(rig.Player, 51222019, persisted: true);
        TradeSession shop = rig.OpenShop();
        rig.OfferToShop(shop, sold);
        rig.CommitShop(shop);
        Assert.IsTrue(rig.ActionMove(rig.Player, moved), "the move applied");
        rig.DirtyMove(rig.Player, moved);

        rig.Flush.HardFlush(rig.Player);

        rig.AssertHealthy();
        rig.AssertStoredWhereHeld(rig.Player, moved);
        rig.AssertStorageMatchesMemory();
    }

    /// <summary>
    /// Taking an item out of a mission chest queues its row's move to the player off the tick. The same ordering holds
    /// there: an action queued ahead of that take must not store a later move of the item first, or the take's older
    /// place overwrites it and a relog finds the item where the player no longer has it.
    /// </summary>
    [TestMethod]
    public void AChestTakeQueuedBehindAnotherActionIsNotOverwrittenByItsOlderPlace()
    {
        using var rig = new Rig();
        Item kept = rig.Carried(rig.Player, 51222020, persisted: true);
        Assert.IsTrue(rig.ActionMove(rig.Player, kept), "the move applied");
        Item taken = rig.TakeFromChest(rig.Player, 51222021);
        rig.DirtyMove(rig.Player, taken);

        rig.Flush.HardFlush(rig.Player);

        rig.AssertHealthy();
        rig.AssertStoredWhereHeld(rig.Player, taken);
        rig.AssertStorageMatchesMemory();
    }

    [TestMethod]
    public void SellingAFreshlyLootedItemBehindAnotherQueuedActionLeavesNoRow()
    {
        using var rig = new Rig();
        Item kept = rig.Carried(rig.Player, 51222010, persisted: true);
        Item looted = rig.Carried(rig.Player, 51222011, persisted: false);
        Assert.IsTrue(rig.ActionMove(rig.Player, kept), "the move applied");
        TradeSession shop = rig.OpenShop();
        rig.OfferToShop(shop, looted);
        rig.CommitShop(shop);
        Assert.AreEqual(0, shop.InitiatorOffer.Count, "the sale went through");

        rig.Flush.HardFlush(rig.Player);

        rig.AssertHealthy();
        Assert.IsFalse(rig.Store.Has(looted.InstanceId), "A sold item that was never stored gets no row.");
        rig.AssertStorageMatchesMemory();
    }

    [TestMethod]
    public void DeletingAFreshlyLootedItemBehindAnotherQueuedActionLeavesNoRow()
    {
        using var rig = new Rig();
        Item kept = rig.Carried(rig.Player, 51222012, persisted: true);
        Item looted = rig.Carried(rig.Player, 51222013, persisted: false);
        Assert.IsTrue(rig.ActionMove(rig.Player, kept), "the move applied");
        Assert.IsTrue(rig.Delete(rig.Player, looted), "the delete applied");

        rig.Flush.HardFlush(rig.Player);

        rig.AssertHealthy();
        Assert.IsFalse(rig.Store.Has(looted.InstanceId), "A deleted item that was never stored gets no row.");
        rig.AssertStorageMatchesMemory();
    }

    [TestMethod]
    public void TradingAFreshlyLootedItemBehindAnotherQueuedActionInsertsItOnceForTheReceiver()
    {
        using var rig = new Rig();
        Item kept = rig.Carried(rig.Player, 51222014, persisted: true);
        Item looted = rig.Carried(rig.Player, 51222015, persisted: false);
        Assert.IsTrue(rig.ActionMove(rig.Player, kept), "the move applied");
        rig.TradeWithPartner(looted, null);

        rig.Flush.HardFlush(rig.Player);
        rig.Flush.HardFlush(rig.Partner);

        rig.AssertHealthy();
        rig.AssertStoredWhereHeld(rig.Partner, looted);
        rig.AssertStorageMatchesMemory();
    }

    /// <summary>
    /// Team request 8 (bug #648, Shuttleport hand-ins): an item dragged into the Give Item window from an open bag was
    /// refused with a return the client answers by moving its copy into its first free main-inventory slot, while the
    /// server kept the item in the bag; from then on every action naming either slot reached the wrong item until the
    /// player zoned. The client allows the drag, so the server takes the item from the bag, as it does from the inventory.
    /// </summary>
    [TestMethod]
    public void KnubotAddFromAnOpenBagTakesTheItemFromTheBag()
    {
        using var rig = new Rig();
        Item bagged = rig.InBag(51222010);
        TradeSession knubot = rig.OpenKnubot();
        rig.TakeContainerAdds();

        rig.OfferToKnubotFromBag(knubot, bagged);

        Assert.AreSame(bagged, knubot.InitiatorOffer.Items.Values.Single(), "the item is on the table");
        Assert.AreEqual(0, rig.Bag.Content.Count, "the bag slot is empty, as it is on the client");
        Assert.AreEqual(0, rig.TakeContainerAdds().Length, "the client's own move stands; nothing is sent back");

        Assert.IsTrue(rig.Trades.TryCommitKnubot(rig.Player, rig.Npc, 0));
        rig.Flush.HardFlush(rig.Player);

        rig.AssertHealthy();
        rig.Store.AssertRow(bagged, (int)rig.Npc.Identity.Type, NpcId, bagged.InstanceId);
    }

    /// <summary>The client takes an item back out of the window into its main inventory, wherever it came from; so does the server.</summary>
    [TestMethod]
    public void KnubotItemFromABagTakenBackOutOfTheWindowLandsInTheMainInventory()
    {
        using var rig = new Rig();
        Item bagged = rig.InBag(51222011);
        TradeSession knubot = rig.OpenKnubot();
        rig.OfferToKnubotFromBag(knubot, bagged);

        rig.RemoveFromKnubot(knubot, knubot.InitiatorOffer.Items.Single().Key);

        Assert.AreEqual(0, knubot.InitiatorOffer.Count);
        Assert.AreEqual(rig.Player.Inventory.Inventory.FindFreeSlot() - 1, rig.SlotOf(bagged), "the first free main-inventory slot");
        Assert.AreEqual(0, rig.Bag.Content.Count);
        rig.Flush.HardFlush(rig.Player);

        rig.AssertHealthy();
        rig.AssertStoredWhereHeld(bagged);
        rig.AssertStorageMatchesMemory();
    }

    /// <summary>
    /// The client moves a locked item into its window as well. The server keeps a locked item where it is, so the
    /// client's copy is walked back: the return (which the client always answers by taking the item into its first free
    /// main-inventory slot) and then the ordinary move acknowledgement from that slot to the server's slot.
    /// </summary>
    [TestMethod]
    public void KnubotAddOfALockedItemWalksTheClientCopyBackToTheServerSlot()
    {
        using var rig = new Rig();
        Item gap = rig.Carried(51222012, persisted: true);
        Item locked = rig.Carried(51222013, persisted: true);
        int gapSlot = rig.SlotOf(gap);
        int lockedSlot = rig.SlotOf(locked);
        Assert.IsTrue(rig.Delete(rig.Player, gap), "the earlier slot is free again");
        locked.Locked = true;
        TradeSession knubot = rig.OpenKnubot();
        rig.TakeContainerAdds();

        rig.OfferToKnubot(knubot, locked);

        Assert.AreEqual(0, knubot.InitiatorOffer.Count, "a locked item is not taken");
        Assert.AreSame(locked, rig.Player.Inventory.Inventory.Content[lockedSlot], "the server did not move it");
        ContainerAddItem[] told = rig.TakeContainerAdds();
        Assert.AreEqual(2, told.Length, "the return, then the move to the server's slot");
        Assert.AreEqual(IdentityType.KnuBotTradeWindow, told[0].Source.Type);
        Assert.AreEqual(0, told[0].Source.Instance, "the window index the client put it in");
        Assert.AreEqual(0x6F, told[0].Slot);
        Assert.IsTrue(told[1].Source == new Identity { Type = IdentityType.Inventory, Instance = gapSlot }, "from the client's first free slot");
        Assert.IsTrue(told[1].Target == rig.Player.Identity);
        Assert.AreEqual(lockedSlot, told[1].Slot, "to where the server holds it");

        locked.Locked = false;
        rig.Flush.HardFlush(rig.Player);
        rig.AssertHealthy();
        rig.AssertStorageMatchesMemory();
    }

    /// <summary>With no free slot before the item's own, the client's return already lands it where the server holds it.</summary>
    [TestMethod]
    public void KnubotAddOfALockedItemWithNoEarlierGapNeedsOnlyTheReturn()
    {
        using var rig = new Rig();
        Item locked = rig.Carried(51222016, persisted: true);
        locked.Locked = true;
        TradeSession knubot = rig.OpenKnubot();
        rig.TakeContainerAdds();

        rig.OfferToKnubot(knubot, locked);

        ContainerAddItem[] told = rig.TakeContainerAdds();
        Assert.AreEqual(1, told.Length);
        Assert.AreEqual(IdentityType.KnuBotTradeWindow, told[0].Source.Type);
        Assert.AreEqual(0x6F, told[0].Slot);
        Assert.AreSame(locked, rig.Player.Inventory.Inventory.Content[rig.SlotOf(locked)]);
        locked.Locked = false;
        rig.AssertHealthy();
    }

    /// <summary>
    /// A locked bag item stays in the bag at its slot: the client's copy is returned (first free main-inventory slot),
    /// deleted from that slot, and the open bag's contents are sent again, so a gap below the item's slot cannot put
    /// the copy in the wrong bag slot.
    /// </summary>
    [TestMethod]
    public void KnubotAddOfALockedBagItemReturnsTheCopyAndShowsTheBagAsItIs()
    {
        using var rig = new Rig();
        Item gone = rig.InBag(51222019);
        Item bagged = rig.InBag(51222017);
        int bagSlot = rig.Bag.Content.Single(pair => ReferenceEquals(pair.Value, bagged)).Key;
        Assert.IsTrue(rig.Bag.Remove(rig.Bag.Content.Single(pair => ReferenceEquals(pair.Value, gone)).Key) != null, "a gap below the item");
        bagged.Locked = true;
        TradeSession knubot = rig.OpenKnubot();
        rig.TakeSent();

        rig.OfferToKnubotFromBag(knubot, bagged);

        Assert.AreEqual(0, knubot.InitiatorOffer.Count);
        Assert.AreSame(bagged, rig.Bag.Content[bagSlot], "the server did not move it");
        MessageBody[] told = rig.TakeSent();
        Assert.AreEqual(3, told.Length, "the return, the delete of the landed copy, the bag's contents");
        var back = (ContainerAddItem)told[0];
        Assert.AreEqual(IdentityType.KnuBotTradeWindow, back.Source.Type);
        Assert.AreEqual(0x6F, back.Slot);
        var delete = (CharacterActionMessage)told[1];
        Assert.AreEqual(CharacterActionType.DeleteItem, delete.Action);
        Assert.IsTrue(delete.Target == new Identity { Type = IdentityType.Inventory, Instance = rig.Player.Inventory.Inventory.FindFreeSlot() },
            "the client's first free main-inventory slot, where its copy landed");
        var contents = (InventoryUpdateMessage)told[2];
        Assert.IsTrue(contents.InventoryIdentity == rig.Bag.Identity);
        Assert.AreEqual(Rig.BagHandle, contents.Handle);
        Assert.AreEqual(1, contents.Items.Length);
        Assert.AreEqual(bagSlot, contents.Items[0].Placement, "the item at its real bag slot");
        Assert.AreEqual(bagged.LowId, contents.Items[0].ItemLowId);
        bagged.Locked = false;
        rig.AssertHealthy();
    }

    /// <summary>An add naming an empty server slot: the client's copy is sent where the client will put it; nothing on the server moves.</summary>
    [TestMethod]
    public void KnubotAddNamingAnEmptySlotOnlyReturnsTheClientCopy()
    {
        using var rig = new Rig();
        Item kept = rig.Carried(51222018, persisted: true);
        TradeSession knubot = rig.OpenKnubot();
        rig.TakeContainerAdds();

        rig.OfferToKnubotAt(knubot, new Identity { Type = IdentityType.Inventory, Instance = rig.SlotOf(kept) + 5 });

        Assert.AreEqual(0, knubot.InitiatorOffer.Count);
        ContainerAddItem[] told = rig.TakeContainerAdds();
        Assert.AreEqual(1, told.Length);
        Assert.AreEqual(IdentityType.KnuBotTradeWindow, told[0].Source.Type);
        Assert.AreEqual(0x6F, told[0].Slot);
        Assert.AreSame(kept, rig.Player.Inventory.Inventory.Content[rig.SlotOf(kept)]);
        rig.AssertHealthy();
    }

    /// <summary>
    /// Every queued commit in random order against everything else that queues, moves or dirties an item, with the lanes
    /// driven at random points. This catches an ordering hole that no case above names. Whatever the interleaving, no
    /// write may reach a row ahead of its insert, nothing is quarantined, and storage ends exactly where memory holds each
    /// item. A failure names the seed and the steps that led to it.
    /// </summary>
    /// <summary>
    /// LE-02: a trade's joint commit sits in both lanes. When the giver's lane fails terminally ahead of it (here the
    /// database refuses an earlier retire, as it refuses retiring a bag with rows) and is discarded, the joint never
    /// runs. The partner's memory shows a trade storage will never hold, so the partner is quarantined at once and its
    /// lane emptied, instead of playing on unsaved until a forced save failed hours later.
    /// </summary>
    [TestMethod]
    public void ADiscardedLaneAbandonsItsTradeAndQuarantinesThePartnerAtOnce()
    {
        using var rig = new Rig();
        Item doomed = rig.Carried(51222101, persisted: true);
        Item given = rig.Carried(51222102, persisted: true);
        Item returned = rig.Carried(rig.Partner, 51222103, persisted: true);
        int givenSlot = rig.SlotOf(given);
        int returnedSlot = rig.Partner.Inventory.Inventory.Content.Single(pair => ReferenceEquals(pair.Value, returned)).Key;
        rig.Store.Refuse = batch => batch.Locations.Any(location => location.InstanceId == doomed.InstanceId);

        Assert.IsTrue(rig.Delete(rig.Player, doomed), "the delete is applied in memory and queued");
        rig.TradeWithPartner(given, returned);
        Assert.IsTrue(rig.Partner.Lane.HasQueuedWork, "the joint sits in the partner's lane");

        Assert.ThrowsExactly<InvalidOperationException>(() => rig.Flush.HardFlush(rig.Player));

        Assert.IsTrue(rig.Player.IsPersistenceQuarantined);
        Assert.IsTrue(rig.Partner.IsPersistenceQuarantined, "the partner's memory shows a trade that will never be stored");
        Assert.AreEqual(SessionState.Closed, rig.PartnerSession.State, "the partner reloads from storage");
        Assert.IsFalse(rig.Player.Lane.HasQueuedWork);
        Assert.IsFalse(rig.Partner.Lane.HasQueuedWork, "the abandoned joint is gone from the partner's lane");
        // Storage never saw the trade or the delete: a relog loads every item where it was.
        rig.Store.AssertRow(doomed, (int)IdentityType.Inventory, CharacterId, rig.Store.Rows().Single(row => row.Key == doomed.InstanceId).Value.Slot);
        rig.Store.AssertRow(given, (int)IdentityType.Inventory, CharacterId, givenSlot);
        rig.Store.AssertRow(returned, (int)IdentityType.Inventory, PartnerId, returnedSlot);
        // A later hard flush of the partner writes nothing from its stale memory.
        rig.Flush.HardFlush(rig.Partner);
        rig.Store.AssertRow(returned, (int)IdentityType.Inventory, PartnerId, returnedSlot);
    }

    /// <summary>
    /// LE-03: taking a worn item off into a bag is delayed by its equip delay. While that move is pending the bag is
    /// locked, so it cannot be put on the trade table; the move then completes into the bag, which is still the player's.
    /// </summary>
    [TestMethod]
    public void ABagReceivingAPendingUnequipCannotBeOfferedUntilTheMoveCompletes()
    {
        using var rig = new Rig();
        InventoryMoveService moves = rig.Moves();
        Item worn = rig.Worn(51222111);
        Item bag = rig.BagItem;
        TradeSession trade = rig.OpenPlayerTrade();

        rig.UnequipIntoBag(moves, worn);
        Assert.IsTrue(moves.HasPending(CharacterId), "the unequip waits for its equip delay");
        Assert.IsTrue(worn.Locked);
        Assert.IsTrue(bag.Locked, "the destination bag is locked while the move is pending");
        Assert.IsFalse(rig.TryOffer(rig.Player, trade, bag), "a locked bag cannot be put on the table");

        moves.Tick(rig.Player.Playfield!, 60);
        Assert.IsFalse(moves.HasPending(CharacterId));
        Assert.IsFalse(bag.Locked, "the bag is free again once the move completed");
        Assert.IsTrue(rig.Bag.Content.Values.Any(item => ReferenceEquals(item, worn)), "the item landed in the bag");
        Assert.IsFalse(rig.Player.Inventory.Armor.Content.Values.Any(item => ReferenceEquals(item, worn)));
        Assert.IsTrue(rig.TryOffer(rig.Player, trade, bag), "the bag can be offered afterwards");
        rig.Flush.HardFlush(rig.Player);
        rig.Store.AssertRow(worn, (int)IdentityType.Container, rig.Bag.Identity.Instance,
            rig.Bag.Content.Single(pair => ReferenceEquals(pair.Value, worn)).Key);
        rig.AssertNoFailure();
    }

    /// <summary>
    /// LE-03 the other way round: a bag already on the table (locked by the offer) takes no unequip, so the trade that
    /// follows carries the bag as it was offered and the worn item stays worn.
    /// </summary>
    [TestMethod]
    public void AnOfferedBagTakesNoPendingUnequipAndTradesAsOffered()
    {
        using var rig = new Rig();
        InventoryMoveService moves = rig.Moves();
        Item worn = rig.Worn(51222121);
        Item bag = rig.BagItem;
        int wornSlot = rig.Player.Inventory.Armor.Offset;
        TradeSession trade = rig.OpenPlayerTrade();
        Assert.IsTrue(rig.TryOffer(rig.Player, trade, bag), "the free bag goes on the table");

        rig.UnequipIntoBag(moves, worn);
        Assert.IsFalse(moves.HasPending(CharacterId), "a bag on the table takes nothing");
        Assert.AreSame(worn, rig.Player.Inventory.Armor.Content[wornSlot], "the item stays worn");
        Assert.IsFalse(worn.Locked);

        rig.CommitPlayerTrade(trade);

        Assert.AreSame(rig.Partner, rig.Holder(bag), "the bag changed hands as offered");
        Assert.IsFalse(rig.Player.Inventory.TryGetBackpackPage(bag.Identity, out _), "the giver forgot the bag's page");
        moves.Tick(rig.Player.Playfield!, 60);
        Assert.AreSame(worn, rig.Player.Inventory.Armor.Content[wornSlot]);
        rig.Flush.HardFlush(rig.Player);
        rig.Flush.HardFlush(rig.Partner);
        rig.Store.AssertRow(worn, (int)IdentityType.ArmorPage, CharacterId, wornSlot);
        rig.AssertStorageMatchesMemory();
    }

    /// <summary>
    /// LE-03: an item inside an offered bag that becomes locked after the offer (an equip from inside the bag is
    /// pending) refuses the trade at commit, not only at the offer; the row is never written under another player's bag.
    /// </summary>
    [TestMethod]
    public void ATradeIsRefusedAtCommitWhenAnItemInsideTheOfferedBagBecameLocked()
    {
        using var rig = new Rig();
        Item inside = rig.InBag(51222141);
        Item bag = rig.BagItem;
        TradeSession trade = rig.OpenPlayerTrade();
        Assert.IsTrue(rig.TryOffer(rig.Player, trade, bag), "the bag goes on the table");

        inside.Locked = true; // a pending equip from inside the bag, as InventoryMoveService locks it
        rig.CommitPlayerTrade(trade);

        Assert.IsTrue(trade.InitiatorOffer.Items.Values.Any(item => ReferenceEquals(item, bag)), "the trade did not go through: the bag is still on the table");
        Assert.AreNotSame(rig.Partner, rig.Holder(bag), "the partner never received the bag");
        Assert.IsTrue(rig.Player.Inventory.TryGetBackpackPage(bag.Identity, out Container page) && ReferenceEquals(page, rig.Bag),
            "the owner keeps the bag's page");
        Assert.IsTrue(rig.Bag.Content.Values.Any(item => ReferenceEquals(item, inside)));
        Assert.IsFalse(rig.Partner.Lane.HasQueuedWork, "no joint commit was queued");
        rig.Store.AssertRow(inside, (int)IdentityType.Container, rig.Bag.Identity.Instance,
            rig.Bag.Content.Single(pair => ReferenceEquals(pair.Value, inside)).Key);
        rig.Store.AssertRow(bag, (int)IdentityType.Inventory, CharacterId, rig.Store.Rows().Single(row => row.Key == bag.InstanceId).Value.Slot);
    }

    /// <summary>
    /// LE-03, the last line of defence: a pending move whose destination page is no longer the player's (the bag changed
    /// hands by any path) is dropped at completion. The item stays where it was and no row is written under a page
    /// another player now holds.
    /// </summary>
    [TestMethod]
    public void APendingMoveIntoABagThatLeftThePlayerIsDroppedAtCompletion()
    {
        using var rig = new Rig();
        InventoryMoveService moves = rig.Moves();
        Item worn = rig.Worn(51222131);
        Item bag = rig.BagItem;
        Container bagPage = rig.Bag;
        int wornSlot = rig.Player.Inventory.Armor.Offset;

        rig.UnequipIntoBag(moves, worn);
        Assert.IsTrue(moves.HasPending(CharacterId));
        // The bag changes hands: the giver's page is forgotten, as a committed trade forgets it.
        rig.Player.Inventory.ForgetBackpackPage(bag.Identity);

        moves.Tick(rig.Player.Playfield!, 60);

        Assert.IsFalse(moves.HasPending(CharacterId));
        Assert.AreSame(worn, rig.Player.Inventory.Armor.Content[wornSlot], "the item stays worn");
        Assert.AreEqual(0, bagPage.Content.Count, "nothing landed in the page that left");
        Assert.IsFalse(worn.Locked);
        rig.Flush.HardFlush(rig.Player);
        rig.Store.AssertRow(worn, (int)IdentityType.ArmorPage, CharacterId, wornSlot);
    }

    [TestMethod]
    public void RandomInterleavingsOfQueuedCommitsKeepStorageInStepWithMemory()
    {
        for (int seed = 1; seed <= 300; seed++)
        {
            var steps = new List<string>();
            try
            {
                using var rig = new Rig();
                var random = new Random(seed);
                for (int step = 0; step < 150; step++)
                {
                    steps.Add(rig.RandomStep(random));
                    rig.AssertNoFailure();
                }
                rig.Flush.HardFlush(rig.Player);
                rig.Flush.HardFlush(rig.Partner);
                rig.AssertHealthy();
                rig.AssertStorageMatchesMemory();
            }
            catch (Exception exception)
            {
                throw new AssertFailedException("seed " + seed + " after: " + string.Join(", ", steps)
                    + Environment.NewLine + exception.Message, exception);
            }
        }
    }

    /// <summary>
    /// The ordering above holds only because each place that counts an item as persisted ahead of its row's insert has
    /// its rows claimed by the queued commit (<c>QueuedRowClaim</c>). A new place that sets <c>Item.IsPersisted</c> fails
    /// here until it is reviewed: either its row exists by then, or its commit claims the row and a case above covers it.
    /// </summary>
    [TestMethod]
    public void EveryPlaceThatMarksAnItemPersistedIsReviewed()
    {
        // File under ZoneEngine_New -> assignments to IsPersisted, and whether it marks items ahead of a queued insert.
        var reviewed = new Dictionary<string, (int Count, bool AheadOfInsert)>(StringComparer.Ordinal)
        {
            ["Core/Inventory/InventoryActionService.cs"] = (1, true),        // TryCommit: the plan's inserts, claimed.
            ["Core/Inventory/InventoryGrantPlan.cs"] = (2, true),            // Publish before commit, claimed; MarkStored.
            ["Core/Trade/TradeService.cs"] = (2, true),                      // PlanRows: the plan's inserts, claimed; ApplyDeliveries.
            ["Core/Inventory/InventoryMoveService.cs"] = (1, false),         // A rescaled item keeps its instance id and state.
            ["Core/Inventory/ItemBuilder.cs"] = (1, false),                  // Built from a stored row.
            ["Core/Inventory/PlayerInventory.cs"] = (1, false),              // MarkNewlyPersisted: after the insert ran.
            ["Core/Quests/Dungeons/MissionChestPopulation.cs"] = (1, false), // Rebuilt from the chest's stored rows.
        };

        var problems = new List<string>();
        Dictionary<string, int> found = CountInEngineSource(new Regex(@"\.IsPersisted\s*=(?!=)"));
        foreach ((string file, int count) in found)
        {
            int known = reviewed.TryGetValue(file, out var entry) ? entry.Count : 0;
            if (known != count)
                problems.Add(file + " sets IsPersisted " + count + " time(s); reviewed " + known);
        }
        foreach (string file in reviewed.Keys.Where(file => !found.ContainsKey(file)))
            problems.Add(file + " no longer sets IsPersisted; drop it from the reviewed list");
        foreach (string file in reviewed.Where(pair => pair.Value.AheadOfInsert && found.ContainsKey(pair.Key)).Select(pair => pair.Key))
            if (!ClaimsRows(file))
                problems.Add(file + " marks items persisted ahead of their insert but no longer claims their rows (QueuedRowClaim)");

        Assert.AreEqual(0, problems.Count, "A write queued ahead of an item's insert turns its move into an update of a missing"
            + " row, which quarantines the character. Review each change:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Each commit queued in a player's lane that writes item rows planned on the tick must claim those rows
    /// (<c>QueuedRowClaim</c>). Otherwise lane work queued ahead of it stores a newer state of the item first, and the
    /// commit's older plan then overwrites it, or inserts a row the plan drops. A new queued commit fails here until it
    /// is reviewed: either it writes no item rows planned on the tick, or it claims them and a case above covers it.
    /// </summary>
    [TestMethod]
    public void EveryQueuedLaneCommitIsReviewed()
    {
        // File under ZoneEngine_New -> lane commits queued there, and whether they write item rows planned on the tick.
        var reviewed = new Dictionary<string, (int Count, bool WritesPlannedItemRows)>(StringComparer.Ordinal)
        {
            ["Core/Inventory/InventoryActionService.cs"] = (2, true),      // Shared-source relocation; TryCommit.
            ["Core/Trade/TradeService.cs"] = (3, true),                    // Player trade (joint), sale, Knubot hand-in.
            ["Core/Trade/TradeService.Purchase.cs"] = (1, true),           // Purchase: PlanRows.
            ["Core/Quests/QuestService.Completion.cs"] = (4, true),        // Rewards through InventoryGrantPlan (claimed in
                                                                           // PublishBeforeCommit); mission transactions and
                                                                           // stat mutations write no item rows.
            ["Core/Quests/QuestService.cs"] = (1, false),                  // Quest state row.
            ["Core/Organizations/OrganizationService.Bank.cs"] = (2, false), // Cash and the org bank.
            ["Core/Cities/CityService.cs"] = (1, false),                   // A house's confirmation behind the house item's
                                                                           // retire (TryCommit, already queued). A district is
                                                                           // paid from the org bank in its claim transaction.
        };

        var problems = new List<string>();
        Dictionary<string, int> found = CountInEngineSource(new Regex(@"\b_?flush\.Enqueue(Joint)?\("));
        foreach ((string file, int count) in found)
        {
            int known = reviewed.TryGetValue(file, out var entry) ? entry.Count : 0;
            if (known != count)
                problems.Add(file + " queues " + count + " lane commit(s); reviewed " + known);
        }
        foreach (string file in reviewed.Keys.Where(file => !found.ContainsKey(file)))
            problems.Add(file + " no longer queues lane commits; drop it from the reviewed list");
        foreach (string file in reviewed.Where(pair => pair.Value.WritesPlannedItemRows && found.ContainsKey(pair.Key)).Select(pair => pair.Key))
            if (!ClaimsRows(file) && !File.ReadAllText(Path.Combine(EngineRoot(), file)).Contains("PublishBeforeCommit(", StringComparison.Ordinal))
                problems.Add(file + " queues commits that write planned item rows but claims none (QueuedRowClaim)");

        Assert.AreEqual(0, problems.Count, "A lane commit that writes item rows planned on the tick without claiming them"
            + " lets earlier lane work write the item first. Review each change:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    static bool ClaimsRows(string file)
    {
        string text = File.ReadAllText(Path.Combine(EngineRoot(), file));
        return (text.Contains("new QueuedRowClaim(", StringComparison.Ordinal) || text.Contains("Claim = new QueuedRowClaim(", StringComparison.Ordinal)
            || text.Contains("PlanRows(", StringComparison.Ordinal))
            && (text.Contains(".Release()", StringComparison.Ordinal) || text.Contains("PersistPlan(", StringComparison.Ordinal));
    }

    static Dictionary<string, int> CountInEngineSource(Regex pattern)
    {
        string root = EngineRoot();
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal))
                continue;
            int count = pattern.Matches(File.ReadAllText(file)).Count;
            if (count > 0)
                found[relative] = count;
        }
        Assert.IsTrue(found.Count > 0, "No engine source found under " + root);
        return found;
    }

    static string EngineRoot() => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, "..", "ZoneEngine_New"));

    static string SourceFile([CallerFilePath] string path = "") => path;

    sealed class Rig : IDisposable
    {
        internal readonly Player Player;
        internal readonly Player Partner;
        internal readonly NpcCharacter Npc;
        internal readonly Session Session = new();
        internal readonly Session PartnerSession = new();
        internal readonly Store Store = new();
        internal readonly Logger Log = new();
        internal readonly InventoryFlushService Flush;
        internal readonly TradeService Trades;
        internal readonly InventoryActionService Actions;
        readonly Items _items = new();
        int _nextLooted = 51400000;

        internal Rig()
        {
            Player = Online(CharacterId, "Lane", Session);
            Partner = Online(PartnerId, "Partner", PartnerSession);
            Npc = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = NpcId }, _items) { Name = "Quest Giver" };

            // No writer ever finds a player: a lane runs only when the test drives it with a hard flush.
            Flush = new InventoryFlushService(_ => null, () => [Player, Partner], Store, Log);
            IGameData data = DispatchProxy.Create<IGameData, Refuse>();
            var templates = new Templates();
            var ids = new Ids();
            Trades = new TradeService(Log, data, templates, new HashItemMinter(data, templates, _items), ids, Flush, Store);
            Actions = new InventoryActionService(Store, Flush, ids, Log);
        }

        Player Online(int id, string name, Session session)
        {
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, Log, _items) { FirstName = name };
            player.Inventory.Apply(new CharacterHydrationResult(), id, _items);
            player.Stats.Set(CharacterStat.Cash, 1000);
            player.EnterOnline(session);
            return player;
        }

        internal Item Carried(int instanceId, bool persisted) => Carried(Player, instanceId, persisted);

        /// <summary>An item in the next free inventory slot: stored (its row seeded), or freshly looted (a dirty insert).</summary>
        internal Item Carried(Player player, int instanceId, bool persisted)
        {
            Container page = player.Inventory.Inventory;
            int slot = page.FindFreeSlot();
            Assert.IsTrue(slot >= 0, "a free slot");
            Item item = _items.Create(5000, 5000, 1, ItemSource.Loot, instanceId: instanceId);
            item.IsPersisted = persisted;
            Assert.IsTrue(page.Add(slot, item));
            if (persisted)
                Store.Seed(item, (int)page.Identity.Type, player.Identity.Instance, slot);
            else
                player.Inventory.MarkDirty(item, page, slot);
            return item;
        }

        /// <summary>
        /// A take from a mission chest, as <c>InventoryMoveService.TakeSharedLoot</c> applies it under the gate: the row's
        /// move to the player is queued first (locking the chest row as last seen), then the item moves in memory.
        /// </summary>
        internal Item TakeFromChest(Player player, int instanceId)
        {
            Container page = player.Inventory.Inventory;
            int slot = page.FindFreeSlot();
            Assert.IsTrue(slot >= 0, "a free slot");
            Item item = _items.Create(5000, 5000, 1, ItemSource.Loot, instanceId: instanceId);
            item.IsPersisted = true;
            var chest = new Identity { Type = IdentityType.Container, Instance = ChestId };
            Store.Seed(item, (int)chest.Type, ChestId, 0);
            lock (player.PersistenceGate)
            {
                Actions.QueueSharedSourceRelocation(player, item, item.ToRecord(chest, 0, item.StackCount), page.Identity, slot);
                Assert.IsTrue(page.Add(slot, item));
            }
            return item;
        }

        internal Item QueueQuestReward() => QueueQuestReward(Player);

        /// <summary>
        /// A quest reward, queued as <c>QuestService.CompleteTemplate</c> queues it (under the gate, as a Knubot line's
        /// effects run): the commit inserting its parked row is enqueued, the item published into its slot before that
        /// commit runs, and the commit marks it stored.
        /// </summary>
        internal Item QueueQuestReward(Player player)
        {
            Item reward = _items.CreateWithNewInstance(6000, 6000, 1, ItemSource.Quest);
            lock (player.PersistenceGate)
            {
                Assert.IsTrue(InventoryGrantPlan.TryCreate(player, [reward], out InventoryGrantPlan grant, allowOverflow: true));
                IReadOnlyList<ItemInstanceRecord> parked = grant.ParkedRows();
                Flush.Enqueue(player, () =>
                {
                    Store.Insert(parked);
                    grant.MarkStored();
                });
                Assert.IsTrue(grant.PublishBeforeCommit());
            }
            return reward;
        }

        /// <summary>A player trade between the two, committed as both sides accepting does: one joint commit in both lanes.</summary>
        internal void TradeWithPartner(Item? given, Item? returned)
        {
            var session = new TradeSession(new Identity { Type = IdentityType.TradeWindow, Instance = 93 }, TradeKind.Player,
                Player, Partner, machine: null);
            Invoke("Register", Player, session);
            Invoke("Register", Partner, session);
            if (given != null)
                Offer(Player, session, given);
            if (returned != null)
                Offer(Partner, session, returned);
            Invoke("CommitPlayerTrade", session);
            Assert.IsTrue(given == null || Holder(given) == Partner, "the partner holds what was given");
            Assert.IsTrue(returned == null || Holder(returned) == Player, "the player holds what was returned");
        }

        /// <summary>A move to another free slot committed through <see cref="InventoryActionService.TryCommit"/>.</summary>
        internal bool ActionMove(Player player, Item item)
        {
            Container page = player.Inventory.Inventory;
            int from = SlotOf(player, item);
            int to = page.FindFreeSlot();
            return to >= 0 && Actions.TryCommit(player, [new InventoryRowChange(item, page.Identity, to, item.StackCount)],
                () => page.Content.TryGetValue(from, out Item? current) && ReferenceEquals(current, item) && !page.Content.ContainsKey(to),
                () =>
                {
                    page.Content.Remove(from);
                    Assert.IsTrue(page.Add(to, item));
                });
        }

        /// <summary>A plain move to another free slot, stored by the coalesced flush as a dirty entry.</summary>
        internal void DirtyMove(Player player, Item item)
        {
            Container page = player.Inventory.Inventory;
            int to = page.FindFreeSlot();
            if (to < 0)
                return;
            page.Content.Remove(SlotOf(player, item));
            Assert.IsTrue(page.Add(to, item));
            player.Inventory.MarkDirty(item, page, to);
        }

        internal bool Delete(Player player, Item item)
            => Actions.TryDelete(player, new Identity { Type = IdentityType.Inventory, Instance = SlotOf(player, item) }, item.InstanceId);

        /// <summary>One random step of the interleaving soak; returns what it did.</summary>
        internal string RandomStep(Random random)
        {
            Player who = random.Next(3) == 0 ? Partner : Player;
            string side = who == Player ? "P" : "Q";
            int free = who.Inventory.Inventory.Capacity - who.Inventory.Inventory.Content.Count;
            Item? held = Pick(random, who);
            switch (random.Next(12))
            {
                case 0 when free > 4:
                    Carried(who, Interlocked.Increment(ref _nextLooted), persisted: false);
                    return side + " loot";
                case 1 when free > 4:
                    QueueQuestReward(who);
                    return side + " reward";
                case 2 when held != null && who == Player:
                {
                    TradeSession knubot = OpenKnubot();
                    OfferToKnubot(knubot, held);
                    Assert.IsTrue(Trades.TryCommitKnubot(Player, Npc, 0), "the hand-in went through");
                    if (random.Next(2) == 0 && free > 4)
                    {
                        QueueQuestReward(Player);
                        return "P hand-in " + held.InstanceId + " + reward";
                    }
                    return "P hand-in " + held.InstanceId;
                }
                case 3 when held != null && who == Player:
                {
                    TradeSession shop = OpenShop();
                    OfferToShop(shop, held);
                    CommitShop(shop);
                    Assert.AreEqual(0, shop.InitiatorOffer.Count, "the sale went through");
                    return "P sell " + held.InstanceId;
                }
                case 4 when PlayerTradeFits():
                {
                    Item? given = random.Next(4) == 0 ? null : Pick(random, Player);
                    Item? returned = random.Next(3) == 0 ? Pick(random, Partner) : null;
                    if (given == null && returned == null)
                        return "-";
                    TradeWithPartner(given, returned);
                    return "trade " + (given?.InstanceId ?? 0) + "/" + (returned?.InstanceId ?? 0);
                }
                case 5 when held != null && free > 0:
                    ActionMove(who, held);
                    return side + " action-move " + held.InstanceId;
                case 6 when held != null:
                    Assert.IsTrue(Delete(who, held), "the delete applied");
                    return side + " delete " + held.InstanceId;
                case 7 when held != null && free > 0:
                    DirtyMove(who, held);
                    return side + " move " + held.InstanceId;
                case 8:
                    Flush.HardFlush(who);
                    return side + " flush";
                case 9 when free > 4:
                    TakeFromChest(who, Interlocked.Increment(ref _nextLooted));
                    return side + " chest-take";
                case 10 when who == Player && Bag.FindFreeSlot() >= 0:
                {
                    // Handed in straight from an open bag, or taken back out of the window into the main inventory.
                    Item bagged = InBag(Interlocked.Increment(ref _nextLooted));
                    TradeSession knubot = OpenKnubot();
                    OfferToKnubotFromBag(knubot, bagged);
                    Assert.IsTrue(knubot.InitiatorOffer.Items.Values.Any(offered => ReferenceEquals(offered, bagged)), "the bag item is on the table");
                    if (random.Next(2) == 0 || free == 0)
                    {
                        Assert.IsTrue(Trades.TryCommitKnubot(Player, Npc, 0), "the hand-in went through");
                        return "P bag hand-in " + bagged.InstanceId;
                    }
                    Invoke("RemoveKnubotItem", Player, knubot, knubot.InitiatorOffer, knubot.InitiatorOffer.Items.Single(pair => ReferenceEquals(pair.Value, bagged)).Key);
                    Assert.AreSame(Player, Holder(bagged), "the item came back into the main inventory, where the client puts it");
                    Invoke("Unregister", Player);
                    return "P bag take-back " + bagged.InstanceId;
                }
                default:
                    return "-";
            }
        }

        bool PlayerTradeFits()
            => Player.Inventory.Inventory.Capacity - Player.Inventory.Inventory.Content.Count > 4
                && Partner.Inventory.Inventory.Capacity - Partner.Inventory.Inventory.Content.Count > 4;

        /// <summary>A carried item other than the open bag, which the bag steps need in place.</summary>
        static Item? Pick(Random random, Player player)
        {
            Item[] items = player.Inventory.Inventory.Content.Values.Where(item => !InventoryMoveService.IsBagItem(item)).ToArray();
            return items.Length == 0 ? null : items[random.Next(items.Length)];
        }

        internal Player? Holder(Item item)
        {
            foreach (Player player in new[] { Player, Partner })
                foreach (Container page in new[] { player.Inventory.Inventory, player.Inventory.Overflow })
                    if (page.Content.Values.Any(held => ReferenceEquals(held, item)))
                        return player;
            return null;
        }

        internal TradeSession OpenKnubot()
        {
            var session = new TradeSession(Identity.None, TradeKind.Knubot, Player, partner: null, machine: null, Npc);
            Invoke("Register", Player, session);
            return session;
        }

        internal void OfferToKnubot(TradeSession session, Item item)
            => Invoke("AddKnubotItem", Player, session, session.InitiatorOffer,
                new Identity { Type = IdentityType.Inventory, Instance = SlotOf(item) });

        /// <summary>The add the client sends for a slot of an open bag: the bag's handle in the high 16 bits, the slot below.</summary>
        internal void OfferToKnubotFromBag(TradeSession session, Item item)
        {
            int slot = Bag.Content.Single(pair => ReferenceEquals(pair.Value, item)).Key;
            Invoke("AddKnubotItem", Player, session, session.InitiatorOffer,
                new Identity { Type = IdentityType.Backpack, Instance = (BagHandle << 16) | slot });
        }

        internal const int BagHandle = 3;
        const int BagId = 51222900;
        Container? _bag;

        /// <summary>A carried, open bag the client knows by <see cref="BagHandle"/>.</summary>
        internal Container Bag
        {
            get
            {
                if (_bag != null)
                    return _bag;
                var bagIdentity = new Identity { Type = IdentityType.Container, Instance = BagId };
                Container inventory = Player.Inventory.Inventory;
                int slot = inventory.FindFreeSlot();
                Item bag = _items.Create(99228, 99228, 1, ItemSource.Loot, instanceId: BagId, identity: bagIdentity);
                bag.IsPersisted = true;
                Assert.IsTrue(inventory.Add(slot, bag));
                Store.Seed(bag, (int)inventory.Identity.Type, Player.Identity.Instance, slot);
                _bag = Player.Inventory.GetOrCreateBackpackPage(bag, bagIdentity, new Identity { Type = IdentityType.Inventory, Instance = slot });
                _bag.IsHydrated = true;
                _bag.IsOpen = true;
                _bag.InventoryHandle = BagHandle;
                Player.Inventory.RegisterBackpackHandle(BagHandle, bagIdentity);
                return _bag;
            }
        }

        /// <summary>A stored item in the bag's next free slot.</summary>
        internal Item InBag(int instanceId)
        {
            Container bag = Bag;
            int slot = bag.FindFreeSlot();
            Assert.IsTrue(slot >= 0, "a free bag slot");
            Item item = _items.Create(5000, 5000, 1, ItemSource.Loot, instanceId: instanceId);
            item.IsPersisted = true;
            Assert.IsTrue(bag.Add(slot, item));
            Store.Seed(item, (int)bag.Identity.Type, bag.Identity.Instance, slot);
            return item;
        }

        /// <summary>Everything the client was told since the last call, oldest first.</summary>
        internal MessageBody[] TakeSent()
        {
            MessageBody[] sent = Session.Sent.ToArray();
            Session.Sent.Clear();
            return sent;
        }

        /// <summary>The ContainerAddItem frames the client was told since the last call, oldest first.</summary>
        internal ContainerAddItem[] TakeContainerAdds()
        {
            ContainerAddItem[] adds = Session.Sent.OfType<ContainerAddItem>().ToArray();
            Session.Sent.Clear();
            return adds;
        }

        internal TradeSession OpenShop()
        {
            var machine = new VendingMachine(new Identity { Type = IdentityType.VendingMachine, Instance = 91 }, new ItemTemplate { Id = 9000 });
            machine.Playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
            machine.Stats.Set(CharacterStat.BuyModifier, 50);
            machine.Stats.Set(CharacterStat.SellModifier, 100);
            return new TradeSession(new Identity { Type = IdentityType.TradeWindow, Instance = 92 }, TradeKind.Shop, Player, null, machine);
        }

        internal void OfferToShop(TradeSession session, Item item)
        {
            int slot = SlotOf(item);
            Invoke("HandleAddItem", Player, session, new TradeMessage
            {
                Identity = Player.Identity, Action = TradeAction.AddItem, Target = Player.Identity,
                Container = new Identity { Type = IdentityType.Inventory, Instance = slot }
            });
            Assert.AreSame(item, session.InitiatorOffer.Items.Values.Single(), "the item is on the table");
        }

        internal void CommitShop(TradeSession session) => Invoke("CommitShop", Player, session);

        /// <summary>Nothing failed so far: no error logged, nobody quarantined or disconnected.</summary>
        internal void AssertNoFailure()
        {
            Assert.AreEqual(string.Empty, Log.Problems);
            foreach ((Player player, Session session) in new[] { (Player, Session), (Partner, PartnerSession) })
            {
                Assert.IsFalse(player.IsPersistenceQuarantined, "character " + player.Identity.Instance + " must not be quarantined.");
                Assert.AreNotEqual(SessionState.Closed, session.State, "character " + player.Identity.Instance + "'s session must stay open.");
            }
        }

        internal void AssertHealthy()
        {
            AssertNoFailure();
            foreach (Player player in new[] { Player, Partner })
            {
                Assert.IsFalse(player.Lane.HasQueuedWork, "Everything queued for " + "character " + player.Identity.Instance + " ran.");
                Assert.IsFalse(player.Inventory.HasDirtyEntries, "Every dirty entry of " + "character " + player.Identity.Instance + " was written once its insert had run.");
            }
        }

        internal void AssertStoredWhereHeld(Item item) => AssertStoredWhereHeld(Player, item);

        /// <summary>The item's row is where the player holds it now (its move out of the parked insert row landed).</summary>
        internal void AssertStoredWhereHeld(Player player, Item item)
        {
            foreach (Container page in new[] { player.Inventory.Inventory, player.Inventory.Overflow })
                foreach (KeyValuePair<int, Item> held in page.Content)
                    if (ReferenceEquals(held.Value, item))
                    {
                        Store.AssertRow(item, (int)page.Identity.Type, page.Identity.Instance, held.Key);
                        return;
                    }
            Assert.Fail("character " + player.Identity.Instance + " does not hold item " + item.InstanceId + ".");
        }

        /// <summary>
        /// Storage is what a relog would load: a row for each item either player holds, where it is held, and no row left
        /// on either player's pages for an item they no longer hold.
        /// </summary>
        internal void AssertStorageMatchesMemory()
        {
            foreach (Player player in new[] { Player, Partner })
            {
                var pages = new[] { player.Inventory.Inventory, player.Inventory.Overflow };
                foreach (Container page in pages)
                    foreach (Item item in page.Content.Values)
                        AssertStoredWhereHeld(player, item);
                foreach ((int id, (int Type, int Owner, int Slot) row) in Store.Rows())
                {
                    Container? page = pages.FirstOrDefault(p => (int)p.Identity.Type == row.Type && p.Identity.Instance == row.Owner);
                    if (page == null && row.Type == (int)IdentityType.OverflowWindow && row.Owner == player.Identity.Instance)
                        Assert.Fail("Item " + id + " is still parked: its move out of the reward insert never landed.");
                    if (page != null)
                        Assert.IsTrue(page.Content.TryGetValue(row.Slot, out Item? held) && held.InstanceId == id,
                            "Row of item " + id + " sits on character " + player.Identity.Instance + "'s page slot " + row.Slot
                                + ", which does not hold it: " + Store.History(id));
                }
            }
        }

        internal int SlotOf(Item item) => SlotOf(Player, item);

        /// <summary>A player trade session with both registered and nothing on the table yet.</summary>
        internal TradeSession OpenPlayerTrade()
        {
            var session = new TradeSession(new Identity { Type = IdentityType.TradeWindow, Instance = 94 }, TradeKind.Player,
                Player, Partner, machine: null);
            Invoke("Register", Player, session);
            Invoke("Register", Partner, session);
            return session;
        }

        /// <summary>The client's add of a carried item to the table; true when the server put it there.</summary>
        internal bool TryOffer(Player player, TradeSession session, Item item)
        {
            int slot = SlotOf(player, item);
            Invoke("HandleAddItem", player, session, new TradeMessage
            {
                Identity = player.Identity, Action = TradeAction.AddItem, Target = player.Identity,
                Container = new Identity { Type = IdentityType.Inventory, Instance = slot }
            });
            return session.OfferFor(player).Items.Values.Any(offered => ReferenceEquals(offered, item));
        }

        internal void CommitPlayerTrade(TradeSession session) => Invoke("CommitPlayerTrade", session);

        /// <summary>The move service over this rig's flush and actions, with the player on a playfield so moves run.</summary>
        internal InventoryMoveService Moves()
        {
            Player.Playfield ??= (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
            return new InventoryMoveService(Log, Flush, Actions);
        }

        /// <summary>A stored item worn on the armor page's first slot.</summary>
        internal Item Worn(int instanceId)
        {
            Container armor = Player.Inventory.Armor;
            int slot = armor.Offset;
            Item item = _items.Create(5000, 5000, 1, ItemSource.Loot, instanceId: instanceId);
            item.IsPersisted = true;
            Assert.IsTrue(armor.Add(slot, item), "the armor slot is free");
            Store.Seed(item, (int)armor.Identity.Type, Player.Identity.Instance, slot);
            return item;
        }

        /// <summary>The client's ContainerAddItem taking the worn item off into the open bag.</summary>
        internal void UnequipIntoBag(InventoryMoveService moves, Item worn)
        {
            Container armor = Player.Inventory.Armor;
            int slot = armor.Content.Single(pair => ReferenceEquals(pair.Value, worn)).Key;
            moves.Handle(Player, new ClientContainerAddItem
            {
                Identity = Player.Identity,
                Source = new Identity { Type = armor.Identity.Type, Instance = slot },
                Target = Bag.Identity
            });
        }

        internal Item BagItem
        {
            get
            {
                _ = Bag;
                return Player.Inventory.Inventory.Content.Values.Single(item => item.InstanceId == BagId);
            }
        }

        internal void OfferToKnubotAt(TradeSession session, Identity source)
            => Invoke("AddKnubotItem", Player, session, session.InitiatorOffer, source);

        internal void RemoveFromKnubot(TradeSession session, int tradeSlot)
            => Invoke("RemoveKnubotItem", Player, session, session.InitiatorOffer, tradeSlot);

        static int SlotOf(Player player, Item item) => player.Inventory.Inventory.Content.Single(pair => ReferenceEquals(pair.Value, item)).Key;

        void Offer(Player player, TradeSession session, Item item)
        {
            int slot = SlotOf(player, item);
            Invoke("HandleAddItem", player, session, new TradeMessage
            {
                Identity = player.Identity, Action = TradeAction.AddItem, Target = player.Identity,
                Container = new Identity { Type = IdentityType.Inventory, Instance = slot }
            });
            Assert.IsTrue(session.OfferFor(player).Items.Values.Any(offered => ReferenceEquals(offered, item)), "the item is on the table");
        }

        void Invoke(string method, params object[] args)
            => typeof(TradeService).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Trades, args);

        public void Dispose() => Flush.Dispose();
    }

    /// <summary>item_instances as the MySQL DAO writes it: location updates are staged first, on rows that must exist.</summary>
    sealed class Store : ICharacterCoalesceCommit, ITradePersistence, IInventoryMutationPersistence
    {
        readonly object _sync = new();
        readonly Dictionary<int, (int Type, int Owner, int Slot)> _rows = new();
        readonly Dictionary<int, List<string>> _history = new();
        int _batches;

        /// <summary>Every write of the item's row, in order, for a failure message.</summary>
        internal string History(int instanceId)
        {
            lock (_sync)
                return _history.TryGetValue(instanceId, out List<string>? writes) ? string.Join(" -> ", writes) : "never written";
        }

        void Note(int instanceId, string write)
        {
            if (!_history.TryGetValue(instanceId, out List<string>? writes))
                _history[instanceId] = writes = [];
            writes.Add(write);
        }

        internal KeyValuePair<int, (int Type, int Owner, int Slot)>[] Rows()
        {
            lock (_sync)
                return [.. _rows];
        }

        internal void Seed(Item item, int type, int owner, int slot)
        {
            lock (_sync)
            {
                _rows.Add(item.InstanceId, (type, owner, slot));
                Note(item.InstanceId, "seeded " + (type, owner, slot));
            }
        }

        internal bool Has(int instanceId)
        {
            lock (_sync)
                return _rows.ContainsKey(instanceId);
        }

        internal void AssertRow(Item item, int type, int owner, int slot)
        {
            lock (_sync)
            {
                Assert.IsTrue(_rows.TryGetValue(item.InstanceId, out var row), "No row for item " + item.InstanceId + ".");
                Assert.AreEqual((type, owner, slot), row, "Row of item " + item.InstanceId + ": " + History(item.InstanceId));
            }
        }

        internal void Insert(IReadOnlyList<ItemInstanceRecord> rows) => Write(rows, []);

        public void Persist(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates, int characterId,
            IReadOnlyList<int> uploadedNanoIds, IReadOnlyList<ActiveNanoRecord>? activeNanos, IReadOnlyList<SkillLockRecord>? skillLocks)
            => Write(inserts, updates);

        public void Persist(TradePersistenceBatch batch) => Write(batch.Inserts, batch.Updates);

        /// <summary>A batch the database refuses outright (a container retired with rows in it): a terminal failure.</summary>
        internal Func<InventoryMutationBatch, bool>? Refuse;

        /// <summary>As the DAO: a shared source row must still be exactly where this server last saw it, or the take fails.</summary>
        public void Persist(InventoryMutationBatch batch)
        {
            if (Refuse?.Invoke(batch) == true)
                throw new InvalidOperationException("Cannot retire a nonempty item container.");
            lock (_sync)
            {
                foreach (ItemInstanceRecord expected in batch.ExpectedSources)
                    if (!_rows.TryGetValue(expected.InstanceId, out var row)
                        || row != (expected.ContainerType, expected.ContainerInstance, expected.ContainerPlacement))
                        throw new InvalidOperationException("Shared source row changed for InstanceId=" + expected.InstanceId
                            + ": " + History(expected.InstanceId));
                Write(batch.Inserts, batch.Locations);
            }
        }

        void Write(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates)
        {
            lock (_sync)
            {
                foreach (ItemLocationUpdate update in updates)
                    if (!_rows.ContainsKey(update.InstanceId))
                        throw new InvalidOperationException("UpdateLocations found no row for InstanceId=" + update.InstanceId);
                foreach (ItemInstanceRecord insert in inserts)
                    if (_rows.ContainsKey(insert.InstanceId))
                        throw new InvalidOperationException("Duplicate insert for InstanceId=" + insert.InstanceId);
                int batch = ++_batches;
                foreach (ItemInstanceRecord insert in inserts)
                {
                    _rows[insert.InstanceId] = (insert.ContainerType, insert.ContainerInstance, insert.ContainerPlacement);
                    Note(insert.InstanceId, "#" + batch + " insert " + _rows[insert.InstanceId]);
                }
                foreach (ItemLocationUpdate update in updates)
                {
                    _rows[update.InstanceId] = (update.ContainerType, update.ContainerInstance, update.ContainerPlacement);
                    Note(update.InstanceId, "#" + batch + " update " + _rows[update.InstanceId]);
                }
            }
        }
    }

    /// <summary>A stand-in for game data the trade paths under test never read; any read fails the test.</summary>
    public class Refuse : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new NotSupportedException("Game data is not part of this test: " + targetMethod?.Name);
    }

    sealed class Logger : IZoneLogger
    {
        readonly object _sync = new();
        string _problems = string.Empty;

        internal string Problems { get { lock (_sync) return _problems; } }

        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { lock (_sync) _problems += "WARN " + message + Environment.NewLine; }
        public void Error(string message) { lock (_sync) _problems += "ERROR " + message + Environment.NewLine; }
        public void Error(Exception exception, string message) { lock (_sync) _problems += "ERROR " + message + " " + exception + Environment.NewLine; }
        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }

    sealed class Session : IZoneSession
    {
        /// <summary>What the client was told, in order.</summary>
        internal readonly List<MessageBody> Sent = [];

        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public bool TransferToPlayfield(Playfield destination, Vector3 landing) => false;
        public void Send(byte[] packet) { }
        public void Send(MessageBody body) => Sent.Add(body);
        public void Send(MessageBody body, int sender, int receiver) { }
        public void SendInitiateCompression() { }
        public void Close() => State = SessionState.Closed;
    }

    sealed class Items : IItemBuilder
    {
        int _next = 51222700;

        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null) => new()
            {
                LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount,
                InstanceId = instanceId, Identity = identity ?? Identity.None, Definition = CreateTemplate(lowId, highId, quality)
            };

        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
        {
            Item item = Create(lowId, highId, quality, source, stackCount);
            item.AssignInstanceId(Interlocked.Increment(ref _next));
            return item;
        }

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
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
        int _next = 51300000;
        public int Allocate() => Interlocked.Increment(ref _next);
    }
}
