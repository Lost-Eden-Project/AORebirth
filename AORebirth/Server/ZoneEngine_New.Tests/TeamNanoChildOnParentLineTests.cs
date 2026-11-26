namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;

using AORebirth.Database.Domain.Characters;
using AORebirth.Enums;
using AORebirth.Interfaces.Persistence.Characters;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;
using LostEden.Protocol.Messages.N3Messages;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Helpers;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Nanos;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Lost Eden 2026-11-25: a Trader casting Refresh Outfit (Team) 223241 saw "NCU error: Better nano program already
/// running." on every tick and never got nano back. The parent (line 588, order 11, 60 s) team-casts its nano-heal
/// 216580 (line 588, order 10, 8 s) on the land and every 8 s. It runs on the caster alone, so on the caster the child
/// met its own, stronger parent and was refused. A buff never blocks, and is never replaced by, the child it launches.
/// </summary>
[TestClass]
public sealed class TeamNanoChildOnParentLineTests
{
    const int ParentId = 223241;
    const int ChildId = 216580;
    const int Line = 588;

    static readonly Identity Caster = new() { Type = IdentityType.SimpleChar, Instance = 451 };

    [TestMethod]
    public void ABuffNeitherBlocksNorLeavesForTheChildNanoItLaunchesOnItsOwnLine()
    {
        Buff parent = Buff.Create(Parent(), Caster, nanoInstance: 1, DateTime.UtcNow);

        BuffApplyDecision decision = BuffApplyRules.Evaluate(Child(), [parent], maxNcu: 60, out IReadOnlyList<Buff> replaced);

        Assert.AreEqual(BuffApplyDecision.Apply, decision);
        Assert.AreEqual(0, replaced.Count);
    }

    [TestMethod]
    public void AStrongerBuffThatLaunchesSomeOtherNanoStillBlocksAWeakerOneOnItsLine()
    {
        Buff other = Buff.Create(
            Nano(223300, order: 11, flags: 65538, modifiers: [TeamCast(216581, ticks: 7)]),
            Caster,
            nanoInstance: 1,
            DateTime.UtcNow);

        BuffApplyDecision decision = BuffApplyRules.Evaluate(Child(), [other], maxNcu: 60, out _);

        Assert.AreEqual(BuffApplyDecision.RefusedStrainStronger, decision);
    }

    [TestMethod]
    public void TheCasterGetsTheTeamChildOnTheLandAndOnEveryTickWithoutAnNcuError()
    {
        var items = new Items(Parent(), Child());
        var inventory = new Inventory();
        var session = new Session();
        Player caster = NewPlayer(session, items);
        DateTime start = DateTime.UtcNow;

        Assert.IsTrue(NanoRuntime.TryApplyImmediate(caster, caster, ParentId, items, inventory, start));
        Buff parent = Require(caster, ParentId);
        Buff first = Require(caster, ChildId);

        // The 8 s tick, run the way NanoRuntime.Tick runs it from the parent's NCU entry.
        ItemSpell tick = parent.SpellList[EventType.OnUse].Single(spell => spell.TickCount == 7);
        parent.ExecutePeriodicSpell(caster, caster, tick, inventory, items);

        Assert.AreSame(parent, Require(caster, ParentId), "The child must not replace its parent.");
        Assert.AreNotSame(first, Require(caster, ChildId), "The tick must land the child on the caster again.");

        int ncuError = ClientFeedback.Create(caster.Identity, "Feedback_NCUErrorBetterProgramRunning").MessageId;
        Assert.IsFalse(
            session.Sent.OfType<FeedbackMessage>().Any(message => message.MessageId == ncuError),
            "No \"Better nano program already running\" for the caster's own child.");
    }

