namespace AORebirth.Database.Domain.Missions
{
    using System.Data;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using AORebirth.Interfaces.Persistence.Missions;
    using Dapper;

    public sealed partial class MySqlMissionDao
    {
        /// <summary>Inserts one item row a mission transaction grants (a chest's contents, a quest's mission item).</summary>
        private static void InsertItemRow(IDbConnection connection, IDbTransaction transaction, MissionItemInstanceData item, int ownerId)
        {
            if (item == null || item.InstanceId <= 0 || item.ContainerInstance != ownerId || item.ContainerType <= 0 || item.ContainerPlacement < 0
                || item.LowId <= 0 || item.HighId <= 0 || item.Quality <= 0 || item.StackCount <= 0)
                throw new ArgumentException("Invalid owned item-instance grant plan.");
            connection.Execute("INSERT INTO item_instances (InstanceId,ContainerType,ContainerInstance,ContainerPlacement,ItemType,LowId,HighId,Quality,StackCount,Source) VALUES (@InstanceId,@ContainerType,@ContainerInstance,@ContainerPlacement,@ItemType,@LowId,@HighId,@Quality,@StackCount,@Source)", item, transaction);
        }

        /// <summary>IdentityType Weapon/Armor/Implant/Inventory/Bank/Social: the character's own pages, not a backpack or another owner.</summary>
        private static bool IsOwnedTopLevelRow(MissionItemInstanceData item, int ownerId)
            => item.ContainerInstance == ownerId && (item.ContainerType == 101 || item.ContainerType == 102
                || item.ContainerType == 103 || item.ContainerType == 104 || item.ContainerType == 105 || item.ContainerType == 115);

        /// <summary>
        /// Takes a consumed mission item out of the owner's main inventory (ContainerType 0, placement = instance), only when
        /// the row still matches exactly what the caller observed; otherwise the transaction fails and rolls back.
        /// </summary>
        private static void RetireItemRow(IDbConnection connection, IDbTransaction transaction, MissionItemInstanceData item, int ownerId)
        {
            if (item.ContainerInstance != ownerId || item.ContainerType != 104)
                throw new ArgumentException("Objective consumption requires the exact owned main-inventory row.");
            if (!IsOwnedTopLevelRow(item, ownerId))
                throw new ArgumentException("Cleanup requires the exact owned top-level inventory row.");
            if (connection.Execute("UPDATE item_instances SET ContainerType=0,ContainerPlacement=InstanceId WHERE InstanceId=@InstanceId AND ContainerType=@ContainerType AND ContainerInstance=@ContainerInstance AND ContainerPlacement=@ContainerPlacement AND ItemType=@ItemType AND LowId=@LowId AND HighId=@HighId AND Quality=@Quality AND StackCount=@StackCount AND Source=@Source", item, transaction) != 1)
                throw new InvalidOperationException("Mission item ownership/location/shape changed; the transaction rolled back.");
        }
        private sealed partial class MySqlMissionDaoTransaction : IMissionInventoryMutationTransaction
        {
            public void ApplyInventoryMutation(
                IReadOnlyList<MissionItemInstanceData> grants,
                IReadOnlyList<MissionItemInstanceData> consumed)
            {
                this.EnsureActive();
                try
                {
                    this.ApplyInventoryMutationCore(grants, consumed);
                }
                catch
                {
                    this.failed = true;
                    throw;
                }
            }

            private void ApplyInventoryMutationCore(
                IReadOnlyList<MissionItemInstanceData> grants,
                IReadOnlyList<MissionItemInstanceData> consumed)
            {
                if (grants == null || consumed == null) throw new ArgumentNullException("inventoryPlan");
                // A grant goes to the main inventory or, when that is full, the character's saved overflow window
                // (IdentityType.OverflowWindow 0x6E); an item is only ever consumed from the main inventory.
                if (grants.Any(item => item == null || (item.ContainerType != 104 && item.ContainerType != 0x6E))
                    || consumed.Any(item => item == null || item.ContainerType != 104)
                    || grants.Concat(consumed).Any(item => item.InstanceId <= 0 || item.ContainerInstance != this.CharacterId
                    || item.ContainerPlacement < 0 || item.StackCount <= 0)
                    || grants.Concat(consumed).Select(item => item.InstanceId).Distinct().Count() != grants.Count + consumed.Count)
                    throw new ArgumentException("Authored inventory mutation requires distinct exact owned main-inventory or overflow rows.");

                // Runtime rejects bag use, and the durable boundary also rejects any surviving
                // child row (including unhydrated content). 0xC749 is IdentityType.Container.
                // The owner transaction's repeatable-read range lock prevents orphaning races.
                foreach (var item in consumed)
                {
                    if (this.connection.Query<int>(
                        "SELECT InstanceId FROM item_instances WHERE ContainerType=0xC749 AND ContainerInstance=@InstanceId LIMIT 1 FOR UPDATE",
                        new { item.InstanceId }, this.transaction).Any())
                        throw new InvalidOperationException("Authored item retirement would orphan container contents.");
                    RetireItemRow(this.connection, this.transaction, item, this.CharacterId);
                }
                foreach (var item in grants) InsertItemRow(this.connection, this.transaction, item, this.CharacterId);
            }
        }
    }
}
