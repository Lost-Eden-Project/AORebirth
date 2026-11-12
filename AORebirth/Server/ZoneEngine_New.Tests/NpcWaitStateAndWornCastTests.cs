namespace ZoneEngine_New.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Threading;

    using AORebirth.Core.GameData;
    using AORebirth.Enums;

    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using ZoneEngine_New.Core.Characters;
    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Mobs;
    using ZoneEngine_New.Core.Nanos;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Pets;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Playfield.Locality;
    using ZoneEngine_New.Core.Trade;
    using ZoneEngine_New.Core.WorldSimulation;

    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// Bugs #161/#393 (an NPC never had WaitState, so a kit used on a pet read the unset sentinel) and #174
    /// (a worn item's OnWear/OnWield cast was lost on every relog and zone change).
    /// </summary>
    [TestClass]
    public sealed class NpcWaitStateAndWornCastTests
    {
        const int WearNanoId = 930001;

        [TestMethod]
        public void SpawnedNpcStartsWithWaitStateZero()
        {
            using var world = new ArrivalWorld(new StubItemBuilder());
            var bare = new NpcCharacter(new Identity { Type = IdentityType.CanbeAffected, Instance = 9 }, new StubItemBuilder());
            Assert.IsTrue(StatCollection.IsUnset(bare.Stats.Get(CharacterStat.WaitState)));

            NpcCharacter npc = world.Spawns.SpawnMob(Template(), new Vector3(0, 0, 0));

            Assert.AreEqual(0, npc.Stats.Get(CharacterStat.WaitState));
        }

        [TestMethod]
        public void ArrivalRunsWornCastsAgain()
        {
            var items = new StubItemBuilder().Add(TestNanos.Create(WearNanoId, durationCentiseconds: 72000000));
            using var world = new ArrivalWorld(items);
            Player player = world.Player(5);
            Item gear = TestWorld.CreateItem(lowId: 3000, highId: 3000, name: "Deck");
            gear.Definition.SpellList[EventType.OnWear] = [Cast(WearNanoId)];
            Assert.IsTrue(player.Inventory.Armor.Add(0x11, gear));
            Assert.AreEqual(0, player.Buffs.Count);

            world.Spawns.ArriveFromTransfer(player, new Vector3(0, 0, 0));

            Assert.AreEqual(1, player.Buffs.Count);
            Assert.AreEqual(WearNanoId, player.Buffs[0].Id);

            world.Spawns.ArriveFromTransfer(player, new Vector3(1, 0, 0));

            Assert.AreEqual(1, player.Buffs.Count);
        }

        [TestMethod]
        public void ArrivalRunsWieldCastsOfTheWeaponPage()
        {
            var items = new StubItemBuilder().Add(TestNanos.Create(WearNanoId, durationCentiseconds: 72000000));
            using var world = new ArrivalWorld(items);
            Player player = world.Player(6);
            Item deck = TestWorld.CreateItem(lowId: 3001, highId: 3001, name: "Cyberdeck");
            deck.Definition.SpellList[EventType.OnWield] = [Cast(WearNanoId)];
            Assert.IsTrue(player.Inventory.Equipment.Add(6, deck));

            world.Spawns.ArriveFromTransfer(player, new Vector3(0, 0, 0));

            Assert.AreEqual(1, player.Buffs.Count);
            Assert.AreEqual(WearNanoId, player.Buffs[0].Id);
        }

        static ItemSpell Cast(int nanoId)
            => new()
            {
                FunctionType = (int)FunctionType.CastNano,
                Arguments = [nanoId],
                Requirements = new List<ItemRequirement>()
            };

        static MobTemplate Template()
            => new()
            {
                Hash = "TSTN",
                Name = "Rhinoman",
                TemplateId = 1,
                MinLevel = 10,
                MaxLevel = 10,
                Stats = new Dictionary<int, int>
                {
                    [(int)CharacterStat.Level] = 10,
                    [(int)CharacterStat.MaxHealth] = 100,
                    [(int)CharacterStat.Health] = 100,
                    [(int)CharacterStat.NPCFamily] = 95
                }
            };

        /// <summary>A bare playfield with the services a player arrival and an NPC spawn go through.</summary>
        sealed class ArrivalWorld : IDisposable
        {
            readonly ServiceProvider _services;
            readonly InventoryFlushService _flush;
            readonly DynelRegistry _registry = new();

            public Playfield Playfield { get; }

            public SpawnService Spawns => _services.GetRequiredService<SpawnService>();

            public ArrivalWorld(StubItemBuilder items)
            {
                var locality = new PlayfieldLocality(127, null);
                var manager = (PlayfieldManager)RuntimeHelpers.GetUninitializedObject(typeof(PlayfieldManager));
                typeof(PlayfieldManager).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, new Lock());
                typeof(PlayfieldManager).GetField("_playersByCharacterId", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(manager, new Dictionary<int, Player>());
                _flush = new InventoryFlushService(new Lazy<PlayfieldManager>(() => manager), new NoCoalesce(), new StubLogger());
                var data = new StubGameData(HashItemCatalog.Parse("{}"), allowMissingCatMesh: true);
                var catalog = new StubCatalog();
                var minter = new HashItemMinter(data, catalog, items);
                var ids = new StubInstanceIdAllocator();
                var actions = new InventoryActionService(new NoMutation(), _flush, ids, new StubLogger());
                var trade = new TradeService(new StubLogger(), data, catalog, minter, ids, _flush, new NoTradePersistence());
                var snapshots = new SnapshotStore();
                Playfield? playfield = null;
                Playfield Field() => playfield!;
                _services = new ServiceCollection()
                    .AddSingleton(_registry)
                    .AddSingleton(locality)
                    .AddSingleton<IGameData>(data)
                    .AddSingleton(new WorldSimulationAccess())
                    .AddSingleton<IInventoryRepository>(new QuietInventory())
                    .AddSingleton<IItemBuilder>(items)
                    .AddSingleton(actions)
                    .AddSingleton(new InventoryMoveService(new StubLogger(), _flush, actions))
                    .AddSingleton(trade)
                    .AddSingleton<SpawnService>(services => new SpawnService(services, _registry, new StubLogger(), Field(),
                        manager, data, items, minter, _flush, trade,
                        new CharacterSnapshotService(snapshots, snapshots, new StubLogger())))
                    .AddSingleton(services => new PetService(Field(), data, new StubLogger()))
                    .BuildServiceProvider();
                playfield = BarePlayfield.Create(127, _services);
                Playfield = playfield;
            }

            public Player Player(int id)
            {
                Player player = TestWorld.CreatePlayer(id);
                player.Stats.Set(CharacterStat.MaxNCU, 60);
                player.Stats.Set(CharacterStat.Level, 20);
                player.Stats.Set(CharacterStat.MaxHealth, 100);
                player.Stats.Set(CharacterStat.Health, 100);
                var session = new RecordingZoneSession { State = SessionState.InPlay };
                session.BindPlayer(player);
                player.Session = session;
                return player;
            }

            public void Dispose()
            {
                _flush.Dispose();
                _services.Dispose();
            }
        }

        sealed class NoMutation : IInventoryMutationPersistence
        {
            public void Persist(InventoryMutationBatch batch) => throw new InvalidOperationException("No inventory mutation expected.");
        }

        sealed class NoTradePersistence : ITradePersistence
        {
            public void Persist(TradePersistenceBatch batch) => throw new InvalidOperationException("No trade expected.");
        }

        sealed class NoCoalesce : ICharacterCoalesceCommit
        {
            public void Persist(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates, int characterId,
                IReadOnlyList<int> nanos, IReadOnlyList<ActiveNanoRecord>? activeNanos, IReadOnlyList<SkillLockRecord>? skillLocks)
            {
            }
        }

        sealed class SnapshotStore : ICharacterRepository, IStatRepository
        {
            public CharacterRecord? GetById(int id) => throw new NotSupportedException();

            public void SetOnline(int id)
            {
            }

            public void SetOffline(int id)
            {
            }

            public void SaveLocation(CharacterRecord character, int online) => throw new NotSupportedException();

            public void SaveSnapshot(CharacterRecord character, int online, IReadOnlyList<StatRecord> stats)
            {
            }

            public bool SaveOnlineCheckpoint(int characterId, CharacterRecord? location, IReadOnlyList<StatRecord> stats) => true;

            public IReadOnlyList<StatRecord> GetForCharacter(int id) => [];

            public void UpsertForCharacter(int id, IReadOnlyList<StatRecord> stats) => throw new NotSupportedException();
        }

        sealed class QuietInventory : IInventoryRepository
        {
            public IReadOnlyList<ItemInstanceRecord> GetCarriedItems(int characterId) => [];

            public IReadOnlyList<ItemInstanceRecord> GetBankItems(int characterId) => [];

            public IReadOnlyList<ItemInstanceRecord> GetContainerItems(int containerInstanceId) => [];

            public int LeaseInstanceIdBlock(int count) => 1;

            public ItemInstanceRecord Insert(ItemInstanceRecord item) => item;

            public void UpdateLocation(int instanceId, int containerType, int containerInstance, int containerPlacement)
            {
            }

            public void UpdateLocations(IReadOnlyList<ItemLocationUpdate> locations)
            {
            }

            public void PersistNewAndUpdateLocations(
                IReadOnlyList<ItemInstanceRecord> inserts,
                IReadOnlyList<ItemLocationUpdate> updates)
            {
            }
        }
    }
}
