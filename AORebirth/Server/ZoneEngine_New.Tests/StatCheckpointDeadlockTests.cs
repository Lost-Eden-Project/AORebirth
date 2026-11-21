namespace ZoneEngine_New.Tests;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;

using AORebirth.Database.Domain.Characters;
using AORebirth.Interfaces.Persistence.Characters;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using MySqlConnector;

/// <summary>
/// Live v0.13.6 (2026-11-21) logged a stats checkpoint chosen as an InnoDB deadlock victim about seven times a minute:
/// "Deadlock found when trying to get lock; try restarting transaction" out of the stats write, then a 1 s lane retry
/// at Error level, and a hard flush (mission accept, trade) failed outright on its first deadlock. Two fixes ship
/// together: WriteStats updates the rows a character already has and inserts only new ones (the multi-row upsert's
/// duplicate path left next-key locks other characters collided on; proven in Tools/SweepCheckpointDaoValidation), and
/// the DAO restarts a transaction InnoDB rolled back before COMMIT. These tests drive the checkpoint save through a
/// fake connection that deadlocks on demand.
/// </summary>
[TestClass]
public sealed class StatCheckpointDeadlockTests
{
    const int CharacterId = 712;

    [TestMethod]
    public void CheckpointDeadlockedOnceIsRestartedAndStored()
    {
        var db = new FakeDatabase { DeadlocksLeft = 1, Existing = { 27, 214 } };
        var dao = new MySqlCharacterPersistenceDao(db.Connect);
        long before = MySqlCharacterPersistenceDao.DeadlockRetries;

        bool stored = dao.SaveOnlineCheckpoint(CharacterId, null!, Stats((27, 500), (214, 300)));

        Assert.IsTrue(stored);
        Assert.AreEqual(2, db.Transactions.Count, "one transaction per attempt");
        Assert.IsTrue(db.Transactions[0].RolledBack && !db.Transactions[0].Committed, "the deadlocked attempt rolls back");
        Assert.IsTrue(db.Transactions[1].Committed, "the restarted attempt commits");
        Assert.AreEqual(2, db.StatWrites.Count, "the stat write ran once per attempt");
        Assert.IsTrue(MySqlCharacterPersistenceDao.DeadlockRetries > before);
    }

    [TestMethod]
    public void CheckpointDeadlockedOnEveryAttemptSurfacesTheDeadlockAfterTheLastOne()
    {
        var db = new FakeDatabase { DeadlocksLeft = int.MaxValue };
        var dao = new MySqlCharacterPersistenceDao(db.Connect);

        MySqlException failure = Assert.ThrowsExactly<MySqlException>(
            () => dao.SaveOnlineCheckpoint(CharacterId, null!, Stats((27, 500))));

        Assert.AreEqual(1213, failure.Number);
        Assert.AreEqual(MySqlCharacterPersistenceDao.DeadlockAttempts, db.Transactions.Count);
        Assert.IsTrue(db.Transactions.All(t => t.RolledBack && !t.Committed));
    }

    [TestMethod]
    public void DuplicateRowIsNotRestarted()
    {
        var db = new FakeDatabase { DeadlocksLeft = int.MaxValue, ErrorCode = MySqlErrorCode.DuplicateKeyEntry };
        var dao = new MySqlCharacterPersistenceDao(db.Connect);

        Assert.ThrowsExactly<CharacterPersistenceStateConflictException>(
            () => dao.SaveOnlineCheckpoint(CharacterId, null!, Stats((27, 500))));

        Assert.AreEqual(1, db.Transactions.Count, "a state conflict is the caller's to resolve, never retried here");
    }

    [TestMethod]
    public void ExistingStatsAreUpdatedAndOnlyNewOnesInserted()
    {
        var db = new FakeDatabase { Existing = { 27, 54 } };
        var dao = new MySqlCharacterPersistenceDao(db.Connect);

        Assert.IsTrue(dao.SaveOnlineCheckpoint(CharacterId, null!, Stats((214, 300), (27, 500), (54, 60), (1, 7))));

        FakeCommand update = db.StatWrites.Single(w => w.CommandText.StartsWith("UPDATE stats", StringComparison.Ordinal));
        FakeCommand insert = db.StatWrites.Single(w => w.CommandText.StartsWith("INSERT INTO stats", StringComparison.Ordinal));
        CollectionAssert.AreEquivalent(new[] { 27, 54 }, update.Values("@S"), "rows the character has are updated in place");
        CollectionAssert.AreEquivalent(new[] { 214, 1 }, insert.Values("@S"), "only rows it lacks are inserted");
        CollectionAssert.AreEqual(Paired(update), new[] { (27, 500), (54, 60) }.OrderBy(p => p.Item1).ToArray(), "each value travels with its stat");
        CollectionAssert.AreEqual(Paired(insert), new[] { (1, 7), (214, 300) }, "each value travels with its stat");
        Assert.AreEqual(CharacterId, update.Parameters.Value("@Instance"));
        Assert.AreEqual(CharacterId, insert.Parameters.Value("@Instance"));
        Assert.AreEqual(2, db.StatWrites.Count, "one statement per kind");
    }

