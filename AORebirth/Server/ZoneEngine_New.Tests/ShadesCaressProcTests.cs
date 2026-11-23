namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

using AORebirth.Enums;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Inventory.Dat;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Nanos;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Bug #677: Shade's Caress (266300) lands 275242 on the Shade's enemy, a defensive proc AddDefProc [100, 275243]. Its
/// friendly heal 275243 names the Target, so it belongs to whoever hits that enemy; it healed the enemy instead. A
/// weapon hit on the debuffed enemy now heals the attacker by their level (under 150: 500, 150-199: 700, 200+: 1200)
/// and leaves the enemy as it was. The templates below copy items.dat; the last test checks them against it.
/// </summary>
[TestClass]
public sealed class ShadesCaressProcTests
{
    const int CaressDebuff = 275242;
    const int CaressHeal = 275243;

    [TestMethod]
    [DataRow(120, 500)]
    [DataRow(175, 700)]
    [DataRow(210, 1200)]
    public void HittingTheDebuffedEnemyHealsTheAttackerNotTheEnemy(int attackerLevel, int heal)
    {
        using var world = new World();
        Player shade = world.NewPlayer(6771, level: attackerLevel, health: 1000);
        NpcCharacter enemy = world.NewNpc(6772, health: 500);
        DateTime now = DateTime.UtcNow;
        Assert.AreEqual(BuffApplyDecision.Apply,
            enemy.TryApplyBuff(NanoSpell.From(world.Items.CreateTemplate(CaressDebuff, CaressDebuff, 1)), shade.Identity,
                now, out _, out _));

        ProcRules.OnWeaponHit(shade, enemy, weapon: null);
        NanoRuntime.Tick(shade, now.AddSeconds(2));
        NanoRuntime.Tick(enemy, now.AddSeconds(2));

        Assert.AreEqual(1000 + heal, shade.Stats.GetOrZero(CharacterStat.Health), "the attacker gets the heal");
        Assert.AreEqual(500, enemy.Stats.GetOrZero(CharacterStat.Health), "the debuffed enemy is not healed");
        Assert.IsFalse(shade.Buffs.Any(buff => buff.Id == CaressHeal), "the 1 cs heal buff ran out");
        Assert.IsTrue(enemy.Buffs.Any(buff => buff.Id == CaressDebuff), "the debuff stays on the enemy");
    }

    [TestMethod]
    public void EveryHitOnTheDebuffedEnemyHealsAgain()
    {
        using var world = new World();
        Player shade = world.NewPlayer(6771, level: 210, health: 1000);
        NpcCharacter enemy = world.NewNpc(6772, health: 500);
        DateTime now = DateTime.UtcNow;
        enemy.TryApplyBuff(NanoSpell.From(world.Items.CreateTemplate(CaressDebuff, CaressDebuff, 1)), shade.Identity,
            now, out _, out _);

        for (int hit = 0; hit < 2; hit++)
        {
            ProcRules.OnWeaponHit(shade, enemy, weapon: null);
            NanoRuntime.Tick(shade, DateTime.UtcNow.AddSeconds(2));
        }

        Assert.AreEqual(3400, shade.Stats.GetOrZero(CharacterStat.Health), "one heal per hit, no more");
        Assert.AreEqual(500, enemy.Stats.GetOrZero(CharacterStat.Health));
    }

    [TestMethod]
    public void TheShadeHitByTheEnemyGetsNothingFromIt()
    {
        using var world = new World();
        Player shade = world.NewPlayer(6771, level: 210, health: 1000);
        NpcCharacter enemy = world.NewNpc(6772, health: 500);
        DateTime now = DateTime.UtcNow;
        enemy.TryApplyBuff(NanoSpell.From(world.Items.CreateTemplate(CaressDebuff, CaressDebuff, 1)), shade.Identity,
            now, out _, out _);

        ProcRules.OnWeaponHit(enemy, shade, weapon: null);
        NanoRuntime.Tick(shade, now.AddSeconds(2));
        NanoRuntime.Tick(enemy, now.AddSeconds(2));

        Assert.AreEqual(1000, shade.Stats.GetOrZero(CharacterStat.Health));
        Assert.AreEqual(500, enemy.Stats.GetOrZero(CharacterStat.Health), "the debuff only procs when its holder is hit");
    }

