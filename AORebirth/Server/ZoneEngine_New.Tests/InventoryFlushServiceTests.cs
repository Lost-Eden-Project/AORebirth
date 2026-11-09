namespace ZoneEngine_New.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// The write-behind flush keeps the perk and research sets independent of the inventory batch: a player who
    /// finished research must keep it even when the inventory rows can no longer be saved (bug #292).
    /// </summary>
    [TestClass]
    public sealed class InventoryFlushServiceTests
    {
        const int TrainedPerk = 1001;
        const int ResearchInProgress = 2002;

        [TestMethod]
        public void InventoryConflictStillSavesTrainedPerksAndResearch()
        {
            using var world = new Fixture();
            world.MarkEverythingDirty();
            world.Coalesce.Failure = new DatabaseStateConflictException(new Exception("duplicate row"));

            Assert.ThrowsExactly<DatabaseStateConflictException>(() => world.Flush.HardFlush(world.Player));

            Assert.IsTrue(world.Player.IsPersistenceQuarantined);
            CollectionAssert.AreEqual(new[] { TrainedPerk }, world.Perks.Saves.Single());
            CollectionAssert.AreEqual(new[] { new ResearchProgressRecord(ResearchInProgress, 500) }, world.Research.Saves.Single());
            Assert.IsFalse(world.Player.TrainedPerks.IsDirty);
            Assert.IsFalse(world.Player.Research.IsDirty);
        }

        [TestMethod]
        public void UnknownInventoryOutcomeStillSavesTrainedPerksAndResearch()
        {
            using var world = new Fixture();
            world.MarkEverythingDirty();
            world.Coalesce.Failure = new DatabaseCommitOutcomeUnknownException(new Exception("connection lost"));

            Assert.ThrowsExactly<DatabaseCommitOutcomeUnknownException>(() => world.Flush.HardFlush(world.Player));

            Assert.IsTrue(world.Player.IsPersistenceQuarantined);
            CollectionAssert.AreEqual(new[] { TrainedPerk }, world.Perks.Saves.Single());
            CollectionAssert.AreEqual(new[] { new ResearchProgressRecord(ResearchInProgress, 500) }, world.Research.Saves.Single());
        }

        [TestMethod]
        public void TransientInventoryFailureKeepsInventoryDirtyAfterThePerkSetsAreSaved()
        {
            using var world = new Fixture();
            world.MarkEverythingDirty();
            world.Coalesce.Failure = new InvalidOperationException("deadlock");

            Assert.ThrowsExactly<InvalidOperationException>(() => world.Flush.HardFlush(world.Player));

            Assert.IsFalse(world.Player.IsPersistenceQuarantined);
            Assert.IsTrue(world.Player.Inventory.HasDirtyEntries, "The inventory batch must be retried.");
            Assert.AreEqual(1, world.Perks.Saves.Count);
            Assert.AreEqual(1, world.Research.Saves.Count);
            Assert.IsFalse(world.Player.TrainedPerks.IsDirty);

            world.Coalesce.Failure = null;
            world.Flush.HardFlush(world.Player);
            Assert.IsFalse(world.Player.Inventory.HasDirtyEntries);
            Assert.AreEqual(1, world.Perks.Saves.Count, "An unchanged perk set is not written again.");
        }

        [TestMethod]
        public void ResearchWaitsForAFailedTrainedPerkWrite()
        {
            using var world = new Fixture();
            world.Player.TrainedPerks.Add(TrainedPerk);
            world.Player.Research.Set(ResearchInProgress, 500);
            world.Perks.Failure = new InvalidOperationException("database unavailable");

            world.Flush.HardFlush(world.Player);

            Assert.AreEqual(0, world.Research.Saves.Count, "Research must never be stored ahead of the perk it depends on.");
            Assert.IsTrue(world.Player.TrainedPerks.IsDirty);
            Assert.IsTrue(world.Player.Research.IsDirty);

            world.Perks.Failure = null;
            world.Flush.HardFlush(world.Player);
            CollectionAssert.AreEqual(new[] { TrainedPerk }, world.Perks.Saves.Single());
            CollectionAssert.AreEqual(new[] { new ResearchProgressRecord(ResearchInProgress, 500) }, world.Research.Saves.Single());
            Assert.IsFalse(world.Player.TrainedPerks.IsDirty);
            Assert.IsFalse(world.Player.Research.IsDirty);
        }

        sealed class Fixture : IDisposable
        {
            public readonly Player Player = TestWorld.CreatePlayer(21);
            public readonly RecordingCoalesce Coalesce = new();
            public readonly PerkStore Perks = new();
            public readonly ResearchStore Research = new();
            public readonly InventoryFlushService Flush;

            public Fixture()
            {
                // Empty initialized registry; no world, DB or heartbeat is needed by this boundary.
                var manager = (PlayfieldManager)RuntimeHelpers.GetUninitializedObject(typeof(PlayfieldManager));
                typeof(PlayfieldManager).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, new Lock());
                typeof(PlayfieldManager).GetField("_playersByCharacterId", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(manager, new Dictionary<int, Player>());
                Flush = new InventoryFlushService(new Lazy<PlayfieldManager>(() => manager), Coalesce, new StubLogger(),
                    trainedPerks: Perks, research: Research);
            }

            public void MarkEverythingDirty()
            {
                Item item = TestWorld.CreateItem(instanceId: 39, persisted: false);
                Player.Inventory.MarkDirty(item, Player.Inventory.Inventory, 64);
                Player.TrainedPerks.Add(TrainedPerk);
                Player.Research.Set(ResearchInProgress, 500);
            }

            public void Dispose() => Flush.Dispose();
        }

        sealed class RecordingCoalesce : ICharacterCoalesceCommit
        {
            public Exception? Failure;

            public void Persist(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates, int characterId,
                IReadOnlyList<int> uploadedNanoIds, IReadOnlyList<ActiveNanoRecord>? activeNanos, IReadOnlyList<SkillLockRecord>? skillLocks)
            {
                if (Failure != null) throw Failure;
            }
        }

        sealed class PerkStore : ITrainedPerkRepository
        {
            public readonly List<int[]> Saves = new();
            public Exception? Failure;

            public IReadOnlyList<int> GetForCharacter(int characterId) => [];

            public void Save(int characterId, IReadOnlyList<int> perkIds)
            {
                if (Failure != null) throw Failure;
                Saves.Add(perkIds.ToArray());
            }
        }

        sealed class ResearchStore : IResearchRepository
        {
            public readonly List<ResearchProgressRecord[]> Saves = new();

            public IReadOnlyList<ResearchProgressRecord> GetForCharacter(int characterId) => [];

            public void Save(int characterId, IReadOnlyList<ResearchProgressRecord> research) => Saves.Add(research.ToArray());
        }
    }
}
