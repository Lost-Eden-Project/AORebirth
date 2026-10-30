namespace ZoneEngine_New.Tests;

using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AORebirth.Interfaces.Persistence.Characters;
using ZoneEngine_New.Core.DailyLogin;
using ZoneEngine_New.Core.Network;

[TestClass]
public sealed class DailyLoginClaimBridgeTests
{
    static readonly DateTime Today = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void CompletedReceiptRepairsStaleBoardEvenWhenItsPendingRequestIsAlreadyResolved()
    {
        using var world = new World();
        world.Files.WriteResult(world.Result);
        world.Bridge.HandleClaim(world.Player, "account", Today);
        world.AssertCurrentBoard();
        Assert.AreEqual(0, world.Dao.Commits);
        Assert.AreEqual(0, world.Session.Sent.OfType<SmokeLounge.AOtomation.Messaging.Messages.N3Messages.AddTemplateMessage>().Count());
    }

    [TestMethod]
    public void LostFileProjectionsAreRebuiltFromCommittedReceiptWithoutGrantingAgain()
    {
        using var world = new World();
        string board = Path.Combine(world.Root, "board-character-101.json");
        Directory.CreateDirectory(board); // A real projection write failure after the DB commit.
        world.Bridge.HandleClaim(world.Player, "account", Today);
        Assert.IsTrue(File.Exists(Path.Combine(world.Root, "request-recovery.json")));
        Assert.IsFalse(File.Exists(Path.Combine(world.Root, "result-character-101-recovery.json")));
        Directory.Delete(board);
        world.Bridge.HandleClaim(world.Player, "account", Today);
        world.AssertCurrentBoard();
        DailyLoginClaimResult restored = DailyLoginClaimResult.Parse(File.ReadAllText(Path.Combine(world.Root, "result-character-101-recovery.json")));
        Assert.IsTrue(restored.Committed && restored.Replayed);
        Assert.AreEqual(0, world.Dao.Commits);
        Assert.AreEqual(0, world.Player.Inventory.Inventory.Content.Count);
        Assert.AreEqual(0, world.Session.Sent.OfType<SmokeLounge.AOtomation.Messaging.Messages.N3Messages.AddTemplateMessage>().Count());
    }

    [TestMethod]
    public void VerifiedLegacyReplayPublishesCharacterReceiptWithoutMutatingItsOriginal()
    {
        using var world = new World();
        const string original = """{"ok":true,"committed":true,"terminal":true,"accountKey":"account","claimToken":"recovery","day":2,"taken":[1,2],"lastGrantedUtc":"2026-10-29"}""";
        string path = Path.Combine(world.Root, "result-recovery.json");
        File.WriteAllText(path, original);
        world.Bridge.HandleClaim(world.Player, "account", Today);
        world.AssertCurrentBoard();
        Assert.AreEqual(original, File.ReadAllText(path));
        var qualified = DailyLoginClaimResult.Parse(File.ReadAllText(Path.Combine(world.Root, "result-character-101-recovery.json")));
        Assert.AreEqual(101, qualified.CharacterId);
        Assert.IsTrue(qualified.Committed && qualified.Replayed);
        Assert.AreEqual(0, world.Dao.Commits);
    }

    [TestMethod]
    public void AnotherCharacterCannotTurnUnattributedHistoryIntoANewGrant()
    {
        using var world = new World();
        const string original = """{"ok":true,"committed":true,"accountKey":"account","claimToken":"recovery","day":2,"taken":[1,2]}""";
        File.WriteAllText(Path.Combine(world.Root, "result-recovery.json"), original);
        File.WriteAllText(Path.Combine(world.Root, "request-recovery.json"), """{"CharacterId":102,"AccountKey":"account","ClaimToken":"recovery"}""");
        world.Player = TestWorld.CreatePlayer(102);
        world.Player.Session = world.Session;
        world.Session.BindPlayer(world.Player);
        world.Bridge.HandleClaim(world.Player, "account", Today);
        Assert.AreEqual(0, world.Dao.Commits);
        Assert.AreEqual(0, world.Player.Inventory.Inventory.Content.Count);
        Assert.AreEqual(original, File.ReadAllText(Path.Combine(world.Root, "result-recovery.json")));
        Assert.IsFalse(File.Exists(Path.Combine(world.Root, "result-character-102-recovery.json")));
    }

