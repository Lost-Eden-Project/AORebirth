namespace AORebirth.Database.Domain.Characters
{
    using System;
    using System.Data;
    using System.Globalization;
    using System.Linq;
    using AORebirth.Interfaces.Persistence.Characters;
    using SmokeLounge.AOtomation.Messaging.GameData;

    public sealed partial class MySqlCharacterPersistenceDao : IDailyLoginClaimDao
    {
        const string DailyLoginColumns = "d.AccountId,l.Username,d.ClaimToken,d.CharacterId,d.ClaimDateUtc,d.StateJson,d.ResultJson,d.IsLegacyImport";

        public DailyLoginClaimSnapshotData LoadDailyLoginState(string accountUsername)
        {
            ValidateDailyLoginAccount(accountUsername);
            return Read(c => ReadDailyLoginRow(c, callerTransaction,
                "SELECT " + DailyLoginColumns + " FROM dailyloginclaims d JOIN login l ON l.Id=d.AccountId "
                + "WHERE l.Username=@Account ORDER BY d.Id DESC LIMIT 1", "@Account", accountUsername));
        }

        public DailyLoginClaimSnapshotData FindDailyLoginClaim(string accountUsername, string claimToken)
        {
            ValidateDailyLoginAccount(accountUsername);
            ValidateDailyLoginToken(claimToken);
            return Read(c => ReadDailyLoginRow(c, callerTransaction,
                "SELECT " + DailyLoginColumns + " FROM dailyloginclaims d JOIN login l ON l.Id=d.AccountId "
                + "WHERE l.Username=@Account AND d.ClaimToken=@Token LIMIT 1", "@Account", accountUsername, "@Token", claimToken));
        }

        public DailyLoginClaimSnapshotData ImportDailyLoginState(string accountUsername, int characterId,
            string stateJson, DateTime? lastGrantedDateUtc, long nowUtcTicks)
        {
            ValidateDailyLoginAccount(accountUsername);
            ValidateDailyLoginJson(stateJson, nameof(stateJson));
            ValidateDailyLoginDate(lastGrantedDateUtc);
            ValidateDailyLoginTicks(nowUtcTicks);
            if (characterId <= 0) throw new ArgumentOutOfRangeException(nameof(characterId));
            if (lastGrantedDateUtc.HasValue && lastGrantedDateUtc.Value.Date > new DateTime(nowUtcTicks, DateTimeKind.Utc).Date)
                throw new ArgumentException("Imported daily-login progress has a future grant date.", nameof(lastGrantedDateUtc));
            return Transaction((c, t) =>
            {
                var account = LockDailyLoginAccount(c, t, accountUsername, characterId);
                var current = ReadLatestDailyLogin(c, t, account.AccountId);
                // The original files seed a new ledger once. A stale file can never replace durable progress.
                if (current != null) return current;
                var imported = new DailyLoginClaimSnapshotData
                {
                    AccountId = account.AccountId, AccountUsername = account.AccountUsername,
                    ClaimToken = "legacy-import:" + account.AccountId.ToString(CultureInfo.InvariantCulture),
                    CharacterId = characterId, ClaimDateUtc = lastGrantedDateUtc,
                    StateJson = stateJson, ResultJson = "{}", IsLegacyImport = true
                };
                WriteDailyLoginRow(c, t, imported, nowUtcTicks);
                return imported;
            }, IsolationLevel.ReadCommitted);
        }

        public DailyLoginClaimCommitResultData CommitDailyLoginClaim(DailyLoginClaimCommitData request)
        {
            ValidateDailyLoginCommit(request);
            return Transaction((c, t) =>
            {
                var account = LockDailyLoginAccount(c, t, request.AccountUsername, request.CharacterId);
                // An acknowledged or lost-ACK retry always resolves its immutable receipt before today's gate.
                var prior = ReadDailyLoginRow(c, t,
                    "SELECT " + DailyLoginColumns + " FROM dailyloginclaims d JOIN login l ON l.Id=d.AccountId "
                    + "WHERE d.ClaimToken=@Token LIMIT 1", "@Token", request.ClaimToken);
                if (prior != null)
                    return DailyLoginResult(prior.AccountId == account.AccountId
                        ? DailyLoginClaimCommitStatus.AlreadyApplied : DailyLoginClaimCommitStatus.TokenOwnedByAnotherAccount,
                        prior.AccountId == account.AccountId ? prior : null);

                var current = ReadLatestDailyLogin(c, t, account.AccountId);
                if (current != null && current.ClaimDateUtc.HasValue && current.ClaimDateUtc.Value.Date >= request.ClaimDateUtc.Date)
                    return DailyLoginResult(DailyLoginClaimCommitStatus.AlreadyClaimedToday, current);
                if (!string.Equals(current == null ? null : current.ClaimToken, request.ExpectedStateToken, StringComparison.Ordinal))
                    return DailyLoginResult(DailyLoginClaimCommitStatus.StateChanged, current);

                var completed = new DailyLoginClaimSnapshotData
                {
                    AccountId = account.AccountId, AccountUsername = account.AccountUsername,
                    ClaimToken = request.ClaimToken, CharacterId = request.CharacterId, ClaimDateUtc = request.ClaimDateUtc,
                    StateJson = request.StateJson, ResultJson = request.ResultJson, IsLegacyImport = false
                };
                // Reuse the exact inventory insert primitive. The receipt and every stack commit or roll back together.
                foreach (var item in request.Inserts) WriteItem(c, t, item);
                WriteDailyLoginRow(c, t, completed, request.NowUtcTicks);
                return DailyLoginResult(DailyLoginClaimCommitStatus.Applied, completed);
            }, IsolationLevel.ReadCommitted);
        }

        static DailyLoginClaimSnapshotData LockDailyLoginAccount(IDbConnection c, IDbTransaction t, string username, int characterId)
        {
            var account = new DailyLoginClaimSnapshotData();
            using (var command = Command(c, t, "SELECT Id,Username FROM login WHERE Username=@Account FOR UPDATE", "@Account", username))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) throw new InvalidOperationException("Daily-login account is unavailable.");
                account.AccountId = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                account.AccountUsername = reader.GetString(1);
            }
            using (var command = Command(c, t, "SELECT Username,Online FROM characters WHERE Id=@Character FOR UPDATE", "@Character", characterId))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read() || !string.Equals(reader.GetString(0), account.AccountUsername, StringComparison.OrdinalIgnoreCase)
                    || reader.IsDBNull(1) || Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture) == 0)
                    throw new InvalidOperationException("Daily-login character does not belong to the active account.");
            }
            return account;
        }

        static DailyLoginClaimSnapshotData ReadLatestDailyLogin(IDbConnection c, IDbTransaction t, int accountId)
            => ReadDailyLoginRow(c, t,
                "SELECT " + DailyLoginColumns + " FROM dailyloginclaims d JOIN login l ON l.Id=d.AccountId "
                + "WHERE d.AccountId=@Account ORDER BY d.Id DESC LIMIT 1 FOR UPDATE", "@Account", accountId);

        static DailyLoginClaimSnapshotData ReadDailyLoginRow(IDbConnection c, IDbTransaction t, string sql, params object[] parameters)
        {
            using (var command = Command(c, t, sql, parameters))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) return null;
                return new DailyLoginClaimSnapshotData
                {
                    AccountId = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture), AccountUsername = reader.GetString(1),
                    ClaimToken = reader.GetString(2), CharacterId = Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                    ClaimDateUtc = reader.IsDBNull(4) ? (DateTime?)null : DateTime.SpecifyKind(reader.GetDateTime(4).Date, DateTimeKind.Utc),
                    StateJson = reader.GetString(5), ResultJson = reader.GetString(6), IsLegacyImport = Convert.ToBoolean(reader.GetValue(7), CultureInfo.InvariantCulture)
                };
            }
        }

        static void WriteDailyLoginRow(IDbConnection c, IDbTransaction t, DailyLoginClaimSnapshotData row, long nowUtcTicks)
            => Execute(c, t, "INSERT INTO dailyloginclaims (AccountId,ClaimToken,CharacterId,ClaimDateUtc,StateJson,ResultJson,IsLegacyImport,CreatedAtUtcTicks) "
                + "VALUES (@Account,@Token,@Character,@Day,@State,@Result,@Imported,@Now)",
                "@Account", row.AccountId, "@Token", row.ClaimToken, "@Character", row.CharacterId,
                "@Day", row.ClaimDateUtc.HasValue ? (object)row.ClaimDateUtc.Value.Date : DBNull.Value,
                "@State", row.StateJson, "@Result", row.ResultJson, "@Imported", row.IsLegacyImport ? 1 : 0, "@Now", nowUtcTicks);

        static DailyLoginClaimCommitResultData DailyLoginResult(DailyLoginClaimCommitStatus status, DailyLoginClaimSnapshotData snapshot)
            => new DailyLoginClaimCommitResultData { Status = status, Snapshot = snapshot };

        static void ValidateDailyLoginAccount(string username)
        {
            if (string.IsNullOrWhiteSpace(username) || username.Length > 32 || username.Any(c => c < 32 || c > 126))
                throw new ArgumentException("A valid account username is required.", nameof(username));
        }

        static void ValidateDailyLoginToken(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length > 64 || token.Any(c => !(c >= 'a' && c <= 'z'
                || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '_' || c == '-')))
                throw new ArgumentException("A bounded daily-login claim token is required.", nameof(token));
        }

        static void ValidateDailyLoginJson(string json, string name)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 65536)
                throw new ArgumentException("Daily-login state and receipt must be nonempty bounded JSON.", name);
        }

        static void ValidateDailyLoginDate(DateTime? day)
        {
            if (day.HasValue && (day.Value.Year < 1000 || day.Value != day.Value.Date || day.Value.Kind == DateTimeKind.Local))
                throw new ArgumentException("Daily-login dates must be whole UTC days.", nameof(day));
        }

        static void ValidateDailyLoginTicks(long ticks)
        {
            if (ticks <= 0 || ticks > DateTime.MaxValue.Ticks) throw new ArgumentOutOfRangeException(nameof(ticks));
        }

        static void ValidateDailyLoginCommit(DailyLoginClaimCommitData request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            ValidateDailyLoginAccount(request.AccountUsername);
            ValidateDailyLoginToken(request.ClaimToken);
            ValidateDailyLoginJson(request.StateJson, nameof(request.StateJson));
            ValidateDailyLoginJson(request.ResultJson, nameof(request.ResultJson));
            ValidateDailyLoginDate(request.ClaimDateUtc);
            ValidateDailyLoginTicks(request.NowUtcTicks);
            if (request.ClaimDateUtc.Date != new DateTime(request.NowUtcTicks, DateTimeKind.Utc).Date)
                throw new ArgumentException("Daily-login grant date must match the authoritative UTC request time.", nameof(request));
            if (request.CharacterId <= 0 || request.Inserts == null || request.Inserts.Count == 0 || request.Inserts.Count > 1000)
                throw new ArgumentException("A complete positive daily-login inventory grant is required.", nameof(request));
            if (request.Inserts.Any(i => i == null || i.InstanceId <= 0 || i.ContainerInstance != request.CharacterId
                || i.ContainerType != (int)IdentityType.Inventory || i.ContainerPlacement < 0 || i.StackCount <= 0
                || i.LowId <= 0 || i.HighId <= 0 || i.Quality <= 0 || i.Source < 0 || i.Source > byte.MaxValue)
                || request.Inserts.Select(i => i.InstanceId).Distinct().Count() != request.Inserts.Count
                || request.Inserts.Select(i => i.ContainerPlacement).Distinct().Count() != request.Inserts.Count)
                throw new ArgumentException("Daily-login rows must be distinct, valid inventory placements for their character.", nameof(request));
        }
    }
}
