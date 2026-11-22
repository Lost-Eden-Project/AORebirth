namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using AORebirth.Core.GameData;
using AORebirth.Enums;

using LostEden.Protocol;
using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;
using LostEden.Protocol.Messages.N3Messages;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Nanos;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Organizations;
using ZoneEngine_New.Core.Playfield;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Bug #370 / #359: after spending IP in the skill window the client's IP stat was never sent again, so reopening the
/// window showed the spent IP as still there.
/// </summary>
[TestClass]
public sealed class SkillTrainingIpRefreshTests
{
    [TestMethod]
    public void TrainingSendsTheIpLeftToSpend()
    {
        Player player = Fixes.SolitusSoldier(7101);
        player.RebaseStats();
        int before = player.AvailableIp;
        player.Stats.DrainDirty();

        Assert.IsTrue(SkillTraining.TryTrain(player, Fixes.Pairs((CharacterStat.Strength, 7)), SkillCatalog.Default, out string? rejection), rejection);

        int available = player.AvailableIp;
        Assert.IsTrue(available < before, "the raise cost IP");
        GameTuple<Stat, int>[] sent = player.Stats.DrainDirty();
        Assert.IsTrue(sent.Any(pair => pair.Value1 == (Stat)CharacterStat.IP), "IP was not queued for the client after training");
        Assert.AreEqual(available, sent.First(pair => pair.Value1 == (Stat)CharacterStat.IP).Value2, "the client is sent what is left to spend");
    }

    [TestMethod]
    public void RefusedTrainingSendsNoIp()
    {
        Player player = Fixes.SolitusSoldier(7102);
        player.RebaseStats();
        int before = player.AvailableIp;
        player.Stats.DrainDirty();

        // Far over the level 1 cap: refused, nothing spent, nothing to send.
        Assert.IsFalse(SkillTraining.TryTrain(player, Fixes.Pairs((CharacterStat.Strength, 400)), SkillCatalog.Default, out _));

        Assert.AreEqual(before, player.AvailableIp);
        Assert.IsFalse(player.Stats.DrainDirty().Any(pair => pair.Value1 == (Stat)CharacterStat.IP));
    }
}

/// <summary>
/// Bug #513: a garden key worn in a HUD slot did nothing on its statue; it only worked from the inventory. A worn item
/// can be picked up onto a world item now, but never one the world item would use up: a worn item is never destroyed in
/// place, so that would pay out and leave it worn.
/// </summary>
[TestClass]
public sealed class WornItemOnStatueTests
{
    const int GardenKey = 226987;
    const int Insignia = 214781;
    const int HudSlot = 1;

    [TestMethod]
    public void AWornGardenKeyOpensItsStatueAndStaysWorn()
    {
        Player player = Fixes.Player(7201);
        Item key = Wear(player, GardenKey);

        Assert.IsTrue(UseOnStatue(player, key, new Identity { Type = IdentityType.WeaponPage, Instance = HudSlot }));

        Assert.AreEqual(1, player.Stats.Get(CharacterStat.Strength), "the key's statue function ran");
        Assert.AreSame(key, player.Inventory.Equipment.Content[HudSlot], "the key is not used up");
    }

    [TestMethod]
    public void AWornItemTheStatueWouldUseUpDoesNothing()
    {
        Player player = Fixes.Player(7202);
        Item insignia = Wear(player, Insignia);

        Assert.IsFalse(UseOnStatue(player, insignia, new Identity { Type = IdentityType.WeaponPage, Instance = HudSlot }));

        Assert.AreNotEqual(1, player.Stats.Get(CharacterStat.Agility), "no payout for an item that cannot be used up");
        Assert.AreSame(insignia, player.Inventory.Equipment.Content[HudSlot], "still worn, still one");
    }

