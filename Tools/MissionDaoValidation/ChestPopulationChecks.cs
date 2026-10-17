namespace AORebirth.Tools.MissionDaoValidation
{
    using System;
    using System.Linq;
    using System.Threading.Tasks;
    using AORebirth.Database.Domain.Characters;
    using AORebirth.Database.Domain.Missions;
    using AORebirth.Database.Domain.Quests;
    using AORebirth.Interfaces.Persistence.Characters;
    using AORebirth.Interfaces.Persistence.Missions;
    using MySqlConnector;

    internal static partial class Program
    {
        private static void ValidateChestPopulation(MySqlMissionDao dao, string connectionString)
        {
            var quests = new MySqlCharacterQuestDao(() => new MySqlConnection(connectionString));
            SeedCompletionQuest(quests, "chest.persist");
            var request = ChestRequest("chest.persist", 930000, 930001, 3);
            Require(InitializeChests(dao, request), "chest-first-materialization-commits");
            Require(!InitializeChests(new MySqlMissionDao(() => Open(connectionString)), ChestRequest("chest.persist", 930010, 930011)),
                "chest-restart-reloads-sealed-population");
            Require(Scalar(connectionString, "SELECT COUNT(*) FROM item_instances WHERE InstanceId=930011") == 0,
                "chest-retry-does-not-generate-new-contents");
            Require(dao.Execute(CompletionOwner, tx => tx.GetFlag(new MissionKeyData(CompletionOwner, "chest.persist"), MissionChestPopulationFlags.Seal(CompletionAssigned))).Value == "1",
                "chest-count-sealed-with-snapshot");

            foreach (string mismatch in new[] { "run", "layout", "owner" })
            {
                var wrong = ChestRequest("chest.persist", 930020, 930021);
                if (mismatch == "run") wrong.AssignedAtUtcTicks++;
                if (mismatch == "layout") wrong.ExpectedAcgBuildingGeneratorJson = "{}";
                Expect<InvalidOperationException>(() => dao.Execute(mismatch == "owner" ? 101 : CompletionOwner,
                    tx => ((IMissionChestPopulationTransaction)tx).TryInitializeChestPopulation(wrong)), "chest-exact-" + mismatch + "-required");
            }
            SeedCompletionQuest(quests, "chest.ended", state: 5);
            Expect<InvalidOperationException>(() => InitializeChests(dao, ChestRequest("chest.ended", 930030, 930031)),
                "chest-ended-mission-cannot-generate");
            SeedCompletionQuest(quests, "chest.expired");
            var expired = ChestRequest("chest.expired", 930040, 930041); expired.NowUtcTicks = 3000;
            Expect<InvalidOperationException>(() => InitializeChests(dao, expired), "chest-expired-mission-cannot-generate");

            SeedCompletionQuest(quests, "chest.rollback");
            var rollback = ChestRequest("chest.rollback", 930050, 930051, 2);
            rollback.Chests[0].Items[1].InstanceId = 930001; // An existing row fails after the first new insert.
            Expect<MySqlException>(() => InitializeChests(dao, rollback), "chest-item-insert-failure-rolls-back-population");
            Require(Scalar(connectionString, "SELECT COUNT(*) FROM item_instances WHERE InstanceId=930051") == 0
                && dao.Execute(CompletionOwner, tx => tx.GetFlag(new MissionKeyData(CompletionOwner, "chest.rollback"), MissionChestPopulationFlags.Seal(CompletionAssigned))) == null,
                "chest-failed-population-has-no-items-or-seal");
            var oversized = ChestRequest("chest.rollback", 930060, 930061);
            oversized.Chests[0].SnapshotJson = new string('x', 1025);
            Expect<ArgumentException>(() => InitializeChests(dao, oversized), "chest-snapshot-respects-existing-flag-capacity");
            var tooMany = ChestRequest("chest.rollback", 930070, 930071, 4);
            Expect<ArgumentException>(() => InitializeChests(dao, tooMany), "chest-never-persists-more-than-three-items");
            var mismatchedContainer = ChestRequest("chest.rollback", 930080, 930081);
            mismatchedContainer.Chests[0].SnapshotJson = "{\"ContainerInstance\":123}";
            Expect<ArgumentException>(() => InitializeChests(dao, mismatchedContainer), "chest-snapshot-container-must-match-durable-items");
            Expect<InvalidOperationException>(() => dao.Execute(CompletionOwner, tx =>
            {
                tx.SaveFlag(new MissionKeyData(CompletionOwner, "chest.rollback"), new MissionFlagData
                {
                    CharacterId = CompletionOwner, QuestId = "chest.rollback", FlagKey = MissionChestPopulationFlags.Chest(CompletionAssigned, 99),
                    Value = "{}", CreatedAtUtcTicks = 2000, UpdatedAtUtcTicks = 2000
                });
                return ((IMissionChestPopulationTransaction)tx).TryInitializeChestPopulation(ChestRequest("chest.rollback", 930090, 930091));
            }), "chest-unsealed-partial-state-cannot-reroll");
            Require(Scalar(connectionString, "SELECT COUNT(*) FROM missionflags WHERE QuestId='chest.rollback'") == 0,
                "chest-unsealed-detection-rolls-back-whole-callback");

            SeedCompletionQuest(quests, "chest.concurrent");
            var initializations = new bool[2];
            Parallel.For(0, 2, i => initializations[i] = InitializeChests(dao, ChestRequest("chest.concurrent", 931000 + i * 10, 931001 + i * 10)));
            Require(initializations.Count(value => value) == 1
                && Scalar(connectionString, "SELECT COUNT(*) FROM item_instances WHERE InstanceId IN (931001,931011)") == 1,
                "chest-concurrent-first-materialization-one-population");
            ValidateUncertainChestInitialization(dao, quests, connectionString);
            ValidateChestTransfers(quests, connectionString);
        }

        private static void ValidateUncertainChestInitialization(MySqlMissionDao dao, MySqlCharacterQuestDao quests, string connectionString)
        {
            foreach (string point in new[] { "commit-before", "commit-after" })
            {
                string questId = "chest." + point;
                int container = point == "commit-before" ? 932000 : 932100;
                SeedCompletionQuest(quests, questId);
                var transport = new FaultConnection(connectionString) { FailurePoint = point };
                Expect<MissionCommitOutcomeUnknownException>(() => InitializeChests(new MySqlMissionDao(() => transport),
                    ChestRequest(questId, container, container + 1)), "chest-uncertain-" + point + "-reported");
                bool initialized = InitializeChests(dao, ChestRequest(questId, container + 10, container + 11));
                Require(initialized == (point == "commit-before")
                    && Scalar(connectionString, "SELECT COUNT(*) FROM item_instances WHERE InstanceId IN (" + (container + 1) + "," + (container + 11) + ")") == 1,
                    "chest-uncertain-" + point + "-rereads-seal-without-duplicate-items");
            }
        }

        private static void ValidateChestTransfers(MySqlCharacterQuestDao quests, string connectionString)
        {
            var items = new MySqlCharacterPersistenceDao(() => Open(connectionString));
            PersistedItemData[] original = items.LoadContainerItems(930000).OrderBy(row => row.ContainerPlacement).ToArray();
            Require(original.Length == 3, "chest-restart-loads-three-original-instances");
            CharacterInventoryMutationData first = ChestTransfer(original[0], 101, 64);
            items.CommitInventoryMutation(first);
            Require(items.LoadContainerItems(930000).Count == 2 && items.LoadCarriedItems(101).Any(row => row.InstanceId == original[0].InstanceId),
                "chest-nonowner-player-loots-original-instance-and-preserves-remainder");
            Expect<InvalidOperationException>(() => items.CommitInventoryMutation(ChestTransfer(original[0], 102, 64)),
                "chest-duplicate-stale-request-cannot-move-looted-item");
            Require(items.LoadCarriedItems(101).Any(row => row.InstanceId == original[0].InstanceId), "chest-stale-request-retains-original-player-owner");
            Expect<MySqlException>(() => items.CommitInventoryMutation(ChestTransfer(original[1], 101, 64)),
                "chest-occupied-target-rolls-back-source-transfer");
            Require(items.LoadContainerItems(930000).Any(row => row.InstanceId == original[1].InstanceId && row.ContainerPlacement == 1),
                "chest-failed-transfer-item-remains-in-original-slot");

            var successes = new bool[2];
            Parallel.For(0, 2, i =>
            {
                try { items.CommitInventoryMutation(ChestTransfer(original[1], 101 + i, 65)); successes[i] = true; }
                catch (InvalidOperationException) { }
            });
            Require(successes.Count(value => value) == 1 && items.LoadContainerItems(930000).Count == 1,
                "chest-concurrent-two-player-loot-one-winner");

            var transport = new FaultConnection(connectionString) { FailurePoint = "commit-after" };
            Expect<CharacterPersistenceCommitOutcomeUnknownException>(() => new MySqlCharacterPersistenceDao(() => transport)
                .CommitInventoryMutation(ChestTransfer(original[2], 101, 66)), "chest-transfer-lost-ack-after-real-commit");
            Require(items.LoadContainerItems(930000).Count == 0
                && items.LoadCarriedItems(101).Any(row => row.InstanceId == original[2].InstanceId),
                "chest-transfer-restart-load-reflects-committed-ownership");
            Expect<InvalidOperationException>(() => items.CommitInventoryMutation(ChestTransfer(original[2], 102, 66)),
                "chest-lost-ack-stale-retry-cannot-duplicate-or-steal");

            // Purge only unlooted container rows; original chest items already carried by players survive.
            Require(quests.PurgeEndedGeneratedQuests(4000, 50) > 0, "chest-ended-retention-purge-runs");
            Require(items.LoadCarriedItems(101).Any(row => row.InstanceId == original[0].InstanceId)
                && items.LoadCarriedItems(101).Any(row => row.InstanceId == original[2].InstanceId),
                "chest-cleanup-retains-transferred-player-items");
            Require(Scalar(connectionString, "SELECT COUNT(*) FROM item_instances WHERE ContainerType=0xC749 AND ContainerInstance IN (931000,931010,932010,932100)") == 0
                && Scalar(connectionString, "SELECT COUNT(*) FROM missionflags WHERE QuestId LIKE 'chest.%' AND FlagKey LIKE 'mission-chest:1:%'") == 0,
                "chest-cleanup-removes-only-unlooted-contents-and-owned-chest-flags");
        }

        private static bool InitializeChests(MySqlMissionDao dao, MissionChestPopulationRequest request)
            => dao.Execute(CompletionOwner, tx => ((IMissionChestPopulationTransaction)tx).TryInitializeChestPopulation(request));

        private static MissionChestPopulationRequest ChestRequest(string quest, int container, int firstItem, int count = 1)
            => new MissionChestPopulationRequest
            {
                QuestId = quest, AssignedAtUtcTicks = CompletionAssigned, ExpectedAcgBuildingGeneratorJson = CompletionBinding, NowUtcTicks = 2000,
                Chests = new[] { new MissionChestInitialData { ContainerInstance = container,
                    SnapshotJson = "{\"ContainerInstance\":" + container + ",\"RoomIndex\":1,\"X\":1.5,\"Y\":0,\"Z\":2.5,\"YawDegrees\":90}",
                    Items = Enumerable.Range(0, count).Select(slot => new MissionItemInstanceData { InstanceId = firstItem + slot,
                        ContainerType = 0xC749, ContainerInstance = container, ContainerPlacement = slot, ItemType = 0xC76D,
                        LowId = 10, HighId = 10, Quality = 50, StackCount = 1, Source = 1 }).ToArray() } }
            };

        private static CharacterInventoryMutationData ChestTransfer(PersistedItemData source, int looter, int slot)
            => new CharacterInventoryMutationData { CharacterId = looter, ExpectedSources = new[] { source },
                Locations = new[] { new ItemLocationData { InstanceId = source.InstanceId, ContainerType = 104,
                    ContainerInstance = looter, ContainerPlacement = slot } } };
    }
}
