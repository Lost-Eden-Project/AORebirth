namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using AORebirth.Core.GameData;
using AORebirth.Database.Domain.Quests;
using AORebirth.Enums;
using AORebirth.Interfaces.Persistence.Missions;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Quests;
using ZoneEngine_New.Core.Quests.Dungeons;
using ZoneEngine_New.Core.Trade;
using ZoneEngine_New.Core.WorldSimulation;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Lost Eden: a Find Person ("Locate a person") target is an ordinary attackable NPC. A player who fought and killed it
/// instead of selecting it was left with a mission nothing could complete: the target was dead and only selecting it
/// earned the objective. Killing the bound target now earns it too, through the same single stored earn as selecting it,
/// so the two together never earn twice. Runs against the runtime GameData (AO_REBIRTH_GAMEDATA_PATH).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class FindPersonObjectiveKillTests
{
    const string QuestId = "D90000001";
    const int CharacterId = 4343;
    const int TargetInstance = 90000002;

    [TestMethod]
    public void KillingTheFindPersonTargetCompletesTheObjective()
    {
        using var rig = new Rig();
        rig.OnTick(() => rig.Quests.OnNpcKilled(rig.Target, [rig.Player]));
        rig.Settle();

        Assert.AreEqual(string.Empty, rig.Log.Problems);
        Assert.IsFalse(rig.Player.IsPersistenceQuarantined);
        Assert.AreEqual(1, rig.Earns, "The kill stores the objective's earn once.");
        Assert.IsTrue(rig.Quest.IsRewardPending, "The mission shows its objective done and its reward due.");
    }

    [TestMethod]
    public void SelectingTheTargetStillCompletesTheObjective()
    {
        using var rig = new Rig();
        rig.OnTick(() => rig.Quests.OnNpcTargeted(rig.Player, rig.Target));
        rig.Settle();

        Assert.AreEqual(string.Empty, rig.Log.Problems);
        Assert.AreEqual(1, rig.Earns);
        Assert.IsTrue(rig.Quest.IsRewardPending);
    }

    /// <summary>Selecting and then killing the target on the same tick, before the selection's earn lands: one earn.</summary>
    [TestMethod]
    public void SelectingThenKillingBeforeTheEarnLandsEarnsOnce()
    {
        using var rig = new Rig();
        rig.OnTick(() =>
        {
            rig.Quests.OnNpcTargeted(rig.Player, rig.Target);
            rig.Quests.OnNpcKilled(rig.Target, [rig.Player]);
        });
        rig.Settle();

        Assert.AreEqual(string.Empty, rig.Log.Problems);
        Assert.IsFalse(rig.Player.IsPersistenceQuarantined);
        Assert.AreEqual(1, rig.Earns, "The second trigger finds the earn in flight and queues nothing.");
        Assert.IsTrue(rig.Quest.IsRewardPending);
    }

    /// <summary>Selected and earned, then killed (or killed and earned, then selected): nothing more is earned.</summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void TheSecondTriggerAfterTheEarnLandedEarnsNothing(bool selectFirst)
    {
        using var rig = new Rig();
        void Select() => rig.Quests.OnNpcTargeted(rig.Player, rig.Target);
        void Kill() => rig.Quests.OnNpcKilled(rig.Target, [rig.Player]);
        rig.OnTick(selectFirst ? Select : Kill);
        rig.Settle();
        rig.OnTick(selectFirst ? Kill : Select);
        rig.Settle();

        Assert.AreEqual(string.Empty, rig.Log.Problems);
        Assert.IsFalse(rig.Player.IsPersistenceQuarantined);
        Assert.AreEqual(1, rig.Earns);
    }

    [TestMethod]
    public void KillingAnotherNpcInTheDungeonEarnsNothing()
    {
        using var rig = new Rig();
        NpcCharacter other = rig.AddNpc(TargetInstance + 1);
        rig.OnTick(() => rig.Quests.OnNpcKilled(other, [rig.Player]));
        rig.Settle();

        Assert.AreEqual(string.Empty, rig.Log.Problems);
        Assert.AreEqual(0, rig.Earns);
        Assert.IsFalse(rig.Quest.IsRewardPending);
    }

    /// <summary>A player who does not hold the mission kills its target: the mission's owner earns nothing from it.</summary>
    [TestMethod]
    public void AnotherPlayerKillingTheTargetEarnsTheirNothing()
    {
        using var rig = new Rig();
        Player stranger = rig.AddPlayer(CharacterId + 1);
        rig.OnTick(() => rig.Quests.OnNpcKilled(rig.Target, [stranger]));
        rig.Settle();

        Assert.AreEqual(string.Empty, rig.Log.Problems);
        Assert.AreEqual(0, rig.Earns);
        Assert.IsFalse(rig.Quest.IsRewardPending);
    }

    sealed class Rig : IDisposable
    {
        internal readonly Player Player;
        internal readonly NpcCharacter Target;
        internal PlayerQuest Quest = null!;
        internal readonly Logger Log = new();
        internal readonly QuestService Quests;
        internal int Earns;
        readonly QuestDungeonPlayfield _world = Blank<QuestDungeonPlayfield>();
        readonly PlayfieldManager _manager = Blank<PlayfieldManager>();
        readonly DynelRegistry _registry = new();
        readonly Dictionary<int, Player> _players = new();
        readonly InventoryFlushService _flush;
        readonly ServiceProvider _services;
        readonly Items _items = new();
        readonly string _parametersJson;

        internal Rig()
        {
            string root = GameDataPaths.ResolveRuntimeRoot();
            var data = new GameDataStore(Log, null, root);
            var layouts = new DungeonLayoutGenerator(data, Log);
            ResolvedDungeon? resolved = layouts.IsAvailable ? layouts.Resolve(null, 18, out _) : null;
            if (resolved == null)
            {
                Assert.Inconclusive("The runtime GameData at " + root + " cannot generate mission dungeons.");
                return;
            }

            var parameters = new QuestDungeonParameters
            {
                Seed = 1656684152, Quality = 18, MissionType = 1, EntranceInstance = -1073741024,
                DungeonPlayfield = QuestDungeonIds.FirstPlayfield + 100
            };
            Assert.IsTrue(layouts.ConfigureNewDungeon(parameters, resolved, false, 0, out string? problem), problem);
            parameters.TokenReward = null;
            parameters.ObjectiveNpc = new MissionNpcObjectiveBinding
            {
                QuestId = QuestId, OwnerId = CharacterId, Instance = TargetInstance, NpcHash = "BGMG", Quality = 22,
                RoomIndex = 12, X = 152.2f, Y = 5f, Z = 33.2f, MissionType = 1
            };
            _parametersJson = parameters.ToJson();
            Assert.IsTrue(QuestDungeonParameters.TryParse(_parametersJson, out _), "The fixture's mission parameters must parse.");

            Set(_manager, "_sync", new Lock());
            Set(_manager, "_playersByCharacterId", _players);
            Set(_manager, "_playfields", new Dictionary<int, Playfield> { [parameters.DungeonPlayfield] = _world });
            Set(_world, "<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield, Instance = parameters.DungeonPlayfield });
            Set(_world, "<QuestId>k__BackingField", QuestId);
            Set(_world, "_tickSync", new Lock());
            Set(_world, "_lifecycleSync", new Lock());
            Set(_world, "_inbound", new PlayfieldInboundQueue());
            Set(_world, "_dynelRegistry", _registry);
            Set(_world, "_logger", Log);
            Set(_world, "_playfieldManager", _manager);
            _services = new ServiceCollection().AddSingleton(_registry).AddSingleton(new WorldSimulationAccess()).BuildServiceProvider();
            Set(_world, "_serviceProvider", _services);

            Player = AddPlayer(CharacterId);
            Target = AddNpc(TargetInstance);

            _flush = new InventoryFlushService(id => _players.GetValueOrDefault(id), () => _players.Values.ToArray(), new Persistence(), Log);
            Quests = new QuestService(new QuestCatalog(data, Log), new Store(), _items, _flush, Log, () => DateTime.UtcNow, MissionDao(),
                new CharacterSnapshotService(Fake<ICharacterRepository>(), Fake<IStatRepository>(), Log));
            var template = new QuestTemplate { Hash = QuestId, Name = "Locate a person (QL 18)", Action = QuestTemplate.TargetNpcAction };
            template.Objective.Npc = "BGMG";
            DateTime now = DateTime.UtcNow;
            OnTick(() => Quest = Quests.AdoptGenerated(Player, QuestId, template, now, now.AddDays(2), _parametersJson));
            Log.Reset();
        }

        internal Player AddPlayer(int id)
        {
            var session = new Session();
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, Log, _items) { FirstName = "Seeker" + id };
            player.Inventory.Apply(new CharacterHydrationResult(), id, _items);
            player.Stats.Set(CharacterStat.Level, 23);
            player.Stats.Set(CharacterStat.Health, 100);
            player.Stats.Set(CharacterStat.MaxHealth, 100);
            player.QuestLog = new QuestLog();
            player.EnterOnline(session);
            player.Playfield = _world;
            player.Position = new Vector3(150f, 5f, 33f);
            _registry.Register(player);
            _players[id] = player;
            return player;
        }

        internal NpcCharacter AddNpc(int instance)
        {
            var npc = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = instance }, _items)
            {
                Name = "Slimefang Banescale", Playfield = _world, Position = new Vector3(152.2f, 5f, 33.2f), SpawnHash = "BGMG"
            };
            _registry.Register(npc);
            return npc;
        }

        /// <summary>Lets every queued mission transaction run, then its result apply on the playfield's tick.</summary>
        internal void Settle()
        {
            foreach (Player player in _players.Values)
                _flush.HardFlush(player);
            OnTick(() => ((PlayfieldInboundQueue)Get(_world, "_inbound")).Drain(null!, null!, _world));
        }

        internal void OnTick(Action work)
        {
            lock ((Lock)Get(_world, "_tickSync"))
                work();
        }

        /// <summary>
        /// One stored mission row, read as the database would hold it: once earned, its progress is full, so a second
        /// earn transaction finds the objective no longer collectible.
        /// </summary>
        IMissionDao MissionDao()
        {
            CharacterQuestCompletionSnapshotData Stored() => new()
            {
                CharacterId = CharacterId, QuestId = QuestId, Source = (int)QuestSource.Generated,
                OwnerType = (int)QuestOwnerType.Character, OwnerId = CharacterId,
                State = (int)QuestState.Active, Progress = Math.Min(Earns, 1), RequiredCount = 1,
                AssignedAtUtcTicks = Quest.AssignedAtUtc.Ticks, AcgBuildingGeneratorJson = _parametersJson,
                DefinitionJson = "{}"
            };

            ICompletionTransaction transaction = Fake<ICompletionTransaction>((method, args) => method.Name switch
            {
                "get_CharacterId" => CharacterId,
                nameof(ICharacterQuestCompletionTransaction.ReadCharacterQuest) => Stored(),
                nameof(ICharacterQuestCompletionTransaction.TryEarnCharacterQuest) => Earn(),
                _ => throw new AssertFailedException("Unexpected mission transaction call " + method.Name)
            });
            CharacterQuestCompletionResult Earn()
            {
                Interlocked.Increment(ref Earns);
                return new CharacterQuestCompletionResult { Status = CharacterQuestCompletionStatus.Earned, Quest = Stored() };
            }
            return Fake<IMissionDao>((method, args) =>
            {
                if (method.Name != nameof(IMissionDao.Execute))
                    throw new AssertFailedException("Unexpected mission DAO call " + method.Name);
                try
                {
                    return ((Delegate)args![^1]!).DynamicInvoke(transaction);
                }
                catch (TargetInvocationException failure) when (failure.InnerException != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.InnerException).Throw();
                    throw;
                }
            });
        }

        public void Dispose()
        {
            _flush.Dispose();
            _services.Dispose();
        }
    }

    sealed class Store : ICharacterQuestStore
    {
        public bool TryAssignDungeonQuest(int ownerId, GeneratedQuestRow quest, CharacterQuestRow characterRow,
            QuestDungeonKeyItemRow key, QuestDungeonKeyItemRow? sourceItem) => throw new AssertFailedException("No acceptance here.");
        public IList<CharacterQuestRow> Load(int characterId) => [];
        public IList<GeneratedQuestRow> LoadGeneratedFor(int characterId) => [];
        public GeneratedQuestRow? LoadGenerated(string questId) => null;
        public void Save(CharacterQuestRow row) { }
        public void SaveGenerated(GeneratedQuestRow row) { }
        public bool TryAcceptOfferQuest(int ownerId, int offerType, int offerInstance, GeneratedQuestRow quest,
            CharacterQuestRow characterRow, QuestDungeonKeyItemRow key, long nowUtcTicks) => throw new AssertFailedException("No terminal offer here.");
        public string? TryAddDuplicateKey(int sourceKeyInstanceId, QuestDungeonKeyItemRow copy, int maxKeys, long nowUtcTicks) => null;
        public IDictionary<int, string> LoadKeyQuests(IReadOnlyCollection<int> keyInstanceIds) => new Dictionary<int, string>();
        public IList<RetiredDungeonKey> EndQuestKeys(string questId) => [];
        public IList<int> RetireUnlinkedKeys(IReadOnlyCollection<int> keyInstanceIds, int keyLowId) => [];
        public int RetireDeadKeysForCharacter(int characterId, int keyLowId, IReadOnlyCollection<int> ownedContainerTypes, int bagContainerType) => 0;
        public IList<string> LoadExpiredQuestsWithKeys(long nowUtcTicks, int limit) => [];
        public int PurgeEndedGeneratedQuests(long cutoffUtcTicks, int limit) => 0;
        public void CloseCharacterQuests(string questId, int state, long nowUtcTicks) { }
    }

    sealed class Logger : IZoneLogger
    {
        readonly object _sync = new();
        string _problems = string.Empty;

        internal string Problems { get { lock (_sync) return _problems; } }

        internal void Reset()
        {
            lock (_sync)
                _problems = string.Empty;
        }

        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { lock (_sync) _problems += "WARN " + message + Environment.NewLine; }
        public void Error(string message) { lock (_sync) _problems += "ERROR " + message + Environment.NewLine; }
        public void Error(Exception exception, string message) { lock (_sync) _problems += "ERROR " + message + " " + exception + Environment.NewLine; }
        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }

    sealed class Session : IZoneSession
    {
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public bool TransferToPlayfield(Playfield destination, Vector3 landing) => false;
        public void Send(byte[] packet) { }
        public void Send(MessageBody body) { }
        public void Send(MessageBody body, int sender, int receiver) { }
        public void SendInitiateCompression() { }
        public void Close() => State = SessionState.Closed;
    }

    sealed class Persistence : ICharacterCoalesceCommit, IInventoryMutationPersistence, ITradePersistence
    {
        public void Persist(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates, int characterId,
            IReadOnlyList<int> uploadedNanoIds, IReadOnlyList<ActiveNanoRecord>? activeNanos, IReadOnlyList<SkillLockRecord>? skillLocks) { }
        public void Persist(InventoryMutationBatch batch) { }
        public void Persist(TradePersistenceBatch batch) { }
    }

    sealed class Items : IItemBuilder
    {
        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null) => new()
            {
                LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount,
                InstanceId = instanceId, Identity = identity ?? Identity.None, Definition = CreateTemplate(lowId, highId, quality)
            };

        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
            => throw new NotSupportedException();

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }

    /// <summary>The transaction the quest completion code casts the mission DAO's transaction to.</summary>
    public interface ICompletionTransaction : IMissionDaoTransaction, ICharacterQuestCompletionTransaction
    {
    }

    public class FakeProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?>? Handler;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Handler != null ? Handler(targetMethod!, args) : throw new AssertFailedException("Unexpected call " + targetMethod!.Name);
    }

    static T Fake<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        T proxy = DispatchProxy.Create<T, FakeProxy>();
        ((FakeProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    static T Blank<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    static object Get(object target, string name) => Field(target.GetType(), name).GetValue(target)!;

    static void Set(object target, string name, object value) => Field(target.GetType(), name).SetValue(target, value);

    static FieldInfo Field(Type type, string name)
    {
        for (Type? at = type; at != null; at = at.BaseType)
            if (at.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) is FieldInfo field)
                return field;
        throw new MissingFieldException(type.Name, name);
    }
}
