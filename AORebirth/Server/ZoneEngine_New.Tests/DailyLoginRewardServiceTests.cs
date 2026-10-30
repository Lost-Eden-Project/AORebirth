namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using AORebirth.Enums;
using AORebirth.Interfaces.Persistence.Characters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using ZoneEngine_New.Core.DailyLogin;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using Vector3 = AORebirth.Core.Vector.Vector3;

[TestClass]
public sealed class DailyLoginRewardServiceTests
{
    static readonly DateTime Today = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void GrantPublishesOnlyAfterTheCompleteDurableCommitAndPreservesExistingInventory()
    {
        using var world = new World();
        Item original = world.AddOriginal();
        world.Dao.BeforeCommit = request =>
        {
            Assert.AreEqual(1, world.Player.Inventory.Inventory.Content.Count);
            Assert.AreSame(original, world.Player.Inventory.Inventory.Content.Values.Single());
            Assert.AreEqual(0, world.Session.Messages.Count);
            Assert.AreEqual(1, request.Inserts.Count);
            Assert.AreEqual(25, request.Inserts[0].StackCount);
            Assert.AreEqual("character-import:1:111", request.ExpectedStateToken);
        };
        DailyLoginClaimResult result = world.Claim("token_1");
        Assert.IsTrue(result.Ok && result.Committed && result.Terminal);
        Assert.AreEqual(1, result.Day);
        Assert.AreEqual(25, world.Dao.Rows.Single().StackCount);
        Assert.AreEqual(2, world.Player.Inventory.Inventory.Content.Count);
        Assert.AreSame(original, world.Player.Inventory.Inventory.Content[64]);
        Assert.AreEqual(25, world.Player.Inventory.Inventory.Content.Values.Single(item => !ReferenceEquals(item, original)).StackCount);
        Assert.AreEqual(1, world.Session.Messages.Count);
        CollectionAssert.AreEqual(new[] { 1 }, DailyLoginAccountState.Parse(world.Dao.Current!.StateJson).Taken);
    }

    [TestMethod]
    public void ReplayedTokenReturnsTheSavedResultWithoutAnotherGrantOrPacket()
    {
        using var world = new World();
        Assert.IsTrue(world.Claim("token_1").Ok);
        int packets = world.Session.Messages.Count;
        DailyLoginClaimResult result = world.Claim("token_1", Today.AddDays(1));
        Assert.IsTrue(result.Ok && result.Committed && result.Replayed);
        Assert.AreEqual(1, world.Dao.Calls);
        Assert.AreEqual(1, world.Dao.Rows.Count);
        Assert.AreEqual(1, world.Player.Inventory.Inventory.Content.Count);
        Assert.AreEqual(packets, world.Session.Messages.Count);
    }

    [TestMethod]
    public void CharactersOnTheSameAccountHaveIndependentDailyLocksAndCalendars()
    {
        using var first = new World();
        Assert.IsTrue(first.Claim("first").Ok);
        using var second = new World(first.Dao, characterId: 112);
        DailyLoginClaimResult result = second.Claim("second");
        Assert.IsTrue(result.Ok && result.Committed && result.ClaimedToday);
        Assert.AreEqual(1, result.Day);
        Assert.AreEqual(112, result.CharacterId);
        Assert.AreEqual(2, first.Dao.Rows.Count);
        Assert.AreEqual(1, second.Player.Inventory.Inventory.Content.Count);
        Assert.AreEqual(1, second.Session.Messages.Count);
        Assert.IsFalse(first.Claim("first_again").Ok, "The same character remains locked for this UTC day.");
        Assert.AreEqual(2, first.Claim("first_tomorrow", Today.AddDays(1)).Day);
        CollectionAssert.AreEqual(new[] { 1, 2 }, DailyLoginAccountState.Parse(first.Dao.LoadDailyLoginState("account", 111).StateJson).Taken);
        CollectionAssert.AreEqual(new[] { 1 }, DailyLoginAccountState.Parse(first.Dao.LoadDailyLoginState("account", 112).StateJson).Taken);
    }

