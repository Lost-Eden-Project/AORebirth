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
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Mobs;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.Trade;
using ZoneEngine_New.Core.WorldSimulation;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// A Persistent NPC (MonsterCombatRules.json "Features": ["Persistent"]: a buff-giver like UIFR that players wait on)
/// is up as soon as its playfield loads and stays in the world with no player near: its cell never sleeps it away.
/// </summary>
[TestClass]
public sealed class PersistentNpcTests
{
    const string PersistentHash = "PERS";
    const string OrdinaryHash = "ORDN";

    [TestMethod]
    public void PersistentNpcSpawnsAtLoadAndIsNotSleepDespawned()
    {
        using var content = new Content();
        using var world = new World(content.Root);
        HashSpawnPoint persistent = world.Points.Single(point => point.HashText == PersistentHash);
        HashSpawnPoint ordinary = world.Points.Single(point => point.HashText == OrdinaryHash);

        var scratch = new List<MobTemplate>();
        Assert.IsTrue(IsPersistent(world.System, persistent, scratch), "The rules feature must reach the spawn point.");
        Assert.IsFalse(IsPersistent(world.System, ordinary, scratch));
        CollectionAssert.Contains(AlwaysColdSpawnCells(world.System), persistent.CellId,
            "A Persistent point's cell must be held Cold from load so it spawns with no player near.");

        for (int i = 0; i < 4 && (persistent.MemberCount == 0 || ordinary.MemberCount == 0); i++)
        {
            world.Tick(persistent);
            world.Tick(ordinary);
        }

        NpcCharacter npc = persistent.MembersSnapshot().OfType<NpcCharacter>().Single();
        Assert.IsTrue(npc.HasFeature(NpcFeature.Persistent));
        Assert.AreEqual(1, ordinary.MemberCount);

        Invoke(world.System, "OnCellSleep", persistent.CellId);
        if (ordinary.CellId != persistent.CellId)
            Invoke(world.System, "OnCellSleep", ordinary.CellId);

        Assert.AreSame(npc, persistent.MembersSnapshot().Single(), "A Persistent NPC must not be sleep-despawned.");
        Assert.AreSame(world.Playfield, npc.Playfield);
        Assert.IsTrue(world.Registry.TryGet(npc.Identity, out _));
        Assert.AreEqual(0, ordinary.MemberCount, "An ordinary NPC still sleep-despawns.");

        // Waking again never duplicates it.
        world.Tick(persistent);
        Assert.AreEqual(1, persistent.MemberCount);
        Assert.AreEqual(1, world.Registry.Dynels().OfType<NpcCharacter>().Count(n => n.HasFeature(NpcFeature.Persistent)));
    }

    [TestMethod]
    public void PersistentNpcRespawnsOnItsOwnTimerAfterADeath()
    {
        using var content = new Content();
        using var world = new World(content.Root);
        HashSpawnPoint persistent = world.Points.Single(point => point.HashText == PersistentHash);
        world.Tick(persistent);
        NpcCharacter first = persistent.MembersSnapshot().OfType<NpcCharacter>().Single();

        first.DespawnAsDeath();
        Assert.AreEqual(HashSpawnState.Dead, persistent.State);
        world.Tick(persistent);
        Assert.AreEqual(0, persistent.MemberCount, "Its Spawns.json respawn time still applies.");

        persistent.NextSpawnTime = DateTime.UtcNow.AddSeconds(-1);
        world.Tick(persistent);
        NpcCharacter respawned = persistent.MembersSnapshot().OfType<NpcCharacter>().Single();
        Assert.AreNotEqual(first.Identity, respawned.Identity);
        Assert.AreEqual(1, world.Registry.Dynels().OfType<NpcCharacter>().Count(n => n.HasFeature(NpcFeature.Persistent)));
    }

