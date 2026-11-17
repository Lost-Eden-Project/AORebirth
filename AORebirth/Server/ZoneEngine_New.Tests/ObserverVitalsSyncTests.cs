namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;
using LostEden.Protocol.Messages.N3Messages;

using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.Teams;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Bug #516: observers of a character see its bars as the stats were last sent to them, so they must end every tick
/// on the full MaxHealth / MaxNanoEnergy (and Health when it carries a bonus), while the character's own client keeps
/// receiving the bases it adds its own modifiers to.
/// </summary>
[TestClass]
public sealed class ObserverVitalsSyncTests
{
    const int PlayfieldId = 4516;
    const int BaseMaxHealth = 3367;
    const int GearMaxHealth = 1661; // full 5028
    const int BuffMaxHealth = 854;  // Gargantuan Health

    [TestMethod]
    public void MaxHealthBuffLandingExpiringAndHealLeaveObserversOnTheFullValue()
    {
        using var rig = new Rig();
        (Player subject, Player observer) = rig.SubjectAndObserver(BaseMaxHealth + GearMaxHealth);

        // The buff lands: bonus layer only; RebaseVital keeps a full character full.
        subject.Stats.AddBonus(CharacterStat.MaxHealth, BuffMaxHealth, dirty: true);
        subject.Stats.Set(CharacterStat.Health, BaseMaxHealth + GearMaxHealth + BuffMaxHealth, dirty: true);
        rig.EndTick(subject);
        Assert.AreEqual(5882, LastSeen(rig.Session(observer), subject, CharacterStat.MaxHealth));
        Assert.AreEqual(5882, LastSeen(rig.Session(observer), subject, CharacterStat.Health));

        // The buff ends.
        subject.Stats.AddBonus(CharacterStat.MaxHealth, -BuffMaxHealth, dirty: true);
        subject.Stats.Set(CharacterStat.Health, BaseMaxHealth + GearMaxHealth, dirty: true);
        rig.EndTick(subject);
        Assert.AreEqual(5028, LastSeen(rig.Session(observer), subject, CharacterStat.MaxHealth));
        Assert.AreEqual(5028, LastSeen(rig.Session(observer), subject, CharacterStat.Health));

        // Damage, then a heal.
        subject.Stats.Set(CharacterStat.Health, 4000, dirty: true);
        rig.EndTick(subject);
        subject.Stats.Set(CharacterStat.Health, 4500, dirty: true);
        rig.EndTick(subject);
        Assert.AreEqual(5028, LastSeen(rig.Session(observer), subject, CharacterStat.MaxHealth));
        Assert.AreEqual(4500, LastSeen(rig.Session(observer), subject, CharacterStat.Health));

        // A base change flushes the bare base to everyone; observers end the tick on the full value again,
        // the character itself keeps the base.
        subject.Stats.Set(CharacterStat.MaxHealth, BaseMaxHealth + 100, dirty: true);
        rig.EndTick(subject);
        Assert.AreEqual(5128, LastSeen(rig.Session(observer), subject, CharacterStat.MaxHealth));
        Assert.AreEqual(BaseMaxHealth + 100, LastSeen(rig.Session(subject), subject, CharacterStat.MaxHealth));
        Assert.IsFalse(rig.Session(subject).Bodies.OfType<StatMessage>().Where(m => m.Identity == subject.Identity)
                .SelectMany(m => m.Stats).Any(s => s.Value1 == CharacterStat.MaxHealth && s.Value2 != BaseMaxHealth + 100),
            "The character itself only ever receives its base max health.");
    }

    [TestMethod]
    public void MaxHealthDebuffOnAFullCharacterShowsFullToObservers()
    {
        using var rig = new Rig();
        (Player subject, Player observer) = rig.SubjectAndObserver(BaseMaxHealth + GearMaxHealth);

        subject.Stats.AddBonus(CharacterStat.MaxHealth, -BuffMaxHealth, dirty: true);
        subject.Stats.Set(CharacterStat.Health, BaseMaxHealth + GearMaxHealth - BuffMaxHealth, dirty: true);
        rig.EndTick(subject);

        int health = LastSeen(rig.Session(observer), subject, CharacterStat.Health);
        int maxHealth = LastSeen(rig.Session(observer), subject, CharacterStat.MaxHealth);
        Assert.AreEqual(maxHealth, health, "A full character shows 100% to its observers.");
        Assert.AreEqual(BaseMaxHealth + GearMaxHealth - BuffMaxHealth, maxHealth);
    }