    [TestMethod]
    public void AnInventoryInsigniaIsStillUsedUpForItsPayout()
    {
        Player player = Fixes.Player(7203);
        Item insignia = new() { InstanceId = Insignia, LowId = Insignia, HighId = Insignia, Quality = 1, StackCount = 1, Definition = new ItemTemplate { Id = Insignia } };
        int slot = player.Inventory.Inventory.FindFreeSlot();
        Assert.IsTrue(player.Inventory.Inventory.Add(slot, insignia));

        Assert.IsTrue(UseOnStatue(player, insignia, new Identity { Type = IdentityType.Inventory, Instance = slot }));

        Assert.AreEqual(1, player.Stats.Get(CharacterStat.Agility));
        Assert.IsFalse(player.Inventory.Inventory.Content.ContainsKey(slot), "used up");
    }

    static Item Wear(Player player, int id)
    {
        Item item = new() { InstanceId = id, LowId = id, HighId = id, Quality = 1, StackCount = 1, Definition = new ItemTemplate { Id = id } };
        Assert.IsTrue(player.Inventory.Equipment.Add(HudSlot, item));
        return item;
    }

    static bool UseOnStatue(Player player, Item used, Identity slot)
        => Statue().ExecuteSpells(
            EventType.OnUseItemOn,
            player,
            Fixes.NoInventoryRepository(),
            new Fixes.Items(),
            new SpellCriteria { SecondaryItemTemplate = used.LowId, Subject = used, SubjectSlot = slot });

    /// <summary>Enel Statue 223559 in miniature: the key teleports (here: Strength 1), the insignia pays and is destroyed.</summary>
    static ItemTemplate Statue() => new()
    {
        Id = 223559,
        SpellList = new Dictionary<EventType, List<ItemSpell>>
        {
            [EventType.OnUseItemOn] =
            [
                Fixes.SetWhenUsedItemIs(CharacterStat.Strength, GardenKey),
                Fixes.SetWhenUsedItemIs(CharacterStat.Agility, Insignia),
                new ItemSpell
                {
                    FunctionType = (int)FunctionType.DestroyItem,
                    Target = (int)ItemTarget.Self,
                    Arguments = [],
                    Requirements = [Fixes.Leaf(CharacterStat.SecondaryItemTemplate, Operator.EqualTo, Insignia)]
                }
            ]
        }
    };
}

/// <summary>
/// Bug #605: Trader Accumulator's proc (210941) drains nano as well as health ([214, -20, -20, 0, 50]); only health drains
/// worked, so the trader never got nano back. The drain gives back a share of what it actually took, never more.
/// </summary>
[TestClass]
public sealed class NanoDrainHitTests
{
    [TestMethod]
    public void ANanoDrainTakesFromTheTargetAndGivesTheSourceItsShare()
    {
        (Player trader, Player target) = Pair(traderNano: 100, targetNano: 500);

        Assert.IsTrue(Drain().ExecuteSpells(EventType.OnUse, target, Fixes.NoInventoryRepository(), new Fixes.Items(), source: trader));

        Assert.AreEqual(480, target.Stats.GetOrZero(CharacterStat.CurrentNano));
        Assert.AreEqual(110, trader.Stats.GetOrZero(CharacterStat.CurrentNano), "half of the 20 taken");
    }

    [TestMethod]
    public void ADrainOnAnEmptyTargetGivesNothing()
    {
        (Player trader, Player target) = Pair(traderNano: 100, targetNano: 6);

        Drain().ExecuteSpells(EventType.OnUse, target, Fixes.NoInventoryRepository(), new Fixes.Items(), source: trader);
        Drain().ExecuteSpells(EventType.OnUse, target, Fixes.NoInventoryRepository(), new Fixes.Items(), source: trader);

        Assert.AreEqual(0, target.Stats.GetOrZero(CharacterStat.CurrentNano));
        Assert.AreEqual(103, trader.Stats.GetOrZero(CharacterStat.CurrentNano), "half of the 6 there was, then nothing");
    }

    [TestMethod]
    public void TheReturnStopsAtTheSourcesMaxNano()
    {
        (Player trader, Player target) = Pair(traderNano: 995, targetNano: 500);

        Drain().ExecuteSpells(EventType.OnUse, target, Fixes.NoInventoryRepository(), new Fixes.Items(), source: trader);

        Assert.AreEqual(1000, trader.Stats.GetOrZero(CharacterStat.CurrentNano));
    }

