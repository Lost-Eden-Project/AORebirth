namespace AORebirth.Database.Domain.Characters
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Linq;
#if AOREBIRTH_ZONE_NEW
    using LostEden.Protocol.GameData;
#else
    using SmokeLounge.AOtomation.Messaging.GameData;
#endif
    using AORebirth.Interfaces.Persistence.Characters;

    public sealed partial class MySqlCharacterPersistenceDao
    {
        public void SaveInventoryAndUploadedNanos(int characterId, IList<PersistedItemData> inserts,
            IList<ItemLocationData> locations, IList<int> uploadedNanoIds,
            IList<PersistedActiveNanoData>? activeNanos = null,
            IList<PersistedSkillLockData>? skillLocks = null)
        {
            if (inserts == null || locations == null || uploadedNanoIds == null) throw new ArgumentNullException("batch");
            if (inserts.Count == 0 && locations.Count == 0 && uploadedNanoIds.Count == 0 && activeNanos == null && skillLocks == null) return;
            if (skillLocks != null
                && (skillLocks.Any(l => l.StatId < 0 || l.ExpiresAtUtcTicks <= 0)
                    || skillLocks.Select(l => l.StatId).Distinct().Count() != skillLocks.Count))
                throw new ArgumentException("Invalid or duplicate skill lock.", nameof(skillLocks));
            if (activeNanos != null
                && (activeNanos.Any(n => n.NanoId <= 0 || n.NanoInstance <= 0 || n.DurationCentiseconds < 0 || n.ExpiresAtUtcTicks < 0)
                    // Strain 0 is "no strain": those nanos never conflict in NCU, so only real strains must be unique.
                    || activeNanos.Where(n => n.Strain > 0).Select(n => n.Strain).Distinct().Count() != activeNanos.Count(n => n.Strain > 0)
                    || activeNanos.Select(n => n.NanoInstance).Distinct().Count() != activeNanos.Count))
                throw new ArgumentException("Invalid or duplicate active nano identity.", nameof(activeNanos));
            Transaction((c, t) =>
            {
                WriteItems(c, t, inserts, locations);
                WriteUploadedNanos(c, t, characterId, uploadedNanoIds);
                if (activeNanos != null)
                {
                    Execute(c, t, "DELETE FROM charactersactivenanos WHERE CharacterId=@Id", "@Id", characterId);
                    foreach (var nano in activeNanos)
                        Execute(c, t, "INSERT INTO charactersactivenanos (CharacterId,NanoId,Strain,NanoInstance,DurationCentiseconds,ExpiresAtUtcTicks) "
                            + "VALUES (@Id,@Nano,@Strain,@Instance,@Duration,@Expiry)", "@Id", characterId,
                            "@Nano", nano.NanoId, "@Strain", nano.Strain, "@Instance", nano.NanoInstance,
                            "@Duration", nano.DurationCentiseconds, "@Expiry", nano.ExpiresAtUtcTicks);
                }
                if (skillLocks != null)
                {
                    Execute(c, t, "DELETE FROM characterskilllocks WHERE CharacterId=@Id", "@Id", characterId);
                    foreach (var skillLock in skillLocks)
                        Execute(c, t, "INSERT INTO characterskilllocks (CharacterId,StatId,ExpiresAtUtcTicks) VALUES (@Id,@Stat,@Expiry)",
                            "@Id", characterId, "@Stat", skillLock.StatId, "@Expiry", skillLock.ExpiresAtUtcTicks);
                }
                return 0;
            });
        }

        public IList<PersistedSkillLockData> LoadSkillLocks(int characterId)
        {
            if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
            return Query("SELECT StatId,ExpiresAtUtcTicks FROM characterskilllocks WHERE CharacterId=@Id ORDER BY StatId",
                r => new PersistedSkillLockData { StatId = r.GetInt32(0), ExpiresAtUtcTicks = r.GetInt64(1) }, "@Id", characterId);
        }

        public void CommitInventoryMutation(CharacterInventoryMutationData mutation)
        {
            if (mutation == null) throw new ArgumentNullException(nameof(mutation));
            if (mutation.CharacterId <= 0) throw new ArgumentOutOfRangeException(nameof(mutation));
            Transaction((c, t) =>
            {
                AssertExpectedSources(c, t, mutation);
                WriteItems(c, t, mutation.Inserts, mutation.Locations);
                AssertContainersEmpty(c, t, mutation.EmptyContainersBeforeRetire);
                WriteStacks(c, t, mutation.Stacks);
                WriteQualities(c, t, mutation.Qualities);
                WriteUploadedNanos(c, t, mutation.CharacterId, mutation.UploadedNanoIds);
                WriteStats(c, t, mutation.CharacterId, mutation.FinalStats);
                if (mutation.PlayerShop != null)
                    WritePlayerShop(c, t, mutation.CharacterId, mutation.PlayerShop);
                return 0;
            }, IsolationLevel.RepeatableRead); // Preserve the child-range gap lock even on READ COMMITTED servers.
        }

        /// <summary>
        /// A player shop change, after the same transaction's item rows are written. Every check that fails throws
        /// <see cref="InvalidOperationException"/>, so nothing of the transaction is stored and the acting character is
        /// reloaded from storage: the server's memory and the shop's rows disagree.
        /// </summary>
        private static void WritePlayerShop(IDbConnection c, IDbTransaction t, int characterId, PlayerShopMutationData shop)
        {
            if (shop.ShopId <= 0)
                throw new ArgumentOutOfRangeException(nameof(shop), "A player shop change needs its shop.");
            if (shop.Kind != PlayerShopMutationKind.Sale && shop.OwnerId != characterId)
                throw new ArgumentException("Only the shop's owner changes it.", nameof(shop));
            const int ShopItems = (int)IdentityType.PlayerShopInventory;
            switch (shop.Kind)
            {
                case PlayerShopMutationKind.Rent:
                    if (string.IsNullOrEmpty(shop.Name) || shop.PlayfieldId <= 0 || shop.TerminalInstance == 0)
                        throw new ArgumentException("A rented shop needs its terminal and a name.", nameof(shop));
                    Execute(c, t, "INSERT INTO player_shops (ShopId,PlayfieldId,TerminalInstance,OwnerId,Name,Balance,RentedAtUtc) "
                        + "VALUES (@Shop,@Playfield,@Terminal,@Owner,@Name,0,UTC_TIMESTAMP(6))",
                        "@Shop", shop.ShopId, "@Playfield", shop.PlayfieldId, "@Terminal", shop.TerminalInstance,
                        "@Owner", shop.OwnerId, "@Name", shop.Name);
                    return;
                case PlayerShopMutationKind.Rename:
                    if (string.IsNullOrEmpty(shop.Name))
                        throw new ArgumentException("A shop needs a name.", nameof(shop));
                    LockOwnedShop(c, t, shop);
                    Execute(c, t, "UPDATE player_shops SET Name=@Name WHERE ShopId=@Shop", "@Name", shop.Name, "@Shop", shop.ShopId);
                    return;
                case PlayerShopMutationKind.Abandon:
                    if (LockOwnedShop(c, t, shop) != 0)
                        throw new InvalidOperationException("The shop still holds credits.");
                    if (Count(c, t, "SELECT COUNT(*) FROM item_instances WHERE ContainerType=@Type AND ContainerInstance=@Shop",
                            "@Type", ShopItems, "@Shop", shop.ShopId) != 0
                        || Count(c, t, "SELECT COUNT(*) FROM player_shop_items WHERE ShopId=@Shop", "@Shop", shop.ShopId) != 0)
                        throw new InvalidOperationException("The shop still has items for sale.");
                    if (Execute(c, t, "DELETE FROM player_shops WHERE ShopId=@Shop AND OwnerId=@Owner AND Balance=0",
                            "@Shop", shop.ShopId, "@Owner", shop.OwnerId) != 1)
                        throw new InvalidOperationException("The shop changed before it could be abandoned.");
                    return;
                case PlayerShopMutationKind.Stock:
                    if (shop.ItemInstanceId <= 0 || shop.Price <= 0 || shop.MaxItems <= 0)
                        throw new ArgumentOutOfRangeException(nameof(shop));
                    LockOwnedShop(c, t, shop);
                    if (Count(c, t, "SELECT COUNT(*) FROM item_instances WHERE InstanceId=@Item AND ContainerType=@Type AND ContainerInstance=@Shop",
                            "@Item", shop.ItemInstanceId, "@Type", ShopItems, "@Shop", shop.ShopId) != 1)
                        throw new InvalidOperationException("The stocked item is not in the shop.");
                    Execute(c, t, "INSERT INTO player_shop_items (ItemInstanceId,ShopId,Price) VALUES (@Item,@Shop,@Price)",
                        "@Item", shop.ItemInstanceId, "@Shop", shop.ShopId, "@Price", shop.Price);
                    if (Count(c, t, "SELECT COUNT(*) FROM item_instances WHERE ContainerType=@Type AND ContainerInstance=@Shop",
                            "@Type", ShopItems, "@Shop", shop.ShopId) > shop.MaxItems)
                        throw new InvalidOperationException("The shop is full.");
                    return;
                case PlayerShopMutationKind.Price:
                    if (shop.ItemInstanceId <= 0 || shop.Price <= 0 || shop.Price == shop.ExpectedPrice)
                        throw new ArgumentOutOfRangeException(nameof(shop));
                    LockOwnedShop(c, t, shop);
                    if (Execute(c, t, "UPDATE player_shop_items SET Price=@Price WHERE ItemInstanceId=@Item AND ShopId=@Shop AND Price=@Expected",
                            "@Price", shop.Price, "@Item", shop.ItemInstanceId, "@Shop", shop.ShopId, "@Expected", shop.ExpectedPrice) != 1)
                        throw new InvalidOperationException("The item's price changed before it could be set.");
                    return;
                case PlayerShopMutationKind.Unstock:
                    if (shop.ItemInstanceId <= 0)
                        throw new ArgumentOutOfRangeException(nameof(shop));
                    LockOwnedShop(c, t, shop);
                    if (Execute(c, t, "DELETE FROM player_shop_items WHERE ItemInstanceId=@Item AND ShopId=@Shop",
                            "@Item", shop.ItemInstanceId, "@Shop", shop.ShopId) != 1)
                        throw new InvalidOperationException("The item was not for sale in the shop.");
                    return;
                case PlayerShopMutationKind.Sale:
                    WriteSale(c, t, characterId, shop);
                    return;
                case PlayerShopMutationKind.Withdraw:
                    if (shop.Amount <= 0)
                        throw new ArgumentOutOfRangeException(nameof(shop));
                    if (LockOwnedShop(c, t, shop) < shop.Amount)
                        throw new InvalidOperationException("The shop does not hold that many credits.");
                    if (Execute(c, t, "UPDATE player_shops SET Balance=Balance-@Amount WHERE ShopId=@Shop AND Balance>=@Amount",
                            "@Amount", shop.Amount, "@Shop", shop.ShopId) != 1)
                        throw new InvalidOperationException("The shop's balance changed before the withdrawal.");
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(shop), "Unknown player shop change.");
            }
        }

        /// <summary>
        /// A purchase: the shop is still rented by the owner the buyer saw (never the buyer), the item still asks the
        /// price the buyer paid, and the balance can take it. The item row's move to the buyer is in the same transaction.
        /// </summary>
        private static void WriteSale(IDbConnection c, IDbTransaction t, int buyerId, PlayerShopMutationData shop)
        {
            if (shop.ItemInstanceId <= 0 || shop.ExpectedPrice <= 0 || shop.MaxBalance <= 0)
                throw new ArgumentOutOfRangeException(nameof(shop));
            long balance;
            int owner;
            using (IDbCommand command = Command(c, t, "SELECT OwnerId,Balance FROM player_shops WHERE ShopId=@Shop FOR UPDATE", "@Shop", shop.ShopId))
            using (IDataReader reader = command.ExecuteReader())
            {
                if (!reader.Read())
                    throw new InvalidOperationException("The shop is no longer rented.");
                owner = reader.GetInt32(0);
                balance = reader.GetInt64(1);
            }
            if (owner == buyerId || owner != shop.OwnerId)
                throw new InvalidOperationException("The shop's owner changed before the sale.");
            if (balance + shop.ExpectedPrice > shop.MaxBalance)
                throw new InvalidOperationException("The shop cannot hold the sale's credits.");
            if (Execute(c, t, "DELETE FROM player_shop_items WHERE ItemInstanceId=@Item AND ShopId=@Shop AND Price=@Price",
                    "@Item", shop.ItemInstanceId, "@Shop", shop.ShopId, "@Price", shop.ExpectedPrice) != 1)
                throw new InvalidOperationException("The item was not for sale at that price.");
            if (Execute(c, t, "UPDATE player_shops SET Balance=Balance+@Price WHERE ShopId=@Shop",
                    "@Price", (long)shop.ExpectedPrice, "@Shop", shop.ShopId) != 1)
                throw new InvalidOperationException("The shop's balance could not take the sale.");
        }

        /// <summary>Locks the shop row for its owner and returns its balance; throws when the owner no longer has it.</summary>
        private static long LockOwnedShop(IDbConnection c, IDbTransaction t, PlayerShopMutationData shop)
        {
            using (IDbCommand command = Command(c, t, "SELECT Balance FROM player_shops WHERE ShopId=@Shop AND OwnerId=@Owner FOR UPDATE",
                "@Shop", shop.ShopId, "@Owner", shop.OwnerId))
            {
                object balance = command.ExecuteScalar();
                if (balance == null || balance is DBNull)
                    throw new InvalidOperationException("The shop is not rented by its owner any more.");
                return Convert.ToInt64(balance, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        private static long Count(IDbConnection c, IDbTransaction t, string sql, params object[] parameters)
        {
            using (IDbCommand command = Command(c, t, sql, parameters))
                return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public IList<PersistedPlayerShopData> LoadPlayerShops()
        {
            return Query("SELECT s.ShopId,s.PlayfieldId,s.TerminalInstance,s.OwnerId,COALESCE(c.Name,''),s.Name,s.Balance "
                + "FROM player_shops s LEFT JOIN characters c ON c.Id=s.OwnerId",
                r => new PersistedPlayerShopData
                {
                    ShopId = r.GetInt32(0), PlayfieldId = r.GetInt32(1), TerminalInstance = r.GetInt32(2), OwnerId = r.GetInt32(3),
                    OwnerName = r.GetString(4), Name = r.GetString(5), Balance = r.GetInt64(6)
                });
        }

        public IList<PersistedPlayerShopPriceData> LoadPlayerShopPrices()
        {
            return Query("SELECT ItemInstanceId,ShopId,Price FROM player_shop_items",
                r => new PersistedPlayerShopPriceData { ItemInstanceId = r.GetInt32(0), ShopId = r.GetInt32(1), Price = r.GetInt32(2) });
        }

        public IList<PersistedItemData> LoadPlayerShopItems()
        {
            return Query("SELECT " + ItemColumns + " FROM item_instances WHERE ContainerType=@Type",
                ReadItem, "@Type", (int)IdentityType.PlayerShopInventory);
        }

        public void CommitItemCredits(ItemCreditMutationData mutation)
        {
            if (mutation == null) throw new ArgumentNullException(nameof(mutation));
            if (mutation.Characters.Count < 1 || mutation.Characters.Count > 2
                || mutation.Characters.Any(c => c.CharacterId <= 0 || c.Cash < 0
                    || c.Currencies.Any(s => s.StatId == (int)CharacterStat.Cash || s.StatValue < 0))
                || mutation.Characters.Select(c => c.CharacterId).Distinct().Count() != mutation.Characters.Count)
                throw new ArgumentException("One or two distinct positive character ids and nonnegative cash are required.", nameof(mutation));
            Transaction((c, t) =>
            {
                foreach (var character in mutation.Characters.OrderBy(x => x.CharacterId)) LockCharacter(c, t, character.CharacterId);
                WriteItems(c, t, mutation.Inserts, mutation.Locations);
                foreach (var character in mutation.Characters.OrderBy(x => x.CharacterId))
                {
                    WriteUploadedNanos(c, t, character.CharacterId, character.UploadedNanoIds);
                    WriteStats(c, t, character.CharacterId,
                        new[] { new CharacterStatData { StatId = (int)CharacterStat.Cash, StatValue = character.Cash } }
                            .Concat(character.Currencies).ToArray());
                }
                return 0;
            });
        }

        public IList<PersistedActiveNanoData> LoadActiveNanos(int characterId)
        {
            if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
            return Query("SELECT NanoId,Strain,NanoInstance,DurationCentiseconds,ExpiresAtUtcTicks FROM charactersactivenanos "
                + "WHERE CharacterId=@Id ORDER BY Strain,NanoInstance", r => new PersistedActiveNanoData
                { NanoId = r.GetInt32(0), Strain = r.GetInt32(1), NanoInstance = r.GetInt32(2),
                    DurationCentiseconds = r.GetInt32(3), ExpiresAtUtcTicks = r.GetInt64(4) }, "@Id", characterId);
        }

        public void CommitActiveNanos(IList<CharacterActiveNanoData> characters)
        {
            if (characters == null) throw new ArgumentNullException(nameof(characters));
            if (characters.Count == 0 || characters.Any(c => c.CharacterId <= 0)
                || characters.Select(c => c.CharacterId).Distinct().Count() != characters.Count)
                throw new ArgumentException("Distinct positive nano owners are required.", nameof(characters));
            foreach (var character in characters)
                if (character.ActiveNanos.Any(n => n.NanoId <= 0 || n.NanoInstance <= 0 || n.DurationCentiseconds < 0 || n.ExpiresAtUtcTicks < 0)
                    || character.ActiveNanos.Select(n => n.Strain).Distinct().Count() != character.ActiveNanos.Count
                    || character.ActiveNanos.Select(n => n.NanoInstance).Distinct().Count() != character.ActiveNanos.Count)
                    throw new ArgumentException("Invalid or duplicate active nano identity.", nameof(characters));
            Transaction((c, t) =>
            {
                foreach (var character in characters.OrderBy(x => x.CharacterId)) LockCharacter(c, t, character.CharacterId);
                foreach (var character in characters.OrderBy(x => x.CharacterId))
                {
                    Execute(c, t, "DELETE FROM charactersactivenanos WHERE CharacterId=@Id", "@Id", character.CharacterId);
                    foreach (var nano in character.ActiveNanos)
                        Execute(c, t, "INSERT INTO charactersactivenanos (CharacterId,NanoId,Strain,NanoInstance,DurationCentiseconds,ExpiresAtUtcTicks) "
                            + "VALUES (@Id,@Nano,@Strain,@Instance,@Duration,@Expiry)", "@Id", character.CharacterId,
                            "@Nano", nano.NanoId, "@Strain", nano.Strain, "@Instance", nano.NanoInstance,
                            "@Duration", nano.DurationCentiseconds, "@Expiry", nano.ExpiresAtUtcTicks);
                    WriteStats(c, t, character.CharacterId, character.BaseStats);
                }
                return 0;
            });
        }
    }
}
