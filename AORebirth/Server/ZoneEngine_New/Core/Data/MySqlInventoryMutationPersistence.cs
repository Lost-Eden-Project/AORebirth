namespace ZoneEngine_New.Core.Data
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using AORebirth.Interfaces.Persistence.Characters;
    using ZoneEngine_New.Core.Logging;
    using static SharedCharacterPersistence;
    public sealed class MySqlInventoryMutationPersistence : IInventoryMutationPersistence
    {
        readonly ICharacterPersistenceDao _persistence;
        public MySqlInventoryMutationPersistence(MySqlInventoryRepository inventory, MySqlUploadedNanoRepository nanos)
        {
            ArgumentNullException.ThrowIfNull(nanos);
            _persistence = (inventory ?? throw new ArgumentNullException(nameof(inventory))).Persistence;
        }
        public void Persist(InventoryMutationBatch batch) => Run(() => _persistence.CommitInventoryMutation(new CharacterInventoryMutationData
        {
            CharacterId = batch.CharacterId, Inserts = batch.Inserts.Select(Map).ToArray(), Locations = batch.Locations.Select(Map).ToArray(),
            ExpectedSources = batch.ExpectedSources.Select(Map).ToArray(),
            Stacks = batch.Stacks.Select(v => new ItemStackData { InstanceId = v.InstanceId, ExpectedCount = v.ExpectedCount, FinalCount = v.FinalCount }).ToArray(),
            Qualities = batch.Qualities.Select(v => new ItemQualityData { InstanceId = v.InstanceId, ExpectedQuality = v.ExpectedQuality, FinalQuality = v.FinalQuality }).ToArray(),
            UploadedNanoIds = batch.UploadedNanoIds.ToArray(), FinalStats = batch.FinalStats.Select(Map).ToArray(), EmptyContainersBeforeRetire = batch.EmptyContainersBeforeRetire.ToArray(),
            PlayerShop = batch.PlayerShop is PlayerShopWrite shop ? new PlayerShopMutationData
            {
                Kind = (PlayerShopMutationKind)(int)shop.Kind, ShopId = shop.ShopId, OwnerId = shop.OwnerId, PlayfieldId = shop.PlayfieldId,
                TerminalInstance = shop.TerminalInstance, Name = shop.Name, ItemInstanceId = shop.ItemInstanceId, Price = shop.Price,
                ExpectedPrice = shop.ExpectedPrice, Amount = shop.Amount, MaxItems = shop.MaxItems, MaxBalance = shop.MaxBalance
            } : null
        }));
    }
}
