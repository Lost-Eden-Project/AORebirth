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
using LostEden.Protocol.Messages.N3Messages;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.MessageHandlers;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.Quests;
using ZoneEngine_New.Core.WorldSimulation;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Key to the Garden of Gilthar (Adonis): "Find still intact Ark." (5DNY) and "Find wrecked Ark." (DYOQ) are Quests.json
/// Find Item steps on world items standing in Adonis for everyone (the Ancient Control Panels, Spawns.json hash spawns).
/// Live closed each step the moment the player selected (LookAt) its panel, taking nothing. Nothing on the server
/// completed them: only a generated mission's owner-bound dungeon objective earned a Find Item. Selecting a world item
/// the step's hash covers now completes it, through the LookAt handler as the client drives it. Runs against the
/// runtime GameData (AO_REBIRTH_GAMEDATA_PATH).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class OutdoorFindItemTargetTests
{
    const string IntactArkStep = "5DNY";
    const string WreckedArkStep = "DYOQ";
    const int IntactPanelTemplate = 236702;
    const int WreckedPanelTemplate = 236705;
    const int CharacterId = 4872001;

    [TestMethod]
    public void SelectingTheArkCompletesTheStepAndLeavesTheItemInTheWorld()
    {
        using var rig = new Rig();
        WorldItem panel = rig.PlaceItem(IntactPanelTemplate, sent: true);
        rig.Give(rig.Player, IntactArkStep);

        rig.LookAt(rig.Player, panel);
        rig.Settle();

        Assert.AreEqual(string.Empty, rig.Log.Problems);
        Assert.IsFalse(rig.Player.IsPersistenceQuarantined);
        Assert.AreEqual(QuestState.Completed, rig.Quest(rig.Player, IntactArkStep).State, "Selecting the panel finishes the step.");
        Assert.IsTrue(rig.Quest(rig.Player, WreckedArkStep).IsActive, "The journal moves on to the next step.");
        Assert.AreEqual(1, rig.Completions, "One stored completion.");
        Assert.IsTrue(rig.IsInWorld(panel), "Nothing is picked up: the panel stays for every other holder.");
        Assert.AreEqual(0, rig.Session(rig.Player).Sent.OfType<AddTemplateMessage>().Count(), "No item lands in the inventory.");
    }

    /// <summary>The two steps in order: each panel completes only the step whose hash covers it, and only once.</summary>
    [TestMethod]
    public void EachPanelCompletesOnlyTheStepThatCoversItAndOnlyOnce()
    {
        using var rig = new Rig();
        WorldItem intact = rig.PlaceItem(IntactPanelTemplate, sent: true);
        WorldItem wrecked = rig.PlaceItem(WreckedPanelTemplate, sent: true);
        rig.Give(rig.Player, IntactArkStep);

        rig.LookAt(rig.Player, wrecked);
        rig.Settle();
        Assert.IsTrue(rig.Quest(rig.Player, IntactArkStep).IsActive, "The wrecked Ark does not finish 'Find still intact Ark.'");
        Assert.AreEqual(0, rig.Completions);

        rig.LookAt(rig.Player, intact);
        rig.Settle();
        Assert.AreEqual(QuestState.Completed, rig.Quest(rig.Player, IntactArkStep).State);
        Assert.IsTrue(rig.Quest(rig.Player, WreckedArkStep).IsActive);
        Assert.AreEqual(1, rig.Completions);

        rig.LookAt(rig.Player, intact);
        rig.Settle();
        Assert.AreEqual(1, rig.Completions, "Selecting the intact panel again earns nothing more.");
        Assert.IsTrue(rig.Quest(rig.Player, WreckedArkStep).IsActive);

        rig.LookAt(rig.Player, wrecked);
        rig.Settle();
        Assert.AreEqual(string.Empty, rig.Log.Problems);
        Assert.AreEqual(QuestState.Completed, rig.Quest(rig.Player, WreckedArkStep).State, "The wrecked Ark finishes its own step.");
        Assert.AreEqual(2, rig.Completions);
        Assert.IsTrue(rig.IsInWorld(intact) && rig.IsInWorld(wrecked));
    }

    /// <summary>Two LookAts on the same tick, before anything is stored: one completion.</summary>
    [TestMethod]
    public void SelectingTwiceOnOneTickCompletesOnce()
    {
        using var rig = new Rig();
        WorldItem panel = rig.PlaceItem(IntactPanelTemplate, sent: true);
        rig.Give(rig.Player, IntactArkStep);

        rig.OnTick(() =>
        {
            rig.Look(rig.Player, panel);
            rig.Look(rig.Player, panel);
        });
        rig.Settle();

        Assert.AreEqual(string.Empty, rig.Log.Problems);
        Assert.IsFalse(rig.Player.IsPersistenceQuarantined);
        Assert.AreEqual(1, rig.Completions);
        Assert.AreEqual(1, rig.Journal(rig.Player).Count(quest => quest.QuestId == WreckedArkStep), "The next step is given once.");
    }

    /// <summary>A LookAt naming a world item this client was never sent (a spoofed identity) completes nothing.</summary>
    [TestMethod]
    public void AnItemTheClientWasNotSentCompletesNothing()
    {
        using var rig = new Rig();
        WorldItem unseen = rig.PlaceItem(IntactPanelTemplate, sent: false);
        rig.Give(rig.Player, IntactArkStep);

        rig.LookAt(rig.Player, unseen);
        rig.Settle();

        Assert.AreEqual(string.Empty, rig.Log.Problems);
        Assert.IsTrue(rig.Quest(rig.Player, IntactArkStep).IsActive);
        Assert.AreEqual(0, rig.Completions);
    }

    /// <summary>The panel stands there for everyone: a second holder of the step completes it on the same panel.</summary>
    [TestMethod]
    public void ASecondHolderCompletesOnTheSamePanel()
    {
        using var rig = new Rig();
        WorldItem panel = rig.PlaceItem(IntactPanelTemplate, sent: true);
        Player other = rig.AddPlayer(CharacterId + 1);
        rig.Give(rig.Player, IntactArkStep);
        rig.Give(other, IntactArkStep);

        rig.LookAt(rig.Player, panel);
        rig.Settle();
        Assert.AreEqual(QuestState.Completed, rig.Quest(rig.Player, IntactArkStep).State);
        Assert.IsTrue(rig.Quest(other, IntactArkStep).IsActive, "Another player's selection completes nothing for this holder.");

        rig.LookAt(other, panel);
        rig.Settle();

        Assert.AreEqual(string.Empty, rig.Log.Problems);
        Assert.AreEqual(QuestState.Completed, rig.Quest(other, IntactArkStep).State);
        Assert.AreEqual(2, rig.Completions);
        Assert.IsTrue(rig.IsInWorld(panel));
    }

    sealed class Rig : IDisposable
    {
        internal readonly Player Player;
        internal readonly Logger Log = new();
        internal readonly QuestService Quests;
        internal int Completions;
        readonly Playfield _world = Blank<Playfield>();
        readonly PlayfieldManager _manager = Blank<PlayfieldManager>();
        readonly DynelRegistry _registry = new();
        readonly PlayfieldLocality _locality;
        readonly Dictionary<int, Player> _players = new();
        readonly Dictionary<Player, Session> _sessions = new();
        readonly InventoryFlushService _flush;
        readonly ServiceProvider _services;
        readonly Items _items = new();
        readonly LookAtMessageHandler _handler = new();
        const int PlayfieldId = 4872;

        internal Rig()
        {
            string root = GameDataPaths.ResolveRuntimeRoot();
            var data = new GameDataStore(Log, null, root);
            var catalog = new QuestCatalog(data, Log);
            if (!catalog.TryGet(IntactArkStep, out QuestTemplate intact) || !intact.IsFindItem || intact.Objective.WorldItem == null
                || !catalog.TryGet(WreckedArkStep, out QuestTemplate wrecked) || !wrecked.IsFindItem || wrecked.Objective.WorldItem == null)
            {
                Assert.Inconclusive("The runtime GameData at " + root + " has no Find Item steps " + IntactArkStep + "/" + WreckedArkStep + ".");
                return;
            }

            Set(_manager, "_sync", new Lock());
            Set(_manager, "_playersByCharacterId", _players);
            Set(_manager, "_playfields", new Dictionary<int, Playfield> { [PlayfieldId] = _world });
            Set(_world, "<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = PlayfieldId });
            Set(_world, "_tickSync", new Lock());
            Set(_world, "_lifecycleSync", new Lock());
            Set(_world, "_inbound", new PlayfieldInboundQueue());
            Set(_world, "_dynelRegistry", _registry);
            Set(_world, "_logger", Log);
            Set(_world, "_playfieldManager", _manager);
            _locality = new PlayfieldLocality(PlayfieldId, null);

            _flush = new InventoryFlushService(id => _players.GetValueOrDefault(id), () => _players.Values.ToArray(), new Persistence(), Log);
            Quests = new QuestService(catalog, new Store(), _items, _flush, Log, () => DateTime.UtcNow, MissionDao(),
                new CharacterSnapshotService(Fake<ICharacterRepository>(), Fake<IStatRepository>(), Log));
            _services = new ServiceCollection().AddSingleton(_registry).AddSingleton(_locality).AddSingleton<IGameData>(data)
                .AddSingleton(Quests).AddSingleton(new WorldSimulationAccess()).BuildServiceProvider();
            Set(_world, "_serviceProvider", _services);

            Player = AddPlayer(CharacterId);
            Log.Reset();
        }

        internal Player AddPlayer(int id)
        {
            var session = new Session();
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, Log, _items) { FirstName = "Seeker" + id };
            player.Inventory.Apply(new CharacterHydrationResult(), id, _items);
            player.Stats.Set(CharacterStat.Level, 180);
            player.Stats.Set(CharacterStat.Health, 100);
            player.Stats.Set(CharacterStat.MaxHealth, 100);
            player.QuestLog = new QuestLog();
            player.EnterOnline(session);
            player.Playfield = _world;
            player.Position = new Vector3(2800f, 151f, 2530f);
            _registry.Register(player);
            _players[id] = player;
            _sessions[player] = session;
            OnTick(() =>
            {
                _locality.RegisterDynel(player);
                _locality.ActivatePlayerVisibility(player);
            });
            return player;
        }

        /// <summary>
        /// A world item of <paramref name="templateId"/> next to the players, as a Spawns.json hash spawn stands. With
        /// <paramref name="sent"/> false it is registered but never shown to any client.
        /// </summary>
        internal WorldItem PlaceItem(int templateId, bool sent)
        {
            var item = new WorldItem(_registry.AllocateStaticDynelIdentity(), _items.CreateTemplate(templateId, templateId, 1), templateId, templateId, 1)
            {
                Playfield = _world, Position = new Vector3(2803.6f, 151.2f, 2532.4f)
            };
            OnTick(() =>
            {
                _registry.Register(item);
                if (!sent)
                    return;
                _locality.RegisterDynel(item);
                foreach (Player player in _players.Values)
                {
                    _locality.ActivatePlayerVisibility(player);
                    Assert.IsTrue(_locality.IsVisibleTo(item, player), "fixture: the client was sent the item");
                }
            });
            return item;
        }

        internal void Give(Player player, string hash)
            => OnTick(() => Assert.IsTrue(Quests.TryAssignTemplate(player, hash, out string given), given));

        /// <summary>The client's LookAt on <paramref name="item"/>, on the playfield's tick as the handler runs.</summary>
        internal void LookAt(Player player, WorldItem item) => OnTick(() => Look(player, item));

        internal void Look(Player player, WorldItem item)
            => _handler.Handle(new LookAtMessage { Identity = player.Identity, Target = item.Identity, Unknown1 = 1 }, _sessions[player]);

        internal PlayerQuest Quest(Player player, string hash)
            => Quests.GetLog(player).Quests.Values.Single(quest => quest.QuestId == hash);

        internal IEnumerable<PlayerQuest> Journal(Player player) => Quests.GetLog(player).Quests.Values;

        internal Session Session(Player player) => _sessions[player];

        internal bool IsInWorld(WorldItem item) => _registry.TryGet(item.Identity, out Dynel? registered) && ReferenceEquals(registered, item);

        /// <summary>Lets every queued quest transaction run, then its result apply on the playfield's tick.</summary>
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

        /// <summary>Runs every transaction against a store that holds each player's quests as the tick shows them.</summary>
        IMissionDao MissionDao()
        {
            CharacterQuestCompletionSnapshotData Stored(int characterId, string questId)
            {
                PlayerQuest quest = Quests.GetLog(_players[characterId]).Quests.Values.Single(held => held.QuestId == questId);
                return new CharacterQuestCompletionSnapshotData
                {
                    CharacterId = characterId, QuestId = questId, Source = (int)quest.Source,
                    AssignedAtUtcTicks = quest.AssignedAtUtc.Ticks, AcgBuildingGeneratorJson = quest.AcgBuildingGeneratorJson!
                };
            }

            ICompletionTransaction Transaction(int characterId) => Fake<ICompletionTransaction>((method, args) => method.Name switch
            {
                "get_CharacterId" => characterId,
                nameof(ICharacterQuestCompletionTransaction.ReadCharacterQuest) => Stored(characterId, (string)args![0]!),
                nameof(ICharacterQuestCompletionTransaction.TryEarnCharacterQuest) => new CharacterQuestCompletionResult
                {
                    Status = CharacterQuestCompletionStatus.Earned, Quest = Stored(characterId, ((CharacterQuestEarnRequest)args![0]!).QuestId)
                },
                nameof(ICharacterQuestCompletionTransaction.TryCompleteCharacterQuest) => Complete(),
                _ => throw new AssertFailedException("Unexpected mission transaction call " + method.Name)
            });
            CharacterQuestCompletionResult Complete()
            {
                Interlocked.Increment(ref Completions);
                return new CharacterQuestCompletionResult { Status = CharacterQuestCompletionStatus.Applied };
            }
            return Fake<IMissionDao>((method, args) =>
            {
                if (method.Name != nameof(IMissionDao.Execute))
                    throw new AssertFailedException("Unexpected mission DAO call " + method.Name);
                try
                {
                    return ((Delegate)args![^1]!).DynamicInvoke(Transaction((int)args[0]!));
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
            QuestDungeonKeyItemRow key, QuestDungeonKeyItemRow? sourceItem) => throw new AssertFailedException("No dungeon here.");
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
        internal readonly List<MessageBody> Sent = [];
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public bool TransferToPlayfield(Playfield destination, Vector3 landing) => false;
        public void Send(byte[] packet) { }
        public void Send(MessageBody body) => Sent.Add(body);
        public void Send(MessageBody body, int sender, int receiver) => Send(body);
        public void SendInitiateCompression() { }
        public void Close() => State = SessionState.Closed;
    }

    /// <summary>Accepts every write: these tests are about what the tick decides, not what is stored.</summary>
    sealed class Persistence : ICharacterCoalesceCommit, IInventoryMutationPersistence, ITradePersistence
    {
        public void Persist(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates, int characterId,
            IReadOnlyList<int> uploadedNanoIds, IReadOnlyList<ActiveNanoRecord>? activeNanos, IReadOnlyList<SkillLockRecord>? skillLocks) { }
        public void Persist(InventoryMutationBatch batch) { }
        public void Persist(TradePersistenceBatch batch) { }
    }

    sealed class Items : IItemBuilder
    {
        int _next = 700000;

        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null) => new()
            {
                LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount,
                InstanceId = instanceId, Identity = identity ?? Identity.None, Definition = CreateTemplate(lowId, highId, quality)
            };

        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
        {
            Item item = Create(lowId, highId, quality, source, stackCount);
            item.AssignInstanceId(++_next);
            return item;
        }

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality, Name = "Ancient Control Panel" };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }

    /// <summary>The transaction the quest completion code casts the mission DAO's transaction to.</summary>
    public interface ICompletionTransaction : IMissionDaoTransaction, ICharacterQuestCompletionTransaction
    {
    }

    /// <summary>An interface stub: every call goes to <see cref="Handler"/>, or fails the test when there is none.</summary>
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
