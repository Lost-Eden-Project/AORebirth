namespace ZoneEngine_New.Tests;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ZoneEngine_New.Core.DailyLogin;

[TestClass]
public sealed class DailyLoginClaimFilesTests
{
    static readonly DateTime Today = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void PendingRequiresMatchingAuthenticatedAccountOrCharacterAndRejectsConflictingHints()
    {
        using var files = new Files();
        files.Write(0, "request-account.json", Pending("account_only", 0, "dailyA"));
        files.Write(0, "request-character.json", Pending("character_only", 101, ""));
        files.Write(0, "request-both.json", Pending("both", 101, "DAILYA"));
        files.Write(0, "request-wrong-character.json", Pending("wrong_character", 102, "dailyA"));
        files.Write(0, "request-wrong-account.json", Pending("wrong_account", 101, "dailyB"));
        files.Write(0, "pending-latest.json", Pending("anonymous_global", 0, ""));
        files.Write(0, "pending-account-dailya.json", Pending("anonymous_account_path", 0, ""));
        files.Write(0, "last-active-account.json", "{\"accountKey\":\"dailyA\",\"characterId\":101}");

        string[] tokens = files.Bridge().Pending("dailyA", 101).Select(request => request.ClaimToken).Order().ToArray();
        CollectionAssert.AreEqual(new[] { "account_only", "both", "character_only" }, tokens);
        Assert.AreEqual(0, files.Bridge().Pending("dailyC", 103).Count,
            "A global last-active file and anonymous pending requests cannot route another account's claim.");
    }

    [TestMethod]
    public void LegacyCharacterAliasIsAcceptedOnlyForTheAuthenticatedCharacter()
    {
        using var files = new Files();
        files.Write(0, "pending-101.json", Pending("old_character_alias", 101, "character:101"));
        Assert.AreEqual("old_character_alias", files.Bridge().Pending("dailyA", 101).Single().ClaimToken);
        Assert.AreEqual(0, files.Bridge().Pending("dailyA", 102).Count);
    }