    static (Player Trader, Player Target) Pair(int traderNano, int targetNano)
    {
        Player trader = Fixes.Player(7301);
        Player target = Fixes.Player(7302);
        foreach ((Player player, int nano) in new[] { (trader, traderNano), (target, targetNano) })
        {
            player.Stats.Set(CharacterStat.MaxNanoEnergy, 1000, StatDetail.Base);
            player.Stats.Set(CharacterStat.CurrentNano, nano, StatDetail.Base);
            player.Stats.Set(CharacterStat.Health, 1000, StatDetail.Base);
        }

        return (trader, target);
    }

    static ItemTemplate Drain() => new()
    {
        Id = 210941,
        SpellList = new Dictionary<EventType, List<ItemSpell>>
        {
            [EventType.OnUse] =
            [
                new ItemSpell
                {
                    FunctionType = (int)FunctionType.DrainHit,
                    Target = (int)ItemTarget.Target,
                    Arguments = [(int)CharacterStat.CurrentNano, -20, -20, 0, 50],
                    Requirements = []
                }
            ]
        }
    };
}

/// <summary>
/// Bug #212: Keeper Ward (210508: ChangeVariable [AttackShield 516, 1]) never absorbed anything. A weapon hit now spends
/// the charge: the Ward entry that gave it leaves NCU, so one Ward absorbs one auto-attack.
/// </summary>
[TestClass]
public sealed class AttackShieldTests
{
    [TestMethod]
    public void AWardChargeIsSpentOnceAndItsEntryLeavesNcu()
    {
        Player keeper = Fixes.Player(7401);
        Assert.IsNotNull(keeper.TryRestoreBuff(NanoSpell.From(Ward()), keeper.Identity, 1, DateTime.UtcNow.AddMinutes(2)));
        keeper.Stats.Set(CharacterStat.AttackShield, 1, StatDetail.Bonus);

        Assert.IsTrue(keeper.TryConsumeAttackShield(out int left), "the first hit is absorbed");
        Assert.AreEqual(0, left, "the shielded line says (0) left");
        Assert.IsFalse(keeper.TryGetBuff(210508, out _), "the charge's NCU entry is gone");
        Assert.IsFalse(keeper.TryConsumeAttackShield(out _), "a spent charge absorbs nothing more");
    }

    [TestMethod]
    public void TheBlockFeedbackIsRetailText()
    {
        Assert.AreEqual(25272340, ZoneEngine_New.Core.Helpers.ClientFeedback.AttackBlockedByAttackShield, "Your attack was blocked by an attack shield!");
        Assert.AreEqual(236786603, ZoneEngine_New.Core.Helpers.ClientFeedback.AttackShieldBlocked, "Your attack shield blocked the attack! (%d) left.");
    }

    [TestMethod]
    public void NoChargeAbsorbsNothing()
    {
        Player keeper = Fixes.Player(7402);
        Assert.IsNotNull(keeper.TryRestoreBuff(NanoSpell.From(Ward()), keeper.Identity, 1, DateTime.UtcNow.AddMinutes(2)));
        keeper.Stats.Set(CharacterStat.AttackShield, 0, StatDetail.Bonus);

        Assert.IsFalse(keeper.TryConsumeAttackShield(out _));
        Assert.IsTrue(keeper.TryGetBuff(210508, out _));
    }

    [TestMethod]
    public void AShieldNoNcuEntryGrantsIsNeverSpentOrEndless()
    {
        Player keeper = Fixes.Player(7403);
        keeper.Stats.Set(CharacterStat.AttackShield, 1, StatDetail.Bonus);

        Assert.IsFalse(keeper.TryConsumeAttackShield(out _));
    }

    static ItemTemplate Ward() => new()
    {
        Id = 210508,
        Flags = (int)NanoFlags.IsBuff,
        Stats = new Dictionary<CharacterStat, int> { [CharacterStat.TimeExist] = 12500, [CharacterStat.MetaType] = 532 },
        SpellList = new Dictionary<EventType, List<ItemSpell>>
        {
            [EventType.OnUse] =
            [
                new ItemSpell
                {
                    FunctionType = (int)FunctionType.ChangeVariable,
                    Target = (int)ItemTarget.Self,
                    Arguments = [(int)CharacterStat.AttackShield, 1],
                    Requirements = []
                }
            ]
        }
    };
}