    sealed class World : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "aorebirth-daily-recovery-" + Guid.NewGuid().ToString("N"));
        public Core.Entities.Player Player = TestWorld.CreatePlayer(101);
        public RecordingZoneSession Session = new() { State = SessionState.InPlay };
        public DailyLoginClaimFiles Files;
        public DailyLoginClaimResult Result;
        public Dao Dao;
        public DailyLoginClaimBridge Bridge;
        public World()
        {
            Directory.CreateDirectory(Root);
            Player.Session = Session;
            Session.BindPlayer(Player);
            var state = new DailyLoginAccountState { Taken = [1, 2], ClaimedCount = 2, LastClaimUtc = "2026-10-29", LastGrantedUtc = "2026-10-29" };
            Result = DailyLoginClaimResult.ForState("account", 101, "recovery", state, Today);
            Result.Ok = Result.Committed = Result.Terminal = true;
            Result.Day = 2;
            Dao = new Dao(new DailyLoginClaimSnapshotData
            {
                AccountId = 1, AccountUsername = "account", CharacterId = 101, ClaimToken = "recovery",
                StateJson = state.ToJson(), ResultJson = Result.ToJson(), ClaimDateUtc = Today.Date
            });
            Files = new DailyLoginClaimFiles([Root]);
            File.WriteAllText(Path.Combine(Root, "request-recovery.json"), """{"CharacterId":101,"AccountKey":"account","ClaimToken":"recovery"}""");
            File.WriteAllText(Path.Combine(Root, "account-account.json"), new DailyLoginAccountState
                { Taken = [1], LastGrantedUtc = "2026-10-28" }.ToJson());
            Bridge = new DailyLoginClaimBridge(new DailyLoginRewardService(null!, null!, null!, Dao), Dao, Files, new StubLogger());
        }
        public void AssertCurrentBoard()
        {
            var board = DailyLoginClaimResult.Parse(File.ReadAllText(Path.Combine(Root, "board-character-101.json")));
            Assert.AreEqual(101, board.CharacterId);
            CollectionAssert.AreEqual(new[] { 1, 2 }, board.Taken);
            Assert.IsTrue(board.ClaimedToday);
        }
        public void Dispose()
        {
            if (!Path.GetFullPath(Root).StartsWith(Path.Combine(Path.GetTempPath(), "aorebirth-daily-recovery-"), StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected owned test directory.");
            Directory.Delete(Root, recursive: true);
        }
    }

    sealed class Dao(DailyLoginClaimSnapshotData snapshot) : IDailyLoginClaimDao
    {
        public int Commits;
        public DailyLoginClaimSnapshotData LoadDailyLoginState(string accountUsername, int characterId)
            => snapshot.AccountUsername == accountUsername && snapshot.CharacterId == characterId ? snapshot : null!;
        public DailyLoginClaimSnapshotData FindDailyLoginClaim(string accountUsername, int characterId, string claimToken)
            => snapshot.AccountUsername == accountUsername && snapshot.CharacterId == characterId && snapshot.ClaimToken == claimToken ? snapshot : null!;
        public bool HasDailyLoginHistory(string accountUsername) => snapshot.AccountUsername == accountUsername;
        public DailyLoginClaimSnapshotData ImportDailyLoginState(string accountUsername, int characterId, string stateJson, DateTime? lastGrantedDateUtc, long nowUtcTicks)
            => throw new InvalidOperationException("Committed progress must never be imported again.");
        public DailyLoginClaimCommitResultData CommitDailyLoginClaim(DailyLoginClaimCommitData request)
        {
            Commits++;
            throw new InvalidOperationException("A receipt replay must never grant another inventory row.");
        }
    }
}
