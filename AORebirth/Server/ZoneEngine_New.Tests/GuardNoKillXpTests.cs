namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
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
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Mobs;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.Trade;
using ZoneEngine_New.Core.WorldSimulation;

/// <summary>
/// Lost Eden 2026-11-24: level 200 Urban Guard Patrollers gave kill XP every few minutes ("the only steady source of
/// level 200 mobs"). A guard (the Guard feature in MonsterCombatRules.json) is worth no kill XP at all, not even the grey
/// 1 XP; the same mob without the feature still is. The kill runs the real path: hash spawn, the player's damage, death.
/// </summary>
[TestClass]
public sealed class GuardNoKillXpTests
{
    const string GuardHash = "GRDX";
    const string MobHash = "MOBX";
    const int MobLevel = 10;

    [TestMethod]
    public void KillingAGuardGivesNoXpWhileTheSameMobWithoutGuardDoes()
    {
        using var content = new Content();
        using var world = new World(content.Root);
        NpcCharacter guard = world.Spawn(GuardHash);
        NpcCharacter mob = world.Spawn(MobHash);
        Player player = world.Player(1, MobLevel);

        Assert.IsTrue(guard.HasFeature(NpcFeature.Guard));
        Assert.IsFalse(mob.HasFeature(NpcFeature.Guard));

        int before = Xp(player);
        Assert.IsTrue(world.Kill(player, guard), "The player's hit must kill the guard.");
        Assert.AreEqual(before, Xp(player), "A guard's kill must give no XP.");

        Assert.IsTrue(world.Kill(player, mob));
        Assert.IsTrue(Xp(player) > before, "The same mob without the Guard feature still gives kill XP.");
    }

    [TestMethod]
    public void AGreyGuardGivesNotEvenOneXp()
    {
        using var content = new Content();
        using var world = new World(content.Root);
        NpcCharacter guard = world.Spawn(GuardHash);
        NpcCharacter mob = world.Spawn(MobHash);
        // Level 30 against level 10: more than XPKillRange (5) below, so both are grey to the player.
        Player player = world.Player(1, 30);

        int before = Xp(player);
        Assert.IsTrue(world.Kill(player, guard));
        Assert.AreEqual(before, Xp(player), "A grey guard is worth nothing, not the grey 1 XP.");

        Assert.IsTrue(world.Kill(player, mob));
        Assert.AreEqual(before + 1, Xp(player), "A grey ordinary mob is still worth 1 XP.");
    }

    static int Xp(Player player) => player.Stats.GetOrZero(CharacterStat.XP);

    static T Blank<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    static void Field(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    sealed class Content : IDisposable
    {
        internal const int PlayfieldId = 668;
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "aorebirth-guard-xp-test-" + Guid.NewGuid().ToString("N"));

        internal Content()
        {
            string spawns = Path.Combine(Root, "Playfields", PlayfieldId.ToString(), "Spawns.json");
            Directory.CreateDirectory(Path.GetDirectoryName(spawns)!);
            File.WriteAllText(Path.Combine(Root, "NpcTemplates.json"),
                "{" + Template(GuardHash) + "," + Template(MobHash) + ","
                + "\"AAAA\":{\"Templates\":[{\"Name\":\"Diagnostic fixture\",\"Level\":1,\"Stats\":{\"54\":1}}]}}");
            File.WriteAllText(Path.Combine(Root, MonsterCombatRulesCatalog.FileName),
                "{\"" + GuardHash + "\":{\"Features\":[\"Guard\"]}}");
            File.WriteAllText(Path.Combine(Root, GameDataPaths.XpFileName), XpRows());
            File.WriteAllText(spawns, JsonSerializer.Serialize(new PlayfieldSpawnsData
            {
                SchemaVersion = 1,
                PlayfieldId = PlayfieldId,
                Spawns = [Entry(GuardHash, [4, 5, 6]), Entry(MobHash, [5, 5, 6])]
            }));
        }

        // A Clan (Side 1, NPCFamily 1) level 10 mob with 100 health and its own 500 XP stat, as Urban Guard Patroller.
        static string Template(string hash)
            => "\"" + hash + "\":{\"Templates\":[{\"Name\":\"" + hash + " fixture\",\"Level\":" + MobLevel + ",\"Stats\":{\"54\":"
               + MobLevel + ",\"33\":1,\"455\":1,\"52\":500,\"1\":100,\"27\":100}}]}";

        static string XpRows()
        {
            var json = new StringBuilder("{ \"NoKillXpFamilies\": [ { \"Family\": 0 } ]");
            for (int level = 1; level <= 40; level++)
                json.Append(", \"").Append(level).Append("\": { \"KillAward\": ").Append(100 + level * 10)
                    .Append(", \"NextLevelXp\": 1000000, \"XPKillRange\": 5 }");
            return json.Append(" }").ToString();
        }

        static PlayfieldSpawnEntry Entry(string hash, float[] position) => new()
        {
            HashText = hash, Position = position, Radius = 0, MinLevel = MobLevel, MaxLevel = MobLevel,
            RespawnChance = 100, RespawnTime = 600
        };

        public void Dispose() => Directory.Delete(Root, true);
    }