/// <summary>Bug #463: Red Dawn always healed for its QL 1 amount; its tier templates carry no Attack map to scale by.</summary>
[TestClass]
public sealed class PerkActionSkillScalingTests
{
    static readonly int RedDawn = PerkActionCatalog.PackHash("REDW");

    [TestMethod]
    public void RedDawnScalesWithMartialArts()
    {
        Assert.AreEqual((254454, 1), Resolve(martialArts: 1));
        Assert.AreEqual((254454, 500), Resolve(martialArts: 1000), "2 MA per QL: tier 1 tops out at 1000");
        Assert.AreEqual((254456, 250), Resolve(martialArts: 1500));
        Assert.AreEqual((254458, 500), Resolve(martialArts: 3500), "past the last tier it holds the last QL");
    }

    [TestMethod]
    public void TheRuntimeCatalogScalesRedDawnByMartialArts()
    {
        Assert.IsTrue(PerkActionCatalog.Default.TryGet(RedDawn, out PerkActionDefinition definition), "Red Dawn in PerkActions.json");
        Assert.IsNotNull(definition.Skills);
        Assert.AreEqual(100, definition.Skills[CharacterStat.MartialArts]);
    }

    [TestMethod]
    public void AnUnknownSkillNameFailsTheLoad()
    {
        string root = Catalog("\"Skills\": { \"NotASkill\": 100 },");
        Assert.ThrowsExactly<InvalidDataException>(() => PerkActionCatalog.Load(root));
    }

    static (int LowId, int Quality) Resolve(int martialArts)
    {
        var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = 7501 }, new Fixes.Logger(), new Fixes.Items())
        {
            FirstName = "Dawn",
            PerkActionCatalog = PerkActionCatalog.Load(Catalog("\"Skills\": { \"MartialArts\": 100 },"))
        };
        player.Stats.Set(CharacterStat.MartialArts, martialArts, StatDetail.Base);

        MethodInfo resolve = typeof(Player).GetMethod("ResolvePerkActionTemplate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var result = ((ItemTemplate, int, int, int))resolve.Invoke(player, [new Player.PerkAction(254454, 1, RedDawn)])!;
        return (result.Item2, result.Item4);
    }

    static string Catalog(string skills)
    {
        string root = Path.Combine(Path.GetTempPath(), "perk-actions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, GameDataPaths.PerkActionsFileName),
            "{ \"PerkActions\": [ { \"Hash\": \"REDW\", \"Name\": \"Red Dawn\", " + skills + " \"Tiers\": ["
            + "{ \"Tier\": 1, \"LowId\": 254454, \"HighId\": 254455, \"LowQl\": 1, \"HighQl\": 500 },"
            + "{ \"Tier\": 2, \"LowId\": 254456, \"HighId\": 254457, \"LowQl\": 1, \"HighQl\": 500 },"
            + "{ \"Tier\": 3, \"LowId\": 254458, \"HighId\": 254459, \"LowQl\": 100, \"HighQl\": 500 } ] } ] }");
        return root;
    }
}

/// <summary>Bug #631 / #610: the info window (T) showed no organization rank; the info packet never carried it.</summary>
[TestClass]
public sealed class OrganizationRankInfoTests
{
    [TestMethod]
    public void AMembersInfoCarriesItsRankName()
    {
        Player player = Fixes.Player(7601);
        player.Organization = new PlayerOrganization(42, "Lost Eden", "Veteran");

        InfoPacket info = player.BuildInfoPacket().Info;

        Assert.AreEqual(0x41, info.Flags, "Unknown1 | Clan, as retail sends an org member");
        Assert.AreEqual("Veteran", info.ClanLevel);
        Assert.AreEqual(0, info.ClanCity);
        Assert.AreEqual(info.ClanLevel, RoundTrip(info).ClanLevel, "the client reads the rank back");
    }