    static (int, int)[] Paired(FakeCommand command)
        => command.Values("@S").Zip(command.Values("@V"), (s, v) => (s, v)).OrderBy(p => p.s).ToArray();

    static IList<CharacterStatData> Stats(params (int StatId, int Value)[] stats)
        => stats.Select(s => new CharacterStatData { StatId = s.StatId, StatValue = s.Value }).ToList();

    /// <summary>
    /// The MySqlConnector exception the server-side rollback surfaces as. Its constructors are internal, so the
    /// test builds it the way the driver does.
    /// </summary>
    static MySqlException ServerError(MySqlErrorCode code)
    {
        foreach (ConstructorInfo ctor in typeof(MySqlException).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            ParameterInfo[] parameters = ctor.GetParameters();
            if (parameters.Length == 0 || parameters[0].ParameterType != typeof(MySqlErrorCode))
                continue;
            object?[] arguments = parameters.Select(p =>
                p.ParameterType == typeof(MySqlErrorCode) ? (object)code
                : p.ParameterType == typeof(string)
                    ? (p.Name?.IndexOf("state", StringComparison.OrdinalIgnoreCase) >= 0 ? "40001" : code.ToString())
                : null).ToArray();
            try
            {
                var exception = (MySqlException)ctor.Invoke(arguments);
                if (exception.Number == (int)code)
                    return exception;
            }
            catch (TargetInvocationException)
            {
            }
        }
        throw new AssertInconclusiveException("MySqlConnector exposes no constructor this test can build a MySqlException from.");
    }

    sealed class FakeDatabase
    {
        public int DeadlocksLeft;
        public MySqlErrorCode ErrorCode = MySqlErrorCode.LockDeadlock;
        /// <summary>StatIds the character's stats rows already hold.</summary>
        public readonly HashSet<int> Existing = [];
        public readonly List<FakeTransaction> Transactions = [];
        /// <summary>Every UPDATE/INSERT against the stats table, in execution order, deadlocked ones included.</summary>
        public readonly List<FakeCommand> StatWrites = [];

        public IDbConnection Connect() => new FakeConnection(this);

        public object? Scalar(FakeCommand command)
            => command.CommandText.StartsWith("SELECT Online FROM characters", StringComparison.Ordinal) ? 1 : null;

        public IDataReader Reader(FakeCommand command)
        {
            if (!command.CommandText.StartsWith("SELECT StatId FROM stats", StringComparison.Ordinal))
                throw new NotSupportedException(command.CommandText);
            Assert.AreEqual(CharacterId, command.Parameters.Value("@Instance"), "the read is the saving character's");
            return new FakeStatIdReader(Existing.OrderBy(id => id).ToArray());
        }

        public int NonQuery(FakeCommand command)
        {
            bool statsWrite = command.CommandText.StartsWith("UPDATE stats", StringComparison.Ordinal)
                || command.CommandText.StartsWith("INSERT INTO stats", StringComparison.Ordinal);
            if (!statsWrite)
                return 1;
            StatWrites.Add(command);
            if (DeadlocksLeft > 0)
            {
                DeadlocksLeft--;
                throw ServerError(ErrorCode);
            }
            return command.Values("@S").Length;
        }
    }

    sealed class FakeConnection(FakeDatabase db) : IDbConnection
    {
        public string ConnectionString { get; set; } = string.Empty;
        public int ConnectionTimeout => 0;
        public string Database => "fake";
        public ConnectionState State { get; private set; } = ConnectionState.Closed;

        public IDbTransaction BeginTransaction() => BeginTransaction(IsolationLevel.Unspecified);

        public IDbTransaction BeginTransaction(IsolationLevel il)
        {
            var transaction = new FakeTransaction(this, il);
            db.Transactions.Add(transaction);
            return transaction;
        }

        public void ChangeDatabase(string databaseName) { }
        public void Close() => State = ConnectionState.Closed;
        public IDbCommand CreateCommand() => new FakeCommand(db) { Connection = this };
        public void Open() => State = ConnectionState.Open;
        public void Dispose() => Close();
    }

