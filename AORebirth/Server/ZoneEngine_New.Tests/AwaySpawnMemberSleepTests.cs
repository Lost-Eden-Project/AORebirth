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
/// A spawn point sleeps with its own cell, but a member standing in a cell that is still awake (a rotation spawn that
/// rolled a site cells away, right next to a player) stays in the world until the cell it stands in sleeps. Live
/// defect: Mooching Dryad (MODY) in 4872 vanished beside a player 81 s after spawning because its point's cell, about
/// 100 m away, had no one near.
/// </summary>
[TestClass]
public sealed class AwaySpawnMemberSleepTests
{
    const string Hash = "AWAY";
    static readonly Vector3 AwaySite = new(300f, 0f, 300f);

    [TestMethod]
    public void MemberBesideAPlayerOutlivesItsPointsCellSleeping()
    {
        using var content = new Content();
        using var world = new World(content.Root);
        HashSpawnPoint point = world.Points.Single();
        world.Tick(point);
        NpcCharacter npc = point.MembersSnapshot().OfType<NpcCharacter>().Single();
        int awayCell = world.MoveTo(npc, AwaySite);
        Assert.AreNotEqual(point.CellId, awayCell, "The fixture needs the member in a different cell.");

        world.SetHeat(awayCell, CellHeat.Hot);
        world.SetHeat(point.CellId, CellHeat.Asleep);
        world.Sleep(point.CellId);

        Assert.AreSame(npc, point.MembersSnapshot().Single(), "A member next to a player must not vanish with its point's empty cell.");
        Assert.AreSame(world.Playfield, npc.Playfield);
        Assert.IsTrue(world.Registry.TryGet(npc.Identity, out _));

        // The cell it stands in goes to sleep: now it leaves, and the point keeps its roll.
        world.SetHeat(awayCell, CellHeat.Asleep);
        world.Sleep(awayCell);

        Assert.AreEqual(0, point.MemberCount, "It sleeps with the cell it stands in.");
        Assert.IsNull(npc.Playfield);
        Assert.IsFalse(world.Registry.TryGet(npc.Identity, out _));
        Assert.AreEqual(HashSpawnState.Alive, point.State, "Sleeping is not a death: no respawn timer, no new roll.");
        Assert.IsTrue(point.HoldsRoll);

        // Waking brings back exactly one, never a duplicate.
        world.SetHeat(point.CellId, CellHeat.Hot);
        world.Tick(point);
        world.Tick(point);
        Assert.AreEqual(1, point.MemberCount);
        Assert.AreEqual(1, world.Registry.Dynels().OfType<NpcCharacter>().Count());
    }

    [TestMethod]
    public void AwayCellSleepingLeavesTheMemberWhileItsPointIsAwake()
    {
        using var content = new Content();
        using var world = new World(content.Root);
        HashSpawnPoint point = world.Points.Single();
        world.Tick(point);
        NpcCharacter npc = point.MembersSnapshot().OfType<NpcCharacter>().Single();
        int awayCell = world.MoveTo(npc, AwaySite);

        world.SetHeat(point.CellId, CellHeat.Hot);
        world.SetHeat(awayCell, CellHeat.Asleep);
        world.Sleep(awayCell);

        Assert.AreSame(npc, point.MembersSnapshot().Single(), "Only the point's own cell decides while it is awake.");
        Assert.AreSame(world.Playfield, npc.Playfield);
    }

    [TestMethod]
    public void MemberInItsPointsSleepingCellStillSleeps()
    {
        using var content = new Content();
        using var world = new World(content.Root);
        HashSpawnPoint point = world.Points.Single();
        world.Tick(point);
        NpcCharacter npc = point.MembersSnapshot().OfType<NpcCharacter>().Single();

        world.SetHeat(point.CellId, CellHeat.Asleep);
        world.Sleep(point.CellId);

        Assert.AreEqual(0, point.MemberCount);
        Assert.IsNull(npc.Playfield);
        Assert.AreEqual(0, world.Registry.Dynels().OfType<NpcCharacter>().Count());
    }

    static T Blank<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    static void Field(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    static object GetField(object target, string name)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    static void Invoke(object target, string name, object argument)
        => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, [argument]);

    sealed class Content : IDisposable
    {
        internal const int PlayfieldId = 666;
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "aorebirth-away-member-test-" + Guid.NewGuid().ToString("N"));

        internal Content()
        {
            string spawns = Path.Combine(Root, "Playfields", PlayfieldId.ToString(), "Spawns.json");
            Directory.CreateDirectory(Path.GetDirectoryName(spawns)!);
            File.WriteAllText(Path.Combine(Root, "NpcTemplates.json"),
                "{\"" + Hash + "\":{\"Templates\":[{\"Name\":\"Away fixture\",\"Level\":1,\"Stats\":{\"54\":1,\"1\":100},\"Features\":[\"NoCombat\"]}]},"
                + "\"AAAA\":{\"Templates\":[{\"Name\":\"Diagnostic fixture\",\"Level\":1,\"Stats\":{\"54\":1}}]}}");
            File.WriteAllText(spawns, JsonSerializer.Serialize(new PlayfieldSpawnsData
            {
                SchemaVersion = 1,
                PlayfieldId = PlayfieldId,
                Spawns =
                [
                    new PlayfieldSpawnEntry
                    {
                        HashText = Hash, Position = [20f, 0f, 20f], Radius = 0, MinLevel = 1, MaxLevel = 1,
                        RespawnChance = 100, RespawnTime = 600
                    }
                ]
            }));
        }

        public void Dispose() => Directory.Delete(Root, true);
    }

    /// <summary>An outdoor playfield with only what hash spawning needs, the way <c>PersistentNpcTests</c> builds one.</summary>
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
            Locality = new PlayfieldLocality(id, new PlayfieldMetaData
            {
                SchemaVersion = 1,
                TilemapFormat = PlayfieldMetaData.ChunkedGroundFormat,
                TilemapResource = 1,
                Width = 100,
                Height = 100,
                TileSize = 1f
            });
            Assert.IsTrue(Locality.Grid.IsOutdoor);
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

        internal List<HashSpawnPoint> Points => (List<HashSpawnPoint>)GetField(System, "_allPoints");

        internal void Tick(HashSpawnPoint point) => Invoke(System, "OnCellTick", point.CellId);

        internal void Sleep(int cellId) => Invoke(System, "OnCellSleep", cellId);

        /// <summary>What the heat scheduler records for a cell on a heartbeat (Hot near a player, Asleep after cooling).</summary>
        internal void SetHeat(int cellId, CellHeat heat)
        {
            var scheduler = (CellHeatScheduler)GetField(Locality, "_heatScheduler");
            ((Dictionary<int, CellHeat>)GetField(scheduler, "_heatByCell"))[cellId] = heat;
        }

        /// <summary>Walks the NPC to <paramref name="position"/>, re-placing it in that position's cell; the cell id.</summary>
        internal int MoveTo(Dynel dynel, Vector3 position)
        {
            dynel.Position = position;
            Cell cell = Locality.Grid.ResolveCell(position);
            dynel.Cell?.Remove(dynel);
            cell.Add(dynel);
            dynel.Cell = cell;
            return cell.Id;
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
