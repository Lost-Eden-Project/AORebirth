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

using Utility.Config;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Configuration;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Helpers;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Mobs;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.Trade;
using ZoneEngine_New.Core.WorldSimulation;

/// <summary>
/// A blue-named monster is NoCombat unless its hash lists "Combat" in MonsterCombatRules.json: then it spawns
/// attackable, with a combat brain, a player can attack it and it can attack the player, and it keeps its blue name.
/// </summary>
[TestClass]
public sealed class BlueNameCombatTests
{
    const string FriendlyHash = "BLUF";
    const string FighterHash = "BLUC";
    const string ForcedNoCombatHash = "BLUN";
    const int BlueName = (int)CharacterFlags.HasBlueName;

    [TestMethod]
    public void BlueNamedMonsterWithCombatSpawnsAsAFighter()
    {
        using var content = new Content();
        using var world = new World(content.Root);
        NpcCharacter fighter = world.Spawn(FighterHash);
        Player player = NewPlayer();

        Assert.IsFalse(fighter.HasFeature(NpcFeature.NoCombat), "Combat must cancel the blue name's NoCombat.");
        Assert.IsTrue(fighter.Attackable);
        Assert.IsNotNull(fighter.Brain, "A fighting blue-named monster needs a combat brain to aggro and fight back.");
        Assert.IsTrue(CombatRules.CanAttack(player, fighter), "The player must be able to attack it.");
        Assert.IsTrue(CombatRules.CanAttack(fighter, player), "It must be able to fight the player.");

        int flags = ClientFlags(fighter);
        Assert.AreNotEqual(0, flags & (int)ItemFlags.CanBeAttacked, "The client must be told it can be attacked.");
        Assert.AreNotEqual(0, flags & BlueName, "It keeps its blue name.");
    }

    [TestMethod]
    public void BlueNamedMonsterWithoutCombatStaysNoCombat()
    {
        using var content = new Content();
        using var world = new World(content.Root);
        NpcCharacter friendly = world.Spawn(FriendlyHash);
        Player player = NewPlayer();

        Assert.IsTrue(friendly.HasFeature(NpcFeature.NoCombat));
        Assert.IsFalse(friendly.Attackable);
        Assert.IsNull(friendly.Brain);
        Assert.IsFalse(CombatRules.CanAttack(player, friendly));
        Assert.IsFalse(CombatRules.CanAttack(friendly, player));
        Assert.AreEqual(0, ClientFlags(friendly) & (int)ItemFlags.CanBeAttacked);
    }

    [TestMethod]
    public void ExplicitNoCombatWinsOverCombat()
    {
        using var content = new Content();
        using var world = new World(content.Root);
        NpcCharacter npc = world.Spawn(ForcedNoCombatHash);

        Assert.IsFalse(npc.Attackable, "A listed NoCombat is never undone by Combat.");
        Assert.IsNull(npc.Brain);
        Assert.IsFalse(CombatRules.CanAttack(NewPlayer(), npc));
    }

    [TestMethod]
    public void CombatDoesNotWakeTheUnresolvedPlaceholder()
    {
        NpcTemplateCatalog catalog = NpcTemplateCatalog.Parse(
            "{\"AAAA\":{\"Templates\":[{\"Name\":\"To Be Determined\",\"Level\":1,\"Stats\":{\"54\":1}}]}}");
        catalog.RuleFeatures = _ => new[] { NpcFeature.Combat };

        Assert.IsTrue(catalog.TryResolve("ZZZZ", 1, out MobTemplate fallback));
        Assert.IsTrue(fallback.Has(NpcFeature.NoCombat));
    }

    static Player NewPlayer()
        => new(new Identity { Type = IdentityType.SimpleChar, Instance = 1 }, new Logger(), new Items()) { FirstName = "Blue" };

    static int ClientFlags(Character character)
        => (int)typeof(Character).GetMethod("GetClientCharacterFlags", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(character, null)!;

    static T Blank<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    static void Field(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    sealed class Content : IDisposable
    {
        internal const int PlayfieldId = 666;
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "aorebirth-bluename-test-" + Guid.NewGuid().ToString("N"));

        internal Content()
        {
            string spawns = Path.Combine(Root, "Playfields", PlayfieldId.ToString(), "Spawns.json");
            Directory.CreateDirectory(Path.GetDirectoryName(spawns)!);
            File.WriteAllText(Path.Combine(Root, "NpcTemplates.json"),
                "{" + Template(FriendlyHash) + "," + Template(FighterHash) + "," + Template(ForcedNoCombatHash) + ","
                + "\"AAAA\":{\"Templates\":[{\"Name\":\"Diagnostic fixture\",\"Level\":1,\"Stats\":{\"54\":1}}]}}");
            File.WriteAllText(Path.Combine(Root, MonsterCombatRulesCatalog.FileName),
                "{\"" + FighterHash + "\":{\"Features\":[\"Combat\"]},"
                + "\"" + ForcedNoCombatHash + "\":{\"Features\":[\"Combat\",\"NoCombat\"]}}");
            File.WriteAllText(spawns, JsonSerializer.Serialize(new PlayfieldSpawnsData
            {
                SchemaVersion = 1,
                PlayfieldId = PlayfieldId,
                Spawns = [Entry(FriendlyHash, [4, 5, 6]), Entry(FighterHash, [8, 5, 9]), Entry(ForcedNoCombatHash, [12, 5, 3])]
            }));
        }

        static string Template(string hash)
            => "\"" + hash + "\":{\"Templates\":[{\"Name\":\"" + hash + " fixture\",\"Level\":1,\"Stats\":{\"0\":"
               + BlueName + ",\"54\":1,\"1\":100,\"27\":100}}]}";

        static PlayfieldSpawnEntry Entry(string hash, float[] position) => new()
        {
            HashText = hash, Position = position, Radius = 0, MinLevel = 1, MaxLevel = 1,
            RespawnChance = 100, RespawnTime = 600
        };

        public void Dispose() => Directory.Delete(Root, true);
    }

    /// <summary>A playfield with only what hash spawning needs, the way <c>PersistentNpcTests</c> builds one.</summary>
    sealed class World : IDisposable
    {
        readonly ServiceProvider _services;

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
            var items = new Items();
            var catalog = new Catalog();
            var minter = new HashItemMinter(Data, catalog, items);
            var moves = new InventoryMoveService(new Logger(), Blank<InventoryFlushService>(), Blank<InventoryActionService>());
            var activation = new NpcContentActivationService(Playfield, Registry, Locality, items, catalog, Data);
            _services = new ServiceCollection()
                .AddSingleton(Registry).AddSingleton(Locality).AddSingleton<IGameData>(Data)
                .AddSingleton(NullProxy<IInventoryRepository>.Create())
                .AddSingleton<IItemBuilder>(items).AddSingleton<IItemTemplateCatalog>(catalog).AddSingleton(new WorldSimulationAccess())
                .AddSingleton(moves).AddSingleton(activation).AddSingleton(settings)
                .BuildServiceProvider();
            Field(Playfield, "_serviceProvider", _services);
            var spawns = new SpawnService(_services, Registry, new Logger(), Playfield, Blank<PlayfieldManager>(),
                Data, items, minter, Blank<InventoryFlushService>(), Blank<TradeService>(), Blank<CharacterSnapshotService>());
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
