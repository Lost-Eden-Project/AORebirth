namespace AORebirth.Database.Domain.Missions
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Runtime.Serialization;
    using System.Runtime.Serialization.Json;
    using System.Text;
    using AORebirth.Interfaces.Persistence.Missions;
    using Dapper;

    public sealed partial class MySqlMissionDao
    {
        [DataContract]
        private sealed class ChestSnapshotIdentity
        {
            [DataMember(IsRequired = true)]
            public int ContainerInstance { get; set; }
        }

        internal static int ReadChestSnapshotContainer(string snapshotJson)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(snapshotJson ?? string.Empty)))
            {
                var identity = (ChestSnapshotIdentity)new DataContractJsonSerializer(typeof(ChestSnapshotIdentity)).ReadObject(stream);
                if (identity == null || identity.ContainerInstance <= 0)
                    throw new InvalidOperationException("A durable chest snapshot has no valid container identity.");
                return identity.ContainerInstance;
            }
        }

        private sealed partial class MySqlMissionDaoTransaction : IMissionChestPopulationTransaction
        {
            public bool TryInitializeChestPopulation(MissionChestPopulationRequest request)
            {
                this.EnsureActive();
                try { return this.InitializeChestPopulation(request); }
                catch { this.failed = true; throw; }
            }

            private bool InitializeChestPopulation(MissionChestPopulationRequest request)
            {
                if (request == null || string.IsNullOrWhiteSpace(request.QuestId) || request.AssignedAtUtcTicks <= 0
                    || string.IsNullOrEmpty(request.ExpectedAcgBuildingGeneratorJson) || request.NowUtcTicks <= 0 || request.Chests == null)
                    throw new ArgumentException("A chest population requires the exact accepted mission run and frozen layout.");
                CharacterQuestCompletionSnapshotData quest = this.ReadCharacterQuest(request.QuestId);
                if (quest == null || quest.CharacterId != this.CharacterId || quest.Source != 1 || quest.OwnerType != 0
                    || quest.OwnerId != this.CharacterId || quest.AssignedAtUtcTicks != request.AssignedAtUtcTicks
                    || !string.Equals(quest.AcgBuildingGeneratorJson, request.ExpectedAcgBuildingGeneratorJson, StringComparison.Ordinal))
                    throw new InvalidOperationException("The accepted mission ownership, run or frozen layout changed.");

                var key = new MissionKeyData(this.CharacterId, request.QuestId);
                string sealKey = MissionChestPopulationFlags.Seal(request.AssignedAtUtcTicks);
                if (this.GetFlag(key, sealKey) != null)
                    return false;
                if (this.connection.Query<int>("SELECT Id FROM missionflags WHERE CharacterId=@Owner AND QuestId=@Quest "
                    + "AND FlagKey LIKE @Prefix LIMIT 1 FOR UPDATE", new { Owner = this.CharacterId, Quest = request.QuestId,
                        Prefix = MissionChestPopulationFlags.Prefix(request.AssignedAtUtcTicks) + "%" }, this.transaction).Any())
                    throw new InvalidOperationException("An unsealed chest population must be reconciled before initialization.");
                if (quest.State != 1 || quest.ExpiresAtUtcTicks <= request.NowUtcTicks)
                    throw new InvalidOperationException("An ended or expired mission cannot initialize chest contents.");

                var containers = new HashSet<int>();
                var instances = new HashSet<int>();
                foreach (MissionChestInitialData chest in request.Chests)
                {
                    if (chest == null || chest.ContainerInstance <= 0 || !containers.Add(chest.ContainerInstance)
                        || string.IsNullOrWhiteSpace(chest.SnapshotJson) || chest.SnapshotJson.Length > 1024
                        || chest.Items == null || chest.Items.Count < 1 || chest.Items.Count > 3)
                        throw new ArgumentException("Each chest requires a distinct container, bounded snapshot and one to three items.");
                    if (ReadChestSnapshotContainer(chest.SnapshotJson) != chest.ContainerInstance)
                        throw new ArgumentException("The frozen chest snapshot and item container identities differ.");
                    if (this.connection.Query<int>("SELECT InstanceId FROM item_instances WHERE InstanceId=@Id FOR UPDATE",
                        new { Id = chest.ContainerInstance }, this.transaction).Any())
                        throw new InvalidOperationException("A chest container identity is already allocated to an item instance.");
                    for (int slot = 0; slot < chest.Items.Count; slot++)
                    {
                        MissionItemInstanceData item = chest.Items[slot];
                        if (item == null || item.InstanceId <= 0 || !instances.Add(item.InstanceId)
                            || item.ContainerType != 0xC749 || item.ContainerInstance != chest.ContainerInstance
                            || item.ContainerPlacement != slot || item.ItemType <= 0 || item.LowId <= 0 || item.HighId <= 0
                            || item.Quality <= 0 || item.StackCount <= 0 || item.Source < 0 || item.Source > byte.MaxValue)
                            throw new ArgumentException("Chest items require distinct identities and exact contiguous container slots.");
                    }
                }
                if (containers.Overlaps(instances))
                    throw new ArgumentException("A chest container cannot share an identity with its initial item population.");

                for (int index = 0; index < request.Chests.Count; index++)
                {
                    MissionChestInitialData chest = request.Chests[index];
                    string flagKey = MissionChestPopulationFlags.Chest(request.AssignedAtUtcTicks, index);
                    if (this.GetFlag(key, flagKey) != null)
                        throw new InvalidOperationException("An unsealed chest population must be reconciled before initialization.");
                    foreach (MissionItemInstanceData item in chest.Items)
                        InsertGeneratedItem(this.connection, this.transaction, item, chest.ContainerInstance);
                    this.SaveChestFlag(key, flagKey, chest.SnapshotJson, request.NowUtcTicks);
                }
                this.SaveChestFlag(key, sealKey, request.Chests.Count.ToString(CultureInfo.InvariantCulture), request.NowUtcTicks);
                return true;
            }

            private void SaveChestFlag(MissionKeyData key, string flagKey, string value, long now)
                => this.SaveFlag(key, new MissionFlagData
                {
                    CharacterId = key.CharacterId, QuestId = key.QuestId, FlagKey = flagKey, Value = value,
                    CreatedAtUtcTicks = now, UpdatedAtUtcTicks = now
                });
        }
    }
}
