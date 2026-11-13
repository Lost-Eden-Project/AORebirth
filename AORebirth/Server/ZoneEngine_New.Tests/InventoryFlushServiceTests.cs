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

        [TestMethod]
        public void PerksLoadedShortNeverEraseWhatStorageHolds()
        {
            // The login that dropped trained perks and research: nothing loaded, then one perk trained. The save names
            // that perk and nothing to remove, so the stored research stays.
            using var world = new Fixture();
            world.Player.TrainedPerks.Restore([]);
            world.Player.TrainedPerks.Add(TrainedPerk);

            world.Flush.HardFlush(world.Player);

            TrainedPerkChanges saved = world.Perks.Changes.Single();
            CollectionAssert.AreEqual(new[] { TrainedPerk }, saved.Trained.ToArray());
            Assert.AreEqual(0, saved.Removed.Count);
            Assert.IsFalse(saved.ReplaceAll);
        }

        [TestMethod]
        public void OnlyAnUntrainedPerkIsTakenOutOfStorageAndAFailedWriteKeepsIt()
        {
            using var world = new Fixture();
            world.Player.TrainedPerks.Restore([TrainedPerk, 1002]);
            Assert.IsTrue(world.Player.TrainedPerks.Remove(1002));
            world.Perks.Failure = new InvalidOperationException("database unavailable");

            world.Flush.HardFlush(world.Player);
            Assert.IsTrue(world.Player.TrainedPerks.IsDirty);

            world.Perks.Failure = null;
            world.Flush.HardFlush(world.Player);
            TrainedPerkChanges saved = world.Perks.Changes.Single();
            CollectionAssert.AreEqual(new[] { TrainedPerk }, saved.Trained.ToArray());
            CollectionAssert.AreEqual(new[] { 1002 }, saved.Removed.ToArray());
            Assert.IsFalse(saved.ReplaceAll);

            // Trained again before the next save: it is no longer a removal.
            world.Player.TrainedPerks.Remove(TrainedPerk);
            world.Player.TrainedPerks.Add(TrainedPerk);
            world.Flush.HardFlush(world.Player);
            Assert.AreEqual(0, world.Perks.Changes.Last().Removed.Count);
        }

        [TestMethod]
        public void OnlyAGmReplaceMakesStorageExactlyTheSet()
        {
            using var world = new Fixture();
            world.Player.TrainedPerks.Restore([TrainedPerk]);
            world.Player.TrainedPerks.Replace([1002]);
            world.Player.Research.Set(ResearchInProgress, 500);
            world.Player.Research.Clear();

            world.Flush.HardFlush(world.Player);

            Assert.IsTrue(world.Perks.Changes.Single().ReplaceAll);
            Assert.IsTrue(world.Research.Changes.Single().ReplaceAll);
        }

        [TestMethod]
        public void ResearchSavesItsRowsAndRemovesOnlyAFinishedTier()
        {
            using var world = new Fixture();
            world.Player.Research.Restore([]);
            world.Player.Research.Set(ResearchInProgress, 500);
            world.Flush.HardFlush(world.Player);
            ResearchChanges first = world.Research.Changes.Single();
            Assert.AreEqual(0, first.Removed.Count);
            Assert.IsFalse(first.ReplaceAll);

            world.Player.Research.Set(ResearchInProgress, 0);
            world.Flush.HardFlush(world.Player);
            ResearchChanges finished = world.Research.Changes.Last();
            Assert.AreEqual(0, finished.Rows.Count);
            CollectionAssert.AreEqual(new[] { ResearchInProgress }, finished.Removed.ToArray());
        }

        [TestMethod]
        public void ALoginMovedOutOfAGoneDungeonKeepsEverythingItLoaded()
        {
            var loaded = new ZoneEngine_New.Core.Characters.CharacterHydrationResult
            {
                Character = new CharacterRecord { Id = 21, Name = "Researcher", Playfield = 24000001, X = 1, Y = 2, Z = 3, HeadingW = 1, AccountFlags = 4, GmLevel = 2 },
                Stats = [new StatRecord { StatId = 54, StatValue = 37 }],
                UploadedNanoIds = [5],
                ActiveNanos = [new ActiveNanoRecord()],
                SkillLocks = [new SkillLockRecord()],
                TrainedPerkIds = [TrainedPerk, 2790],
                PerkLocks = [new SkillLockRecord()],
                Research = [new ResearchProgressRecord(ResearchInProgress, 500)],
                NanoLineLocks = [new SkillLockRecord()],
                PetNames = [new PetNameRecord(1, "Rex")],
                Organization = new OrganizationRecord(7, default, "Org", 21, "Researcher", 0, "", "", "")
            };

            var moved = ZoneEngine_New.Core.MessageHandlers.ZoneLoginHandler.WithLoginPosition(
                loaded, 4364, new AORebirth.Core.Vector.Vector3(64.5f, 101.1f, 181.8f));

            Assert.AreEqual(4364, moved.Character.Playfield);
            Assert.AreEqual(64.5f, moved.Character.X);
            // Everything but the place: compare with the place put back.
            Assert.AreEqual(loaded, moved with { Character = loaded.Character });
            Assert.AreEqual(loaded.Character, moved.Character with { Playfield = 24000001, X = 1, Y = 2, Z = 3 });
            Assert.AreSame(loaded.TrainedPerkIds, moved.TrainedPerkIds);
            Assert.AreSame(loaded.Research, moved.Research);
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

            public readonly List<TrainedPerkChanges> Changes = new();

            public void Save(int characterId, TrainedPerkChanges changes)
            {
                if (Failure != null) throw Failure;
                Changes.Add(changes);
                Saves.Add(changes.Trained.ToArray());
            }
        }

        sealed class ResearchStore : IResearchRepository
        {
            public readonly List<ResearchProgressRecord[]> Saves = new();

            public IReadOnlyList<ResearchProgressRecord> GetForCharacter(int characterId) => [];

            public readonly List<ResearchChanges> Changes = new();

            public void Save(int characterId, ResearchChanges changes)
            {
                Changes.Add(changes);
                Saves.Add(changes.Rows.ToArray());
            }
        }
    }
}
