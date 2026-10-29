namespace AORebirth.Interfaces.Persistence.Characters
{
    using System;
    using System.Collections.Generic;

    /// <summary>Account-owned daily-login receipts and inventory share the character DAO's transaction.</summary>
    public interface IDailyLoginClaimDao
    {
        DailyLoginClaimSnapshotData LoadDailyLoginState(string accountUsername);
        DailyLoginClaimSnapshotData FindDailyLoginClaim(string accountUsername, string claimToken);
        DailyLoginClaimSnapshotData ImportDailyLoginState(string accountUsername, int characterId,
            string stateJson, DateTime? lastGrantedDateUtc, long nowUtcTicks);
        DailyLoginClaimCommitResultData CommitDailyLoginClaim(DailyLoginClaimCommitData request);
    }

    public sealed class DailyLoginClaimSnapshotData
    {
        public int AccountId { get; set; }
        public string AccountUsername { get; set; }
        public string ClaimToken { get; set; }
        public int CharacterId { get; set; }
        public DateTime? ClaimDateUtc { get; set; }
        public string StateJson { get; set; }
        public string ResultJson { get; set; }
        public bool IsLegacyImport { get; set; }
    }

    public sealed class DailyLoginClaimCommitData
    {
        public string AccountUsername { get; set; }
        public int CharacterId { get; set; }
        public string ClaimToken { get; set; }
        public DateTime ClaimDateUtc { get; set; }
        public string ExpectedStateToken { get; set; }
        public string StateJson { get; set; }
        public string ResultJson { get; set; }
        public IList<PersistedItemData> Inserts { get; set; } = new List<PersistedItemData>();
        public long NowUtcTicks { get; set; }
    }

    public enum DailyLoginClaimCommitStatus
    {
        Applied,
        AlreadyApplied,
        AlreadyClaimedToday,
        StateChanged,
        TokenOwnedByAnotherAccount
    }

    public sealed class DailyLoginClaimCommitResultData
    {
        public DailyLoginClaimCommitStatus Status { get; set; }
        public DailyLoginClaimSnapshotData Snapshot { get; set; }
    }
}