    /// <summary>
    /// Lost Eden v0.15.0 (2026-11-26): with parent and child both running on line 588, the caster's save rejected its
    /// own NCU ("Invalid or duplicate active nano identity.") and the flush lane retried that write every 10 s. Every
    /// later inventory change for the character queued behind it unsaved, and hard flushes broke the playfield tick.
    /// The save must store both rows, through the coalesced flush and through the NCU-only write.
    /// </summary>
    [TestMethod]
    public void TheCastersSaveStoresAParentAndTheChildItLaunchedOnTheSameLine()
    {
        var items = new Items(Parent(), Child());
        Player caster = NewPlayer(new Session(), items);
        Assert.IsTrue(NanoRuntime.TryApplyImmediate(caster, caster, ParentId, items, new Inventory(), DateTime.UtcNow));

        List<ActiveNanoRecord> snapshot = caster.TakeDirtyActiveNanos()!;
        Assert.IsNotNull(snapshot, "The land must leave NCU dirty for the flush.");
        CollectionAssert.AreEquivalent(new[] { ParentId, ChildId }, snapshot.Select(n => n.NanoId).ToArray());
        Assert.IsTrue(snapshot.All(n => n.Strain == Line), "Both run on line 588.");

        var flush = new RecordingDatabase();
        var logger = new Logger();
        var flushDao = new MySqlCharacterPersistenceDao(flush.Connect);
        new MySqlCharacterCoalesceCommit(
                new MySqlInventoryRepository(logger, flushDao),
                new MySqlUploadedNanoRepository(logger, flushDao),
                new MySqlActiveNanoRepository(flushDao),
                logger)
            .Persist([], [], Caster.Instance, [], snapshot, null);
        AssertStored(flush, snapshot);

        var ncuOnly = new RecordingDatabase();
        new MySqlActiveNanoRepository(new MySqlCharacterPersistenceDao(ncuOnly.Connect)).WriteReplaceAll(Caster.Instance, snapshot);
        AssertStored(ncuOnly, snapshot);
    }

    [TestMethod]
    public void TheSaveStillRefusesTwoNcuRowsWithOneNanoInstance()
    {
        var dao = new MySqlCharacterPersistenceDao(new RecordingDatabase().Connect);
        PersistedActiveNanoData[] rows =
        [
            new() { NanoId = ParentId, Strain = Line, NanoInstance = 7, DurationCentiseconds = 6000, ExpiresAtUtcTicks = 1 },
            new() { NanoId = ChildId, Strain = Line, NanoInstance = 7, DurationCentiseconds = 800, ExpiresAtUtcTicks = 1 }
        ];

        Assert.ThrowsExactly<ArgumentException>(() => dao.SaveInventoryAndUploadedNanos(Caster.Instance, [], [], [], rows));
        Assert.ThrowsExactly<ArgumentException>(() => dao.CommitActiveNanos(
            [new CharacterActiveNanoData { CharacterId = Caster.Instance, BaseStats = [], ActiveNanos = rows }]));
    }

    static void AssertStored(RecordingDatabase db, IReadOnlyList<ActiveNanoRecord> snapshot)
    {
        Assert.IsTrue(db.Transactions.Count == 1 && db.Transactions[0].Committed, "One committed transaction.");
        Assert.AreEqual(1, db.Statements.Count(s => s.Text.StartsWith("DELETE FROM charactersactivenanos", StringComparison.Ordinal)));
        CollectionAssert.AreEquivalent(
            snapshot.Select(n => n.NanoInstance).ToArray(),
            db.Statements.Where(s => s.Text.StartsWith("INSERT INTO charactersactivenanos", StringComparison.Ordinal))
                .Select(s => (int)s.Values["@Instance"]!).ToArray(),
            "Each running nano is stored once.");
    }

    static Buff Require(Character character, int nanoId)
    {
        Assert.IsTrue(character.TryGetBuff(nanoId, out Buff? buff), "Nano " + nanoId + " must run on the character.");
        return buff!;
    }

    /// <summary>Refresh Outfit (Team) 223241: flags 65538, line 588, order 11, 60 s, NCU 1; team-casts 216580 on the land and 7x every 8 s.</summary>
    static NanoSpell Parent()
        => Nano(ParentId, order: 11, flags: 65538, duration: 6000,
            modifiers: [TeamCast(ChildId, ticks: 1), TeamCast(ChildId, ticks: 7)]);

    /// <summary>Refresh Outfit 216580: flags 606466 (IsBuff, NoRemoveNoNCUFriendly, NotRemovable, NoTimerNotify), line 588, order 10, 8 s, NCU 1.</summary>
    static NanoSpell Child()
        => Nano(ChildId, order: 10, flags: 606466, duration: 800,
            modifiers:
            [
                new ItemSpell
                {
                    FunctionType = (int)FunctionType.Hit,
                    Target = (int)ItemTarget.Target,
                    Arguments = [(int)CharacterStat.CurrentNano, 12, 12]
                }
            ]);