    [TestMethod]
    public void AnotherCharactersTokenCannotReplayItsGrantOrExposeItsCalendar()
    {
        using var first = new World();
        Assert.IsTrue(first.Claim("owned_token").Committed);
        using var second = new World(first.Dao, characterId: 112);
        DailyLoginClaimResult refused = second.Claim("owned_token");
        Assert.IsFalse(refused.Ok || refused.Committed);
        Assert.IsTrue(refused.Terminal);
        Assert.AreEqual(112, refused.CharacterId);
        Assert.AreEqual(0, refused.Taken.Length);
        Assert.AreEqual(0, second.Player.Inventory.Inventory.Content.Count);
        Assert.AreEqual(1, first.Dao.Rows.Count);
        StringAssert.Contains(refused.Message, "different character or account");
        Assert.AreEqual(111, first.Claim("owned_token").CharacterId);
    }

    [TestMethod]
    public void CommitFailureLeavesOriginalInventoryAndProgressIntactAndTokenRetryable()
    {
        using var world = new World();
        Item original = world.AddOriginal();
        world.Dao.Failure = new InvalidOperationException("late write failure");
        DailyLoginClaimResult failed = world.Claim("retry");
        Assert.IsFalse(failed.Ok || failed.Committed || failed.Terminal);
        Assert.AreEqual(0, world.Dao.Rows.Count);
        Assert.AreEqual(0, DailyLoginAccountState.Parse(world.Dao.Current!.StateJson).Taken.Length);
        Assert.AreEqual(1, world.Player.Inventory.Inventory.Content.Count);
        Assert.AreSame(original, world.Player.Inventory.Inventory.Content[64]);
        Assert.AreEqual(0, world.Session.Messages.Count);
        world.Dao.Failure = null;
        Assert.IsTrue(world.Claim("retry").Ok);
        Assert.AreEqual(1, world.Dao.Rows.Count);
    }

    [TestMethod]
    public void FullInventoryRejectsTheEntireMultiItemRewardWithoutProgressOrPartialDelivery()
    {
        using var world = new World(amount: 2, stackable: false);
        TestWorld.FillInventory(world.Player);
        var originals = world.Player.Inventory.Inventory.Content.ToDictionary(pair => pair.Key, pair => pair.Value);
        world.Player.Inventory.Inventory.Content.Remove(originals.Keys.Max());
        originals.Remove(originals.Keys.Max());
        DailyLoginClaimResult result = world.Claim("space");
        Assert.IsFalse(result.Ok || result.Committed || result.Terminal);
        Assert.AreEqual(0, world.Dao.Calls);
        Assert.AreEqual(0, world.Dao.Rows.Count);
        Assert.AreEqual(originals.Count, world.Player.Inventory.Inventory.Content.Count);
        foreach ((int slot, Item item) in originals)
            Assert.AreSame(item, world.Player.Inventory.Inventory.Content[slot]);
        Assert.AreEqual(0, world.Session.Messages.Count);
    }

    [TestMethod]
    public void ReservedDayDoesNotSkipOrAdvanceTheCharacterQueue()
    {
        using var world = new World();
        DailyLoginAccountState legacy = new() { CharacterId = 111, AccountKey = "account", Taken = Enumerable.Range(1, 18).ToArray(), LastClaimUtc = "2026-10-28" };
        DailyLoginClaimResult result = world.Service.Claim(world.Player, "account", "reserved", legacy, Today);
        Assert.IsFalse(result.Ok || result.Committed);
        Assert.IsTrue(result.Terminal);
        Assert.AreEqual(19, result.NextDay);
        Assert.AreEqual(0, world.Dao.Calls);
        Assert.AreEqual(0, world.Player.Inventory.Inventory.Content.Count);
        CollectionAssert.AreEqual(legacy.Taken, DailyLoginAccountState.Parse(world.Dao.Current!.StateJson).Taken);
    }

    [TestMethod]
    public void ExistingDirtyInventoryIsFlushedBeforeTheGrantTransaction()
    {
        using var world = new World();
        Item original = world.AddOriginal();
        world.Player.Inventory.MarkDirty(original, world.Player.Inventory.Inventory, 64);
        world.Dao.BeforeCommit = _ => Assert.AreEqual(1, world.Coalesce.Calls);
        Assert.IsTrue(world.Claim("ordered").Ok);
        Assert.IsFalse(world.Player.Inventory.HasDirtyEntries);
    }