    /// <summary>A playfield with only what hash spawning and a kill need, the way <c>BlueNameCombatTests</c> builds one.</summary>
    sealed class World : IDisposable
    {
        readonly ServiceProvider _services;
        readonly Items _items = new();

        internal World(string root)
        {
            const int id = Content.PlayfieldId;
            var settings = new GameplaySettings { StuckLocation = new GameplayLocation { Playfield = 566, X = 1, Y = 2, Z = 3 } };
            Field(Playfield, "<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = id });
            Field(Playfield, "<ResourceId>k__BackingField", id);
            Field(Playfield, "<FightModes>k__BackingField", DistrictFightModes.None);
            Field(Playfield, "_pendingStatRebases", new ConcurrentDictionary<Character, byte>());
            Field(Playfield, "_lifecycleSync", new Lock());
            Data = new GameDataStore(new Logger(), null, root);
            Locality = new PlayfieldLocality(id, Data.GetPlayfieldMetaData(id));
            var catalog = new Catalog();
            var minter = new HashItemMinter(Data, catalog, _items);
            var moves = new InventoryMoveService(new Logger(), Blank<InventoryFlushService>(), Blank<InventoryActionService>());
            var activation = new NpcContentActivationService(Playfield, Registry, Locality, _items, catalog, Data);
            _services = new ServiceCollection()
                .AddSingleton(Registry).AddSingleton(Locality).AddSingleton<IGameData>(Data)
                .AddSingleton(NullProxy<IInventoryRepository>.Create())
                .AddSingleton<IItemBuilder>(_items).AddSingleton<IItemTemplateCatalog>(catalog).AddSingleton(new WorldSimulationAccess())
                .AddSingleton(moves).AddSingleton(activation).AddSingleton(settings)
                .AddSingleton(new NoKillXpFamilyCatalog(root, [0]))
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

        /// <summary>A Neutral player on this playfield, next to the spawns, at <paramref name="level"/>.</summary>
        internal Player Player(int instance, int level)
        {
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = instance }, new Logger(), _items)
            {
                FirstName = "Killer" + instance, Playfield = Playfield, Position = new(4, 5, 7)
            };
            player.Stats.Set(CharacterStat.Side, 0, StatDetail.Base);
            player.Stats.Set(CharacterStat.Level, level, StatDetail.Base);
            player.Stats.Set(CharacterStat.MaxHealth, 1000, StatDetail.Base);
            player.Stats.Set(CharacterStat.Health, 1000, StatDetail.Base);
            Registry.Register(player);
            return player;
        }

        /// <summary>The player's one hit for all of the NPC's health: the kill credit and death the engine records.</summary>
        internal bool Kill(Player player, NpcCharacter npc)
            => npc.ApplyDamage(player, npc.Stats.GetOrZero(CharacterStat.Health), HitType.Normal);

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

    sealed class Logger : IZoneLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(Exception exception, string message) { }
        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }

    sealed class Catalog : IItemTemplateCatalog
    {
        public bool TryGet(int aoid, out ItemTemplate template)
        {
            template = null!;
            return false;
        }

        public ItemTemplate Require(int aoid) => throw new KeyNotFoundException(aoid.ToString());
    }

    sealed class Items : IItemBuilder
    {
        int _next = 700000;

        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null) => new()
            {
                LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount,
                InstanceId = instanceId, Identity = identity ?? Identity.None, Definition = CreateTemplate(lowId, highId, quality)
            };

        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
        {
            Item item = Create(lowId, highId, quality, source, stackCount);
            item.AssignInstanceId(++_next);
            return item;
        }

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }
}