    sealed class FakeTransaction(FakeConnection connection, IsolationLevel isolation) : IDbTransaction
    {
        public bool Committed;
        public bool RolledBack;
        public IDbConnection Connection => connection;
        public IsolationLevel IsolationLevel => isolation;
        public void Commit() => Committed = true;
        public void Rollback() => RolledBack = true;
        public void Dispose() { }
    }

    sealed class FakeCommand(FakeDatabase db) : IDbCommand
    {
        public readonly FakeParameters Parameters = [];

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public string CommandText { get; set; } = string.Empty;
        public int CommandTimeout { get; set; }
        public CommandType CommandType { get; set; }
        public IDbConnection? Connection { get; set; }
        IDataParameterCollection IDbCommand.Parameters => Parameters;
        public IDbTransaction? Transaction { get; set; }
        public UpdateRowSource UpdatedRowSource { get; set; }

        /// <summary>The values of the numbered parameters with this prefix, in parameter order.</summary>
        public int[] Values(string prefix) => Parameters.Cast<FakeParameter>()
            .Where(p => p.ParameterName.StartsWith(prefix, StringComparison.Ordinal) && p.ParameterName.Length > prefix.Length
                && char.IsDigit(p.ParameterName[prefix.Length]))
            .Select(p => (int)p.Value!).ToArray();

        public void Cancel() { }
        public IDbDataParameter CreateParameter() => new FakeParameter();
        public int ExecuteNonQuery() => db.NonQuery(this);
        public IDataReader ExecuteReader() => db.Reader(this);
        public IDataReader ExecuteReader(CommandBehavior behavior) => db.Reader(this);
        public object? ExecuteScalar() => db.Scalar(this);
        public void Prepare() { }
        public void Dispose() { }
    }

    /// <summary>A one-column, forward-only reader over the StatIds the character already has.</summary>
    sealed class FakeStatIdReader(int[] statIds) : IDataReader
    {
        int _index = -1;

        public bool Read() => ++_index < statIds.Length;
        public int GetInt32(int i) => i == 0 ? statIds[_index] : throw new IndexOutOfRangeException();
        public int FieldCount => 1;
        public int Depth => 0;
        public bool IsClosed { get; private set; }
        public int RecordsAffected => -1;
        public void Close() => IsClosed = true;
        public void Dispose() => Close();
        public bool NextResult() => false;
        public object this[int i] => GetInt32(i);
        public object this[string name] => throw new NotSupportedException();
        public bool GetBoolean(int i) => throw new NotSupportedException();
        public byte GetByte(int i) => throw new NotSupportedException();
        public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length) => throw new NotSupportedException();
        public char GetChar(int i) => throw new NotSupportedException();
        public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length) => throw new NotSupportedException();
        public IDataReader GetData(int i) => throw new NotSupportedException();
        public string GetDataTypeName(int i) => "INT";
        public DateTime GetDateTime(int i) => throw new NotSupportedException();
        public decimal GetDecimal(int i) => throw new NotSupportedException();
        public double GetDouble(int i) => throw new NotSupportedException();
        public Type GetFieldType(int i) => typeof(int);
        public float GetFloat(int i) => throw new NotSupportedException();
        public Guid GetGuid(int i) => throw new NotSupportedException();
        public short GetInt16(int i) => throw new NotSupportedException();
        public long GetInt64(int i) => GetInt32(i);
        public string GetName(int i) => "StatId";
        public int GetOrdinal(string name) => name == "StatId" ? 0 : -1;
        public DataTable? GetSchemaTable() => null;
        public string GetString(int i) => throw new NotSupportedException();
        public object GetValue(int i) => GetInt32(i);
        public int GetValues(object[] values) { values[0] = GetInt32(0); return 1; }
        public bool IsDBNull(int i) => false;
    }

    sealed class FakeParameters : ArrayList, IDataParameterCollection
    {
        public object this[string parameterName]
        {
            get => this[IndexOf(parameterName)]!;
            set => this[IndexOf(parameterName)] = value;
        }

        public object Value(string parameterName) => ((FakeParameter)this[parameterName]).Value!;
        public bool Contains(string parameterName) => IndexOf(parameterName) >= 0;

        public int IndexOf(string parameterName)
        {
            for (int i = 0; i < Count; i++)
                if (((FakeParameter)this[i]!).ParameterName == parameterName)
                    return i;
            return -1;
        }

        public void RemoveAt(string parameterName) => RemoveAt(IndexOf(parameterName));
    }

    sealed class FakeParameter : IDbDataParameter
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
