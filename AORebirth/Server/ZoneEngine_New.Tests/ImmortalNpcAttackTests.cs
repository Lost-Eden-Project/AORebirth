namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;

using AORebirth.Core.GameData;
using AORebirth.Enums;

using LostEden.Protocol.GameData;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Configuration;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Helpers;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Mobs;
using ZoneEngine_New.Core.Nanos;
using ZoneEngine_New.Core.Pets;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.Trade;
using ZoneEngine_New.Core.WorldSimulation;

/// <summary>
/// Lost Eden 2026-11-25, Nascense: a player's attack on a named mob (Craig-Or, Barad-Or, Sun-Len, Orad-Or) was refused
/// with "Your target can't be attacked.", but the player's pet attacked and damaged it. The mobs wear "No Attack 10
/// Minutes" (242892), whose OnWear casts 242890 Immortal: ChangeActionRestriction 0x20000000. The client refuses its own
/// attack on that bit; the server ignored it, so pets (and nanos, specials, other NPCs) went through. Real items.dat
/// from the runtime GameData (AO_REBIRTH_GAMEDATA_PATH); the spawn runs the hash spawn path that equips the item.
/// </summary>
[TestClass]
public sealed class ImmortalNpcAttackTests
{
    const string ImmortalHash = "IMMO";
    const string OrdinaryHash = "ORDN";
    const int NoAttackTenMinutes = 242892;
    const int Immortal = 242890;
    const int PvpFlag = 214879;

    [TestMethod]
    public void NamedMobWearingNoAttackSpawnsImmortal()
    {
        using var content = new Content();
        using var world = new World(content.Root);
        NpcCharacter mob = world.Spawn(ImmortalHash);

        Assert.IsTrue(mob.Buffs.Any(b => b.Id == Immortal), "The worn item casts Immortal on the mob at spawn.");
        Assert.AreNotEqual(ActionRestrictionFlags.None, mob.ActionRestrictionFlags & ActionRestrictionFlags.CantBeAttacked);
        Assert.IsTrue(mob.Attackable, "Immortal is a buff, not NoCombat: the mob is attackable once it ends.");
    }

    [TestMethod]
    public void NeitherThePlayerNorThePetCanAttackOrDamageAnImmortalMob()
    {
        using var content = new Content();
        using var world = new World(content.Root);
        NpcCharacter mob = world.Spawn(ImmortalHash);
        NpcCharacter ordinary = world.Spawn(OrdinaryHash);
        Player owner = world.Player(1);
        NpcCharacter pet = world.Pet(2, owner);

        Assert.IsFalse(CombatRules.CanAttack(owner, mob), "The player is refused, as the client already refuses.");
        Assert.IsFalse(CombatRules.CanAttack(pet, mob), "The pet is refused like its owner.");
        Assert.IsFalse(CombatRules.CanAttack(ordinary, mob), "No NPC fights it either.");

        int health = mob.Stats.GetOrZero(CharacterStat.Health);
        Assert.IsTrue(health > 0);
        Assert.IsFalse(mob.ApplyDamage(pet, 25, HitType.Normal), "A pet's hit lands no damage.");
        Assert.IsFalse(mob.ApplyDamage(owner, 25, HitType.Normal));
        Assert.AreEqual(health, mob.Stats.GetOrZero(CharacterStat.Health));

        // The rule is the buff, not the mob: an ordinary mob is still fair game for the pet and its owner.
        Assert.IsTrue(CombatRules.CanAttack(pet, ordinary));
        Assert.IsTrue(CombatRules.CanAttack(owner, ordinary));
    }

    [TestMethod]
    public void ImmortalEndsAndTheMobCanBeFoughtAgain()
    {
        using var content = new Content();
        using var world = new World(content.Root);
        NpcCharacter mob = world.Spawn(ImmortalHash);
        Player owner = world.Player(1);
        NpcCharacter pet = world.Pet(2, owner);

        Buff immortal = mob.Buffs.Single(b => b.Id == Immortal);
        Assert.IsTrue(immortal.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(5), "Immortal lasts minutes, not a tick.");

        // A playfield tick a minute before it ends: still immortal.
        world.Tick(mob, immortal.ExpiresAtUtc.AddMinutes(-1));
        Assert.IsFalse(CombatRules.CanAttack(pet, mob));

        // The tick after it ends drops the buff and rebases the mob.
        world.Tick(mob, immortal.ExpiresAtUtc.AddSeconds(1));
        Assert.IsFalse(mob.Buffs.Any(b => b.Id == Immortal));
        Assert.AreEqual(ActionRestrictionFlags.None, mob.ActionRestrictionFlags & ActionRestrictionFlags.CantBeAttacked);
        Assert.IsTrue(CombatRules.CanAttack(owner, mob));
        Assert.IsTrue(CombatRules.CanAttack(pet, mob));
    }

