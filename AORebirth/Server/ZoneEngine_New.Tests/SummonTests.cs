namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using AORebirth.Core.GameData;
using AORebirth.Enums;
using AORebirth.Interfaces.Persistence.Shops;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;
using LostEden.Protocol.Messages.N3Messages;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Configuration;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Helpers;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Metrics;
using ZoneEngine_New.Core.Nanos;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.Teams;
using ZoneEngine_New.Core.Trade;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Engineer Summon Player (154914, SummonPlayer) and Summon Team Members (154913, SummonTeamMates), cast the way a player
/// casts them: the cast starts with whatever is selected, the selection may change while the bar runs, and the summon is
/// judged only when the bar finishes, on the caster's tick. Each teammate then starts its move on its own playfield's
/// tick. Real playfields from the runtime GameData (AO_REBIRTH_GAMEDATA_PATH): 100-102 on Rubi-Ka, 4001 in the
/// Shadowlands. Only the network and persistence are doubles.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SummonTests
{
    const int SummonPlayerNano = 154914;
    const int SummonTeamNano = 154913;
    const int CasterPlayfield = 100;
    const int MemberPlayfield = 101;
    const int ThirdPlayfield = 102;
    const int ShadowlandsPlayfield = 4001;
    static readonly DateTime CastAt = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
    static readonly Vector3 CasterSpot = new(120, 10, 340);

    [TestMethod]
    public void Summon_player_brings_the_teammate_selected_when_the_cast_finishes()
    {
        using var w = new Rig();
        Playfield a = w.Playfield(CasterPlayfield), b = w.Playfield(MemberPlayfield);
        Player caster = w.Caster(a), firstPick = w.Player(b, 2), lastPick = w.Player(b, 3);
        w.Team(caster, firstPick, lastPick);

        caster.SetTarget(firstPick.Identity);
        w.StartCast(caster, SummonPlayerNano);
        caster.SetTarget(lastPick.Identity);
        w.FinishCast(caster);
        w.Run(b);

        w.AssertSummonedTo(lastPick, a);
        w.AssertStayed(firstPick);
    }

    [TestMethod]
    public void Summon_player_judges_nothing_when_the_cast_starts()
    {
        using var w = new Rig();
        Playfield a = w.Playfield(CasterPlayfield), b = w.Playfield(MemberPlayfield);
        Player caster = w.Caster(a), mate = w.Player(b, 2);
        w.Team(caster, mate);

        // Nothing selected and the teammate on another playfield: the cast still starts, and nothing is said yet.
        Assert.AreEqual(NanoCastRefusal.None, w.StartCast(caster, SummonPlayerNano));
        Assert.IsNotNull(caster.PendingCast);
        caster.SetTarget(mate.Identity);
        w.FinishCast(caster);
        w.Run(b);

        w.AssertSummonedTo(mate, a);
    }

    [TestMethod]
    public void Summon_player_on_the_same_playfield_moves_the_teammate_in_place()
    {
        using var w = new Rig();
        Playfield a = w.Playfield(CasterPlayfield);
        Player caster = w.Caster(a), mate = w.Player(a, 2);
        w.Stand(mate, new Vector3(500, 10, 500));
        w.Team(caster, mate);

        caster.SetTarget(mate.Identity);
        w.Cast(caster, SummonPlayerNano);

        Assert.AreEqual(0, w.SessionOf(mate).Transfers.Count, "no zone change on the same playfield");
        Assert.AreEqual(1, w.SessionOf(mate).InPlaceTeleports.Count, "the client is moved in place once");
        Assert.AreSame(a, mate.Playfield);
        Assert.AreEqual(CasterSpot.x, mate.Position.x, 0.001);
        Assert.AreEqual(CasterSpot.z, mate.Position.z, 0.001);
    }

    [TestMethod]
    public void Summon_player_refuses_a_selection_outside_the_team()
    {
        using var w = new Rig();
        Playfield a = w.Playfield(CasterPlayfield), b = w.Playfield(MemberPlayfield);
        Player caster = w.Caster(a), mate = w.Player(b, 2), stranger = w.Player(b, 3);
        w.Team(caster, mate);

        caster.SetTarget(stranger.Identity);
        w.Cast(caster, SummonPlayerNano);
        w.Run(b);

        w.AssertStayed(stranger);
        w.AssertStayed(mate);
        Assert.IsTrue(w.SessionOf(caster).Feedback.Contains(SummonService.TargetMustBeInTeam));
    }

    [TestMethod]
    public void Summon_player_without_a_team_moves_no_one()
    {
        using var w = new Rig();
        Playfield a = w.Playfield(CasterPlayfield), b = w.Playfield(MemberPlayfield);
        Player caster = w.Caster(a), other = w.Player(b, 2);

        caster.SetTarget(other.Identity);
        w.Cast(caster, SummonPlayerNano);
        w.Run(b);

        w.AssertStayed(other);
        Assert.IsTrue(w.SessionOf(caster).Feedback.Contains(SummonService.NeedTeam));
    }

    [TestMethod]
    public void Summon_player_with_nothing_selected_moves_no_one()
    {
        using var w = new Rig();
        Playfield a = w.Playfield(CasterPlayfield), b = w.Playfield(MemberPlayfield);
        Player caster = w.Caster(a), mate = w.Player(b, 2);
        w.Team(caster, mate);

        w.Cast(caster, SummonPlayerNano);
        w.Run(b);

        w.AssertStayed(mate);
        Assert.IsTrue(w.SessionOf(caster).Feedback.Contains(SummonService.SelectTeamMember));
    }

    [TestMethod]
    [DataRow(SummonPlayerNano)]
    [DataRow(SummonTeamNano)]
    public void A_teammate_in_the_shadowlands_is_not_summoned_out(int nano)
    {
        using var w = new Rig();
        Playfield a = w.Playfield(CasterPlayfield), sl = w.Playfield(ShadowlandsPlayfield);
        Player caster = w.Caster(a), mate = w.Player(sl, 2);
        w.Team(caster, mate);

        caster.SetTarget(mate.Identity);
        w.Cast(caster, nano);
        w.Run(sl);

        w.AssertStayed(mate);
    }

    [TestMethod]
    [DataRow(SummonPlayerNano)]
    [DataRow(SummonTeamNano)]
    public void No_summon_lands_in_the_shadowlands(int nano)
    {
        using var w = new Rig();
        Playfield sl = w.Playfield(ShadowlandsPlayfield), b = w.Playfield(MemberPlayfield);
        Player caster = w.Caster(sl), mate = w.Player(b, 2);
        w.Team(caster, mate);

        caster.SetTarget(mate.Identity);
        // The nano's own ExpansionPlayfield = 0 use criterion is the client's; the landing refuses on its own.
        w.Land(caster, nano);
        w.Run(b);

        w.AssertStayed(mate);
        Assert.IsTrue(w.SessionOf(caster).Feedback.Contains(SummonService.NotAllowedToTeleportHere));
    }

    [TestMethod]
    [DataRow(SummonPlayerNano)]
    [DataRow(SummonTeamNano)]
    public void No_summon_lands_on_a_listed_playfield(int nano)
    {
        using var w = new Rig(new GameplaySettings { NoSummonPlayfields = [CasterPlayfield] });
        Playfield a = w.Playfield(CasterPlayfield), b = w.Playfield(MemberPlayfield);
        Player caster = w.Caster(a), mate = w.Player(b, 2);
        w.Team(caster, mate);

        caster.SetTarget(mate.Identity);
        w.Cast(caster, nano);
        w.Run(b);

        w.AssertStayed(mate);
        Assert.IsTrue(w.SessionOf(caster).Feedback.Contains(SummonService.NotAllowedToTeleportHere));
    }

    [TestMethod]
    [DataRow(SummonPlayerNano)]
    [DataRow(SummonTeamNano)]
    public void No_summon_takes_a_teammate_out_of_a_listed_playfield(int nano)
    {
        using var w = new Rig(new GameplaySettings { NoSummonPlayfields = [MemberPlayfield] });
        Playfield a = w.Playfield(CasterPlayfield), b = w.Playfield(MemberPlayfield), c = w.Playfield(ThirdPlayfield);
        Player caster = w.Caster(a), listed = w.Player(b, 2), free = w.Player(c, 3);
        w.Team(caster, listed, free);

        caster.SetTarget(listed.Identity);
        w.Cast(caster, nano);
        w.Run(b, c);

        w.AssertStayed(listed);
        if (nano == SummonTeamNano)
            w.AssertSummonedTo(free, a);
    }

    [TestMethod]
    public void Summon_team_brings_every_living_teammate_and_leaves_the_rest()
    {
        using var w = new Rig();
        Playfield a = w.Playfield(CasterPlayfield), b = w.Playfield(MemberPlayfield), sl = w.Playfield(ShadowlandsPlayfield);
        Player caster = w.Caster(a), far = w.Player(b, 2), near = w.Player(a, 3);
        Player dead = w.Player(b, 4), inShadowlands = w.Player(sl, 5), stranger = w.Player(b, 6);
        w.Stand(near, new Vector3(400, 10, 400));
        w.Team(caster, far, near, dead, inShadowlands);
        typeof(Character).GetField("_deathNotified", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(dead, true);
        Assert.IsTrue(dead.IsDead);

        w.Cast(caster, SummonTeamNano);
        w.Run(b, sl);

        w.AssertSummonedTo(far, a);
        Assert.AreEqual(CasterSpot.x, near.Position.x, 0.001, "a teammate beside the caster moves in place");
        w.AssertStayed(dead);
        w.AssertStayed(inShadowlands);
        w.AssertStayed(stranger);
        Assert.AreEqual(0, w.SessionOf(caster).Transfers.Count, "the caster is not summoned");
        Assert.AreEqual(0, w.SessionOf(caster).InPlaceTeleports.Count, "nor moved");
    }

    [TestMethod]
    public void Two_summons_racing_for_one_teammate_move_it_once()
    {
        using var w = new Rig();
        Playfield a = w.Playfield(CasterPlayfield), b = w.Playfield(MemberPlayfield), c = w.Playfield(ThirdPlayfield);
        Player first = w.Caster(a), second = w.Player(c, 2), mate = w.Player(b, 3);
        w.Team(first, second, mate);

        first.SetTarget(mate.Identity);
        second.SetTarget(mate.Identity);
        w.Cast(first, SummonPlayerNano);
        w.Cast(second, SummonPlayerNano);
        w.Run(b);

        // The first summon to reach the teammate's tick starts its move; the second finds it already zoning.
        w.AssertSummonedTo(mate, a);
    }

    [TestMethod]
    [DataRow(SummonPlayerNano)]
    [DataRow(SummonTeamNano)]
    public void A_teammate_in_a_fight_is_not_summoned_out_of_it(int nano)
    {
        using var w = new Rig();
        Playfield a = w.Playfield(CasterPlayfield), b = w.Playfield(MemberPlayfield);
        Player caster = w.Caster(a), fighting = w.Player(b, 2), calm = w.Player(b, 3);
        w.Team(caster, fighting, calm);
        fighting.SetFightingTarget(new Identity { Type = IdentityType.SimpleChar, Instance = 777001 });
        Assert.IsTrue(fighting.IsInCombat);

        caster.SetTarget(fighting.Identity);
        w.Cast(caster, nano);
        w.Run(b, a);

        w.AssertStayed(fighting);
        Assert.IsTrue(w.SessionOf(caster).Formatted.Contains(ClientFeedback.FormatText(SummonService.CannotSummonInFight, fighting.Name)),
            "the caster is told why");
        if (nano == SummonTeamNano)
            w.AssertSummonedTo(calm, a);
    }

    [TestMethod]
    public void A_teammate_who_left_the_team_before_its_move_stays()
    {
        using var w = new Rig();
        Playfield a = w.Playfield(CasterPlayfield), b = w.Playfield(MemberPlayfield);
        Player caster = w.Caster(a), mate = w.Player(b, 2);
        w.Team(caster, mate);

        caster.SetTarget(mate.Identity);
        w.Cast(caster, SummonPlayerNano);
        // The summon waits on the teammate's tick; the teammate leaves the team first.
        w.Teams.TryHandle(mate, new CharacterActionMessage { Identity = mate.Identity, Action = CharacterActionType.LeaveTeam });
        Assert.IsFalse(w.Teams.AreTeammates(caster, mate));
        w.Run(b);

        w.AssertStayed(mate);
    }

    [TestMethod]
    public void A_summon_into_an_indoor_playfield_hands_over_the_casters_way_out()
    {
        using var w = new Rig();
        Playfield a = w.Playfield(CasterPlayfield), b = w.Playfield(MemberPlayfield);
        Player caster = w.Caster(a), mate = w.Player(b, 2);
        w.Team(caster, mate);
        typeof(Playfield).GetField("<MetaData>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(a, new PlayfieldMetaData { IsIndoor = true });
        caster.Stats.Set(CharacterStat.ExternalPlayfieldInstance, 566, StatDetail.Base);
        caster.Stats.Set(CharacterStat.ExternalDoorInstance, 7777, StatDetail.Base);

        caster.SetTarget(mate.Identity);
        w.Cast(caster, SummonPlayerNano);
        w.Run(b);

        w.AssertSummonedTo(mate, a);
        Assert.AreEqual(566, mate.Stats.GetOrZero(CharacterStat.ExternalPlayfieldInstance));
        Assert.AreEqual(7777, mate.Stats.GetOrZero(CharacterStat.ExternalDoorInstance));
    }

    static T Blank<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    static object Field(object target, string name)
        => typeof(Playfield).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    sealed class Rig : IDisposable
    {
        readonly Dictionary<int, Playfield> _playfields = new();
        readonly Dictionary<Player, Session> _sessions = new();

        internal PlayfieldManager Manager { get; }
        // Team messages go out on the member's own tick, as the live engine's dispatcher sends them.
        internal TeamService Teams { get; } = new(dispatchOnOwner: (player, action) =>
            player.Playfield?.DispatchPlayerProjection(player, action));

        internal Rig(GameplaySettings? gameplay = null)
        {
            var logger = new StubLogger();
            var data = new GameDataStore(logger);
            if (!Directory.Exists(Path.Combine(data.RootPath, GameDataPaths.PlayfieldRelativeDirectory(ShadowlandsPlayfield))))
                Assert.Inconclusive("The runtime GameData at " + data.RootPath + " lacks playfield " + ShadowlandsPlayfield + ".");
            var templates = new ItemTemplateCatalog(data, logger);
            var builder = new Items();
            var ids = new InstanceIds();
            PlayfieldManager? manager = null;
            var lazy = new Lazy<PlayfieldManager>(() => manager ?? throw new InvalidOperationException("The manager is not built yet."));
            var persistence = new Persistence();
            var flush = new InventoryFlushService(lazy, persistence, logger);
            var actions = new InventoryActionService(persistence, flush, ids, logger);
            var minter = new HashItemMinter(data, templates, builder);
            manager = new PlayfieldManager(logger, new MessageRouter([], logger), new PlayerHydrator(builder, data), data, builder, minter,
                new Inventory(), ids, new InventoryMoveService(logger, flush, actions), flush,
                new TradeService(logger, data, templates, minter, ids, flush, persistence), Blank<CharacterSnapshotService>(),
                new PlayfieldMetricsRegistry(), Teams, templates, new Shops(), gameplay: gameplay ?? new GameplaySettings(),
                summons: new SummonService(lazy));
            Manager = manager;
        }

        internal Playfield Playfield(int id)
        {
            Playfield playfield = Manager.GetOrCreate(id);
            _playfields[id] = playfield;
            return playfield;
        }

        /// <summary>The caster, at <see cref="CasterSpot"/>.</summary>
        internal Player Caster(Playfield playfield)
        {
            Player caster = Player(playfield, 1);
            Stand(caster, CasterSpot);
            return caster;
        }

        internal Player Player(Playfield playfield, int id)
        {
            var session = new Session();
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new StubLogger(), new Items())
            {
                FirstName = "Summon" + id
            };
            player.Inventory.Apply(new CharacterHydrationResult(), id, new Items());
            player.Stats.Set(CharacterStat.Health, 100);
            player.Stats.Set(CharacterStat.MaxHealth, 100);
            player.Stats.Set(CharacterStat.MaxNCU, 60);
            player.Stats.Set(CharacterStat.Level, 60);
            player.Session = session;
            session.BindPlayer(player);
            player.Playfield = playfield;
            player.TryAddUploadedNano(SummonPlayerNano);
            player.TryAddUploadedNano(SummonTeamNano);
            Manager.RegisterPlayer(player);
            playfield.GetRequiredService<DynelRegistry>().Register(player);
            playfield.GetRequiredService<PlayfieldLocality>().RegisterDynel(player);
            Teams.AttachPlayer(player);
            _sessions[player] = session;
            Stand(player, new Vector3(200 + id, 10, 200));
            return player;
        }

        /// <summary>Places the character and lets its motor settle onto the floor there, as one standing still is.</summary>
        internal void Stand(Player player, Vector3 at)
        {
            Playfield playfield = player.Playfield!;
            OnTick(playfield, () =>
            {
                player.Motor.Warp(playfield.SnapFeetToFloor(at));
                for (int i = 0; i < 40 && player.Motor.IsFalling; i++)
                    player.Motor.Tick(0.05);
            });
            Assert.IsFalse(player.Motor.IsFalling, "standing");
        }

        internal Session SessionOf(Player player) => _sessions[player];

        /// <summary>The first player invites each of the others, who accept.</summary>
        internal void Team(Player leader, params Player[] members)
        {
            foreach (Player member in members)
            {
                Teams.TryHandle(leader, new CharacterActionMessage
                {
                    Identity = leader.Identity, Action = CharacterActionType.TeamRequestInvite, Target = member.Identity,
                    Parameter2 = 1
                });
                Teams.TryHandle(member, new CharacterActionMessage
                {
                    Identity = member.Identity, Action = CharacterActionType.TeamRequestReply, Target = leader.Identity, Parameter2 = 1
                });
                Assert.IsTrue(Teams.AreTeammates(leader, member), "fixture team");
            }
        }

        internal NanoCastRefusal StartCast(Player caster, int nano)
        {
            NanoCastRefusal refusal = NanoCastRefusal.InvalidTarget;
            OnTick(caster.Playfield!, () => refusal = NanoRuntime.TryStartCast(caster, nano, caster.Target, CastAt));
            Assert.AreEqual(NanoCastRefusal.None, refusal, "cast start");
            return refusal;
        }

        internal void FinishCast(Player caster)
        {
            OnTick(caster.Playfield!, () => NanoRuntime.Tick(caster, CastAt.AddSeconds(10)));
            Assert.IsNull(caster.PendingCast, "cast finished");
        }

        internal void Cast(Player caster, int nano)
        {
            StartCast(caster, nano);
            FinishCast(caster);
        }

        /// <summary>The nano landing on its caster, without the cast start's use criteria.</summary>
        internal void Land(Player caster, int nano)
            => OnTick(caster.Playfield!, () => NanoRuntime.TryApplyImmediate(caster, caster, nano, new Items(), new Inventory(), CastAt));

        /// <summary>Runs the playfield's queued inbound work on its own tick.</summary>
        internal void Run(params Playfield[] playfields)
        {
            foreach (Playfield playfield in playfields)
                OnTick(playfield, () => ((PlayfieldInboundQueue)Field(playfield, "_inbound"))
                    .Drain((IMessageRouter)Field(playfield, "_router"), playfield.GetRequiredService<SpawnService>(), playfield));
        }

        static void OnTick(Playfield playfield, Action work)
        {
            lock ((Lock)Field(playfield, "_tickSync"))
                work();
        }

        internal void AssertSummonedTo(Player member, Playfield destination)
        {
            Session session = SessionOf(member);
            Assert.AreEqual(1, session.Transfers.Count, member.Name + " starts one move");
            Assert.AreSame(destination, session.Transfers[0].World);
            Assert.AreEqual(CasterSpot.x, session.Transfers[0].Position.x, 0.001);
            Assert.AreEqual(CasterSpot.z, session.Transfers[0].Position.z, 0.001);
        }

        internal void AssertStayed(Player member)
        {
            Assert.AreEqual(0, SessionOf(member).Transfers.Count, member.Name + " does not move");
            Assert.AreNotEqual(CasterSpot.x, member.Position.x, member.Name + " is not moved in place");
        }

        public void Dispose()
        {
            foreach (Player player in _sessions.Keys)
            {
                player.Playfield?.GetService<DynelRegistry>()?.UnregisterExact(player);
                Manager.UnregisterPlayer(player);
            }
        }
    }

    /// <summary>Summon Player / Summon Team Members as the RDB has them: one argumentless function on the User, a cast bar.</summary>
    sealed class Items : IItemBuilder
    {
        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null)
            => new()
            {
                LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount, InstanceId = instanceId,
                Identity = identity ?? Identity.None, Definition = CreateTemplate(lowId, highId, quality)
            };

        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
            => Create(lowId, highId, quality, source, stackCount);

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality)
        {
            FunctionType? function = lowId switch
            {
                SummonPlayerNano => FunctionType.SummonPlayer,
                SummonTeamNano => FunctionType.SummonTeamMates,
                _ => null
            };
            if (function == null)
                return new ItemTemplate { Id = lowId, Quality = quality };

            return new ItemTemplate
            {
                Id = lowId,
                Name = lowId == SummonPlayerNano ? "Summon Player" : "Summon Team Members",
                Quality = 1,
                CanCancel = true,
                Stats = new Dictionary<CharacterStat, int>
                {
                    [CharacterStat.TimeExist] = 0,
                    [CharacterStat.AttackDelay] = 293,
                    [CharacterStat.RechargeDelay] = 0
                },
                SpellList = new Dictionary<EventType, List<ItemSpell>>
                {
                    [EventType.OnUse] =
                    [
                        new ItemSpell
                        {
                            FunctionType = (int)function.Value,
                            Target = (int)ItemTarget.User,
                            TickCount = 1,
                            Arguments = new List<object>(),
                            Requirements = new List<ItemRequirement>()
                        }
                    ]
                }
            };
        }

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }

    sealed class Session : IZoneSession
    {
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        internal readonly List<(Playfield World, Vector3 Position)> Transfers = [];
        internal readonly List<int> Feedback = [];
        internal readonly List<string> Formatted = [];
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;

        /// <summary>Records the move and, as the real session does, counts as zoning until it completes.</summary>
        public bool TransferToPlayfield(Playfield destination, Vector3 landing)
        {
            if (State != SessionState.InPlay)
                return false;
            Transfers.Add((destination, landing));
            State = SessionState.Loading;
            return true;
        }

        public bool TransferToPlayfield(Playfield destination, Vector3 landing, AORebirth.Core.Vector.Quaternion heading)
            => TransferToPlayfield(destination, landing);
        internal readonly List<Vector3> InPlaceTeleports = [];
        public void SendIntrazoneTeleport(Vector3 landing, AORebirth.Core.Vector.Quaternion heading, int destinationKey)
            => InPlaceTeleports.Add(landing);
        public void Send(byte[] packet) { }
        public void Send(MessageBody body)
        {
            if (body is FeedbackMessage feedback)
                Feedback.Add(feedback.MessageId);
            if (body is FormatFeedbackMessage formatted)
                Formatted.Add(formatted.Message);
        }
        public void Send(MessageBody body, int sender, int receiver) => Send(body);
        public void SendInitiateCompression() { }
        public void Close() => State = SessionState.Closed;
    }

    sealed class Persistence : ICharacterCoalesceCommit, IInventoryMutationPersistence, ITradePersistence
    {
        public void Persist(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates, int characterId,
            IReadOnlyList<int> uploadedNanoIds, IReadOnlyList<ActiveNanoRecord>? activeNanos, IReadOnlyList<SkillLockRecord>? skillLocks)
            => throw new AssertFailedException("A summon writes no character rows.");
        public void Persist(InventoryMutationBatch batch) => throw new AssertFailedException("A summon writes no inventory rows.");
        public void Persist(TradePersistenceBatch batch) => throw new AssertFailedException("A summon trades nothing.");
    }

    sealed class Shops : IShopDao
    {
        public IList<ShopVendorData> ListForPlayfield(int playfieldId) => [];
    }

    sealed class StubLogger : IZoneLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(Exception exception, string message) { }
        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }

    /// <summary>A summon reaches no item rows.</summary>
    sealed class Inventory : IInventoryRepository
    {
        public IReadOnlyList<ItemInstanceRecord> GetCarriedItems(int characterId) => [];
        public IReadOnlyList<ItemInstanceRecord> GetBankItems(int characterId) => [];
        public IReadOnlyList<ItemInstanceRecord> GetContainerItems(int containerInstanceId) => [];
        public int LeaseInstanceIdBlock(int count) => throw new NotSupportedException();
        public ItemInstanceRecord Insert(ItemInstanceRecord item) => throw new NotSupportedException();
        public void UpdateLocation(int instanceId, int containerType, int containerInstance, int containerPlacement) => throw new NotSupportedException();
        public void UpdateLocations(IReadOnlyList<ItemLocationUpdate> locations) => throw new NotSupportedException();
        public void PersistNewAndUpdateLocations(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates) => throw new NotSupportedException();
    }

    sealed class InstanceIds : IItemInstanceIdAllocator
    {
        int _next = 600000;
        public int Allocate() => Interlocked.Increment(ref _next);
    }
}