    static NanoSpell Nano(int id, int order, int flags, int duration = 6000, IEnumerable<ItemSpell>? modifiers = null)
        => NanoSpell.From(new ItemTemplate
        {
            Id = id,
            Name = "Nano " + id,
            Quality = 1,
            Flags = flags,
            Stats = new Dictionary<CharacterStat, int>
            {
                [CharacterStat.TimeExist] = duration,
                [CharacterStat.Level] = 1,
                [CharacterStat.MetaType] = Line,
                [CharacterStat.StackingOrder] = order,
                [CharacterStat.Can] = unchecked((int)CanFlags.ApplyOnFriendly)
            },
            SpellList = new Dictionary<EventType, List<ItemSpell>>
            {
                [EventType.OnUse] = modifiers == null ? [] : [.. modifiers]
            },
            CanCancel = true
        });

    static ItemSpell TeamCast(int nanoId, int ticks)
        => new()
        {
            FunctionType = (int)FunctionType.TeamCastNano,
            Target = (int)ItemTarget.Wearer,
            Arguments = [nanoId],
            TickCount = ticks,
            TickInterval = ticks > 1 ? 800u : 0u
        };

    static Player NewPlayer(Session session, Items items)
    {
        var player = new Player(Caster, new Logger(), items) { FirstName = "Ragudd" };
        player.Inventory.Apply(new CharacterHydrationResult(), Caster.Instance, items);
        player.Stats.Set(CharacterStat.Level, 54);
        player.Stats.Set(CharacterStat.MaxNCU, 116);
        player.Stats.Set(CharacterStat.MaxNanoEnergy, 1394);
        player.Stats.Set(CharacterStat.CurrentNano, 1000);
        player.EnterOnline(session);
        player.MarkClientInPlay(DateTime.UtcNow);
        new PlayfieldLocality(0x160001, null).RegisterDynel(player);
        return player;
    }

