namespace ZoneEngine_New.Core.Organizations
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Linq;

    using Dapper;

    using MySqlConnector;

    using ZoneEngine_New.Core.Data;

    public enum OrgTextField
    {
        Description,
        Objective,
        History
    }

    public enum OrgCreateStatus
    {
        Created,
        NameTaken,
        MemberAlreadyInOrganization
    }

    public readonly record struct OrgCreateResult(OrgCreateStatus Status, int OrganizationId);

    /// <summary>
    /// The organization tables (org_organizations, org_members, org_votes, org_vote_ballots): the only authority for who
    /// belongs to which organization. Every call blocks on the database; callers run it off the playfield tick.
    /// </summary>
    public interface IOrganizationStore
    {
        /// <summary>The character's membership, or null when it belongs to no organization.</summary>
        OrgMembershipRow? LoadMembership(int characterId);

        /// <summary>The organization and its members, or null when it does not exist.</summary>
        (OrgRow Row, IReadOnlyList<OrgMemberRow> Members)? Load(int organizationId);

        /// <summary>The current Version of each organization of <paramref name="organizationIds"/> that still exists.</summary>
        IReadOnlyDictionary<int, long> Versions(IReadOnlyCollection<int> organizationIds);

        /// <summary>
        /// Runs <paramref name="work"/> in one transaction holding the organization's row lock, so every change to one
        /// organization, from any zone instance, is serialized. Commits when it returns, rolls back when it throws.
        /// </summary>
        T Transact<T>(int organizationId, Func<OrgTransaction, T> work);

        /// <summary>Creates the organization with its first members, all or nothing.</summary>
        OrgCreateResult Create(string name, int governingForm, int leaderId, IReadOnlyList<(int CharacterId, int Rank)> members);

        OrgVote? LoadOpenVote(int organizationId);

        int? LoadBallot(long voteId, int characterId);

        /// <summary>Open votes whose time has run out.</summary>
        IReadOnlyList<long> DueVotes(int limit);

        /// <summary>Closes a vote whose time has run out and counts it; null when it was already closed (by another instance).</summary>
        OrgVoteTally? CloseDueVote(long voteId);
    }

    public sealed class MySqlOrganizationStore : IOrganizationStore
    {
        internal const string SelectOrg =
            "SELECT OrganizationId, Name, GoverningForm, LeaderId, Description, Objective, History, Bank, Version "
            + "FROM org_organizations WHERE OrganizationId=@Id";

        // A member whose character no longer exists is not listed (character deletion removes the row; this covers a
        // character removed some other way).
        internal const string SelectMembers =
            "SELECT m.CharacterId, c.Name, m.OrgRank AS StoredRank, m.JoinedAtUtc FROM org_members m "
            + "INNER JOIN characters c ON c.Id=m.CharacterId WHERE m.OrganizationId=@Id ORDER BY m.OrgRank, m.CharacterId";

        internal const string SelectVote =
            "SELECT VoteId, OrganizationId, Question, Choices, StartedBy, StartedAtUtc, OpensAtUtc, EndsAtUtc, ClosedAtUtc, StopReason "
            + "FROM org_votes ";

        internal static MySqlConnection Open()
        {
            var connection = new MySqlConnection(MySqlConnectionSettings.GetRequiredConnectionString());
            try
            {
                connection.Open();
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        public OrgMembershipRow? LoadMembership(int characterId)
        {
            if (characterId <= 0)
                return null;
            using MySqlConnection connection = Open();
            return connection.QuerySingleOrDefault<OrgMembershipRow>(
                "SELECT o.OrganizationId, o.Name, o.GoverningForm, o.LeaderId, m.OrgRank AS StoredRank FROM org_members m "
                + "INNER JOIN org_organizations o ON o.OrganizationId=m.OrganizationId WHERE m.CharacterId=@Id",
                new { Id = characterId });
        }

        public (OrgRow Row, IReadOnlyList<OrgMemberRow> Members)? Load(int organizationId)
        {
            if (organizationId <= 0)
                return null;
            using MySqlConnection connection = Open();
            using MySqlTransaction transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead);
            OrgRow? row = connection.QuerySingleOrDefault<OrgRow>(SelectOrg, new { Id = organizationId }, transaction);
            if (row == null)
                return null;
            List<OrgMemberRow> members = connection.Query<OrgMemberRow>(SelectMembers, new { Id = organizationId }, transaction).AsList();
            transaction.Commit();
            return (row, members);
        }

        public IReadOnlyDictionary<int, long> Versions(IReadOnlyCollection<int> organizationIds)
        {
            var result = new Dictionary<int, long>();
            if (organizationIds.Count == 0)
                return result;
            using MySqlConnection connection = Open();
            foreach (int[] chunk in organizationIds.Distinct().Chunk(500))
                foreach ((int id, long version) in connection.Query<(int, long)>(
                             "SELECT OrganizationId, Version FROM org_organizations WHERE OrganizationId IN @Ids", new { Ids = chunk }))
                    result[id] = version;
            return result;
        }

        public T Transact<T>(int organizationId, Func<OrgTransaction, T> work)
        {
            ArgumentNullException.ThrowIfNull(work);
            using MySqlConnection connection = Open();
            // Read committed: every read after the row lock sees what the previous holder committed.
            using MySqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var tx = new OrgTransaction(connection, transaction, organizationId);
            tx.LockAndLoad();
            T result = work(tx);
            tx.FinishWrites();
            transaction.Commit();
            return result;
        }

        public OrgCreateResult Create(string name, int governingForm, int leaderId, IReadOnlyList<(int CharacterId, int Rank)> members)
        {
            using MySqlConnection connection = Open();
            using MySqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            int[] ids = members.Select(m => m.CharacterId).ToArray();
            if (connection.ExecuteScalar<long>("SELECT COUNT(*) FROM org_members WHERE CharacterId IN @Ids FOR UPDATE",
                    new { Ids = ids }, transaction) > 0)
                return new OrgCreateResult(OrgCreateStatus.MemberAlreadyInOrganization, 0);

            int organizationId;
            try
            {
                organizationId = connection.ExecuteScalar<int>(
                    "INSERT INTO org_organizations (Name, GoverningForm, LeaderId, CreatedAtUtc) "
                    + "VALUES (@Name, @Form, @Leader, UTC_TIMESTAMP(6)); SELECT LAST_INSERT_ID();",
                    new { Name = name, Form = governingForm, Leader = leaderId }, transaction);
            }
            catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
            {
                return new OrgCreateResult(OrgCreateStatus.NameTaken, 0);
            }

            try
            {
                foreach ((int characterId, int rank) in members)
                    connection.Execute(
                        "INSERT INTO org_members (CharacterId, OrganizationId, OrgRank, JoinedAtUtc) VALUES (@Character, @Org, @Rank, UTC_TIMESTAMP(6))",
                        new { Character = characterId, Org = organizationId, Rank = rank }, transaction);
            }
            catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
            {
                transaction.Rollback();
                return new OrgCreateResult(OrgCreateStatus.MemberAlreadyInOrganization, 0);
            }

            transaction.Commit();
            return new OrgCreateResult(OrgCreateStatus.Created, organizationId);
        }

        public OrgVote? LoadOpenVote(int organizationId)
        {
            using MySqlConnection connection = Open();
            return OrgTransaction.MapVote(connection.QuerySingleOrDefault<VoteRow>(
                SelectVote + "WHERE OrganizationId=@Id AND ClosedAtUtc IS NULL ORDER BY VoteId DESC LIMIT 1", new { Id = organizationId }));
        }

        public int? LoadBallot(long voteId, int characterId)
        {
            using MySqlConnection connection = Open();
            return connection.QuerySingleOrDefault<int?>(
                "SELECT Choice FROM org_vote_ballots WHERE VoteId=@Vote AND CharacterId=@Character",
                new { Vote = voteId, Character = characterId });
        }

        public IReadOnlyList<long> DueVotes(int limit)
        {
            using MySqlConnection connection = Open();
            return connection.Query<long>(
                "SELECT VoteId FROM org_votes WHERE ClosedAtUtc IS NULL AND EndsAtUtc <= UTC_TIMESTAMP(6) ORDER BY EndsAtUtc LIMIT @Limit",
                new { Limit = limit }).AsList();
        }

        public OrgVoteTally? CloseDueVote(long voteId)
        {
            using MySqlConnection connection = Open();
            using MySqlTransaction transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            if (connection.Execute(
                    "UPDATE org_votes SET ClosedAtUtc=UTC_TIMESTAMP(6) WHERE VoteId=@Id AND ClosedAtUtc IS NULL AND EndsAtUtc <= UTC_TIMESTAMP(6)",
                    new { Id = voteId }, transaction) == 0)
                return null;
            OrgVote? vote = OrgTransaction.MapVote(connection.QuerySingleOrDefault<VoteRow>(SelectVote + "WHERE VoteId=@Id",
                new { Id = voteId }, transaction));
            if (vote == null)
                return null;
            OrgVoteTally tally = OrgTransaction.Count(connection, transaction, vote);
            transaction.Commit();
            return tally;
        }

        internal sealed record VoteRow(
            long VoteId, int OrganizationId, string Question, string Choices, int StartedBy, DateTime StartedAtUtc,
            DateTime OpensAtUtc, DateTime EndsAtUtc, DateTime? ClosedAtUtc, string? StopReason);
    }

    /// <summary>
    /// One organization's transaction: its row is locked from <see cref="Row"/>'s read until commit. Every write marks
    /// the organization changed; its Version is raised once before the commit, so other instances see a newer version.
    /// </summary>
    public sealed class OrgTransaction
    {
        readonly MySqlConnection _connection;
        readonly MySqlTransaction _transaction;
        bool _changed;
        bool _deleted;

        internal OrgTransaction(MySqlConnection connection, MySqlTransaction transaction, int organizationId)
        {
            _connection = connection;
            _transaction = transaction;
            OrganizationId = organizationId;
        }

        public int OrganizationId { get; }

        /// <summary>The locked row; null when the organization does not exist.</summary>
        public OrgRow? Row { get; private set; }

        public IReadOnlyList<OrgMemberRow> Members { get; private set; } = [];

        public bool Changed => _changed;

        public bool Deleted => _deleted;

        internal void LockAndLoad()
        {
            Row = OrganizationId <= 0
                ? null
                : _connection.QuerySingleOrDefault<OrgRow>(MySqlOrganizationStore.SelectOrg + " FOR UPDATE", new { Id = OrganizationId }, _transaction);
            Members = Row == null
                ? []
                : _connection.Query<OrgMemberRow>(MySqlOrganizationStore.SelectMembers, new { Id = OrganizationId }, _transaction).AsList();
        }

        internal void FinishWrites()
        {
            if (_changed && !_deleted)
                Execute("UPDATE org_organizations SET Version=Version+1 WHERE OrganizationId=@Org", new { });
            _changed = false;
        }

        /// <summary>Raises the version now and reads the organization back as this transaction leaves it; null when deleted.</summary>
        public (OrgRow Row, IReadOnlyList<OrgMemberRow> Members)? Reload()
        {
            FinishWrites();
            if (_deleted)
                return null;
            LockAndLoad();
            return Row == null ? null : (Row, Members);
        }

        int Execute(string sql, object parameters)
        {
            var arguments = new DynamicParameters(parameters);
            arguments.Add("Org", OrganizationId);
            return _connection.Execute(sql, arguments, _transaction);
        }

        int Write(string sql, object parameters)
        {
            int rows = Execute(sql, parameters);
            if (rows > 0)
                _changed = true;
            return rows;
        }

        public void SetRank(int characterId, int rank)
            => Write("UPDATE org_members SET OrgRank=@Rank WHERE CharacterId=@Character AND OrganizationId=@Org",
                new { Rank = Math.Max(0, rank), Character = characterId });

        /// <summary>False when the character already belongs to an organization.</summary>
        public bool AddMember(int characterId, int rank)
        {
            try
            {
                Write("INSERT INTO org_members (CharacterId, OrganizationId, OrgRank, JoinedAtUtc) VALUES (@Character, @Org, @Rank, UTC_TIMESTAMP(6))",
                    new { Character = characterId, Rank = Math.Max(0, rank) });
                return true;
            }
            catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
            {
                return false;
            }
        }

        public bool RemoveMember(int characterId)
            => Write("DELETE FROM org_members WHERE CharacterId=@Character AND OrganizationId=@Org", new { Character = characterId }) > 0;

        public void SetLeader(int characterId)
            => Write("UPDATE org_organizations SET LeaderId=@Character WHERE OrganizationId=@Org", new { Character = characterId });

        public void SetText(OrgTextField field, string value)
        {
            string column = field switch
            {
                OrgTextField.Description => "Description",
                OrgTextField.Objective => "Objective",
                OrgTextField.History => "History",
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            };
            Write("UPDATE org_organizations SET " + column + "=@Value WHERE OrganizationId=@Org", new { Value = value });
            _changed = true;
        }

        /// <summary>False when another organization has the name (ignoring case).</summary>
        public bool TrySetName(string name)
        {
            try
            {
                Write("UPDATE org_organizations SET Name=@Name WHERE OrganizationId=@Org", new { Name = name });
                _changed = true;
                return true;
            }
            catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
            {
                return false;
            }
        }

        /// <summary>Sets the form and puts every member but the leader at <paramref name="memberRank"/>.</summary>
        public void SetGoverningForm(int form, int leaderId, int memberRank)
        {
            Write("UPDATE org_organizations SET GoverningForm=@Form WHERE OrganizationId=@Org", new { Form = form });
            Write("UPDATE org_members SET OrgRank=@Rank WHERE OrganizationId=@Org AND CharacterId<>@Leader",
                new { Rank = Math.Max(0, memberRank), Leader = leaderId });
            _changed = true;
        }

        /// <summary>Adds <paramref name="delta"/> (negative to take) when the balance stays within 0..<paramref name="cap"/>.</summary>
        public bool TryAdjustBank(long delta, long cap)
            => Write("UPDATE org_organizations SET Bank=Bank+@Delta WHERE OrganizationId=@Org AND Bank+@Delta >= 0 AND Bank+@Delta <= @Cap",
                new { Delta = delta, Cap = cap }) > 0;

        /// <summary>Deletes the organization; its members, votes and ballots go with it (foreign keys).</summary>
        public void Delete()
        {
            Execute("DELETE FROM org_organizations WHERE OrganizationId=@Org", new { });
            _deleted = true;
            _changed = true;
        }

        /// <summary>The id and stored name of the character called <paramref name="name"/>, or null.</summary>
        public CharacterName? FindCharacter(string name)
            => _connection.QuerySingleOrDefault<CharacterName>("SELECT Id, Name FROM characters WHERE Name=@Name LIMIT 1",
                new { Name = name }, _transaction);

        public sealed record CharacterName(int Id, string Name);

        public OrgVote? OpenVote()
            => MapVote(_connection.QuerySingleOrDefault<MySqlOrganizationStore.VoteRow>(
                MySqlOrganizationStore.SelectVote + "WHERE OrganizationId=@Org AND ClosedAtUtc IS NULL ORDER BY VoteId DESC LIMIT 1",
                new { Org = OrganizationId }, _transaction));

        public DateTime? LastVoteStartedAtUtc()
            => _connection.QuerySingleOrDefault<DateTime?>("SELECT MAX(StartedAtUtc) FROM org_votes WHERE OrganizationId=@Org",
                new { Org = OrganizationId }, _transaction);

        /// <summary>The database clock: every instance compares vote times against the same one.</summary>
        public DateTime UtcNow()
            => Utc(_connection.QuerySingle<DateTime>("SELECT UTC_TIMESTAMP(6)", transaction: _transaction));

        public long StartVote(string question, IReadOnlyList<string> choices, int startedBy, DateTime startedAtUtc,
            DateTime opensAtUtc, DateTime endsAtUtc)
            => _connection.ExecuteScalar<long>(
                "INSERT INTO org_votes (OrganizationId, Question, Choices, StartedBy, StartedAtUtc, OpensAtUtc, EndsAtUtc) "
                + "VALUES (@Org, @Question, @Choices, @By, @Started, @Opens, @Ends); SELECT LAST_INSERT_ID();",
                new
                {
                    Org = OrganizationId, Question = question, Choices = string.Join(OrgVote.ChoiceSeparator, choices),
                    By = startedBy, Started = startedAtUtc, Opens = opensAtUtc, Ends = endsAtUtc
                }, _transaction);

        public int? Ballot(long voteId, int characterId)
            => _connection.QuerySingleOrDefault<int?>("SELECT Choice FROM org_vote_ballots WHERE VoteId=@Vote AND CharacterId=@Character",
                new { Vote = voteId, Character = characterId }, _transaction);

        /// <summary>False when the member has voted already.</summary>
        public bool CastBallot(long voteId, int characterId, int choice)
        {
            try
            {
                _connection.Execute("INSERT INTO org_vote_ballots (VoteId, CharacterId, Choice, CastAtUtc) VALUES (@Vote, @Character, @Choice, UTC_TIMESTAMP(6))",
                    new { Vote = voteId, Character = characterId, Choice = choice }, _transaction);
                return true;
            }
            catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
            {
                return false;
            }
        }

        public bool StopVote(long voteId, string reason)
            => _connection.Execute("UPDATE org_votes SET ClosedAtUtc=UTC_TIMESTAMP(6), StopReason=@Reason WHERE VoteId=@Vote AND OrganizationId=@Org AND ClosedAtUtc IS NULL",
                new { Vote = voteId, Org = OrganizationId, Reason = reason }, _transaction) > 0;

        internal static OrgVote? MapVote(MySqlOrganizationStore.VoteRow? row)
            => row == null
                ? null
                : new OrgVote(row.VoteId, row.OrganizationId, row.Question,
                    Array.AsReadOnly(row.Choices.Split(OrgVote.ChoiceSeparator, StringSplitOptions.RemoveEmptyEntries)),
                    row.StartedBy, Utc(row.StartedAtUtc), Utc(row.OpensAtUtc), Utc(row.EndsAtUtc),
                    row.ClosedAtUtc is { } closed ? Utc(closed) : null, row.StopReason);

        internal static OrgVoteTally Count(MySqlConnection connection, MySqlTransaction transaction, OrgVote vote)
        {
            var counts = new Dictionary<int, int>();
            int voters = 0;
            foreach ((int choice, int count) in connection.Query<(int, int)>(
                         "SELECT Choice, COUNT(*) FROM org_vote_ballots WHERE VoteId=@Vote GROUP BY Choice", new { Vote = vote.VoteId }, transaction))
            {
                if (choice < 1 || choice > vote.Choices.Count)
                    continue;
                counts[choice] = count;
                voters += count;
            }

            return new OrgVoteTally(vote, counts, voters);
        }

        static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
