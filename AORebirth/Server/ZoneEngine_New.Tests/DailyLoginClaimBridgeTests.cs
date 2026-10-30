namespace ZoneEngine_New.Tests;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AORebirth.Interfaces.Persistence.Characters;
using ZoneEngine_New.Core.DailyLogin;
using ZoneEngine_New.Core.Network;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

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
        Assert.AreEqual(0, world.Session.Sent.OfType<ChatTextMessage>().Count());
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
        Assert.AreEqual(1, world.Session.Sent.OfType<ChatTextMessage>().Count(), "Projection failures must remain visible.");
        world.Session.Sent.Clear();
        Directory.Delete(board);
        world.Bridge.HandleClaim(world.Player, "account", Today);
        world.AssertCurrentBoard();
        DailyLoginClaimResult restored = DailyLoginClaimResult.Parse(File.ReadAllText(Path.Combine(world.Root, "result-character-101-recovery.json")));
        Assert.IsTrue(restored.Committed && restored.Replayed);
        Assert.AreEqual(0, world.Session.Sent.OfType<ChatTextMessage>().Count(), "Receipt recovery must not repeat successful-grant chat.");
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
        Assert.AreEqual(0, world.Session.Sent.OfType<ChatTextMessage>().Count());
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
        Assert.AreEqual(1, world.Session.Sent.OfType<ChatTextMessage>().Count(), "Ownership failures must remain visible.");
    }

    [TestMethod]
    public void EmptyNativeCallbackRepairsTheBoardWithoutSendingAWarning()
    {
        using var world = new World();
        File.Delete(Path.Combine(world.Root, "request-recovery.json"));
        world.Bridge.HandleClaim(world.Player, "account", Today);
        world.AssertCurrentBoard();
        Assert.AreEqual(0, world.Session.Sent.OfType<ChatTextMessage>().Count());
        Assert.AreEqual(0, world.Dao.Commits);
    }

    [TestMethod]
    public void TwoQueuedTokensCommitOneGrantResolveBothRequestsAndSendOnlyOneSuccess()
    {
        using var world = new GrantWorld();
        world.Queue("older_pending", Today.AddMinutes(-2));
        world.Queue("new_pending", Today.AddMinutes(-1));
        world.Bridge.HandleClaim(world.Runtime.Player, "account", Today);
        DailyLoginClaimResult granted = world.Receipt("older_pending");
        DailyLoginClaimResult refused = world.Receipt("new_pending");
        Assert.IsTrue(granted.Ok && granted.Committed && granted.Terminal);
        Assert.IsTrue(refused.Terminal && refused.AlreadyClaimedToday);
        Assert.IsFalse(refused.Ok || refused.Committed);
        Assert.AreEqual(1, world.Runtime.Dao.Rows.Count);
        Assert.AreEqual(1, world.Runtime.Dao.Calls);
        Assert.AreEqual(1, world.Runtime.Player.Inventory.Inventory.Content.Count);
        Assert.AreEqual(0, world.Files.Pending("account", 111).Count, "Both queued tokens must have terminal receipts.");
        Assert.AreEqual(granted.Message, world.Runtime.Session.Messages.OfType<ChatTextMessage>().Single().Text);
        world.Runtime.Session.Messages.Clear();
        world.Bridge.HandleClaim(world.Runtime.Player, "account", Today);
        Assert.AreEqual(0, world.Runtime.Session.Messages.OfType<ChatTextMessage>().Count());
        Assert.AreEqual(1, world.Runtime.Dao.Rows.Count);
    }

    [TestMethod]
    public void AStandaloneDailyLimitStillRejectsAndWarnsAfterAQuietReceiptReplay()
    {
        using var world = new GrantWorld();
        Assert.IsTrue(world.Runtime.Claim("committed_first").Committed);
        world.Runtime.Session.Messages.Clear();
        world.Queue("committed_first", Today.AddMinutes(-2));
        world.Queue("standalone_limit", Today.AddMinutes(-1));
        world.Bridge.HandleClaim(world.Runtime.Player, "account", Today);
        Assert.IsTrue(world.Receipt("committed_first").Replayed);
        DailyLoginClaimResult refused = world.Receipt("standalone_limit");
        Assert.IsTrue(refused.AlreadyClaimedToday && refused.Terminal);
        Assert.IsFalse(refused.Ok || refused.Committed);
        Assert.AreEqual(refused.Message, world.Runtime.Session.Messages.OfType<ChatTextMessage>().Single().Text);
        Assert.AreEqual(1, world.Runtime.Dao.Rows.Count);
        Assert.AreEqual(1, world.Runtime.Dao.Calls);
        Assert.AreEqual(0, world.Files.Pending("account", 111).Count);
    }

    sealed class GrantWorld : IDisposable
    {
        public readonly DailyLoginRewardServiceTests.World Runtime = new();
        public readonly string Root = Path.Combine(Path.GetTempPath(), "aorebirth-daily-chat-" + Guid.NewGuid().ToString("N"));
        public readonly DailyLoginClaimFiles Files;
        public readonly DailyLoginClaimBridge Bridge;
        public GrantWorld()
        {
            Directory.CreateDirectory(Root);
            Files = new DailyLoginClaimFiles([Root]);
            Bridge = new DailyLoginClaimBridge(Runtime.Service, Runtime.Dao, Files, new StubLogger());
        }
        public void Queue(string token, DateTime requested)
            => File.WriteAllText(Path.Combine(Root, "request-" + token + ".json"), JsonSerializer.Serialize(new
            {
                CharacterId = 111, AccountKey = "account", ClaimToken = token, RequestedUtc = requested.ToString("O")
            }));
        public DailyLoginClaimResult Receipt(string token)
            => DailyLoginClaimResult.Parse(File.ReadAllText(Path.Combine(Root, "result-character-111-" + token + ".json")));
        public void Dispose()
        {
            Runtime.Dispose();
            if (!Path.GetFullPath(Root).StartsWith(Path.Combine(Path.GetTempPath(), "aorebirth-daily-chat-"), StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected owned test directory.");
            Directory.Delete(Root, recursive: true);
        }
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
