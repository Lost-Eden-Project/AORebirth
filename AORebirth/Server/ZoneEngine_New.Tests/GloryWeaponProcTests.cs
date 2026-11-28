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
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Bug #773: the upgraded and Special Edition Kyr'Ozch weapons' OnHit is CastChance [Glory, 5%] on the Fightingtarget.
/// Glory 250469 (+24 to all attack skills, 100 s) names only the Wearer, so it is the attacker's buff; it landed in the
/// struck enemy's NCU. The templates below copy items.dat (Special Edition Kyr'Ozch Carbine 288283); the last test checks
/// them against it. The chance is raised to 100 here so a single swing always procs.
/// </summary>
[TestClass]
public sealed class GloryWeaponProcTests
{
    const int Glory = 250469;
    const int Carbine = 288283;
    const int StrikeNano = 250900;

    [TestMethod]
    public void GloryFromAWeaponHitLandsOnTheAttackerNotTheEnemy()
    {
        using var world = new World();
        Player attacker = world.NewPlayer(7731);
        NpcCharacter enemy = world.NewNpc(7732, health: 500);

        attacker.ApplyOnHitProcs(enemy, world.Weapon(GloryChance(100)));

        Assert.IsTrue(attacker.Buffs.Any(buff => buff.Id == Glory), "Glory is in the attacker's NCU");
        Assert.IsFalse(enemy.Buffs.Any(buff => buff.Id == Glory), "the enemy gets no Glory");
    }

    [TestMethod]
    public void AnotherGloryProcRefreshesTheOneBuffAndAddsNoSecond()
    {
        using var world = new World();
        Player attacker = world.NewPlayer(7731);
        NpcCharacter enemy = world.NewNpc(7732, health: 500);
        Item weapon = world.Weapon(GloryChance(100));

        attacker.ApplyOnHitProcs(enemy, weapon);
        attacker.ApplyOnHitProcs(enemy, weapon);

        Assert.AreEqual(1, attacker.Buffs.Count(buff => buff.Id == Glory));
        Assert.IsFalse(enemy.Buffs.Any(buff => buff.Id == Glory));
    }

    [TestMethod]
    public void AWeaponProcThatNamesTheTargetStillLandsOnTheEnemy()
    {
        using var world = new World();
        world.Items.Add(Template(StrikeNano, flags: 0x8000, timeExist: 0, ncu: 0, strain: 0,
            Spell(FunctionType.Hit, ItemTarget.Target, (int)CharacterStat.Health, -40, -40, 0)));
        Player attacker = world.NewPlayer(7731);
        NpcCharacter enemy = world.NewNpc(7732, health: 500);

        attacker.ApplyOnHitProcs(enemy, world.Weapon(
            Spell(FunctionType.CastChance, ItemTarget.Fightingtarget, StrikeNano, 100)));

        Assert.AreEqual(460, enemy.Stats.GetOrZero(CharacterStat.Health), "a damage proc still hits who was struck");
        Assert.AreEqual(1000, attacker.Stats.GetOrZero(CharacterStat.Health));
    }

    [TestMethod]
    public void TemplatesMatchItemsDat()
    {
        string? root = Environment.GetEnvironmentVariable("AO_REBIRTH_GAMEDATA_PATH");
        string path = string.IsNullOrWhiteSpace(root) ? string.Empty : Path.Combine(root, "items.dat");
        if (!File.Exists(path))
            Assert.Inconclusive("Set AO_REBIRTH_GAMEDATA_PATH to the private GameData to compare with items.dat.");

        Dictionary<int, DatItemTemplate> dat = ItemsDatReader.Read(path)
            .Where(entry => entry.ID is Glory or Carbine).ToDictionary(entry => entry.ID);

        ItemTemplate glory = DatItemMapper.ToTemplate(dat[Glory], dat[Glory].Name ?? string.Empty);
        ItemTemplate copy = new Items().CreateTemplate(Glory, Glory, 1);
        Assert.AreEqual(copy.Flags, glory.Flags);
        Assert.AreEqual(copy.Stats[CharacterStat.TimeExist], glory.Stats.GetValueOrDefault(CharacterStat.TimeExist));
        Assert.AreEqual(Describe(copy.SpellList[EventType.OnUse]), Describe(glory.SpellList[EventType.OnUse]));

        ItemTemplate carbine = DatItemMapper.ToTemplate(dat[Carbine], dat[Carbine].Name ?? string.Empty);
        Assert.AreEqual(Describe([GloryChance(5)]), Describe([carbine.SpellList[EventType.OnHit][0]]),
            "the carbine's OnHit casts Glory on the Fightingtarget");
    }

    static ItemSpell GloryChance(int chance) => Spell(FunctionType.CastChance, ItemTarget.Fightingtarget, Glory, chance);

    static string Describe(IEnumerable<ItemSpell> spells)
        => string.Join(" | ", spells.Select(spell =>
            spell.FunctionType + "@" + spell.Target + "[" + string.Join(",", spell.Arguments) + "]"));

    static ItemTemplate Template(int id, int flags, int timeExist, int ncu, int strain, params ItemSpell[] spells) => new()
    {
        Id = id,
        Name = "Nano " + id,
        Quality = 1,
        Flags = flags,
        Stats = new Dictionary<CharacterStat, int>
        {
            [CharacterStat.TimeExist] = timeExist,
            [CharacterStat.Level] = ncu,
            [CharacterStat.MetaType] = strain
        },
        SpellList = new Dictionary<EventType, List<ItemSpell>> { [EventType.OnUse] = [.. spells] }
    };

    static ItemSpell Spell(FunctionType function, ItemTarget target, params int[] arguments) => new()
    {
        FunctionType = (int)function,
        Target = (int)target,
        TickCount = 1,
        Arguments = arguments.Cast<object>().ToList(),
        Requirements = []
    };

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

        public Item Weapon(params ItemSpell[] onHit) => new()
        {
            LowId = Carbine,
            HighId = Carbine,
            Quality = 1,
            Definition = new ItemTemplate
            {
                Id = Carbine,
                Name = "Special Edition Kyr'Ozch Carbine",
                Quality = 1,
                SpellList = new Dictionary<EventType, List<ItemSpell>> { [EventType.OnHit] = [.. onHit] }
            }
        };

        public Player NewPlayer(int id)
        {
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new Logger(), Items)
            {
                FirstName = "Glory" + id
            };
            player.Inventory.Apply(new CharacterHydrationResult(), id, Items);
            player.Stats.Set(CharacterStat.Level, 200, StatDetail.Base);
            player.Stats.Set(CharacterStat.MaxHealth, 5000, StatDetail.Base);
            player.Stats.Set(CharacterStat.Health, 1000, StatDetail.Base);
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
            npc.Stats.Set(CharacterStat.MaxNCU, 60, StatDetail.Base);
            npc.Playfield = _playfield;
            npc.Position = new Vector3(11, 4, 12);
            return npc;
        }

        public void Dispose() => _services.Dispose();

        static void Set(Playfield playfield, string field, object value)
            => typeof(Playfield).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(playfield, value);
    }

    /// <summary>Glory 250469 as items.dat has it (see <see cref="TemplatesMatchItemsDat"/>), plus any added.</summary>
    sealed class Items : IItemBuilder
    {
        readonly Dictionary<int, ItemTemplate> _templates = new()
        {
            [Glory] = Template(Glory, flags: 0x90803, timeExist: 10000, ncu: 1, strain: 791,
                Spell(FunctionType.Modify, ItemTarget.Wearer, 276, 24)),
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