    [TestMethod]
    public void ANonMembersInfoHasNoOrgBlock()
    {
        Player player = Fixes.Player(7602);

        InfoPacket info = player.BuildInfoPacket().Info;

        Assert.AreEqual(0x40, info.Flags);
        Assert.AreEqual(string.Empty, RoundTrip(info).ClanLevel);
    }

    [TestMethod]
    public void LoginMembershipCarriesTheRankName()
    {
        OrganizationRules rules = OrganizationRules.Load(GameDataPaths.ResolveRuntimeRoot(), new Fixes.Logger());
        if (!rules.Enabled)
            Assert.Inconclusive("Organization rules not in this GameData: " + rules.DisabledReason);
        Player player = Fixes.Player(7603);
        GoverningForm form = rules.FormOf(rules.DefaultForm);

        OrganizationService.ApplyHydratedMembership(player, new OrgMembershipRow(42, "Lost Eden", rules.DefaultForm, 1, form.LowestRank), rules);

        Assert.AreEqual(form.RankName(form.SingleRank ? 0 : form.LowestRank), player.Organization!.RankName);
        Assert.AreEqual(player.Organization.RankName, player.BuildInfoPacket().Info.ClanLevel);
    }

    static InfoPacket RoundTrip(InfoPacket info)
    {
        var writer = new PacketWriter();
        info.Write(writer);
        byte[] bytes = writer.ToArray();
        var reader = new SpanReader(bytes);
        var read = new InfoPacket();
        read.Read(ref reader);
        Assert.IsFalse(reader.Failed, "the packet parses");
        return read;
    }
}

/// <summary>
/// Bug #616 / #621: a member who changed side stayed in its organization (which then "changed side" with it). A Side
/// change after login now runs the organization leave for that player, once per change.
/// </summary>
[TestClass]
public sealed class OrganizationSideChangeTests
{
    [TestMethod]
    public void ASideChangeTakesTheMemberOutOfItsOrganizationOnce()
    {
        var rig = new SideRig();
        Player player = rig.Member(7701, organizationId: 42);

        rig.Register(player);
        rig.Register(player);   // a zone change registers the same player again: still one watcher
        player.Stats.Set(CharacterStat.Side, 2, StatDetail.Base);

        Assert.IsTrue(rig.WaitForTransactions(1), "the side change ran the organization's transaction");
        Thread.Sleep(200);
        CollectionAssert.AreEqual(new[] { 42 }, rig.Transactions.ToArray(), "once, for its organization");
    }

    [TestMethod]
    public void OtherStatsAndANonMemberDoNothing()
    {
        var rig = new SideRig();
        Player member = rig.Member(7702, organizationId: 42);
        Player loner = rig.Member(7703, organizationId: 0);
        rig.Register(member);
        rig.Register(loner);

        member.Stats.Set(CharacterStat.Level, 50, StatDetail.Base);
        member.Stats.Set(CharacterStat.Side, 1, StatDetail.Base);   // unchanged
        loner.Stats.Set(CharacterStat.Side, 2, StatDetail.Base);

        Thread.Sleep(300);
        Assert.AreEqual(0, rig.Transactions.Count);
    }

    sealed class SideRig
    {
        internal readonly ConcurrentQueue<int> Transactions = new();
        readonly OrganizationService _service;

        internal SideRig()
        {
            OrganizationRules rules = OrganizationRules.Load(GameDataPaths.ResolveRuntimeRoot(), new Fixes.Logger());
            if (!rules.Enabled)
                Assert.Inconclusive("Organization rules not in this GameData: " + rules.DisabledReason);

            IOrganizationStore store = Fixes.Fake<IOrganizationStore>((method, args) =>
            {
                if (method.Name == nameof(IOrganizationStore.Transact))
                    Transactions.Enqueue((int)args![0]!);
                return method.ReturnType.IsValueType && method.ReturnType != typeof(void)
                    ? Activator.CreateInstance(method.ReturnType)
                    : null;
            });
            IGameData gameData = Fixes.Fake<IGameData>((method, _) =>
                method.Name == "get_" + nameof(IGameData.Organizations) ? rules
                : method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null);
            _service = new OrganizationService(store, gameData, new Lazy<PlayfieldManager>(() => Fixes.Blank<PlayfieldManager>()),
                Fixes.Fake<ZoneEngine_New.Core.Chat.IChatEngineLink>((_, _) => null), new Fixes.Logger());
        }

