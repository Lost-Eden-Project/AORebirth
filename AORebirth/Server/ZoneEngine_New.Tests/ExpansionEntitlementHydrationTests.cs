namespace ZoneEngine_New.Tests
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using SmokeLounge.AOtomation.Messaging.GameData;
    using ZoneEngine_New.Core.Characters;
    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Playfield;
    using Vector3 = AORebirth.Core.Vector.Vector3;

    [TestClass]
    public sealed class ExpansionEntitlementHydrationTests
    {
        [TestMethod]
        public void Persisted_all_expansions_survive_fresh_login_hydration_and_reconnect_publication()
        {
            CharacterHydrationResult persisted = PersistedCharacter();
            var store = new ReadOnlyHydrationStore(persisted);
            var originalStats = persisted.Stats.Select(row => (row.StatId, row.StatValue)).ToArray();

            for (int session = 0; session < 2; session++)
            {
                var service = new CharacterHydrationService(store, store, store, store, store, store, new StubLogger());
                CharacterHydrationResult? loaded = service.LoadForLogin(persisted.Character.Id);
                Assert.IsNotNull(loaded);
                Assert.AreNotSame(persisted.Stats, loaded.Stats);
                CollectionAssert.AreEquivalent(originalStats, loaded.Stats.Select(row => (row.StatId, row.StatValue)).ToArray());
                CollectionAssert.AreEqual(persisted.Items.ToArray(), loaded.Items.ToArray());
                CollectionAssert.AreEqual(persisted.UploadedNanoIds.ToArray(), loaded.UploadedNanoIds.ToArray());

                Player player = TestWorld.CreatePlayer(loaded.Character.Id);
                player.Name = loaded.Character.Name;
                player.Position = new Vector3(loaded.Character.X, loaded.Character.Y, loaded.Character.Z);
                foreach (StatRecord stat in loaded.Stats)
                    player.Stats.Set((CharacterStat)stat.StatId, stat.StatValue, StatDetail.Base);
                var playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
                typeof(Playfield).GetField("<Identity>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(playfield, new Identity { Type = IdentityType.Playfield2, Instance = loaded.Character.Playfield });
                player.Playfield = playfield;
                player.Rebase();
                Assert.AreEqual(2047, player.Stats.GetOrZero(CharacterStat.Expansion));
                Assert.AreEqual(12345, player.Stats.GetOrZero(CharacterStat.Cash));
                Assert.AreEqual(321, player.Stats.GetOrZero(CharacterStat.XP));
                var spawn = player.BuildSpawnMessage();
                Assert.AreEqual(2047, spawn.Expansions);
                PlayerSpawnPayloadValidator.RequireValidMessages(spawn, player.BuildFullCharacterMessage(), isGm: false);
            }

            Assert.AreEqual(2, store.StatReads);
            CollectionAssert.AreEqual(originalStats, persisted.Stats.Select(row => (row.StatId, row.StatValue)).ToArray());
        }

        private static CharacterHydrationResult PersistedCharacter()
        {
            var values = new Dictionary<CharacterStat, int>
            {
                [CharacterStat.Flags] = 0x00081241, [CharacterStat.Breed] = 1, [CharacterStat.Sex] = 2,
                [CharacterStat.Profession] = 1, [CharacterStat.Fatness] = 0, [CharacterStat.Race] = 1,
                [CharacterStat.HeadMesh] = 40683, [CharacterStat.VisualFlags] = 31, [CharacterStat.Scale] = 100,
                [CharacterStat.Level] = 1, [CharacterStat.TitleLevel] = 1, [CharacterStat.Side] = 0,
                [CharacterStat.Expansion] = 2047,
                [CharacterStat.Strength] = 6, [CharacterStat.Agility] = 6, [CharacterStat.Stamina] = 6,
                [CharacterStat.Intelligence] = 6, [CharacterStat.Sense] = 6, [CharacterStat.Psychic] = 6,
                [CharacterStat.BodyDevelopment] = 5, [CharacterStat.NanoPool] = 5,
                [CharacterStat.MaxHealth] = 31, [CharacterStat.Health] = 25,
                [CharacterStat.MaxNanoEnergy] = 29, [CharacterStat.CurrentNano] = 20,
                [CharacterStat.RunSpeed] = 100, [CharacterStat.Cash] = 12345, [CharacterStat.XP] = 321
            };
            return new CharacterHydrationResult
            {
                Character = new CharacterRecord { Id = 9950, Name = "ExpansionFixture", Playfield = 4582,
                    X = 100, Y = 0, Z = 100, HeadingW = 1 },
                Stats = values.Select(pair => new StatRecord { StatId = (int)pair.Key, StatValue = pair.Value }).ToArray(),
                Items = new[] { 1, 64 }.Select(placement => new ItemInstanceRecord
                {
                    InstanceId = placement, ContainerType = (int)IdentityType.Inventory,
                    ContainerInstance = 9950, ContainerPlacement = placement,
                    LowId = 43384, HighId = 43384, Quality = 1, StackCount = 2
                }).ToArray(),
                UploadedNanoIds = new[] { 37 }
            };
        }

        private sealed class ReadOnlyHydrationStore(CharacterHydrationResult stored) :
            ICharacterRepository, IStatRepository, IInventoryRepository,
            IUploadedNanoRepository, IActiveNanoRepository, ISkillLockRepository
        {
            public int StatReads { get; private set; }
            public CharacterRecord? GetById(int id) => id == stored.Character.Id ? stored.Character : null;
            IReadOnlyList<StatRecord> IStatRepository.GetForCharacter(int id)
            {
                Assert.AreEqual(stored.Character.Id, id);
                StatReads++;
                return stored.Stats.Select(row => new StatRecord { StatId = row.StatId, StatValue = row.StatValue }).ToArray();
            }
            public IReadOnlyList<ItemInstanceRecord> GetCarriedItems(int id) => stored.Items;
            IReadOnlyList<int> IUploadedNanoRepository.GetForCharacter(int id) => stored.UploadedNanoIds;
            IReadOnlyList<ActiveNanoRecord> IActiveNanoRepository.GetForCharacter(int id) => stored.ActiveNanos;
            IReadOnlyList<SkillLockRecord> ISkillLockRepository.GetForCharacter(int id) => stored.SkillLocks;
            public void SetOnline(int id) => throw new AssertFailedException("Hydration wrote online state.");
            public void SetOffline(int id) => throw new AssertFailedException("Hydration wrote online state.");
            public void SaveLocation(CharacterRecord character, int online) => throw new AssertFailedException("Hydration wrote location.");
            public void SaveSnapshot(CharacterRecord character, int online, IReadOnlyList<StatRecord> stats)
                => throw new AssertFailedException("Hydration wrote a snapshot.");
            public bool SaveOnlineCheckpoint(int id, CharacterRecord? location, IReadOnlyList<StatRecord> stats)
                => throw new AssertFailedException("Hydration wrote a checkpoint.");
            public void UpsertForCharacter(int id, IReadOnlyList<StatRecord> stats)
                => throw new AssertFailedException("Hydration wrote stats.");
            public IReadOnlyList<ItemInstanceRecord> GetBankItems(int id) => throw new AssertFailedException("Hydration read the bank.");
            public IReadOnlyList<ItemInstanceRecord> GetContainerItems(int id) => throw new AssertFailedException("Hydration read a bag.");
            public int LeaseInstanceIdBlock(int count) => throw new AssertFailedException("Hydration leased item ids.");
            public ItemInstanceRecord Insert(ItemInstanceRecord item) => throw new AssertFailedException("Hydration inserted an item.");
            public void UpdateLocation(int id, int type, int instance, int placement)
                => throw new AssertFailedException("Hydration moved an item.");
            public void UpdateLocations(IReadOnlyList<ItemLocationUpdate> locations)
                => throw new AssertFailedException("Hydration moved items.");
            public void PersistNewAndUpdateLocations(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates)
                => throw new AssertFailedException("Hydration wrote inventory.");
        }
    }
}
