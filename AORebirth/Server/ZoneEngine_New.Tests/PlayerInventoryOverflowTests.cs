namespace ZoneEngine_New.Tests
{
    using System.Collections.Generic;
    using System.Linq;

    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;

    [TestClass]
    public sealed class PlayerInventoryOverflowTests
    {
        [TestMethod]
        public void TryPlacePrefersTheFirstFreeMainInventorySlot()
        {
            Player player = TestWorld.CreatePlayer(1);
            PlayerInventory inventory = player.Inventory;

            Assert.IsTrue(inventory.TryPlace(TestWorld.CreateItem(), out Container page, out int slot));

            Assert.AreSame(inventory.Inventory, page);
            Assert.AreEqual(inventory.Inventory.Offset, slot);
            Assert.AreEqual(0, inventory.Overflow.Content.Count);
        }

        [TestMethod]
        public void TryPlaceFallsThroughToOverflowForEphemeralItemsWhenInventoryIsFull()
        {
            Player player = TestWorld.CreatePlayer(1);
            PlayerInventory inventory = player.Inventory;
            TestWorld.FillInventory(player);

            Item item = TestWorld.CreateItem(instanceId: 77, persisted: false);
            Assert.IsTrue(inventory.TryPlace(item, out Container page, out int slot));

            Assert.AreSame(inventory.Overflow, page);
            Assert.AreEqual(inventory.Overflow.Offset, slot);
            Assert.AreSame(item, inventory.Overflow.Content[slot]);
        }

        [TestMethod]
        public void TryPlacePutsADurableItemInOverflowWhenInventoryIsFull()
        {
            Player player = TestWorld.CreatePlayer(1);
            PlayerInventory inventory = player.Inventory;
            TestWorld.FillInventory(player);

            Item item = TestWorld.CreateItem(instanceId: 77);
            Assert.IsTrue(inventory.TryPlace(item, out Container page, out int slot));

            Assert.AreSame(inventory.Overflow, page);
            Assert.AreSame(item, inventory.Overflow.Content[slot]);
        }

        [TestMethod]
        public void TrimOverflowKeepsTheNewestItemsAndRetiresTheOldest()
        {
            Player player = TestWorld.CreatePlayer(1);
            PlayerInventory inventory = player.Inventory;
            TestWorld.FillInventory(player);
            var graveyard = new Identity { Type = IdentityType.None, Instance = 1 };

            var placed = new List<Item>();
            for (int i = 0; i < 31; i++)
            {
                Item item = TestWorld.CreateItem(lowId: 2000 + i, highId: 2000 + i, instanceId: 2000 + i);
                Assert.IsTrue(inventory.TryPlace(item, out _, out _));
                placed.Add(item);
            }

            List<(Item Item, int Slot)> removed = inventory.TrimOverflow(30, graveyard);

            Assert.AreEqual(1, removed.Count);
            Assert.AreSame(placed[0], removed[0].Item);
            Assert.AreEqual(30, inventory.Overflow.Content.Count);
            CollectionAssert.DoesNotContain(new List<Item>(inventory.Overflow.Content.Values), placed[0]);
            Assert.IsTrue(inventory.HasDirtyEntries, "the evicted durable item's row is retired");
        }

        [TestMethod]
        public void ParkOfferedMovesADurableRowOffItsSlotSoTheSlotCanBeReused()
        {
            Player player = TestWorld.CreatePlayer(1);
            PlayerInventory inventory = player.Inventory;
            Item offered = TestWorld.CreateItem(instanceId: 77);
            inventory.Inventory.Add(inventory.Inventory.Offset, offered);
            inventory.Inventory.Remove(inventory.Inventory.Offset);

            inventory.ParkOffered(offered, characterId: 1);
            Item mover = TestWorld.CreateItem(instanceId: 78);
            Assert.IsTrue(inventory.Inventory.Add(inventory.Inventory.Offset, mover));
            inventory.MarkDirty(mover, inventory.Inventory, inventory.Inventory.Offset);

            PlayerInventory.InventoryDirtyFlush flush = inventory.TakeDirty()!;
            ItemLocationUpdate parked = flush.Updates.Single(update => update.InstanceId == 77);
            ItemLocationUpdate moved = flush.Updates.Single(update => update.InstanceId == 78);
            Assert.AreEqual((int)IdentityType.OverflowWindow, parked.ContainerType);
            Assert.AreEqual(77, parked.ContainerPlacement);
            Assert.AreEqual(inventory.Inventory.Offset, moved.ContainerPlacement);
            Assert.AreNotEqual((parked.ContainerType, parked.ContainerPlacement), (moved.ContainerType, moved.ContainerPlacement));
        }

        [TestMethod]
        public void TryPlaceFailsWhenBothInventoryAndOverflowAreFull()
        {
            Player player = TestWorld.CreatePlayer(1);
            PlayerInventory inventory = player.Inventory;
            TestWorld.FillInventory(player);

            for (int i = 0; i < PlayerInventory.OverflowCapacity; i++)
            {
                Assert.IsTrue(
                    inventory.TryPlace(TestWorld.CreateItem(instanceId: 1000 + i, persisted: false), out _, out _));
            }

            Assert.IsFalse(
                inventory.TryPlace(TestWorld.CreateItem(instanceId: 9999, persisted: false), out _, out _));
        }

        [TestMethod]
        public void HasFreeInventorySlotsCountsOnlyMainInventory()
        {
            Player player = TestWorld.CreatePlayer(1);
            PlayerInventory inventory = player.Inventory;

            Assert.IsTrue(inventory.HasFreeInventorySlots(0));
            Assert.IsTrue(inventory.HasFreeInventorySlots(inventory.Inventory.Capacity));
            Assert.IsFalse(inventory.HasFreeInventorySlots(inventory.Inventory.Capacity + 1));

            TestWorld.FillInventory(player);
            inventory.TryPlace(TestWorld.CreateItem(instanceId: 77, persisted: false), out _, out _);

            Assert.IsFalse(inventory.HasFreeInventorySlots(1));
        }

        [TestMethod]
        public void OverflowIsRemoveOnlySoTheClientCannotDragItemsIn()
        {
            Player player = TestWorld.CreatePlayer(1);

            Assert.IsFalse(player.Inventory.Overflow.Flags.HasFlag(ContainerFlags.CanAdd));
            Assert.IsTrue(player.Inventory.Overflow.Flags.HasFlag(ContainerFlags.CanRemove));
        }

        [TestMethod]
        public void MarkDirtySavesOverflowPlacements()
        {
            Player player = TestWorld.CreatePlayer(1);
            PlayerInventory inventory = player.Inventory;
            TestWorld.FillInventory(player);

            Item item = TestWorld.CreateItem(instanceId: 77, persisted: false);
            inventory.TryPlace(item, out Container page, out int slot);
            inventory.MarkDirty(item, page, slot);

            PlayerInventory.InventoryDirtyFlush flush = inventory.TakeDirty()!;
            ItemInstanceRecord row = flush.Inserts.Single(insert => insert.InstanceId == 77);
            Assert.AreEqual((int)IdentityType.OverflowWindow, row.ContainerType);
            Assert.AreEqual(slot, row.ContainerPlacement);
        }

        [TestMethod]
        public void MarkDirtyTracksMainInventoryPlacements()
        {
            Player player = TestWorld.CreatePlayer(1);
            PlayerInventory inventory = player.Inventory;

            Item item = TestWorld.CreateItem(instanceId: 77);
            inventory.TryPlace(item, out Container page, out int slot);
            inventory.MarkDirty(item, page, slot);

            Assert.IsTrue(inventory.HasDirtyEntries);
        }

        [TestMethod]
        public void MarkOrphanedRehomesADurableItemAndWritesNothingForAnEphemeralOne()
        {
            Player player = TestWorld.CreatePlayer(1);
            PlayerInventory inventory = player.Inventory;
            var graveyard = new Identity { Type = IdentityType.None, Instance = 1 };

            inventory.MarkOrphaned(TestWorld.CreateItem(instanceId: 77, persisted: false), graveyard);
            Assert.IsNull(inventory.TakeDirty(), "an item that was never written needs no row");
            Assert.IsFalse(inventory.HasDirtyEntries);

            inventory.MarkOrphaned(TestWorld.CreateItem(instanceId: 78), graveyard);
            PlayerInventory.InventoryDirtyFlush? flush = inventory.TakeDirty();
            Assert.IsNotNull(flush);
            Assert.AreEqual(0, flush!.Inserts.Count);
            Assert.AreEqual(1, flush.Updates.Count);
            Assert.AreEqual(78, flush.Updates[0].InstanceId);
            Assert.AreEqual((int)IdentityType.None, flush.Updates[0].ContainerType);
            Assert.AreEqual(78, flush.Updates[0].ContainerPlacement);
        }

        /// <summary>
        /// A new item's INSERT is taken by the background flush; before the commit is acknowledged the player destroys
        /// the item (a Newcomer's armour upgrade consumed while its first save was in flight). The next flush must move
        /// the now-stored row to the graveyard, or it stays in the bag slot: duplicated on relog and blocking every
        /// later write into that slot with a duplicate-key error.
        /// </summary>
        [TestMethod]
        public void DiscardDuringAnInFlightInsertRetiresTheRowOnTheNextFlush()
        {
            Player player = TestWorld.CreatePlayer(1);
            PlayerInventory inventory = player.Inventory;
            var graveyard = new Identity { Type = IdentityType.None, Instance = 1 };

            Item item = TestWorld.CreateItem(instanceId: 77, persisted: false);
            Assert.IsTrue(inventory.TryPlace(item, out Container page, out int slot));
            inventory.MarkDirty(item, page, slot);

            // The flush thread takes the batch and sends the INSERT.
            PlayerInventory.InventoryDirtyFlush? inFlight = inventory.TakeDirty();
            Assert.IsNotNull(inFlight);
            Assert.AreEqual(1, inFlight!.Inserts.Count);

            // Meanwhile the game thread consumes the item; its row does not exist yet from its point of view.
            page.Remove(slot);
            inventory.Discard(item, graveyard);

            // The INSERT commits.
            inFlight.MarkNewlyPersisted();
            Assert.IsTrue(item.IsPersisted);

            PlayerInventory.InventoryDirtyFlush? next = inventory.TakeDirty();
            Assert.IsNotNull(next, "the committed row must be retired");
            Assert.AreEqual(0, next!.Inserts.Count);
            Assert.AreEqual(1, next.Updates.Count);
            Assert.AreEqual(77, next.Updates[0].InstanceId);
            Assert.AreEqual((int)IdentityType.None, next.Updates[0].ContainerType);
            Assert.AreEqual(77, next.Updates[0].ContainerPlacement);
        }

        /// <summary>The same race when the in-flight INSERT is rolled back: no row exists, so none is written.</summary>
        [TestMethod]
        public void DiscardDuringAFailedInsertWritesNothing()
        {
            Player player = TestWorld.CreatePlayer(1);
            PlayerInventory inventory = player.Inventory;
            var graveyard = new Identity { Type = IdentityType.None, Instance = 1 };

            Item item = TestWorld.CreateItem(instanceId: 77, persisted: false);
            Assert.IsTrue(inventory.TryPlace(item, out Container page, out int slot));
            inventory.MarkDirty(item, page, slot);
            PlayerInventory.InventoryDirtyFlush? inFlight = inventory.TakeDirty();
            Assert.IsNotNull(inFlight);

            page.Remove(slot);
            inventory.Discard(item, graveyard);

            // The transaction failed: the flush hands its batch back, which must not revive the INSERT.
            inventory.RestoreDirty(inFlight!);
            Assert.IsFalse(item.IsPersisted);

            Assert.IsNull(inventory.TakeDirty());
            Assert.IsFalse(inventory.HasDirtyEntries);
        }

        [TestMethod]
        public void EnumerateHeldItemsSeesOverflowContents()
        {
            Player player = TestWorld.CreatePlayer(1);
            PlayerInventory inventory = player.Inventory;
            TestWorld.FillInventory(player);

            Item item = TestWorld.CreateItem(lowId: 4242, highId: 4242, instanceId: 77, persisted: false);
            inventory.TryPlace(item, out _, out _);

            var held = new List<Item>(inventory.EnumerateHeldItems());

            Assert.IsTrue(held.Contains(item));
            Assert.AreEqual(inventory.Inventory.Capacity + 1, held.Count);
        }

        [TestMethod]
        public void OverflowIsResolvableByItsClientIdentityType()
        {
            Player player = TestWorld.CreatePlayer(1);
            PlayerInventory inventory = player.Inventory;
            TestWorld.FillInventory(player);

            Item item = TestWorld.CreateItem(instanceId: 77, persisted: false);
            inventory.TryPlace(item, out _, out int slot);

            Assert.IsTrue(inventory.TryGetItem(IdentityType.OverflowWindow, slot, out Item found));
            Assert.AreSame(item, found);
        }
    }
}