    [TestMethod]
    public void FlushCarryingMaxNanoWithUnchangedFullValueIsFollowedByTheFullValue()
    {
        using var rig = new Rig();
        (Player subject, Player observer) = rig.SubjectAndObserver(BaseMaxHealth);
        subject.Stats.Set(CharacterStat.MaxNanoEnergy, 500);
        subject.Stats.AddBonus(CharacterStat.MaxNanoEnergy, 100);
        rig.EndTick(subject);
        Assert.AreEqual(600, LastSeen(rig.Session(observer), subject, CharacterStat.MaxNanoEnergy));

        subject.Stats.MarkDirty(CharacterStat.MaxNanoEnergy);
        subject.FlushDirtyStats();
        Assert.AreEqual(500, LastSeen(rig.Session(observer), subject, CharacterStat.MaxNanoEnergy), "The flush sends the base.");
        subject.SyncObserverVitals();
        Assert.AreEqual(600, LastSeen(rig.Session(observer), subject, CharacterStat.MaxNanoEnergy));
        Assert.AreEqual(500, LastSeen(rig.Session(subject), subject, CharacterStat.MaxNanoEnergy));
    }

    [TestMethod]
    public void HealthWithABonusReachesObserversAsTheFullValue()
    {
        using var rig = new Rig();
        (Player subject, Player observer) = rig.SubjectAndObserver(1000);
        subject.Stats.Set(CharacterStat.Health, 800, dirty: true);
        subject.Stats.AddBonus(CharacterStat.Health, 50);
        rig.EndTick(subject);
        Assert.AreEqual(850, LastSeen(rig.Session(observer), subject, CharacterStat.Health));
        Assert.AreEqual(800, LastSeen(rig.Session(subject), subject, CharacterStat.Health));

        // The bonus goes away: observers get the value the base now is.
        subject.Stats.AddBonus(CharacterStat.Health, -50);
        rig.EndTick(subject);
        Assert.AreEqual(800, LastSeen(rig.Session(observer), subject, CharacterStat.Health));

        // Without a bonus the flush alone carries Health; the sync adds nothing.
        rig.Session(observer).Bodies.Clear();
        subject.Stats.Set(CharacterStat.Health, 700, dirty: true);
        rig.EndTick(subject);
        Assert.AreEqual(1, rig.Session(observer).Bodies.OfType<StatMessage>().Count(m => m.Identity == subject.Identity
            && m.Stats.Any(s => s.Value1 == CharacterStat.Health)));
    }

    [TestMethod]
    public void TeamMemberInfoCarriesCurrentHealthAndNanoNotTheirMax()
    {
        using var rig = new Rig();
        (Player member, Player viewer) = rig.SubjectAndObserver(BaseMaxHealth + GearMaxHealth);
        member.Stats.Set(CharacterStat.MaxNanoEnergy, 900);
        member.Stats.AddBonus(CharacterStat.MaxNanoEnergy, 100);
        member.Stats.Set(CharacterStat.CurrentNano, 400);
        member.Stats.Set(CharacterStat.Health, 3000);
        foreach (Player player in new[] { member, viewer })
        {
            player.Name = "Member" + player.Identity.Instance;
            player.Stats.Set(CharacterStat.Level, 60);
            player.Stats.Set(CharacterStat.Profession, 3);
        }

        var teams = new TeamService(dispatchOnOwner: (_, action) => action());
        teams.AttachPlayer(member);
        teams.AttachPlayer(viewer);
        Join(teams, viewer, member);

        TeamMemberInfoMessage vitals = rig.Session(viewer).Bodies.OfType<TeamMemberInfoMessage>().Last(m => m.Member == member.Identity);
        // TeamMemberInfoIIR_t: current nano, max nano, max health, current health; full values.
        Assert.AreEqual(400, vitals.Unknown3);
        Assert.AreEqual(1000, vitals.Unknown4);
        Assert.AreEqual(5028, vitals.Unknown5);
        Assert.AreEqual(3000, vitals.Unknown6);

        // The captured row follows the member's current vitals.
        member.Stats.Set(CharacterStat.Health, 2500);
        member.Stats.Set(CharacterStat.CurrentNano, 150);
        rig.Session(viewer).Bodies.Clear();
        teams.RefreshPlayer(viewer);
        vitals = rig.Session(viewer).Bodies.OfType<TeamMemberInfoMessage>().Last(m => m.Member == member.Identity);
        Assert.AreEqual(150, vitals.Unknown3);
        Assert.AreEqual(2500, vitals.Unknown6);
    }