    [TestMethod]
    public void ADefensiveProcNamingTheWearerStillLandsOnItsHolder()
    {
        // Skin Protection 266727's shape: a friendly defensive proc whose functions name the Wearer.
        const int buffId = 263357;
        const int procId = 266727;
        using var world = new World();
        world.Items.Add(Template(buffId, flags: 0x10000, timeExist: 6000, EventType.OnUse,
            Spell(FunctionType.AddDefProc, ItemTarget.Wearer, 100, procId)));
        world.Items.Add(Template(procId, flags: 0, timeExist: 0, EventType.OnUse,
            Spell(FunctionType.Hit, ItemTarget.Wearer, (int)CharacterStat.Health, 50, 50, 0)));
        Player holder = world.NewPlayer(6771, level: 100, health: 1000);
        NpcCharacter attacker = world.NewNpc(6772, health: 500);
        Assert.AreEqual(BuffApplyDecision.Apply,
            holder.TryApplyBuff(NanoSpell.From(world.Items.CreateTemplate(buffId, buffId, 1)), holder.Identity,
                DateTime.UtcNow, out _, out _));

        ProcRules.OnWeaponHit(attacker, holder, weapon: null);

        Assert.AreEqual(1050, holder.Stats.GetOrZero(CharacterStat.Health));
        Assert.AreEqual(500, attacker.Stats.GetOrZero(CharacterStat.Health));
    }

    [TestMethod]
    public void TemplatesMatchItemsDat()
    {
        string? root = Environment.GetEnvironmentVariable("AO_REBIRTH_GAMEDATA_PATH");
        string path = string.IsNullOrWhiteSpace(root) ? string.Empty : Path.Combine(root, "items.dat");
        if (!File.Exists(path))
            Assert.Inconclusive("Set AO_REBIRTH_GAMEDATA_PATH to the private GameData to compare with items.dat.");

        Dictionary<int, DatItemTemplate> dat = ItemsDatReader.Read(path)
            .Where(entry => entry.ID is CaressDebuff or CaressHeal).ToDictionary(entry => entry.ID);
        var items = new Items();
        foreach (int id in new[] { CaressDebuff, CaressHeal })
        {
            ItemTemplate real = DatItemMapper.ToTemplate(dat[id], dat[id].Name ?? string.Empty);
            ItemTemplate copy = items.CreateTemplate(id, id, 1);
            Assert.AreEqual(real.Flags, copy.Flags, "flags of " + id);
            Assert.AreEqual(real.Stats.GetValueOrDefault(CharacterStat.TimeExist), copy.Stats[CharacterStat.TimeExist]);
            Assert.AreEqual(Describe(real), Describe(copy), "functions of " + id);
        }
    }

    static string Describe(ItemTemplate template)
        => string.Join(" | ", template.SpellList.OrderBy(pair => pair.Key).SelectMany(pair => pair.Value.Select(spell =>
            pair.Key + ":" + spell.FunctionType + "@" + spell.Target + "[" + string.Join(",", spell.Arguments) + "]{"
            + string.Join(",", spell.Requirements.Select(r => r.StatNumber + "/" + r.Operator + "/" + r.Value)) + "}")));

    static ItemTemplate Template(int id, int flags, int timeExist, EventType eventType, params ItemSpell[] spells) => new()
    {
        Id = id,
        Name = "Nano " + id,
        Quality = 1,
        Flags = flags,
        Stats = new Dictionary<CharacterStat, int> { [CharacterStat.TimeExist] = timeExist, [CharacterStat.Level] = 0 },
        SpellList = new Dictionary<EventType, List<ItemSpell>> { [eventType] = [.. spells] }
    };

    static ItemSpell Spell(FunctionType function, ItemTarget target, params int[] arguments)
        => Spell(function, target, [], arguments);

    static ItemSpell Spell(FunctionType function, ItemTarget target, List<ItemRequirement> requirements,
        params int[] arguments) => new()
    {
        FunctionType = (int)function,
        Target = (int)target,
        TickCount = 1,
        Arguments = arguments.Cast<object>().ToList(),
        Requirements = requirements
    };

    static ItemRequirement Level(Operator op, int value)
        => new() { StatNumber = (int)CharacterStat.Level, Operator = (int)op, Value = value, Target = 19 };

    static ItemRequirement Flags(Operator op) => new() { StatNumber = 0, Operator = (int)op, Value = 0, Target = 19 };

    sealed class World : IDisposable
    {
        readonly ServiceProvider _services;
        readonly Playfield _playfield;

        public Items Items { get; } = new();