    [TestMethod]
    public void FlushFailureNeverAttemptsTheGrantTransaction()
    {
        using var world = new World();
        Item original = world.AddOriginal();
        world.Player.Inventory.MarkDirty(original, world.Player.Inventory.Inventory, 64);
        world.Coalesce.Failure = new InvalidOperationException("flush failed");
        DailyLoginClaimResult result = world.Claim("flush");
        Assert.IsFalse(result.Ok || result.Committed || result.Terminal);
        Assert.AreEqual(0, world.Dao.Calls);
        Assert.IsTrue(world.Player.Inventory.HasDirtyEntries);
        Assert.AreSame(original, world.Player.Inventory.Inventory.Content[64]);
        world.Coalesce.Failure = null;
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnknownCommitOutcomeQuarantinesWithoutPublishingAndReceiptCanBeReplayedAfterReload(bool directDaoException)
    {
        using var world = new World();
        world.Dao.UnknownAfterCommit = true;
        world.Dao.DirectDaoException = directDaoException;
        if (directDaoException)
            Assert.ThrowsExactly<CharacterPersistenceCommitOutcomeUnknownException>(() => world.Claim("unknown"));
        else
            Assert.ThrowsExactly<DatabaseCommitOutcomeUnknownException>(() => world.Claim("unknown"));
        Assert.IsTrue(world.Player.IsPersistenceQuarantined);
        Assert.AreEqual(SessionState.Closed, world.Session.State);
        Assert.AreEqual(0, world.Player.Inventory.Inventory.Content.Count);
        Assert.AreEqual(0, world.Session.Messages.Count);
        Assert.AreEqual(1, world.Dao.Rows.Count);
        world.Dao.UnknownAfterCommit = false;
        using var reloaded = new World(world.Dao);
        Assert.IsTrue(reloaded.Claim("unknown").Replayed);
        Assert.AreEqual(1, world.Dao.Rows.Count);
        Assert.AreEqual(0, reloaded.Session.Messages.Count);
    }

    [TestMethod]
    public void CompletedLegacyImportIsRetainedBeforeTheNextDayStartsANewCycle()
    {
        using var world = new World();
        var legacy = new DailyLoginAccountState
        {
            CharacterId = 111, AccountKey = "account",
            Taken = Enumerable.Range(1, 28).ToArray(), ClaimedCount = 28,
            LastClaimUtc = "2026-10-28", LastGrantedUtc = "2026-10-28", CycleCompletedOn = "2026-10-28"
        };
        Assert.IsTrue(world.Service.Claim(world.Player, "account", "newcycle", legacy, Today).Ok);
        Assert.AreEqual(28, DailyLoginAccountState.Parse(world.Dao.ImportJson!).Taken.Length);
        CollectionAssert.AreEqual(new[] { 1 }, DailyLoginAccountState.Parse(world.Dao.Current!.StateJson).Taken);
    }

    [TestMethod]
    public void NonemptyUnattributedAccountStateCannotBeStampedOntoACharacterImport()
    {
        using var world = new World();
        var unknown = new DailyLoginAccountState { Taken = [1], LastGrantedUtc = "2026-10-28", LastCharacterId = 111 };
        Assert.ThrowsExactly<InvalidDataException>(() => world.Service.Claim(world.Player, "account", "unowned", unknown, Today));
        Assert.IsNull(world.Dao.Current);
        Assert.AreEqual(0, world.Dao.Rows.Count);
    }

    [TestMethod]
    public void StateChangedCommitDoesNotPublishTheLosingPlan()
    {
        using var world = new World();
        world.Dao.ForcedStatus = DailyLoginClaimCommitStatus.StateChanged;
        DailyLoginClaimResult result = world.Claim("race");
        Assert.IsFalse(result.Ok || result.Committed || result.Terminal);
        Assert.AreEqual(0, world.Dao.Rows.Count);
        Assert.AreEqual(0, world.Player.Inventory.Inventory.Content.Count);
        Assert.AreEqual(0, world.Session.Messages.Count);
    }

    [TestMethod]
    public void InvalidTokensCannotReachPersistence()
    {
        using var world = new World();
        foreach (string token in new[] { "", "../other", "has:colon", new string('x', 65) })
        {
            Assert.IsFalse(world.Claim(token).Ok);
            Assert.IsNull(world.Dao.Current);
        }
    }

    [TestMethod]
    public void StateNormalizationKeepsIncompleteProgressAcrossMonthsAndMigratesTheLegacyLock()
    {
        DailyLoginAccountState state = DailyLoginAccountState.Parse(
            """{"Month":"2026-09","Taken":[2,1,2],"LastClaimUtc":"2026-10-29"}""").Normalize(Today);
        CollectionAssert.AreEqual(new[] { 1, 2 }, state.Taken);
        Assert.AreEqual(3, state.NextDay);
        Assert.AreEqual("2026-10", state.Month);
        Assert.AreEqual(2, state.ClaimedCount);
        Assert.IsTrue(state.HasClaimedOn(Today));
        Assert.AreEqual("2026-10-29", state.LastGrantedUtc);
        StringAssert.Contains(state.ToJson(), "\"LastGrantedUtc\"");
        Assert.ThrowsExactly<InvalidDataException>(() => new DailyLoginAccountState { LastClaimUtc = "invalid" }.Normalize(Today));
        Assert.ThrowsExactly<InvalidDataException>(() => new DailyLoginAccountState { Taken = [99] }.Normalize(Today));
        Assert.ThrowsExactly<InvalidDataException>(() => new DailyLoginAccountState { Taken = [1] }.Normalize(Today));
    }

    internal sealed class World : IDisposable
    {
        public Player Player;
        public RecordingSession Session = new();
        public Dao Dao;
        public Coalesce Coalesce = new();
        public InventoryFlushService Flush;
        public DailyLoginRewardService Service;
        public World(Dao? dao = null, int amount = 25, bool stackable = true, int characterId = 111)
        {
            Player = TestWorld.CreatePlayer(characterId);
            Dao = dao ?? new Dao();
            Player.Session = Session;
            Session.BindPlayer(Player);
            var manager = (PlayfieldManager)RuntimeHelpers.GetUninitializedObject(typeof(PlayfieldManager));
            typeof(PlayfieldManager).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, new Lock());
            typeof(PlayfieldManager).GetField("_playersByCharacterId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, new Dictionary<int, Player>());
            Flush = new InventoryFlushService(new Lazy<PlayfieldManager>(() => manager), Coalesce, new StubLogger());
            var templates = new Catalog(stackable);
            var selector = new DailyLoginRewardSelector(templates, DailyLoginRewardCatalog.Parse(
                "{\"days\":{\"1\":{\"itemId\":100,\"amount\":" + amount
                + ",\"quality\":1},\"2\":{\"itemId\":100,\"amount\":1},\"19\":{\"itemId\":0,\"amount\":1}}}"));
            Service = new DailyLoginRewardService(selector, new ItemBuilder(templates, new Ids(), new StubLogger()), Flush, Dao);
        }
        public Item AddOriginal()
        {
            Item item = TestWorld.CreateItem(instanceId: 1000);
            Player.Inventory.Inventory.Content[64] = item;
            return item;
        }
        public DailyLoginClaimResult Claim(string token, DateTime? now = null)
            => Service.Claim(Player, "account", token, new DailyLoginAccountState(), now ?? Today);
        public void Dispose() => Flush.Dispose();
    }

    sealed class Catalog(bool stackable) : IItemTemplateCatalog
    {
        readonly ItemTemplate _item = new()
        {
            Id = 100, Quality = 1, Name = "Reward",
            Stats = new() { [CharacterStat.Can] = stackable ? (int)CanFlags.Stackable : 0, [CharacterStat.MaxEnergy] = stackable ? 100 : 0 }
        };
        public bool TryGet(int aoid, out ItemTemplate template) { template = _item; return aoid == 100; }
        public ItemTemplate Require(int aoid) => aoid == 100 ? _item : throw new KeyNotFoundException();
    }
    sealed class Ids : IItemInstanceIdAllocator { int _next = 9000; public int Allocate() => ++_next; }
    internal sealed class Coalesce : ICharacterCoalesceCommit
    {
        public int Calls;
        public Exception? Failure;
        public void Persist(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates,
            int characterId, IReadOnlyList<int> nanos, IReadOnlyList<ActiveNanoRecord>? activeNanos,
            IReadOnlyList<SkillLockRecord>? skillLocks)
        { Calls++; if (Failure is not null) throw Failure; }
    }
    internal sealed class Dao : IDailyLoginClaimDao
    {
        public DailyLoginClaimSnapshotData? Current;
        public readonly List<PersistedItemData> Rows = [];
        readonly Dictionary<string, DailyLoginClaimSnapshotData> _claims = new();
        readonly Dictionary<int, DailyLoginClaimSnapshotData> _states = new();
        public int Calls;
        public Exception? Failure;
        public bool UnknownAfterCommit;
        public bool DirectDaoException;
        public string? ImportJson;
        public Action<DailyLoginClaimCommitData>? BeforeCommit;
        public DailyLoginClaimCommitStatus? ForcedStatus;
        public DailyLoginClaimSnapshotData LoadDailyLoginState(string account, int character)
            => _states.TryGetValue(character, out var state) && state.AccountUsername == account ? state : null!;
        public DailyLoginClaimSnapshotData FindDailyLoginClaim(string account, int character, string token)
            => _claims.TryGetValue(token, out var claim) && claim.AccountUsername == account && claim.CharacterId == character ? claim : null!;
        public bool HasDailyLoginHistory(string account) => _states.Values.Any(state => state.AccountUsername == account);
        public DailyLoginClaimSnapshotData ImportDailyLoginState(string account, int character, string state,
            DateTime? lastDate, long now)
        {
            if (LoadDailyLoginState(account, character) is { } existing) return existing;
            ImportJson = state;
            Current = new DailyLoginClaimSnapshotData
            {
                AccountId = 1, AccountUsername = account, CharacterId = character, ClaimToken = "character-import:1:" + character,
                StateJson = state, ResultJson = "{}", IsLegacyImport = true, ClaimDateUtc = lastDate
            };
            _states[character] = Current;
            return Current;
        }
        public DailyLoginClaimCommitResultData CommitDailyLoginClaim(DailyLoginClaimCommitData request)
        {
            Calls++;
            BeforeCommit?.Invoke(request);
            if (Failure is not null) throw Failure;
            if (ForcedStatus.HasValue) return new() { Status = ForcedStatus.Value, Snapshot = Current! };
            if (_claims.TryGetValue(request.ClaimToken, out var previous))
                return new() { Status = previous.AccountUsername == request.AccountUsername && previous.CharacterId == request.CharacterId
                    ? DailyLoginClaimCommitStatus.AlreadyApplied : DailyLoginClaimCommitStatus.TokenOwnedByAnotherAccount, Snapshot = previous };
            Current = LoadDailyLoginState(request.AccountUsername, request.CharacterId);
            if (Current!.ClaimDateUtc == request.ClaimDateUtc)
                return new() { Status = DailyLoginClaimCommitStatus.AlreadyClaimedToday, Snapshot = Current };
            if (Current.ClaimToken != request.ExpectedStateToken)
                return new() { Status = DailyLoginClaimCommitStatus.StateChanged, Snapshot = Current };
            Current = new DailyLoginClaimSnapshotData
            {
                AccountId = 1, AccountUsername = request.AccountUsername, ClaimToken = request.ClaimToken,
                CharacterId = request.CharacterId, ClaimDateUtc = request.ClaimDateUtc,
                StateJson = request.StateJson, ResultJson = request.ResultJson
            };
            Rows.AddRange(request.Inserts);
            _claims[request.ClaimToken] = Current;
            _states[request.CharacterId] = Current;
            if (UnknownAfterCommit)
            {
                if (DirectDaoException) throw new CharacterPersistenceCommitOutcomeUnknownException(new IOException("lost commit acknowledgement"));
                throw new DatabaseCommitOutcomeUnknownException(new IOException("lost commit acknowledgement"));
            }
            return new() { Status = DailyLoginClaimCommitStatus.Applied, Snapshot = Current };
        }
    }
    internal sealed class RecordingSession : IZoneSession
    {
        public readonly List<MessageBody> Messages = [];
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public void TransferToPlayfield(Playfield destination, Vector3 landing) => throw new NotSupportedException();
        public void Send(byte[] packet) => throw new NotSupportedException();
        public void Send(Message message) => throw new NotSupportedException();
        public void Send(MessageBody body) => Messages.Add(body);
        public void Send(MessageBody body, int sender, int receiver) => Messages.Add(body);
        public void SendInitiateCompression() => throw new NotSupportedException();
        public void Close() => State = SessionState.Closed;
    }
}
