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
            ValidateAcceptedChestLifecycle(dao, quests, connectionString);
        }

        private static void ValidateAcceptedChestLifecycle(MySqlMissionDao dao, MySqlCharacterQuestDao quests, string connectionString)
        {
            const string questId = "chest.accepted.lifecycle";
            int offerId = dao.ReserveIdentities("offer", 1);
            GeneratedMissionOfferBatch batch = ReconciledBatch(offerId, questId);
            batch.OwnerId = CompletionOwner;
            foreach (GeneratedMissionOffer offer in batch.Offers)
                offer.OwnerId = CompletionOwner;
            Require(dao.PublishOffers(batch).Status == GeneratedMissionResultStatus.Applied,
                "chest-lifecycle-generated-offer-published");
            Require(dao.PublishOffers(batch).Status == GeneratedMissionResultStatus.AlreadyApplied,
                "chest-lifecycle-generated-offer-replay-does-not-charge-again");

            var generated = new GeneratedQuestRow { QuestId = questId, OwnerType = 0, OwnerId = CompletionOwner,
                DefinitionJson = CompletionDefinition, AcgBuildingGeneratorJson = CompletionBinding,
                CreatedAtUtcTicks = CompletionAssigned, UpdatedAtUtcTicks = CompletionAssigned, ExpiresAtUtcTicks = 3000 };
            var key = ChestLifecycleKey(940100, CompletionOwner);
            Require(quests.TryAcceptOfferQuest(CompletionOwner, 0xDAC3, offerId, generated, CompletionRow(questId), key, 1500),
                "chest-lifecycle-active-acceptance-atomically-creates-quest-and-key");
            Require(!quests.TryAcceptOfferQuest(CompletionOwner, 0xDAC3, offerId, generated, CompletionRow(questId), key, 1500)
                && quests.LoadKeyQuests(new[] { key.InstanceId })[key.InstanceId] == questId
                && quests.LoadForCharacter(CompletionOwner).Count(row => row.QuestId == questId) == 1,
                "chest-lifecycle-duplicate-acceptance-retains-one-quest-and-key");
            Require(quests.TryAddDuplicateKey(key.InstanceId, ChestLifecycleKey(940101, 102), 2, 2000) == questId,
                "chest-lifecycle-second-player-receives-the-same-dungeon-key-link");
            Require(quests.TryAddDuplicateKey(key.InstanceId, ChestLifecycleKey(940102, 101), 2, 2000) == null
                && Scalar(connectionString, "SELECT COUNT(*) FROM item_instances WHERE InstanceId=940102") == 0,
                "chest-lifecycle-key-limit-refusal-grants-no-item");

            Require(InitializeChests(dao, ChestRequest(questId, 940000, 940001, 3)),
                "chest-lifecycle-accepted-run-populates-once");
            string snapshot = dao.Execute(CompletionOwner, tx => tx.GetFlag(new MissionKeyData(CompletionOwner, questId),
                MissionChestPopulationFlags.Chest(CompletionAssigned, 0))).Value;
            var reloadedQuests = new MySqlCharacterQuestDao(() => new MySqlConnection(connectionString));
            var reloadedMissions = new MySqlMissionDao(() => Open(connectionString));
            var reloadedLinks = reloadedQuests.LoadKeyQuests(new[] { 940100, 940101 });
            Require(reloadedQuests.LoadGeneratedForCharacter(CompletionOwner).Single(row => row.QuestId == questId).AcgBuildingGeneratorJson == CompletionBinding
                && reloadedLinks.Count == 2 && reloadedLinks.Values.All(value => value == questId),
                "chest-lifecycle-fresh-dao-load-retains-frozen-layout-and-both-key-links");
            var reroll = ChestRequest(questId, 940010, 940011, 1);
            reroll.Chests[0].SnapshotJson = "{\"ContainerInstance\":940010,\"RoomIndex\":99,\"X\":99,\"Y\":80,\"Z\":99,\"YawDegrees\":270}";
            Require(!InitializeChests(reloadedMissions, reroll)
                && reloadedMissions.Execute(CompletionOwner, tx => tx.GetFlag(new MissionKeyData(CompletionOwner, questId),
                    MissionChestPopulationFlags.Chest(CompletionAssigned, 0))).Value == snapshot
                && Scalar(connectionString, "SELECT COUNT(*) FROM item_instances WHERE InstanceId=940011") == 0,
                "chest-lifecycle-reconstruction-preserves-snapshot-and-never-rerolls");

            var inventory = new MySqlCharacterPersistenceDao(() => Open(connectionString));
            PersistedItemData[] contents = inventory.LoadContainerItems(940000).OrderBy(row => row.ContainerPlacement).ToArray();
            inventory.CommitInventoryMutation(ChestTransfer(contents[0], CompletionOwner, 89));
            var afterReconnect = new MySqlCharacterPersistenceDao(() => Open(connectionString));
            PersistedItemData carried = afterReconnect.LoadCarriedItems(CompletionOwner).Single(row => row.InstanceId == contents[0].InstanceId);
            Require(carried.ContainerPlacement == 89 && carried.Quality == contents[0].Quality
                && carried.LowId == contents[0].LowId && carried.HighId == contents[0].HighId
                && carried.StackCount == contents[0].StackCount && carried.Source == contents[0].Source
                && afterReconnect.LoadContainerItems(940000).Select(row => row.InstanceId).OrderBy(id => id).SequenceEqual(new[] { 940002, 940003 }),
                "chest-lifecycle-reconnect-load-preserves-player-item-and-partial-chest");
            afterReconnect.CommitInventoryMutation(ChestTransfer(contents[1], 102, 89));
            Expect<InvalidOperationException>(() => afterReconnect.CommitInventoryMutation(ChestTransfer(contents[1], CompletionOwner, 88)),
                "chest-lifecycle-second-player-loot-cannot-be-stolen-by-stale-first-player");
            Require(new MySqlCharacterPersistenceDao(() => Open(connectionString)).LoadCarriedItems(102).Any(row => row.InstanceId == 940002)
                && inventory.LoadContainerItems(940000).Single().InstanceId == 940003,
                "chest-lifecycle-second-player-and-remaining-contents-reload-independently");
            Require(quests.PurgeEndedGeneratedQuests(4000, 50) == 0 && quests.LoadGenerated(questId) != null,
                "chest-lifecycle-linked-keys-prevent-premature-dungeon-purge");
            Require(quests.EndQuestKeys(questId).Count == 2 && quests.LoadKeyQuests(new[] { 940100, 940101 }).Count == 0,
                "chest-lifecycle-end-retires-both-keys-and-links");
            Require(quests.PurgeEndedGeneratedQuests(4000, 50) == 1 && quests.LoadGenerated(questId) == null
                && inventory.LoadContainerItems(940000).Count == 0,
                "chest-lifecycle-purge-removes-unlooted-contents-and-generated-dungeon");
            Require(inventory.LoadCarriedItems(CompletionOwner).Any(row => row.InstanceId == 940001)
                && inventory.LoadCarriedItems(102).Any(row => row.InstanceId == 940002)
                && Scalar(connectionString, "SELECT COUNT(*) FROM item_instances WHERE InstanceId IN (940100,940101) AND ContainerType=0") == 2
                && Scalar(connectionString, "SELECT COUNT(*) FROM missionflags WHERE QuestId='chest.accepted.lifecycle'") == 0,
                "chest-lifecycle-cleanup-retains-player-loot-and-retired-key-history");
        }

        private static QuestDungeonKeyItemRow ChestLifecycleKey(int instance, int owner)
            => new QuestDungeonKeyItemRow { InstanceId = instance, ContainerType = 104, ContainerInstance = owner,
                ContainerPlacement = 90, ItemType = 0xC76D, LowId = 10, HighId = 10, Quality = 1, Source = 0,
                CreatedAtUtcTicks = CompletionAssigned };

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
