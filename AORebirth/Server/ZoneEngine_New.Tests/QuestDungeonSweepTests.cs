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

using LostEden.Protocol.GameData;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Utility.GameData.Missions;

using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Quests;
using ZoneEngine_New.Core.Quests.Dungeons;

/// <summary>
/// The expiry half of the dungeon sweep. Live (2026-11-21) one expired quest whose mission artifact had been sold threw
/// on every sweep and, being the oldest in the batch, held back thirteen later expiries and the purge for a day.
/// Runs against the runtime GameData (AO_REBIRTH_GAMEDATA_PATH) like the other dungeon tests.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class QuestDungeonSweepTests
{
    [TestMethod]
    public void OneFailingExpiredQuestDoesNotHoldBackTheOthersOrThePurge()
    {
        using var rig = new Rig();
        rig.Store.Expired.AddRange(["EXPIRED-BAD", "EXPIRED-GOOD"]);
        rig.Store.BeforeEndKeys = questId =>
        {
            if (questId == "EXPIRED-BAD")
                throw new InvalidOperationException("The mission artifact is no longer held by its exact owner.");
        };

        rig.Service.Sweep();

        CollectionAssert.AreEqual(new[] { "EXPIRED-BAD", "EXPIRED-GOOD" }, rig.Store.Ended, "both expired quests are attempted, oldest first");
        CollectionAssert.AreEqual(new[] { "EXPIRED-GOOD" }, rig.Store.Closed, "the quest that ended is closed; the failed one keeps its rows for the retry");
        Assert.AreEqual(1, rig.Store.PurgeCalls, "the purge still runs after a failed quest");
        StringAssert.Contains(rig.Log.Problems, "EXPIRED-BAD");
        Assert.IsFalse(rig.Log.Problems.Contains("Quest dungeon sweep failed", StringComparison.Ordinal),
            "the failure is confined to its quest, not reported as the whole sweep failing");
    }

    [TestMethod]
    public void ExpiredQuestsEndAndCloseInOrder()
    {
        using var rig = new Rig();
        rig.Store.Expired.AddRange(["EXPIRED-1", "EXPIRED-2"]);

        rig.Service.Sweep();

        CollectionAssert.AreEqual(new[] { "EXPIRED-1", "EXPIRED-2" }, rig.Store.Ended);
        CollectionAssert.AreEqual(new[] { "EXPIRED-1", "EXPIRED-2" }, rig.Store.Closed);
        Assert.AreEqual(1, rig.Store.PurgeCalls);
        Assert.AreEqual(string.Empty, rig.Log.Problems);
    }

    [TestMethod]
    public void TheNextSweepRetriesAQuestThatFailed()
    {
        using var rig = new Rig();
        rig.Store.Expired.Add("EXPIRED-FLAKY");
        int attempts = 0;
        rig.Store.BeforeEndKeys = _ => { if (attempts++ == 0) throw new InvalidOperationException("transient"); };

        rig.Service.Sweep();
        rig.Service.Sweep();

        Assert.AreEqual(2, attempts, "the sweep gate is released after a failed quest so the next interval retries it");
        CollectionAssert.AreEqual(new[] { "EXPIRED-FLAKY" }, rig.Store.Closed);
        Assert.AreEqual(2, rig.Store.PurgeCalls);
    }

    sealed class Rig : IDisposable
    {
        internal readonly Store Store = new();
        internal readonly Logger Log = new();
        internal readonly QuestDungeonService Service;
        readonly PlayfieldManager _manager = Blank<PlayfieldManager>();
        readonly InventoryFlushService _flush;

        internal Rig()
        {
            string root = GameDataPaths.ResolveRuntimeRoot();
            var data = new GameDataStore(Log);
            var items = new Items();
            var ids = new Ids();
            var layouts = new DungeonLayoutGenerator(data, Log);
            if (!layouts.IsAvailable)
                Assert.Inconclusive("The runtime GameData at " + root + " cannot generate mission dungeons.");

            // An empty manager: the sweep's playfield half finds no dungeons and no players; its database half is the subject.
            Set(_manager, "_sync", new Lock());
            Set(_manager, "_playfields", new Dictionary<int, Playfield>());
            Set(_manager, "_playersByCharacterId", new Dictionary<int, Player>());
            Set(_manager, "_dungeonEmptySince", new Dictionary<int, long>());
            var manager = new Lazy<PlayfieldManager>(() => _manager);

            var persistence = new Persistence();
            _flush = new InventoryFlushService(manager, persistence, Log);
            var actions = new InventoryActionService(persistence, _flush, ids, Log);
            var quests = new QuestService(new QuestCatalog(data, Log), Store, items, _flush, Log);
            Service = new QuestDungeonService(Store, quests, MissionDestinationCatalog.Load(root), layouts, items, ids,
                _flush, actions, manager, data, Log);
            Log.Reset();
        }

        public void Dispose()
        {
            Service.Dispose();
            _flush.Dispose();
        }
    }

    sealed class Store : ICharacterQuestStore
    {
        internal readonly List<string> Expired = [];
        internal readonly List<string> Ended = [];
        internal readonly List<string> Closed = [];
        internal int PurgeCalls;
        internal Action<string>? BeforeEndKeys;

        public IList<string> LoadExpiredQuestsWithKeys(long nowUtcTicks, int limit) => Expired.ToArray();
        // No generated row: the sweep takes the keys-only path, which is where the live failure surfaced.
        public GeneratedQuestRow? LoadGenerated(string questId) => null;
        public IList<RetiredDungeonKey> EndQuestKeys(string questId)
        {
            Ended.Add(questId);
            BeforeEndKeys?.Invoke(questId);
            return [];
        }
        public void CloseCharacterQuests(string questId, int state, long nowUtcTicks)
        {
            Assert.AreEqual((int)QuestState.Expired, state);
            Closed.Add(questId);
        }
        public int PurgeEndedGeneratedQuests(long cutoffUtcTicks, int limit) { PurgeCalls++; return 0; }

        public bool TryAssignDungeonQuest(int ownerId, GeneratedQuestRow quest, CharacterQuestRow characterRow,
            QuestDungeonKeyItemRow key, QuestDungeonKeyItemRow? sourceItem) => throw new AssertFailedException();
        public IList<CharacterQuestRow> Load(int characterId) => [];
        public IList<GeneratedQuestRow> LoadGeneratedFor(int characterId) => [];
        public void Save(CharacterQuestRow row) { }
        public void SaveGenerated(GeneratedQuestRow row) { }
        public bool TryAcceptOfferQuest(int ownerId, int offerType, int offerInstance, GeneratedQuestRow quest,
            CharacterQuestRow characterRow, QuestDungeonKeyItemRow key, long nowUtcTicks) => throw new AssertFailedException();
        public string? TryAddDuplicateKey(int sourceKeyInstanceId, QuestDungeonKeyItemRow copy, int maxKeys, long nowUtcTicks) => null;
        public IDictionary<int, string> LoadKeyQuests(IReadOnlyCollection<int> keyInstanceIds) => new Dictionary<int, string>();
        public IList<int> RetireUnlinkedKeys(IReadOnlyCollection<int> keyInstanceIds, int keyLowId) => [];
        public int RetireDeadKeysForCharacter(int characterId, int keyLowId, IReadOnlyCollection<int> ownedContainerTypes, int bagContainerType) => 0;
    }

    sealed class Logger : IZoneLogger
    {
        readonly object _sync = new();
        string _problems = string.Empty;

        internal string Problems { get { lock (_sync) return _problems; } }
        internal void Reset() { lock (_sync) _problems = string.Empty; }

        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { lock (_sync) _problems += "ERROR " + message + Environment.NewLine; }
        public void Error(Exception exception, string message) { lock (_sync) _problems += "ERROR " + message + " " + exception + Environment.NewLine; }
        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
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

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }

    sealed class Ids : IItemInstanceIdAllocator
    {
        int _next = 800000;
        public int Allocate() => Interlocked.Increment(ref _next);
    }

    sealed class Persistence : ICharacterCoalesceCommit, IInventoryMutationPersistence
    {
        public void Persist(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates, int characterId,
            IReadOnlyList<int> uploadedNanoIds, IReadOnlyList<ActiveNanoRecord>? activeNanos, IReadOnlyList<SkillLockRecord>? skillLocks) { }
        public void Persist(InventoryMutationBatch batch) { }
    }

    static T Blank<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    static void Set(object target, string name, object value)
        => (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("No field " + name + " on " + target.GetType().Name)).SetValue(target, value);
}