    static void Join(TeamService teams, Player inviter, Player invitee)
    {
        teams.TryHandle(inviter, new CharacterActionMessage { Identity = inviter.Identity,
            Action = CharacterActionType.TeamRequestInvite, Target = invitee.Identity });
        teams.TryHandle(invitee, new CharacterActionMessage { Identity = invitee.Identity,
            Action = CharacterActionType.TeamRequestReply, Target = inviter.Identity, Parameter2 = 1 });
    }

    static int LastSeen(CaptureSession session, Dynel subject, CharacterStat stat)
        => (int)session.Bodies.OfType<StatMessage>().Where(m => m.Identity == subject.Identity)
            .SelectMany(m => m.Stats).Last(s => s.Value1 == stat).Value2;

    sealed class Rig : IDisposable
    {
        readonly PlayfieldManager _manager = Blank<PlayfieldManager>();
        readonly Playfield _world = Blank<Playfield>();
        readonly DynelRegistry _registry = new();
        readonly PlayfieldLocality _locality = new(PlayfieldId, null);
        readonly Dictionary<int, CaptureSession> _sessions = [];
        readonly ServiceProvider _services;

        internal Rig()
        {
            var logger = new StubLogger();
            Set(_manager, "_sync", new Lock());
            Set(_manager, "_playersByCharacterId", new Dictionary<int, Player>());
            Set(_manager, "_playfields", new Dictionary<int, Playfield> { [PlayfieldId] = _world });
            Set(_world, "<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = PlayfieldId });
            Set(_world, "_tickSync", new Lock());
            Set(_world, "_lifecycleSync", new Lock());
            Set(_world, "_inbound", new PlayfieldInboundQueue());
            Set(_world, "_dynelRegistry", _registry);
            Set(_world, "_logger", logger);
            Set(_world, "_playfieldManager", _manager);
            _services = new ServiceCollection().AddSingleton(_registry).AddSingleton(_locality)
                .AddSingleton<IGameData>(new StubGameData(HashItemCatalog.Parse("{}"))).BuildServiceProvider();
            Set(_world, "_serviceProvider", _services);
        }

        /// <summary>A full-health subject spawned on an observer, with the observer sync caught up.</summary>
        internal (Player Subject, Player Observer) SubjectAndObserver(int maxHealth)
        {
            Player subject = AddPlayer(1, new Vector3(10, 0, 10));
            Player observer = AddPlayer(2, new Vector3(12, 0, 10));
            subject.Stats.Set(CharacterStat.MaxHealth, Math.Min(maxHealth, BaseMaxHealth));
            if (maxHealth > BaseMaxHealth)
                subject.Stats.AddBonus(CharacterStat.MaxHealth, maxHealth - BaseMaxHealth);
            subject.Stats.Set(CharacterStat.Health, maxHealth);
            Owner(() =>
            {
                _locality.ActivatePlayerVisibility(subject);
                _locality.ActivatePlayerVisibility(observer);
            });
            Assert.IsTrue(_locality.IsVisibleTo(subject, observer));
            EndTick(subject);
            return (subject, observer);
        }

        /// <summary>The end of the subject's tick: the dirty flush, then the observer sync.</summary>
        internal void EndTick(Character subject) => Owner(() =>
        {
            subject.FlushDirtyStats();
            subject.SyncObserverVitals();
        });

        Player AddPlayer(int id, Vector3 at)
        {
            Player player = TestWorld.CreatePlayer(id);
            player.Playfield = _world;
            player.Position = at;
            var session = new CaptureSession();
            _sessions[id] = session;
            player.EnterOnline(session);
            _manager.RegisterPlayer(player);
            _registry.Register(player);
            _locality.RegisterDynel(player);
            return player;
        }

        internal CaptureSession Session(Player player) => _sessions[player.Identity.Instance];

        void Owner(Action work)
        {
            lock ((Lock)Get(_world, "_tickSync"))
                work();
        }

        public void Dispose() => _services.Dispose();

        static T Blank<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

        static object Get(object target, string name)
            => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

        static void Set(object target, string name, object value)
            => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    }

    sealed class CaptureSession : IZoneSession
    {
        internal readonly List<MessageBody> Bodies = [];
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public bool TransferToPlayfield(Playfield destination, Vector3 landing) => throw new NotSupportedException();
        public void Send(byte[] packet) { }
        public void Send(Message message) => Bodies.Add(message.Body);
        public void Send(MessageBody body) => Bodies.Add(body);
        public void Send(MessageBody body, int sender, int receiver) => Send(body);
        public void SendInitiateCompression() => throw new NotSupportedException();
        public void Close() => State = SessionState.Closed;
    }
}
