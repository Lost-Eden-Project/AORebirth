namespace AORebirth.Database.Domain.Missions
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using AORebirth.Interfaces.Persistence.Missions;
    using Dapper;

    public sealed partial class MySqlMissionDao
    {
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
                    || request.NowUtcTicks <= 0 || request.Chests == null)
                    throw new ArgumentException("A chest population requires the exact accepted mission run.");
                CharacterQuestCompletionSnapshotData quest = this.ReadCharacterQuest(request.QuestId);
                if (quest == null || quest.CharacterId != this.CharacterId || quest.Source != 1 || quest.OwnerType != 0
                    || quest.OwnerId != this.CharacterId || quest.AssignedAtUtcTicks != request.AssignedAtUtcTicks)
                    throw new InvalidOperationException("The accepted mission ownership or run changed.");
                if (quest.State != 1 || quest.ExpiresAtUtcTicks <= request.NowUtcTicks)
                    throw new InvalidOperationException("An ended or expired mission cannot initialize chest contents.");

                var key = new MissionKeyData(this.CharacterId, request.QuestId);
                string flagKey = MissionChestPopulationFlags.Key(request.AssignedAtUtcTicks);
                if (this.GetFlag(key, flagKey) != null)
                    return false;

                var containers = new HashSet<int>();
                var instances = new HashSet<int>();
                foreach (MissionChestInitialData chest in request.Chests)
                {
                    if (chest == null || chest.ContainerInstance <= 0 || !containers.Add(chest.ContainerInstance)
                        || chest.Items == null || chest.Items.Count > MissionChestPopulationFlags.MaxItemsPerChest)
                        throw new ArgumentException("Each chest requires a distinct container and at most " + MissionChestPopulationFlags.MaxItemsPerChest + " items.");
                    if (this.connection.Query<int>("SELECT InstanceId FROM item_instances WHERE InstanceId=@Id FOR UPDATE",
                        new { Id = chest.ContainerInstance }, this.transaction).Any())
                        throw new InvalidOperationException("A chest container identity is already allocated to an item instance.");
                    for (int slot = 0; slot < chest.Items.Count; slot++)
                    {
                        MissionItemInstanceData item = chest.Items[slot];
                        if (item == null || item.InstanceId <= 0 || !instances.Add(item.InstanceId)
                            || item.ContainerType != 0xC749 || item.ContainerInstance != chest.ContainerInstance
                            || item.ContainerPlacement != slot || item.ItemType <= 0 || item.LowId <= 0 || item.HighId <= 0
                            || item.Quality <= 0 || item.StackCount <= 0)
                            throw new ArgumentException("Chest items require distinct identities and exact contiguous container slots.");
                    }
                }
                if (containers.Overlaps(instances))
                    throw new ArgumentException("A chest container cannot share an identity with its initial item population.");

                foreach (MissionChestInitialData chest in request.Chests)
                    foreach (MissionItemInstanceData item in chest.Items)
                        InsertItemRow(this.connection, this.transaction, item, chest.ContainerInstance);
                this.SaveFlag(key, new MissionFlagData
                {
                    CharacterId = key.CharacterId, QuestId = key.QuestId, FlagKey = flagKey,
                    Value = string.Join(",", request.Chests.Select(chest => chest.ContainerInstance.ToString(CultureInfo.InvariantCulture))),
                    CreatedAtUtcTicks = request.NowUtcTicks, UpdatedAtUtcTicks = request.NowUtcTicks
                });
                return true;
            }
        }
    }
}