    [TestMethod]
    public void PersistentSpawnCellNeverSleepsAfterItCools()
    {
        var slept = new List<int>();
        var spawnTicks = new List<int>();
        CellHeatScheduler scheduler = CreateOutdoorScheduler(out CellGrid grid);
        int persistentCell = grid.ResolveCell(new Vector3(20f, 0f, 20f)).Id;
        int ordinaryCell = grid.ResolveCell(new Vector3(300f, 0f, 300f)).Id;
        Assert.AreNotEqual(persistentCell, ordinaryCell);
        scheduler.ConfigureSpawnHooks([persistentCell, ordinaryCell], slept.Add, spawnTicks.Add, () => { },
            alwaysColdSpawnCellIds: [persistentCell]);

        // Playfield load, nothing near either point: only the Persistent point's cell ticks (and spawns).
        scheduler.Tick([], 0.2);
        CollectionAssert.Contains(spawnTicks, persistentCell);
        CollectionAssert.DoesNotContain(spawnTicks, ordinaryCell);

        // A fight heats both cells, ends, and both cool. Past the sleep time only the ordinary cell sleeps.
        CountingNpc nearPersistent = Fighter(10, grid, new Vector3(20f, 0f, 20f));
        CountingNpc nearOrdinary = Fighter(11, grid, new Vector3(300f, 0f, 300f));
        scheduler.Tick([nearPersistent, nearOrdinary], 0.2);
        Remove(nearPersistent);
        Remove(nearOrdinary);
        scheduler.Tick([], 0.2);
        ExpireColdTimers(scheduler);
        scheduler.Tick([], 0.2);

        CollectionAssert.Contains(slept, ordinaryCell);
        CollectionAssert.DoesNotContain(slept, persistentCell, "A Persistent point's cell must never sleep.");
    }

    [TestMethod]
    public void LivingPersistentNpcKeepsTheCellItWalkedIntoTicking()
    {
        CellHeatScheduler scheduler = CreateOutdoorScheduler(out CellGrid grid);
        var spawnTicks = new List<int>();
        scheduler.ConfigureSpawnHooks([], _ => Assert.Fail("Its cell must not sleep"), spawnTicks.Add, () => { });

        var persistent = new CountingNpc(9) { MobTemplate = new MobTemplate { Features = { NpcFeature.Persistent } } };
        persistent.Stats.Set(CharacterStat.Health, 100);
        var ordinary = new CountingNpc(10);
        ordinary.Stats.Set(CharacterStat.Health, 100);
        Place(grid, persistent, new Vector3(20f, 0f, 20f));
        Place(grid, ordinary, new Vector3(300f, 0f, 300f));

        scheduler.Tick([persistent, ordinary], 0.2);

        Assert.AreEqual(1, persistent.Ticks, "A living Persistent NPC keeps its buffs' timers running with no player near.");
        Assert.AreEqual(0, ordinary.Ticks);
        Assert.AreEqual(0, spawnTicks.Count, "A cell held only by a wandering NPC spawns nothing.");
    }

    static bool IsPersistent(HashSpawnSystem system, HashSpawnPoint point, List<MobTemplate> scratch)
        => (bool)typeof(HashSpawnSystem).GetMethod("IsPersistent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(system, [point, scratch])!;

    static List<int> AlwaysColdSpawnCells(HashSpawnSystem system)
        => (List<int>)typeof(HashSpawnSystem).GetMethod("AlwaysColdSpawnCells", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(system, null)!;

    static void Invoke(object target, string name, object argument)
        => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, [argument]);

    static void ExpireColdTimers(CellHeatScheduler scheduler)
    {
        var timers = (Dictionary<int, DateTime>)typeof(CellHeatScheduler)
            .GetField("_coldSinceUtcByCell", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scheduler)!;
        foreach (int cellId in timers.Keys.ToArray())
            timers[cellId] = DateTime.UtcNow.AddHours(-1);
    }

    static CellHeatScheduler CreateOutdoorScheduler(out CellGrid grid)
    {
        var meta = new PlayfieldMetaData
        {
            SchemaVersion = 1,
            TilemapFormat = PlayfieldMetaData.ChunkedGroundFormat,
            TilemapResource = 1,
            Width = 100,
            Height = 100,
            TileSize = 1f
        };
        LocalityPolicy policy = LocalityPolicy.FromConfig(new LocalitySettings
        {
            EnableCellHeatScheduling = true,
            VisibilityNeighborLevel = 2,
            HotNeighborLevel = 1,
            WarmNeighborLevel = 2,
            CellSleepTime = 30
        });
        grid = new CellGrid(meta, policy.VisibilityNeighborLevel);
        return new CellHeatScheduler(500, grid, policy);
    }

    static CountingNpc Fighter(int instance, CellGrid grid, Vector3 position)
    {
        var npc = new CountingNpc(instance);
        npc.Stats.Set(CharacterStat.Health, 100);
        npc.SetFightingTarget(new Identity { Type = IdentityType.SimpleChar, Instance = 42 });
        Place(grid, npc, position);
        return npc;
    }

    static void Place(CellGrid grid, Dynel dynel, Vector3 position)
    {
        dynel.Position = position;
        Cell cell = grid.ResolveCell(position);
        cell.Add(dynel);
        dynel.Cell = cell;
    }

    static void Remove(Dynel dynel)
    {
        dynel.Cell!.Remove(dynel);
        dynel.Cell = null;
    }

    static T Blank<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    static void Field(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    sealed class CountingNpc(int instance)
        : NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = instance }, new Items())
    {
        internal int Ticks;
        public override void Tick(double deltaTime) => Ticks++;
    }