    sealed class Logger : IZoneLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(Exception exception, string message) { }
        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }

    sealed class Session : IZoneSession
    {
        internal readonly ConcurrentQueue<MessageBody> Sent = new();
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public bool TransferToPlayfield(Playfield destination, Vector3 landing) => false;
        public void Send(byte[] packet) { }
        public void Send(MessageBody body) => Sent.Enqueue(body);
        public void Send(MessageBody body, int sender, int receiver) => Sent.Enqueue(body);
        public void SendInitiateCompression() { }
        public void Close() => State = SessionState.Closed;
    }

    sealed class Items : IItemBuilder
    {
        readonly Dictionary<int, ItemTemplate> _templates;
        int _next = 51500000;

        internal Items(params ItemTemplate[] templates)
            => _templates = templates.ToDictionary(template => template.Id);

        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null) => new()
            {
                LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount,
                InstanceId = instanceId, Identity = identity ?? Identity.None, Definition = CreateTemplate(lowId, highId, quality)
            };

        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
        {
            Item item = Create(lowId, highId, quality, source, stackCount);
            item.AssignInstanceId(Interlocked.Increment(ref _next));
            return item;
        }

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality)
            => _templates.TryGetValue(lowId, out ItemTemplate? template) ? template : new ItemTemplate { Id = lowId, Quality = quality };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }

    /// <summary>Records every statement the DAO runs; the character row exists, so its lock succeeds.</summary>
    sealed class RecordingDatabase
    {
        internal readonly List<RecordedTransaction> Transactions = [];
        internal readonly List<(string Text, Dictionary<string, object?> Values)> Statements = [];

        internal IDbConnection Connect() => new Connection(this);

        sealed class Connection(RecordingDatabase db) : IDbConnection
        {
            [System.Diagnostics.CodeAnalysis.AllowNull]
            public string ConnectionString { get; set; } = string.Empty;
            public int ConnectionTimeout => 0;
            public string Database => "fake";
            public ConnectionState State { get; private set; }
            public IDbTransaction BeginTransaction() => BeginTransaction(IsolationLevel.Unspecified);
            public IDbTransaction BeginTransaction(IsolationLevel il)
            {
                var transaction = new RecordedTransaction(this, il);
                db.Transactions.Add(transaction);
                return transaction;
            }
            public void ChangeDatabase(string databaseName) { }
            public void Close() => State = ConnectionState.Closed;
            public IDbCommand CreateCommand() => new Command(db) { Connection = this };
            public void Open() => State = ConnectionState.Open;
            public void Dispose() => Close();
        }

        sealed class Command(RecordingDatabase db) : IDbCommand
        {
            readonly Parameters _parameters = [];
            [System.Diagnostics.CodeAnalysis.AllowNull]
            public string CommandText { get; set; } = string.Empty;
            public int CommandTimeout { get; set; }
            public CommandType CommandType { get; set; }
            public IDbConnection? Connection { get; set; }
            IDataParameterCollection IDbCommand.Parameters => _parameters;
            public IDbTransaction? Transaction { get; set; }
            public UpdateRowSource UpdatedRowSource { get; set; }
            public void Cancel() { }
            public IDbDataParameter CreateParameter() => new Parameter();
            public int ExecuteNonQuery()
            {
                db.Statements.Add((CommandText, _parameters.Cast<Parameter>().ToDictionary(p => p.ParameterName, p => p.Value)));
                return 1;
            }
            public IDataReader ExecuteReader() => throw new NotSupportedException(CommandText);
            public IDataReader ExecuteReader(CommandBehavior behavior) => throw new NotSupportedException(CommandText);
            public object? ExecuteScalar() => CommandText.StartsWith("SELECT Id FROM characters", StringComparison.Ordinal)
                ? Caster.Instance
                : throw new NotSupportedException(CommandText);
            public void Prepare() { }
            public void Dispose() { }
        }

        sealed class Parameters : System.Collections.ArrayList, IDataParameterCollection
        {
            public object this[string parameterName]
            {
                get => this[IndexOf(parameterName)]!;
                set => this[IndexOf(parameterName)] = value;
            }
            public bool Contains(string parameterName) => IndexOf(parameterName) >= 0;
            public int IndexOf(string parameterName)
            {
                for (int i = 0; i < Count; i++)
                    if (((Parameter)this[i]!).ParameterName == parameterName)
                        return i;
                return -1;
            }
            public void RemoveAt(string parameterName) => RemoveAt(IndexOf(parameterName));
        }

        sealed class Parameter : IDbDataParameter
        {
            public byte Precision { get; set; }
            public byte Scale { get; set; }
            public int Size { get; set; }
            public DbType DbType { get; set; }
            public ParameterDirection Direction { get; set; }
            public bool IsNullable => true;
            [System.Diagnostics.CodeAnalysis.AllowNull]
            public string ParameterName { get; set; } = string.Empty;
            [System.Diagnostics.CodeAnalysis.AllowNull]
            public string SourceColumn { get; set; } = string.Empty;
            public DataRowVersion SourceVersion { get; set; }
            public object? Value { get; set; }
        }
    }

    sealed class RecordedTransaction(IDbConnection connection, IsolationLevel isolation) : IDbTransaction
    {
        internal bool Committed;
        public IDbConnection Connection => connection;
        public IsolationLevel IsolationLevel => isolation;
        public void Commit() => Committed = true;
        public void Rollback() { }
        public void Dispose() { }
    }

    sealed class Inventory : IInventoryRepository
    {
        public IReadOnlyList<ItemInstanceRecord> GetCarriedItems(int characterId) => [];
        public IReadOnlyList<ItemInstanceRecord> GetBankItems(int characterId) => [];
        public IReadOnlyList<ItemInstanceRecord> GetContainerItems(int containerInstanceId) => [];
        public int LeaseInstanceIdBlock(int count) => 1;
        public ItemInstanceRecord Insert(ItemInstanceRecord item) => item;
        public void UpdateLocation(int instanceId, int containerType, int containerInstance, int containerPlacement) { }
        public void UpdateLocations(IReadOnlyList<ItemLocationUpdate> locations) { }
        public void PersistNewAndUpdateLocations(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates) { }
    }
}
