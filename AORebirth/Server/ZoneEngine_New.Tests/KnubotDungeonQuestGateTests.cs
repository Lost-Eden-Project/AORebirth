namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.IO;
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

using Utility.GameData.Missions;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Knubot;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Quests;
using ZoneEngine_New.Core.Quests.Dungeons;
using ZoneEngine_New.Core.Trade;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// A Knubot line applies its effects under the player's persistence gate, and accepting a dungeon quest hard-flushes
/// the player's lane first, which must never run under that gate. The Unicorn Recruiter's {givequest 829N} threw
/// "A hard flush must not run under the player's persistence gate" and gave nothing. The give is now queued on the
/// player's playfield and runs once the gate is released. Runs against the runtime GameData (AO_REBIRTH_GAMEDATA_PATH).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class KnubotDungeonQuestGateTests
{
    const string QuestHash = "829N";
    const string RecruiterHash = "UIRC";
    const int PlayfieldId = 4364;
    const int CharacterId = 4242;

    [TestMethod]
    public void RecruiterAnswerGivesTheDungeonQuestAndKeyAfterTheGate()
    {
        using var rig = new Rig();
        rig.OnTick(() => Assert.IsTrue(rig.Knubot.TryOpen(rig.Session, rig.Npc.Identity)));
        rig.Settle();
        rig.OnTick(() => Assert.IsTrue(rig.Knubot.TryAnswer(rig.Session, rig.Npc.Identity, rig.Option("I want to visit"))));
        rig.Settle();
        int easy = rig.Option("Take it easy on us");

        // Answered as inbound work, as a client's KnuBotAnswer is: the deferred give drains in the same pass.
        bool handled = false;
        int assignedInsideAnswer = -1;
        rig.Inbound(() =>
        {
            handled = rig.Knubot.TryAnswer(rig.Session, rig.Npc.Identity, easy);
            assignedInsideAnswer = rig.Store.Assigned;
        });
        rig.Drain();

        Assert.IsTrue(handled);
        Assert.AreEqual(0, assignedInsideAnswer, "Nothing may be committed while the dialog holds the player's gate.");
        Assert.AreEqual(string.Empty, rig.Log.Problems, "No warning or error: the give ran after the gate.");
        Assert.AreEqual(1, rig.Store.Assigned);
        Assert.IsTrue(rig.Quests.GetLog(rig.Player).TryFind(QuestHash, out PlayerQuest? quest) && quest.IsActive);
        Assert.AreEqual(QuestSource.Generated, quest.Source);
        Item key = rig.Player.Inventory.Inventory.Content.Values.Single(item => item.LowId == QuestDungeonService.KeyLowId);
        Assert.AreEqual(rig.Store.Key!.InstanceId, key.InstanceId);
        Assert.IsTrue(rig.Log.Information.Any(line => line.StartsWith("Dungeon quest accepted", StringComparison.Ordinal)
            && line.Contains("template=" + QuestHash, StringComparison.Ordinal)));
        Assert.IsFalse(rig.Player.IsPersistenceQuarantined);
    }

    const string Easy = "Take it easy on us";
    const string Medium = "We can handle our business";
    const string Hard = "I want the mission Unicorns are too chicken";

    /// <summary>
    /// Team request 24: easy is level-4%, medium level+12%, hard level+53%; above QL 200 only a quarter of the part over
    /// 200 counts, and hard for a player above level 200 is 227 + 1.15 per level over 200. Each answer is given the way
    /// a client answers, and the mission the player receives carries that quality.
    /// </summary>
    [TestMethod]
    public void RecruiterDifficultyGivesTheMothershipAtTheCompressedQuality()
    {
        (int Level, string Reply, int Quality)[] expected =
        [
            (100, Easy, 96), (100, Medium, 112), (100, Hard, 153),
            (150, Easy, 144), (150, Medium, 168), (150, Hard, 207),
            (160, Easy, 154), (160, Medium, 179), (160, Hard, 211),
            (180, Easy, 173), (180, Medium, 200), (180, Hard, 219),
            (200, Easy, 192), (200, Medium, 206), (200, Hard, 227),
            (210, Easy, 200), (210, Medium, 209), (210, Hard, 239),
            (220, Easy, 203), (220, Medium, 212), (220, Hard, 250),
        ];

        using var rig = new Rig();
        var failures = new List<string>();
        foreach ((int level, string reply, int quality) in expected)
        {
            int given = rig.GiveFromRecruiter(level, reply);
            if (given != quality)
                failures.Add("level " + level + " '" + reply + "': QL " + given + ", expected " + quality);
        }

        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
        // The rig has no dungeon style catalog, so the background navmesh prewarm warns; nothing may error.
        Assert.IsFalse(rig.Log.Problems.Contains("ERROR", StringComparison.Ordinal), rig.Log.Problems);
        Assert.IsFalse(rig.Player.IsPersistenceQuarantined);
    }

    /// <summary>
    /// The recruiter's own lines, as GameData writes them: at every player level easy is never above medium nor medium
    /// above hard (strictly below once rounding allows), no difficulty drops as the player levels, and none passes the
    /// highest mission quality.
    /// </summary>
    [TestMethod]
    public void RecruiterDifficultiesStayOrderedAtEveryLevel()
    {
        string path = Path.Combine(GameDataPaths.ResolveRuntimeRoot(), "Knubot", "UnicornRecruiter.json");
        if (!File.Exists(path))
            Assert.Inconclusive("The runtime GameData has no " + path + ".");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        System.Text.Json.JsonElement lines = document.RootElement.GetProperty("Lines");
        KnubotQuestQuality Line(string line)
        {
            string text = string.Join(" ", lines.GetProperty(line).GetProperty("Text").EnumerateArray().Select(piece => piece.GetString()));
            var match = System.Text.RegularExpressions.Regex.Match(text, @"\{givequest " + QuestHash + @" ([^}]*)\}");
            Assert.IsTrue(match.Success, "Line " + line + " gives no " + QuestHash + ": " + text);
            Assert.IsTrue(KnubotQuestQuality.TryParse(match.Groups[1].Value.Split(' '), out KnubotQuestQuality? quality), match.Value);
            return quality!;
        }

        KnubotQuestQuality easy = Line("3"), medium = Line("4"), hard = Line("5");
        int previousEasy = 0, previousMedium = 0, previousHard = 0;
        for (int level = 1; level <= 220; level++)
        {
            int e = easy.Resolve(level), m = medium.Resolve(level), h = hard.Resolve(level);
            Assert.IsTrue(e <= m && m <= h, "level " + level + ": easy " + e + ", medium " + m + ", hard " + h);
            if (level >= 5)
                Assert.IsTrue(e < m && m < h, "level " + level + ": easy " + e + ", medium " + m + ", hard " + h);
            Assert.IsTrue(e >= previousEasy && m >= previousMedium && h >= previousHard, "a difficulty dropped at level " + level);
            Assert.IsTrue(h <= QuestDungeonTemplate.MaxMissionQuality, "hard at level " + level + " is " + h);
            (previousEasy, previousMedium, previousHard) = (e, m, h);
        }
    }

    [TestMethod]
    public void QualityModifiersParseOnlyInTheirWrittenForm()
    {
        Assert.IsTrue(KnubotQuestQuality.TryParse(["level+53%", "pastlevel=200:227+1.15", "softcap=200/4"], out KnubotQuestQuality? both));
        Assert.AreEqual(207, both!.Resolve(150));
        Assert.AreEqual(250, both.Resolve(220));
        Assert.IsTrue(KnubotQuestQuality.TryParse(["level+12%"], out KnubotQuestQuality? plain));
        Assert.AreEqual(246, plain!.Resolve(220), "Without a soft cap the quality is the plain percent.");

        string[][] rejected =
        [
            ["150", "softcap=200/4"],
            ["level", "softcap=200/0"],
            ["level", "softcap=200"],
            ["level", "softcap=200/4", "softcap=150/2"],
            ["level", "pastlevel=200:227"],
            ["level", "pastlevel=200:227+-1"],
            ["level", "pastlevel=:227+1"],
            ["level", "cap=200"],
        ];
        foreach (string[] words in rejected)
            Assert.IsFalse(KnubotQuestQuality.TryParse(words, out _), string.Join(" ", words));
    }

    [TestMethod]
    public void TwoGivesUnderTheGateMakeOneMission()
    {
        using var rig = new Rig();
        rig.OnTick(() =>
        {
            lock (rig.Player.PersistenceGate)
            {
                Assert.IsTrue(rig.Quests.TryAssignTemplate(rig.Player, QuestHash, out string first), first);
                Assert.IsTrue(rig.Quests.TryAssignTemplate(rig.Player, QuestHash, out string second), second);
            }
        });
        Assert.AreEqual(0, rig.Store.Assigned);

        rig.Drain();

        Assert.AreEqual(string.Empty, rig.Log.Problems);
        Assert.AreEqual(1, rig.Store.Assigned);
        Assert.AreEqual(1, rig.Player.Inventory.Inventory.Content.Values.Count(item => item.LowId == QuestDungeonService.KeyLowId));
        Assert.IsTrue(rig.Log.Information.Any(line => line.StartsWith("Deferred dungeon quest " + QuestHash + " skipped", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void GiveUnderTheGateIsRefusedWhenThePlayfieldTakesNoWork()
    {
        using var rig = new Rig();
        rig.Retire();
        string result = string.Empty;
        bool given = true;
        rig.OnTick(() =>
        {
            lock (rig.Player.PersistenceGate)
                given = rig.Quests.TryAssignTemplate(rig.Player, QuestHash, out result);
        });

        Assert.IsFalse(given, result);
        StringAssert.Contains(result, "unavailable");
        Assert.AreEqual(0, rig.Store.Assigned);
    }

    /// <summary>
    /// A finished quest gives its NextStep under the player's gate (<c>CompleteTemplate</c>). A dungeon NextStep used to
    /// throw there after the reward was queued, which quarantined the player and closed their session.
    /// </summary>
    [TestMethod]
    public void FinishingAQuestWhoseNextStepIsADungeonQuestGivesItAfterTheGate()
    {
        const string StepHash = "GTNS";
        using var rig = new Rig(completion: true);
        rig.AddTemplate(new QuestTemplate { Hash = StepHash, Name = "Gate step", Action = QuestTemplate.KillOneAction, NextStep = QuestHash });
        rig.OnTick(() => Assert.IsTrue(rig.Quests.TryAssignTemplate(rig.Player, StepHash, out string given), given));

        bool completed = false;
        string result = string.Empty;
        rig.OnTick(() => completed = rig.Quests.TryComplete(rig.Player, StepHash, out result));

        Assert.IsTrue(completed, result);
        Assert.IsFalse(rig.Player.IsPersistenceQuarantined, rig.Log.Problems);
        Assert.AreEqual(0, rig.Store.Assigned, "The NextStep dungeon waits for the gate to be released.");

        rig.Drain();

        Assert.AreEqual(string.Empty, rig.Log.Problems);
        Assert.IsFalse(rig.Player.IsPersistenceQuarantined);
        Assert.IsTrue(rig.Log.Information.Any(line => line.StartsWith("Quest rewards committed", StringComparison.Ordinal)
            && line.Contains("quest=" + StepHash, StringComparison.Ordinal)));
        Assert.AreEqual(1, rig.Store.Assigned);
        Assert.IsTrue(rig.Quests.GetLog(rig.Player).TryFind(QuestHash, out PlayerQuest? quest) && quest.IsActive);
        Assert.AreEqual(1, rig.Player.Inventory.Inventory.Content.Values.Count(item => item.LowId == QuestDungeonService.KeyLowId));
    }

    /// <summary>
    /// Every quest in the runtime catalog is given the way a dialog line or a NextStep gives it: under the player's gate.
    /// None may throw, quarantine the player or log an error, whatever kind of quest it is. A new quest kind whose give
    /// takes the lane under the gate fails here before it reaches players.
    /// </summary>
    [TestMethod]
    public void EveryCatalogQuestCanBeGivenUnderTheGate()
    {
        using var rig = new Rig();
        var failures = new List<string>();
        foreach (QuestTemplate template in rig.Catalog.Templates.OrderBy(template => template.Hash, StringComparer.Ordinal).ToArray())
        {
            rig.Player.QuestLog = new QuestLog();
            rig.Log.Reset();
            try
            {
                rig.OnTick(() =>
                {
                    lock (rig.Player.PersistenceGate)
                        rig.Quests.TryAssignTemplate(rig.Player, template.Hash, out _);
                });
                rig.Drain();
            }
            catch (Exception exception)
            {
                failures.Add(template.Hash + " threw: " + exception.GetBaseException().Message);
                continue;
            }

            if (rig.Log.Problems.Contains("ERROR", StringComparison.Ordinal))
                failures.Add(template.Hash + " logged: " + rig.Log.Problems.Trim());
            if (rig.Player.IsPersistenceQuarantined)
            {
                failures.Add(template.Hash + " quarantined the player.");
                break;
            }
        }

        Assert.AreEqual(0, failures.Count, failures.Count + " quest(s) cannot be given under the gate:" + Environment.NewLine
            + string.Join(Environment.NewLine, failures.Take(20)));
        Assert.AreEqual(rig.Catalog.Templates.Count(template => template.Dungeon != null), rig.Store.Assigned,
            "Every dungeon quest is given once the gate is released.");
    }

    sealed class Rig : IDisposable
    {
        internal readonly Player Player;
        internal readonly QuestCatalog Catalog;
        internal readonly NpcCharacter Npc;
        internal readonly Session Session = new();
        internal readonly Store Store = new();
        internal readonly Logger Log = new();
        internal readonly QuestService Quests;
        internal readonly KnubotService Knubot;
        readonly Playfield _world = Blank<Playfield>();
        readonly PlayfieldManager _manager = Blank<PlayfieldManager>();
        readonly InventoryFlushService _flush;
        readonly QuestDungeonService _dungeons;
        readonly ServiceProvider _services;
        readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), "aorebirth-knubot-dungeon-" + Guid.NewGuid().ToString("N"));
        long _clock = 1_000_000;

        /// <param name="completion">Wires quest completion: a mission DAO that stores every earn and reward, and a
        /// snapshot service with no repository behind it (the quests completed here change no stats).</param>
        internal Rig(bool completion = false)
        {
            string root = GameDataPaths.ResolveRuntimeRoot();
            var data = new GameDataStore(Log, null, root);
            var items = new Items();
            var templates = new Templates();
            var ids = new Ids();
            var catalog = Catalog = new QuestCatalog(data, Log);
            if (!catalog.TryGet(QuestHash, out QuestTemplate template) || template.Dungeon == null)
                Assert.Inconclusive("The runtime GameData at " + root + " has no dungeon quest " + QuestHash + ".");

            Player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = CharacterId }, Log, items) { FirstName = "Gate" };
            Player.Inventory.Apply(new CharacterHydrationResult(), CharacterId, items);
            Player.Stats.Set(CharacterStat.Level, 60);
            Player.Stats.Set(CharacterStat.Health, 100);
            Player.Stats.Set(CharacterStat.MaxHealth, 100);
            Player.QuestLog = new QuestLog();
            Player.EnterOnline(Session);

            var registry = new DynelRegistry();
            Set(_manager, "_sync", new Lock());
            Set(_manager, "_playersByCharacterId", new Dictionary<int, Player> { [CharacterId] = Player });
            Set(_manager, "_playfields", new Dictionary<int, Playfield> { [PlayfieldId] = _world });
            Directory.CreateDirectory(_cacheRoot);
            Set(_manager, "<DungeonNavMeshes>k__BackingField", new DungeonNavMeshCache(_cacheRoot, Log));
            Set(_world, "<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = PlayfieldId });
            Set(_world, "_tickSync", new Lock());
            Set(_world, "_lifecycleSync", new Lock());
            Set(_world, "_inbound", new PlayfieldInboundQueue());
            Set(_world, "_dynelRegistry", registry);
            Set(_world, "_logger", Log);
            Set(_world, "_playfieldManager", _manager);
            var locality = new ZoneEngine_New.Core.Playfield.Locality.PlayfieldLocality(PlayfieldId, null);
            var activation = new ZoneEngine_New.Core.Mobs.NpcContentActivationService(_world, registry, locality, items, templates);
            _services = new ServiceCollection().AddSingleton(registry).AddSingleton(locality).AddSingleton(activation)
                .BuildServiceProvider();
            Set(_world, "_serviceProvider", _services);

            Player.Playfield = _world;
            Player.Position = new Vector3(151.7f, 100.9f, 164.4f);
            registry.Register(Player);
            Npc = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = 900001 }, items)
            {
                Name = "Unicorn Recruiter", Playfield = _world, Position = Player.Position, SpawnHash = RecruiterHash
            };
            registry.Register(Npc);

            var persistence = new Persistence();
            _flush = new InventoryFlushService(id => id == CharacterId ? Player : null, () => [Player], persistence, Log);
            var actions = new InventoryActionService(persistence, _flush, ids, Log);
            Quests = completion
                ? new QuestService(catalog, Store, items, _flush, Log, () => DateTime.UtcNow, MissionDao(),
                    new CharacterSnapshotService(Fake<ICharacterRepository>(), Fake<IStatRepository>(), Log))
                : new QuestService(catalog, Store, items, _flush, Log);
            var minter = new HashItemMinter(data, templates, items);
            var layouts = new DungeonLayoutGenerator(data, Log);
            if (!layouts.IsAvailable)
                Assert.Inconclusive("The runtime GameData at " + root + " cannot generate mission dungeons.");
            _dungeons = new QuestDungeonService(Store, Quests, MissionDestinationCatalog.Load(root), layouts,
                items, ids, _flush, actions, new Lazy<PlayfieldManager>(() => _manager), data, Log, minter);

            string? overrideRoot = Environment.GetEnvironmentVariable(KnubotCatalog.OverrideVariable);
            Environment.SetEnvironmentVariable(KnubotCatalog.OverrideVariable, null);
            KnubotCatalog scripts;
            try
            {
                scripts = new KnubotCatalog(data, catalog, templates, Log);
            }
            finally
            {
                Environment.SetEnvironmentVariable(KnubotCatalog.OverrideVariable, overrideRoot);
            }

            var trades = new TradeService(Log, data, templates, minter, ids, _flush, persistence);
            Knubot = new KnubotService(scripts, new KnubotEffectServices(Quests, minter, _flush, trades, actions), Log, () => _clock);
            Log.Reset();
        }

        /// <summary>The index of the offered reply that starts with <paramref name="text"/>, in the last reply list sent.</summary>
        internal int Option(string text)
        {
            KnuBotAnswerListMessage list = Session.Sent.OfType<KnuBotAnswerListMessage>().Last();
            int index = Array.FindIndex(list.DialogOptions, option => option.Text.StartsWith(text, StringComparison.Ordinal));
            Assert.IsTrue(index >= 0, "No reply '" + text + "' in: " + string.Join(" | ", list.DialogOptions.Select(option => option.Text)));
            return index;
        }

        /// <summary>
        /// A player of <paramref name="level"/> with no Mothership held talks to the recruiter, picks
        /// <paramref name="reply"/> as a client answers, and returns the quality of the mission given.
        /// </summary>
        internal int GiveFromRecruiter(int level, string reply)
        {
            Player.Stats.Set(CharacterStat.Level, level);
            Player.QuestLog = new QuestLog();
            Store.Generated = null;
            int assigned = Store.Assigned;
            OnTick(() => Assert.IsTrue(Knubot.TryOpen(Session, Npc.Identity)));
            Settle();
            OnTick(() => Assert.IsTrue(Knubot.TryAnswer(Session, Npc.Identity, Option("I want to visit"))));
            Settle();
            int option = Option(reply);
            Inbound(() => Assert.IsTrue(Knubot.TryAnswer(Session, Npc.Identity, option)));
            Drain();
            Settle();
            Assert.AreEqual(assigned + 1, Store.Assigned, "level " + level + " '" + reply + "' gave no mission. " + Log.Problems);
            Assert.IsTrue(QuestDungeonParameters.TryParse(Store.Generated!.AcgBuildingGeneratorJson, out QuestDungeonParameters parameters));
            return parameters.Quality;
        }

        /// <summary>Lets the NPC's queued text run out, a paced packet per tick.</summary>
        internal void Settle()
        {
            for (int i = 0; i < 200; i++)
            {
                _clock += 250;
                OnTick(() => Knubot.Tick(_world));
            }
        }

        internal void OnTick(Action work)
        {
            lock ((Lock)Get(_world, "_tickSync"))
                work();
        }

        internal void Inbound(Action work)
            => Assert.IsTrue(_world.TryEnqueue(new PlayerProjectionInboundItem { Player = Player, Projection = work }));

        internal void Drain() => OnTick(() => ((PlayfieldInboundQueue)Get(_world, "_inbound")).Drain(null!, null!, _world));

        internal void Retire() => Set(_world, "_retiring", true);

        internal void AddTemplate(QuestTemplate template)
        {
            var current = (IReadOnlyDictionary<string, QuestTemplate>)Get(Catalog, "_templates");
            Set(Catalog, "_templates", new Dictionary<string, QuestTemplate>(current, StringComparer.Ordinal) { [template.Hash] = template });
        }

        /// <summary>Runs every transaction against a store that holds this player's quests as the tick shows them.</summary>
        IMissionDao MissionDao()
        {
            CharacterQuestCompletionSnapshotData Stored(string questId)
            {
                PlayerQuest quest = Quests.GetLog(Player).Quests.Values.Single(held => held.QuestId == questId);
                return new CharacterQuestCompletionSnapshotData
                {
                    CharacterId = CharacterId, QuestId = questId, Source = (int)quest.Source,
                    AssignedAtUtcTicks = quest.AssignedAtUtc.Ticks, AcgBuildingGeneratorJson = quest.AcgBuildingGeneratorJson!
                };
            }

            ICompletionTransaction transaction = Fake<ICompletionTransaction>((method, args) => method.Name switch
            {
                "get_CharacterId" => CharacterId,
                nameof(ICharacterQuestCompletionTransaction.ReadCharacterQuest) => Stored((string)args![0]!),
                nameof(ICharacterQuestCompletionTransaction.TryEarnCharacterQuest) => new CharacterQuestCompletionResult
                {
                    Status = CharacterQuestCompletionStatus.Earned, Quest = Stored(((CharacterQuestEarnRequest)args![0]!).QuestId)
                },
                nameof(ICharacterQuestCompletionTransaction.TryCompleteCharacterQuest) => new CharacterQuestCompletionResult
                {
                    Status = CharacterQuestCompletionStatus.Applied
                },
                _ => throw new AssertFailedException("Unexpected mission transaction call " + method.Name)
            });
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
            _dungeons.Dispose();
            _flush.Dispose();
            _services.Dispose();
            try
            {
                Directory.Delete(_cacheRoot, true);
            }
            catch (IOException)
            {
                // A navmesh prewarm may still hold a file; the temp folder is left behind.
            }
        }
    }

    sealed class Store : ICharacterQuestStore
    {
        internal int Assigned;
        internal GeneratedQuestRow? Generated;
        internal QuestDungeonKeyItemRow? Key;

        public bool TryAssignDungeonQuest(int ownerId, GeneratedQuestRow quest, CharacterQuestRow characterRow,
            QuestDungeonKeyItemRow key, QuestDungeonKeyItemRow? sourceItem)
        {
            Assigned++;
            Generated = quest;
            Key = key;
            return true;
        }

        public IList<CharacterQuestRow> Load(int characterId) => [];
        public IList<GeneratedQuestRow> LoadGeneratedFor(int characterId) => Generated != null ? [Generated] : [];
        public GeneratedQuestRow? LoadGenerated(string questId) => Generated?.QuestId == questId ? Generated : null;
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
        internal readonly List<string> Information = [];
        string _problems = string.Empty;

        internal string Problems { get { lock (_sync) return _problems; } }

        internal void Reset()
        {
            lock (_sync)
            {
                Information.Clear();
                _problems = string.Empty;
            }
        }

        public void Debug(string message) { }
        public void Info(string message) { lock (_sync) Information.Add(message); }
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

    /// <summary>Accepts every write: these tests are about ordering against the gate, not what is stored.</summary>
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

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }

    sealed class Templates : IItemTemplateCatalog
    {
        public bool TryGet(int aoid, out ItemTemplate template)
        {
            template = null!;
            return false;
        }

        public ItemTemplate Require(int aoid) => throw new KeyNotFoundException(aoid.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    sealed class Ids : IItemInstanceIdAllocator
    {
        int _next = 800000;
        public int Allocate() => Interlocked.Increment(ref _next);
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