        internal Player Member(int id, int organizationId)
        {
            Player player = Fixes.Player(id);
            player.Stats.Set(CharacterStat.Side, 1, StatDetail.Base);
            if (organizationId > 0)
                player.Organization = new PlayerOrganization(organizationId, "Lost Eden", "Member");
            return player;
        }

        internal void Register(Player player)
            => typeof(OrganizationService).GetMethod("WatchSide", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(_service, [player]);

        internal bool WaitForTransactions(int count)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (Transactions.Count < count && DateTime.UtcNow < deadline)
                Thread.Sleep(20);
            return Transactions.Count >= count;
        }
    }
}

/// <summary>
/// Bugs #619 / #574 / #589: a corpse's loot window showed another corpse's name. Each playfield (every mission floor is
/// one) numbered its corpses from the same start, and the client keeps a container's name by its identity. Corpse
/// identities are engine-wide now.
/// </summary>
[TestClass]
public sealed class CorpseIdentityTests
{
    [TestMethod]
    public void TwoPlayfieldsNeverHandOutTheSameCorpse()
    {
        var floorOne = new DynelRegistry();
        var floorTwo = new DynelRegistry();
        var seen = new HashSet<int>();

        for (int i = 0; i < 50; i++)
        {
            Identity first = floorOne.AllocateCorpseIdentity();
            Identity second = floorTwo.AllocateCorpseIdentity();
            Assert.AreEqual(IdentityType.Corpse, first.Type);
            Assert.IsTrue(seen.Add(first.Instance), "corpse " + first.Instance + " handed out twice");
            Assert.IsTrue(seen.Add(second.Instance), "corpse " + second.Instance + " handed out twice");
        }
    }

    [TestMethod]
    public void ConcurrentDeathsGetDistinctCorpses()
    {
        var registries = Enumerable.Range(0, 8).Select(_ => new DynelRegistry()).ToArray();
        var ids = new ConcurrentBag<int>();

        System.Threading.Tasks.Parallel.For(0, 4000, i => ids.Add(registries[i % registries.Length].AllocateCorpseIdentity().Instance));

        Assert.AreEqual(4000, ids.Distinct().Count());
    }
}

/// <summary>Founding an organization needs the full team of six standing in the founder's zone, not just teamed.</summary>
[TestClass]
public sealed class OrganizationCreateZoneTests
{
    [TestMethod]
    public void OnlyTeammatesInPlayOnTheLeadersPlayfieldCount()
    {
        Playfield here = Fixes.Blank<Playfield>();
        Playfield elsewhere = Fixes.Blank<Playfield>();
        Player leader = Online(7801, here);
        Player beside = Online(7802, here);
        Player away = Online(7803, elsewhere);
        Player gone = Fixes.Player(7804);
        gone.Playfield = here;   // registered but without a session (logging out)

        var manager = Fixes.Blank<PlayfieldManager>();
        Fixes.SetField(manager, "_sync", new Lock());
        Fixes.SetField(manager, "_playersByCharacterId", new Dictionary<int, Player>
        {
            [7801] = leader, [7802] = beside, [7803] = away, [7804] = gone
        });
        OrganizationService service = new(
            Fixes.Fake<IOrganizationStore>((_, _) => null),
            Fixes.Fake<IGameData>((method, _) => method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null),
            new Lazy<PlayfieldManager>(() => manager),
            Fixes.Fake<ZoneEngine_New.Core.Chat.IChatEngineLink>((_, _) => null),
            new Fixes.Logger());

        Assert.IsTrue(InLeadersZone(service, leader, 7801));
        Assert.IsTrue(InLeadersZone(service, leader, 7802));
        Assert.IsFalse(InLeadersZone(service, leader, 7803), "teamed but in another zone");
        Assert.IsFalse(InLeadersZone(service, leader, 7804), "not in play");
        Assert.IsFalse(InLeadersZone(service, leader, 7805), "on another zone instance");
    }