    [TestMethod]
    public void PvpFlagsStillComeFromThePlayersBuffs()
    {
        // ChangeActionRestriction now rebuilds on every character; the real PvP flag nano must keep working through it.
        using var content = new Content();
        using var world = new World(content.Root);
        Player attacker = world.Player(6);
        Player player = world.Player(7);
        player.Stats.Set(CharacterStat.MaxNCU, 60);
        NanoSpell spell = NanoSpell.From(world.Catalog.Require(PvpFlag));

        Assert.IsFalse(CombatRules.CanAttack(attacker, player));
        Assert.AreEqual(BuffApplyDecision.Apply, player.TryApplyBuff(spell, player.Identity, DateTime.UtcNow, out _, out _));
        Assert.AreEqual(ActionRestrictionFlags.PvPEnabled, player.ActionRestrictionFlags);
        Assert.IsTrue(CombatRules.CanAttack(attacker, player));
    }

    static T Blank<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    static void Field(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    sealed class Content : IDisposable
    {
        internal const int PlayfieldId = 667;
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "aorebirth-immortal-test-" + Guid.NewGuid().ToString("N"));

        internal Content()
        {
            string spawns = Path.Combine(Root, "Playfields", PlayfieldId.ToString(), "Spawns.json");
            Directory.CreateDirectory(Path.GetDirectoryName(spawns)!);
            File.WriteAllText(Path.Combine(Root, "NpcTemplates.json"),
                "{" + Template(ImmortalHash, "[[" + NoAttackTenMinutes + "," + NoAttackTenMinutes + "]]") + ","
                + Template(OrdinaryHash, "[]") + ","
                + "\"AAAA\":{\"Templates\":[{\"Name\":\"Diagnostic fixture\",\"Level\":1,\"Stats\":{\"54\":1}}]}}");
            File.WriteAllText(spawns, JsonSerializer.Serialize(new PlayfieldSpawnsData
            {
                SchemaVersion = 1,
                PlayfieldId = PlayfieldId,
                Spawns = [Entry(ImmortalHash, [4, 5, 6]), Entry(OrdinaryHash, [8, 5, 9])]
            }));
        }

        // Side 2 (Omni), as the Nascense named mobs; Health 500.
        static string Template(string hash, string equipment)
            => "\"" + hash + "\":{\"Templates\":[{\"Name\":\"" + hash + " fixture\",\"Level\":30,\"Stats\":{\"54\":30,\"33\":2,"
               + "\"1\":500,\"27\":500,\"181\":100},\"Equipment\":" + equipment + "}]}";

        static PlayfieldSpawnEntry Entry(string hash, float[] position) => new()
        {
            HashText = hash, Position = position, Radius = 0, MinLevel = 30, MaxLevel = 30,
            RespawnChance = 100, RespawnTime = 600
        };

        public void Dispose() => Directory.Delete(Root, true);
    }

    /// <summary>A playfield with only what hash spawning needs (as <c>BlueNameCombatTests</c>), on the real items.dat.</summary>
    sealed class World : IDisposable
    {
        readonly ServiceProvider _services;
        readonly ItemBuilder _items;