        public World()
        {
            var manager = (PlayfieldManager)RuntimeHelpers.GetUninitializedObject(typeof(PlayfieldManager));
            typeof(PlayfieldManager).GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(manager, new System.Threading.Lock());
            typeof(PlayfieldManager).GetField("_playersByCharacterId", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(manager, new Dictionary<int, Player>());
            _playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
            _services = new ServiceCollection()
                .AddSingleton(new DynelRegistry())
                .AddSingleton(manager)
                .AddSingleton<IItemBuilder>(Items)
                .AddSingleton(NoInventoryRepository())
                .BuildServiceProvider();
            Set(_playfield, "_serviceProvider", _services);
            Set(_playfield, "_pendingStatRebases", new ConcurrentDictionary<Character, byte>());
            Set(_playfield, "_lifecycleSync", new System.Threading.Lock());
            Set(_playfield, "<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = 4582 });
            Set(_playfield, "<FightModes>k__BackingField", DistrictFightModes.None);
        }

        public Player NewPlayer(int id, int level, int health)
        {
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new Logger(), Items)
            {
                FirstName = "Shade" + id
            };
            player.Inventory.Apply(new CharacterHydrationResult(), id, Items);
            player.Stats.Set(CharacterStat.Level, level, StatDetail.Base);
            player.Stats.Set(CharacterStat.MaxHealth, 5000, StatDetail.Base);
            player.Stats.Set(CharacterStat.Health, health, StatDetail.Base);
            player.Stats.Set(CharacterStat.MaxNCU, 60, StatDetail.Base);
            player.EnterOnline(new Session());
            player.Playfield = _playfield;
            player.Position = new Vector3(10, 4, 12);
            return player;
        }

        public NpcCharacter NewNpc(int id, int health)
        {
            var npc = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = id }, Items);
            npc.Stats.Set(CharacterStat.Level, 200, StatDetail.Base);
            npc.Stats.Set(CharacterStat.MaxHealth, 5000, StatDetail.Base);
            npc.Stats.Set(CharacterStat.Health, health, StatDetail.Base);
            npc.Playfield = _playfield;
            npc.Position = new Vector3(11, 4, 12);
            return npc;
        }

        public void Dispose() => _services.Dispose();

        static void Set(Playfield playfield, string field, object value)
            => typeof(Playfield).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(playfield, value);
    }

    /// <summary>275242 and 275243 as items.dat has them (see <see cref="TemplatesMatchItemsDat"/>), plus any added.</summary>
    sealed class Items : IItemBuilder
    {
        readonly Dictionary<int, ItemTemplate> _templates = new()
        {
            [CaressDebuff] = Template(CaressDebuff, flags: 0x9C000, timeExist: 1000, EventType.OnUse,
                Spell(FunctionType.AddDefProc, ItemTarget.Target, 100, CaressHeal)),
            [CaressHeal] = Template(CaressHeal, flags: 0x1095002, timeExist: 1, EventType.OnTerminate,
                Spell(FunctionType.Hit, ItemTarget.Target, [Level(Operator.LessThan, 150)],
                    (int)CharacterStat.Health, 500, 500, 0),
                Spell(FunctionType.Hit, ItemTarget.Target,
                    [Level(Operator.GreaterThan, 149), Level(Operator.LessThan, 200), Flags(Operator.And)],
                    (int)CharacterStat.Health, 700, 700, 0),
                Spell(FunctionType.Hit, ItemTarget.Target, [Level(Operator.GreaterThan, 199)],
                    (int)CharacterStat.Health, 1200, 1200, 0)),
        };

        public void Add(ItemTemplate template) => _templates[template.Id] = template;

        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null) => throw new NotSupportedException();

        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
            => throw new NotSupportedException();

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality)
            => _templates.TryGetValue(lowId, out ItemTemplate? template) ? template : new() { Id = 0, Quality = quality };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }

    sealed class Session : IZoneSession
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

    sealed class Logger : IZoneLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) => throw new AssertFailedException("ERROR " + message);
        public void Error(Exception exception, string message) => throw new AssertFailedException("ERROR " + message + " " + exception);
        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }

    static IInventoryRepository NoInventoryRepository()
        => Fake<IInventoryRepository>((method, _) => method.ReturnType.IsValueType && method.ReturnType != typeof(void)
            ? Activator.CreateInstance(method.ReturnType)
            : null);

    public class FakeProxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?>? Handler;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Handler != null ? Handler(targetMethod!, args) : throw new AssertFailedException("Unexpected call " + targetMethod!.Name);
    }

    static T Fake<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        T proxy = DispatchProxy.Create<T, FakeProxy>();
        ((FakeProxy)(object)proxy).Handler = handler;
        return proxy;
    }
}