    static Player Online(int id, Playfield playfield)
    {
        Player player = Fixes.Player(id);
        player.EnterOnline(new Fixes.Session());
        player.Playfield = playfield;
        return player;
    }

    static bool InLeadersZone(OrganizationService service, Player leader, int characterId)
    {
        object?[] args = [leader, characterId, null];
        return (bool)typeof(OrganizationService).GetMethod("InLeadersZone", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, args)!;
    }
}

static class Fixes
{
    internal sealed class Session : IZoneSession
    {
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public bool TransferToPlayfield(Playfield destination, Vector3 landing) => false;
        public void Send(byte[] packet) { }
        public void Send(MessageBody body) { }
        public void Send(MessageBody body, int sender, int receiver) { }
        public void SendInitiateCompression() { }
        public void Close() => State = SessionState.Closed;
    }

    internal static void SetField(object target, string name, object value)
        => (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("No field " + name + " on " + target.GetType().Name)).SetValue(target, value);

    internal static Player Player(int id)
    {
        var items = new Items();
        var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new Logger(), items) { FirstName = "Fix" + id };
        player.Inventory.Apply(new CharacterHydrationResult(), id, items);
        return player;
    }

    internal static Player SolitusSoldier(int id)
    {
        Player player = Player(id);
        player.Stats.Set(CharacterStat.Breed, (int)Breed.Solitus, StatDetail.Base);
        player.Stats.Set(CharacterStat.Profession, (int)Profession.Soldier, StatDetail.Base);
        player.Stats.Set(CharacterStat.Level, 1, StatDetail.Base);
        player.Stats.Set(CharacterStat.TitleLevel, 1, StatDetail.Base);
        player.Stats.Set(CharacterStat.IP, 1500, StatDetail.Base);
        player.Stats.Set(CharacterStat.UsedIP, 0, StatDetail.Base);
        for (CharacterStat ability = CharacterStat.Strength; ability <= CharacterStat.Psychic; ability++)
            player.Stats.Set(ability, 6, StatDetail.Base);
        for (CharacterStat skill = CharacterStat.MartialArts; skill <= CharacterStat.NanoAC; skill++)
            player.Stats.Set(skill, SkillCatalog.SkillFloor, StatDetail.Base);
        return player;
    }

    internal static GameTuple<Stat, uint>[] Pairs(params (CharacterStat Stat, uint Value)[] pairs)
        => pairs.Select(pair => new GameTuple<Stat, uint> { Value1 = (Stat)pair.Stat, Value2 = pair.Value }).ToArray();

    internal static ItemSpell SetWhenUsedItemIs(CharacterStat stat, int usedId) => new()
    {
        FunctionType = (int)FunctionType.Set,
        Target = (int)ItemTarget.Self,
        Arguments = [(int)stat, 1],
        Requirements = [Leaf(CharacterStat.SecondaryItemTemplate, Operator.EqualTo, usedId)]
    };

    internal static ItemRequirement Leaf(CharacterStat stat, Operator op, int value)
        => new() { StatNumber = (int)stat, Operator = (int)op, Value = value };

    internal sealed class Logger : IZoneLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) => throw new AssertFailedException("ERROR " + message);
        public void Error(Exception exception, string message) => throw new AssertFailedException("ERROR " + message + " " + exception);
        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }

    internal sealed class Items : IItemBuilder
    {
        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null) => throw new NotSupportedException();
        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
            => throw new NotSupportedException();
        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };
        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }

    /// <summary>The persistence the item functions are handed; these paths queue their writes elsewhere, so it does nothing.</summary>
    internal static IInventoryRepository NoInventoryRepository()
        => Fake<IInventoryRepository>((method, _) => method.ReturnType.IsValueType && method.ReturnType != typeof(void)
            ? Activator.CreateInstance(method.ReturnType)
            : null);

    public class FakeProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?>? Handler;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Handler != null ? Handler(targetMethod!, args) : throw new AssertFailedException("Unexpected call " + targetMethod!.Name);
    }

    internal static T Fake<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        T proxy = DispatchProxy.Create<T, FakeProxy>();
        ((FakeProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    internal static T Blank<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
}
