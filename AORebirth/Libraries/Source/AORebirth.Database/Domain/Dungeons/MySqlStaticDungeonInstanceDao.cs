#nullable disable

namespace AORebirth.Database.Domain.Dungeons
{
    using System;
    using System.Collections.Generic;
    using System.Data;

    /// <summary>
    /// One row of <c>staticdungeoninstances</c>: a live static dungeon instance, from its build until it is released,
    /// held by one owner at one world entrance (the zone records every instance under one shared owner per entrance).
    /// </summary>
    public sealed class StaticDungeonInstanceRow
    {
        /// <summary>The instance's dungeon Playfield2 id (primary key).</summary>
        public int PlayfieldId { get; set; }

        public uint EntranceType { get; set; }

        public uint EntranceInstance { get; set; }

        /// <summary>0 character, 1 team.</summary>
        public int OwnerKind { get; set; }

        public int OwnerId { get; set; }

        public long CreatedAtUtcTicks { get; set; }
    }

    /// <summary>
    /// <c>staticdungeoninstances</c>: the record of live static dungeon instances every zone instance shares. The
    /// table's keys do the arbitration: one row per dungeon playfield and one per owner and entrance, so two zones
    /// racing to make one team's instance see one insert succeed and the other fail.
    /// </summary>
    public sealed class MySqlStaticDungeonInstanceDao
    {
        const string Columns = "PlayfieldId,EntranceType,EntranceInstance,OwnerKind,OwnerId,CreatedAtUtcTicks";

        readonly Func<IDbConnection> connectionFactory;

        public MySqlStaticDungeonInstanceDao(Func<IDbConnection> connectionFactory)
        {
            this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        }

        /// <summary>The owner's instance at the entrance, or null.</summary>
        public StaticDungeonInstanceRow Find(uint entranceType, uint entranceInstance, int ownerKind, int ownerId)
        {
            IList<StaticDungeonInstanceRow> rows = Query(
                "SELECT " + Columns + " FROM staticdungeoninstances "
                + "WHERE EntranceType=@Type AND EntranceInstance=@Instance AND OwnerKind=@Kind AND OwnerId=@Owner",
                "@Type", entranceType, "@Instance", entranceInstance, "@Kind", ownerKind, "@Owner", ownerId);
            return rows.Count == 0 ? null : rows[0];
        }

        /// <summary>The instance with that dungeon playfield id, or null.</summary>
        public StaticDungeonInstanceRow Load(int playfieldId)
        {
            IList<StaticDungeonInstanceRow> rows = Query(
                "SELECT " + Columns + " FROM staticdungeoninstances WHERE PlayfieldId=@Id",
                "@Id", playfieldId);
            return rows.Count == 0 ? null : rows[0];
        }

        /// <summary>
        /// Records the instance. False when the playfield id is taken or the owner already holds an instance at that
        /// entrance (either unique key); nothing is written then.
        /// </summary>
        public bool TryInsert(StaticDungeonInstanceRow row)
        {
            if (row == null)
                throw new ArgumentNullException(nameof(row));
            if (row.PlayfieldId <= 0)
                throw new ArgumentException("A positive dungeon playfield id is required.", nameof(row));

            return Execute(
                "INSERT IGNORE INTO staticdungeoninstances (" + Columns + ") VALUES "
                + "(@PlayfieldId,@Type,@Instance,@Kind,@Owner,@Created)",
                "@PlayfieldId", row.PlayfieldId,
                "@Type", row.EntranceType,
                "@Instance", row.EntranceInstance,
                "@Kind", row.OwnerKind,
                "@Owner", row.OwnerId,
                "@Created", row.CreatedAtUtcTicks) == 1;
        }

        public void Delete(int playfieldId)
        {
            Execute("DELETE FROM staticdungeoninstances WHERE PlayfieldId=@Id", "@Id", playfieldId);
        }

        /// <summary>Drops up to <paramref name="limit"/> rows created before the cutoff; returns how many went.</summary>
        public int PurgeOlderThan(long cutoffUtcTicks, int limit)
        {
            if (limit <= 0)
                throw new ArgumentOutOfRangeException(nameof(limit));

            return Execute(
                "DELETE FROM staticdungeoninstances WHERE CreatedAtUtcTicks<@Cutoff ORDER BY CreatedAtUtcTicks LIMIT " + limit,
                "@Cutoff", cutoffUtcTicks);
        }

        IList<StaticDungeonInstanceRow> Query(string sql, params object[] parameters)
        {
            var rows = new List<StaticDungeonInstanceRow>();
            using (IDbConnection connection = Open())
            using (IDbCommand command = Prepare(connection, null, sql, parameters))
            using (IDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                    rows.Add(Read(reader));
            }

            return rows;
        }

        int Execute(string sql, params object[] parameters)
        {
            using (IDbConnection connection = Open())
            using (IDbCommand command = Prepare(connection, null, sql, parameters))
                return command.ExecuteNonQuery();
        }

        IDbConnection Open()
        {
            IDbConnection connection = connectionFactory();
            if (connection == null)
                throw new InvalidOperationException("The connection factory returned no connection.");
            if (connection.State != ConnectionState.Open)
                connection.Open();
            return connection;
        }

        static IDbCommand Prepare(IDbConnection connection, IDbTransaction transaction, string sql, object[] parameters)
        {
            if (parameters.Length % 2 != 0)
                throw new ArgumentException("Parameters come in name/value pairs.", nameof(parameters));

            IDbCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            for (int i = 0; i < parameters.Length; i += 2)
            {
                IDbDataParameter parameter = command.CreateParameter();
                parameter.ParameterName = (string)parameters[i];
                parameter.Value = parameters[i + 1] ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }

            return command;
        }

        static StaticDungeonInstanceRow Read(IDataReader reader)
        {
            return new StaticDungeonInstanceRow
            {
                PlayfieldId = Convert.ToInt32(reader[0]),
                EntranceType = Convert.ToUInt32(reader[1]),
                EntranceInstance = Convert.ToUInt32(reader[2]),
                OwnerKind = Convert.ToInt32(reader[3]),
                OwnerId = Convert.ToInt32(reader[4]),
                CreatedAtUtcTicks = Convert.ToInt64(reader[5])
            };
        }
    }
}