    [TestMethod]
    public void LegacyPendingKeepsOriginalBytesAndOneStableTokenAcrossMirrorsReopenAndAccountCasing()
    {
        using var files = new Files();
        const string json = "{\r\n  \"CharacterId\":101, \"AccountKey\":\"dailyA\", \"Day\":7, \"RequestedUtc\":\"2026-10-29T12:00:00Z\"\r\n}";
        byte[] original = Encoding.UTF8.GetBytes(json);
        files.Write(0, "pending-101.json", json);
        files.Write(1, "pending-account-dailya.json", json);
        string token = files.Bridge().Pending("dailyA", 101).Single().ClaimToken;
        Assert.IsTrue(token.StartsWith("legacy-", StringComparison.Ordinal));
        Assert.IsTrue(DailyLoginRewardService.IsValidToken(token));
        Assert.AreEqual(token, files.Bridge().Pending("dailyA", 101).Single().ClaimToken);
        Assert.AreEqual(token, files.Bridge().Pending("DAILYA", 101).Single().ClaimToken);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(files.Path(0, "pending-101.json")));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(files.Path(1, "pending-account-dailya.json")));
    }

    [TestMethod]
    public void LegacyMirrorsUnionTakenDaysOnlyWithinTheirNewestGrantDate()
    {
        using var files = new Files(rootCount: 3);
        files.Write(0, "account-dailya.json", new DailyLoginAccountState
        {
            Taken = [1, 2], LastClaimUtc = "2026-10-28", LastGrantedUtc = "2026-10-28"
        }.ToJson());
        files.Write(1, "account-dailya.json", new DailyLoginAccountState
        {
            Taken = [1, 3], LastClaimUtc = "2026-10-28"
        }.ToJson());
        files.Write(2, "account-dailya.json", new DailyLoginAccountState
        {
            Taken = Enumerable.Range(1, 20).ToArray(), LastGrantedUtc = "2026-10-27"
        }.ToJson());

        DailyLoginAccountState state = files.Bridge().ReadLegacyProgress("dailyA", Today);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, state.Taken);
        Assert.AreEqual(3, state.ClaimedCount);
        Assert.AreEqual("2026-10-28", state.LastGrantedUtc);
        Assert.AreEqual(4, state.NextDay);
    }

    [TestMethod]
    public void CompletedLegacyCycleIsPreservedForImportBeforeNormalNextDayReset()
    {
        using var files = new Files();
        files.Write(0, "account-dailya.json", new DailyLoginAccountState
        {
            Taken = Enumerable.Range(1, 28).ToArray(), ClaimedCount = 28,
            LastGrantedUtc = "2026-10-27", LastClaimUtc = "2026-10-27", CycleCompletedOn = "2026-10-27"
        }.ToJson());
        DailyLoginAccountState retained = files.Bridge().ReadLegacyProgress("dailyA", Today);
        Assert.AreEqual(28, retained.Taken.Length);
        Assert.AreEqual("2026-10-27", retained.LastGrantedUtc);
        Assert.AreEqual("2026-10-27", retained.CycleCompletedOn);
        Assert.AreEqual(0, retained.NextDay);
        Assert.AreEqual(1, retained.Normalize(Today).NextDay);
    }

    [TestMethod]
    public void CorruptLegacyProgressAndPendingRequestsFailClosedWithoutRewritingEvidence()
    {
        using var files = new Files();
        const string corrupt = "{\"Taken\":[";
        files.Write(0, "account-dailya.json", corrupt);
        Assert.ThrowsException<JsonException>(() => files.Bridge().ReadLegacyProgress("dailyA", Today));
        Assert.AreEqual(corrupt, File.ReadAllText(files.Path(0, "account-dailya.json")));
        files.Write(0, "account-dailya.json", "{\"Taken\":[1],\"LastGrantedUtc\":\"not-a-date\"}");
        Assert.ThrowsException<InvalidDataException>(() => files.Bridge().ReadLegacyProgress("dailyA", Today));
        files.Write(0, "pending-101.json", "not JSON");
        Assert.ThrowsException<JsonException>(() => files.Bridge().Pending("dailyA", 101));
        Assert.AreEqual("not JSON", File.ReadAllText(files.Path(0, "pending-101.json")));
    }

    [TestMethod]
    public void BoardMirrorsRetainExactOriginalProgressOnceAndPersistAccountAndCharacterProjections()
    {
        using var files = new Files();
        const string original = "{\r\n \"Taken\": [1], \"LastGrantedUtc\": \"2026-10-28\", \"HistoricalExtra\": \"retain these bytes\"\r\n}\r\n";
        files.Write(0, "account-dailya.json", original);
        var first = new DailyLoginAccountState { Taken = [1, 2], ClaimedCount = 2, LastGrantedUtc = "2026-10-29" };
        files.Bridge().PublishBoard("dailyA", 101, first, Today);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(original),
            File.ReadAllBytes(files.Path(0, "legacy-progress/account-dailya.json")));
        Assert.AreEqual(first.ToJson(), File.ReadAllText(files.Path(0, "account-dailya.json")));
        foreach (int root in new[] { 0, 1 })
        {
            DailyLoginClaimResult account = DailyLoginClaimResult.Parse(File.ReadAllText(files.Path(root, "board-account-dailya.json")));
            DailyLoginClaimResult character = DailyLoginClaimResult.Parse(File.ReadAllText(files.Path(root, "board-character-101.json")));
            Assert.AreEqual("dailyA", account.AccountKey);
            Assert.IsTrue(account.Ok && account.ClaimedToday && account.HasIdentity);
            CollectionAssert.AreEqual(new[] { 1, 2 }, account.Taken);
            Assert.AreEqual(account.ToJson(), character.ToJson());
        }
        var second = new DailyLoginAccountState { Taken = [1, 2, 3], ClaimedCount = 3, LastGrantedUtc = "2026-10-30" };
        files.Bridge().PublishBoard("dailyA", 101, second, Today.AddDays(1));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(original),
            File.ReadAllBytes(files.Path(0, "legacy-progress/account-dailya.json")));
        Assert.AreEqual(3, files.Bridge().ReadLegacyProgress("dailyA", Today.AddDays(1)).Taken.Length);
        Assert.AreEqual(0, Directory.EnumerateFiles(files.Roots[0], ".daily-*.tmp").Count());
    }

    [TestMethod]
    public void ReceiptProjectionsSurviveReopenAndSuppressOnlyTerminalOrCommittedRequests()
    {
        using var files = new Files();
        string pending = Pending("receipt_token", 101, "dailyA");
        files.Write(0, "pending-101.json", pending);
        files.Bridge().WriteResult(new DailyLoginClaimResult
        {
            ClaimToken = "receipt_token", AccountKey = "dailyA", Message = "No room", Terminal = false
        });
        Assert.AreEqual(1, files.Bridge().Pending("dailyA", 101).Count, "Retryable failures must retain their pending request.");
        var receipt = new DailyLoginClaimResult
        {
            ClaimToken = "receipt_token", AccountKey = "dailyA", Ok = true, Committed = true, Terminal = true,
            Day = 1, Amount = 25, Taken = [1], ClaimedCount = 1, NextDay = 2
        };
        files.Bridge().WriteResult(receipt);
        foreach (int root in new[] { 0, 1 })
            Assert.AreEqual(receipt.ToJson(), File.ReadAllText(files.Path(root, "result-receipt_token.json")));
        Assert.AreEqual(0, files.Bridge().Pending("dailyA", 101).Count);
        Assert.AreEqual(pending, File.ReadAllText(files.Path(0, "pending-101.json")), "Completion does not delete historical request evidence.");
        files.Write(0, "request-terminal.json", Pending("terminal_refusal", 101, "dailyA"));
        files.Bridge().WriteResult(new DailyLoginClaimResult { ClaimToken = "terminal_refusal", AccountKey = "dailyA", Terminal = true });
        Assert.AreEqual(0, files.Bridge().Pending("dailyA", 101).Count);
    }

    [TestMethod]
    public void HistoricalSuccessReceiptPreservesProgressWhenItsAccountMirrorWasLostAndCannotBeRegranted()
    {
        using var files = new Files();
        files.Write(0, "result-old_token.json", """{"ok":true,"accountKey":"dailyA","day":3,"taken":[1,2,3],"claimedCount":3,"lastGrantedUtc":"2026-10-28"}""");
        files.Write(0, "pending-101.json", Pending("old_token", 101, "dailyA"));
        DailyLoginAccountState imported = files.Bridge().ReadLegacyProgress("dailyA", Today);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, imported.Taken);
        Assert.AreEqual("2026-10-28", imported.LastGrantedUtc);
        Assert.AreEqual(0, files.Bridge().Pending("dailyA", 101).Count);
    }

    [TestMethod]
    public void OneTokenWithConflictingMatchedOwnersFailsClosed()
    {
        using var files = new Files();
        files.Write(0, "pending-101.json", Pending("conflict", 101, "dailyA"));
        files.Write(1, "pending-account-dailya.json", Pending("conflict", 0, "dailyA"));
        Assert.ThrowsException<InvalidDataException>(() => files.Bridge().Pending("dailyA", 101));
    }

    static string Pending(string token, int character, string account) => JsonSerializer.Serialize(new DailyLoginPendingClaim
    {
        ClaimToken = token, CharacterId = character, AccountKey = account, RequestedUtc = "2026-10-29T12:00:00Z"
    });

    sealed class Files : IDisposable
    {
        readonly string _parent = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aorebirth-daily-files-" + Guid.NewGuid().ToString("N"));
        public string[] Roots { get; }
        public Files(int rootCount = 2)
        {
            Roots = Enumerable.Range(0, rootCount).Select(index => System.IO.Path.Combine(_parent, index.ToString())).ToArray();
            foreach (string root in Roots) Directory.CreateDirectory(root);
        }
        public string Path(int root, string name) => System.IO.Path.Combine(Roots[root], name.Replace('/', System.IO.Path.DirectorySeparatorChar));
        public void Write(int root, string name, string json) => File.WriteAllText(Path(root, name), json, new UTF8Encoding(false));
        public DailyLoginClaimFiles Bridge() => new(Roots);
        public void Dispose() => Directory.Delete(_parent, recursive: true);
    }
}