    sealed class Content : IDisposable
    {
        internal const int PlayfieldId = 665;
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "aorebirth-persistent-test-" + Guid.NewGuid().ToString("N"));

        internal Content()
        {
            string spawns = Path.Combine(Root, "Playfields", PlayfieldId.ToString(), "Spawns.json");
            Directory.CreateDirectory(Path.GetDirectoryName(spawns)!);
            File.WriteAllText(Path.Combine(Root, "NpcTemplates.json"),
                "{\"" + PersistentHash + "\":{\"Templates\":[{\"Name\":\"Persistent fixture\",\"Level\":1,\"Stats\":{\"54\":1,\"1\":100},\"Features\":[\"NoCombat\"]}]},"
                + "\"" + OrdinaryHash + "\":{\"Templates\":[{\"Name\":\"Ordinary fixture\",\"Level\":1,\"Stats\":{\"54\":1,\"1\":100},\"Features\":[\"NoCombat\"]}]},"
                + "\"AAAA\":{\"Templates\":[{\"Name\":\"Diagnostic fixture\",\"Level\":1,\"Stats\":{\"54\":1}}]}}");
            File.WriteAllText(Path.Combine(Root, MonsterCombatRulesCatalog.FileName),
                "{\"" + PersistentHash + "\":{\"Features\":[\"Persistent\"]}}");
            File.WriteAllText(spawns, JsonSerializer.Serialize(new PlayfieldSpawnsData
            {
                SchemaVersion = 1,
                PlayfieldId = PlayfieldId,
                Spawns = [Entry(PersistentHash, [4, 5, 6]), Entry(OrdinaryHash, [8, 5, 9])]
            }));
        }

        static PlayfieldSpawnEntry Entry(string hash, float[] position) => new()
        {
            HashText = hash, Position = position, Radius = 0, MinLevel = 1, MaxLevel = 1,
            RespawnChance = 100, RespawnTime = 600
        };

        public void Dispose() => Directory.Delete(Root, true);
    }

    /// <summary>A playfield with only what hash spawning needs, the way <c>HalloweenActivationTests</c> builds one.</summary>
    sealed class World : IDisposable
    {
        readonly ServiceProvider _services;

        internal World(string root)
        {
            const int id = Content.PlayfieldId;
            var settings = new GameplaySettings { StuckLocation = new GameplayLocation { Playfield = 566, X = 1, Y = 2, Z = 3 } };
            Field(Playfield, "<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = id });
            Field(Playfield, "<ResourceId>k__BackingField", id);
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

        internal List<HashSpawnPoint> Points
            => (List<HashSpawnPoint>)typeof(HashSpawnSystem).GetField("_allPoints", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(System)!;

        internal void Tick(HashSpawnPoint point) => Invoke(System, "OnCellTick", point.CellId);

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