        internal World(string root)
        {
            const int id = Content.PlayfieldId;
            var runtime = new GameDataStore(new Logger());
            if (!File.Exists(Path.Combine(runtime.RootPath, "items.dat")))
                Assert.Inconclusive("Needs the runtime GameData items.dat (AO_REBIRTH_GAMEDATA_PATH).");
            var catalog = new ItemTemplateCatalog(runtime, new Logger());
            Catalog = catalog;
            foreach (int item in new[] { NoAttackTenMinutes, Immortal, PvpFlag })
            {
                if (!catalog.TryGet(item, out _))
                    Assert.Inconclusive("The runtime items.dat lacks " + item + ".");
            }

            var settings = new GameplaySettings { StuckLocation = new GameplayLocation { Playfield = 566, X = 1, Y = 2, Z = 3 } };
            Field(Playfield, "<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = id });
            Field(Playfield, "<ResourceId>k__BackingField", id);
            Field(Playfield, "<FightModes>k__BackingField", DistrictFightModes.None);
            Field(Playfield, "_pendingStatRebases", new ConcurrentDictionary<Character, byte>());
            Field(Playfield, "_lifecycleSync", new Lock());
            Data = new GameDataStore(new Logger(), null, root);
            Locality = new PlayfieldLocality(id, Data.GetPlayfieldMetaData(id));
            _items = new ItemBuilder(catalog, new InstanceIds(), new Logger());
            var minter = new HashItemMinter(Data, catalog, _items);
            var moves = new InventoryMoveService(new Logger(), Blank<InventoryFlushService>(), Blank<InventoryActionService>());
            var activation = new NpcContentActivationService(Playfield, Registry, Locality, _items, catalog, Data);
            _services = new ServiceCollection()
                .AddSingleton(Registry).AddSingleton(Locality).AddSingleton<IGameData>(Data)
                .AddSingleton(NullProxy<IInventoryRepository>.Create())
                .AddSingleton<IItemBuilder>(_items).AddSingleton<IItemTemplateCatalog>(catalog).AddSingleton(new WorldSimulationAccess())
                .AddSingleton(moves).AddSingleton(activation).AddSingleton(settings)
                .BuildServiceProvider();
            Field(Playfield, "_serviceProvider", _services);
            var spawns = new SpawnService(_services, Registry, new Logger(), Playfield, Blank<PlayfieldManager>(),
                Data, _items, minter, Blank<InventoryFlushService>(), Blank<TradeService>(), Blank<CharacterSnapshotService>());
            System = new HashSpawnSystem(Playfield, spawns, Data, new Logger(), settings);
            System.Initialize(Locality);
        }

        internal Playfield Playfield { get; } = Blank<Playfield>();
        internal DynelRegistry Registry { get; } = new();
        internal PlayfieldLocality Locality { get; }
        internal HashSpawnSystem System { get; }
        internal GameDataStore Data { get; }
        internal ItemTemplateCatalog Catalog { get; }

        /// <summary>Wakes the hash's spawn point the way a player nearing it does and returns the NPC it spawned.</summary>
        internal NpcCharacter Spawn(string hash)
        {
            var points = (List<HashSpawnPoint>)typeof(HashSpawnSystem)
                .GetField("_allPoints", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(System)!;
            HashSpawnPoint point = points.Single(p => p.HashText == hash);
            for (int i = 0; i < 4 && point.MemberCount == 0; i++)
                typeof(HashSpawnSystem).GetMethod("OnCellTick", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(System, [point.CellId]);
            return point.MembersSnapshot().OfType<NpcCharacter>().Single();
        }

        /// <summary>The character's part of a playfield tick: its nanos run, then the playfield drains queued rebases.</summary>
        internal void Tick(Character character, DateTime nowUtc)
        {
            NanoRuntime.Tick(character, nowUtc);
            typeof(Playfield).GetMethod("DrainRebases", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Playfield, null);
        }

        /// <summary>An Omni player (Side 2, as Barduun), in no PvP gas.</summary>
        internal Player Player(int instance)
        {
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = instance }, new Logger(), _items)
            {
                FirstName = "Owner" + instance
            };
            player.Stats.Set(CharacterStat.Side, 2, StatDetail.Base);
            player.Stats.Set(CharacterStat.Level, 55, StatDetail.Base);
            return player;
        }

        /// <summary>An attack pet bound to <paramref name="owner"/>, as a summon binds one.</summary>
        internal NpcCharacter Pet(int instance, Player owner)
        {
            var pet = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = instance }, _items);
            pet.Stats.Set(CharacterStat.Health, 100, StatDetail.Base);
            pet.BindPet(new PetController(owner, PetTypes.Attack, "synthetic-pet", 60, expiresUtc: null));
            return pet;
        }

        public void Dispose() => _services.Dispose();
    }

    /// <summary>Answers every call with the default value: for services the spawn path resolves but these NPCs never use.</summary>
    class NullProxy<T> : DispatchProxy where T : class
    {
        internal static T Create() => DispatchProxy.Create<T, NullProxy<T>>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Type? returnType = targetMethod?.ReturnType;
            return returnType == null || returnType == typeof(void) || !returnType.IsValueType ? null : Activator.CreateInstance(returnType);
        }
    }

    sealed class InstanceIds : IItemInstanceIdAllocator
    {
        int _next = 700000;
        public int Allocate() => Interlocked.Increment(ref _next);
    }

    sealed class Logger : IZoneLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(Exception exception, string message) { }
        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }
}
