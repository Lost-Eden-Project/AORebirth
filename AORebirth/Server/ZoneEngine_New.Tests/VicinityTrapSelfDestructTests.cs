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
using LostEden.Protocol.Messages;

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
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.Trade;
using ZoneEngine_New.Core.WorldSimulation;

using EventType = AORebirth.Enums.EventType;
using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// A hash-spawned vicinity trap ends itself, never the player who walks into it. Live defect: Collision Item GASD
/// collsphere_14m.abiff (228380) around Scheol runs SpawnMonster2 and its text on the player (ApplyOn Wearer), then
/// Set Health 0 and Set TimeExist 1 on ApplyOn User, which is the trap itself; every row ran on the player and killed them.
/// </summary>
[TestClass]
public sealed class VicinityTrapSelfDestructTests
{
    const string Hash = "TRAP";
    const int TrapTemplate = 900001;
    static readonly Vector3 TrapSite = new(20f, 0f, 20f);

    [TestMethod]
    public void WalkingIntoTheTrapKeepsThePlayerAliveAndDespawnsTheTrapUntilItsRespawn()
    {
        using var content = new Content();
        using var world = new World(content.Root);
        HashSpawnPoint point = world.Points.Single();
        world.Tick(point);
        WorldItem trap = point.MembersSnapshot().OfType<WorldItem>().Single();
        Assert.IsTrue(world.Registry.TryGet(trap.Identity, out _));

        Player player = world.AddPlayer(1, gmLevel: 0);
        world.WalkOnto(player, trap.Position);

        Assert.AreEqual(100, player.Stats.GetOrZero(CharacterStat.Health), "The trap's self Set Health 0 must not land on the player.");
        Assert.AreEqual(0, player.Stats.GetOrZero(CharacterStat.TimeExist), "Nor its self TimeExist.");
        Assert.AreEqual(1, world.Session(player).Texts, "The player rows still run: the trap's warning text.");
        Assert.IsNull(trap.Playfield, "The trap removes itself.");
        Assert.IsFalse(world.Registry.TryGet(trap.Identity, out _));
        Assert.AreEqual(0, point.MemberCount);
        Assert.AreEqual(HashSpawnState.Dead, point.State, "It is gone as a death, so its spawn point brings it back on its timer.");
        Assert.AreEqual(0, world.Simulation.VicinityTriggerCount, "Its pad goes with it: nobody else can trigger it again.");
    }

    [TestMethod]
    public void TwoPlayersStepInTogetherAndTheTrapFiresOnce()
    {
        using var content = new Content();
        using var world = new World(content.Root);
        HashSpawnPoint point = world.Points.Single();
        world.Tick(point);
        WorldItem trap = point.MembersSnapshot().OfType<WorldItem>().Single();

        Player first = world.AddPlayer(1, gmLevel: 0);
        Player second = world.AddPlayer(2, gmLevel: 0);
        world.WalkOnto([first, second], trap.Position);

        Assert.AreEqual(1, world.Session(first).Texts + world.Session(second).Texts, "One trap, one firing.");
        Assert.AreEqual(100, first.Stats.GetOrZero(CharacterStat.Health));
        Assert.AreEqual(100, second.Stats.GetOrZero(CharacterStat.Health));
        Assert.IsNull(trap.Playfield);
    }

    [TestMethod]
    public void AGameMasterDoesNotSetTheTrapOff()
    {
        using var content = new Content();
        using var world = new World(content.Root);
        HashSpawnPoint point = world.Points.Single();
        world.Tick(point);
        WorldItem trap = point.MembersSnapshot().OfType<WorldItem>().Single();

        Player gm = world.AddPlayer(1, gmLevel: 1);
        world.WalkOnto(gm, trap.Position);

        Assert.AreEqual(100, gm.Stats.GetOrZero(CharacterStat.Health));
        Assert.AreEqual(0, world.Session(gm).Texts);
        Assert.AreSame(world.Playfield, trap.Playfield, "Nothing ran on the GM, so the trap stays armed.");
        Assert.AreEqual(HashSpawnState.Alive, point.State);
    }

    /// <summary>The Collision Item GASD rows as the item catalog maps them (ApplyOn 2 = Wearer, 1 = User).</summary>
    static ItemTemplate TrapEvents()
    {
        static ItemRequirement Equal(CharacterStat stat) => new()
        {
            StatNumber = (int)stat, Operator = (int)Operator.EqualTo, Value = 0
        };

        var template = new ItemTemplate { Id = TrapTemplate, Quality = 1, Name = "Collision Item TRAP" };
        template.SpellList[EventType.OnTargetInVicinity] =
        [
            new ItemSpell
            {
                FunctionType = (int)FunctionType.SystemText, Target = (int)ItemTarget.Wearer, TickCount = 1,
                Arguments = ["You see spiders crawling out of a crack in the ground..", 0, 0, 0, 0, 0],
                Requirements = [Equal(CharacterStat.GmLevel)]
            },
            new ItemSpell
            {
                FunctionType = (int)FunctionType.Set, Target = (int)ItemTarget.User, TickCount = 1,
                Arguments = [(int)CharacterStat.Health, 0]
            },
            new ItemSpell
            {
                FunctionType = (int)FunctionType.Set, Target = (int)ItemTarget.User, TickCount = 1,
                Arguments = [(int)CharacterStat.TimeExist, 1]
            }
        ];
        return template;
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
        internal const int PlayfieldId = 667;
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "aorebirth-vicinity-trap-test-" + Guid.NewGuid().ToString("N"));

        internal Content()
        {
            string spawns = Path.Combine(Root, "Playfields", PlayfieldId.ToString(), "Spawns.json");
            Directory.CreateDirectory(Path.GetDirectoryName(spawns)!);
            File.WriteAllText(Path.Combine(Root, "NpcTemplates.json"),
                "{\"AAAA\":{\"Templates\":[{\"Name\":\"Diagnostic fixture\",\"Level\":1,\"Stats\":{\"54\":1}}]}}");
            File.WriteAllText(Path.Combine(Root, "ItemTemplates.json"),
                "{\"" + Hash + "\":{\"Templates\":[" + TrapTemplate + "," + TrapTemplate + "]}}");
            File.WriteAllText(spawns, JsonSerializer.Serialize(new PlayfieldSpawnsData
            {
                SchemaVersion = 1,
                PlayfieldId = PlayfieldId,
                Spawns =
                [
                    new PlayfieldSpawnEntry
                    {
                        HashText = Hash, Position = [TrapSite.xf, TrapSite.yf, TrapSite.zf], Radius = 0, MinLevel = 1,
                        MaxLevel = 1, RespawnChance = 100, RespawnTime = 600
                    }
                ]
            }));
        }

        public void Dispose() => Directory.Delete(Root, true);
    }

    /// <summary>An outdoor playfield with hash spawning and its soft triggers, the way <c>AwaySpawnMemberSleepTests</c> builds one.</summary>
    sealed class World : IDisposable
    {
        readonly ServiceProvider _services;
        readonly Items _items = new();
        readonly Dictionary<Player, Session> _sessions = new();

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
            Simulation = PlayfieldWorldSimulation.Create(id, new PlayfieldGeometryData(), null, DestinationsCatalog.Instance,
                Data, new Logger());
            var catalog = new Catalog();
            var minter = new HashItemMinter(Data, catalog, _items);
            var moves = new InventoryMoveService(new Logger(), Blank<InventoryFlushService>(), Blank<InventoryActionService>());
            var activation = new NpcContentActivationService(Playfield, Registry, Locality, _items, catalog, Data);
            _services = new ServiceCollection()
                .AddSingleton(Registry).AddSingleton(Locality).AddSingleton<IGameData>(Data)
                .AddSingleton(NullProxy<IInventoryRepository>.Create())
                .AddSingleton<IItemBuilder>(_items).AddSingleton<IItemTemplateCatalog>(catalog)
                .AddSingleton(new WorldSimulationAccess { Instance = Simulation })
                .AddSingleton(moves).AddSingleton(activation).AddSingleton(settings)
                .AddSingleton(_ => Spawns).AddSingleton(_ => System)
                .BuildServiceProvider();
            Field(Playfield, "_serviceProvider", _services);
            Spawns = new SpawnService(_services, Registry, new Logger(), Playfield, Blank<PlayfieldManager>(),
                Data, _items, minter, Blank<InventoryFlushService>(), Blank<TradeService>(), Blank<CharacterSnapshotService>());
            System = new HashSpawnSystem(Playfield, Spawns, Data, new Logger(), settings);
            System.Initialize(Locality);
        }

        internal Playfield Playfield { get; } = Blank<Playfield>();
        internal DynelRegistry Registry { get; } = new();
        internal PlayfieldLocality Locality { get; }
        internal PlayfieldWorldSimulation Simulation { get; }
        internal SpawnService Spawns { get; }
        internal HashSpawnSystem System { get; }
        internal GameDataStore Data { get; }

        internal List<HashSpawnPoint> Points => (List<HashSpawnPoint>)GetField(System, "_allPoints");

        internal void Tick(HashSpawnPoint point) => Invoke(System, "OnCellTick", point.CellId);

        internal Session Session(Player player) => _sessions[player];

        internal Player AddPlayer(int characterId, int gmLevel)
        {
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = characterId }, new Logger(), _items)
            {
                FirstName = "Trap" + characterId
            };
            player.Stats.Set(CharacterStat.Level, 110);
            player.Stats.Set(CharacterStat.MaxHealth, 100);
            player.Stats.Set(CharacterStat.Health, 100);
            player.Stats.Set(CharacterStat.GmLevel, gmLevel);
            var session = new Session();
            session.BindPlayer(player);
            player.Session = session;
            player.Playfield = Playfield;
            player.Position = new Vector3(TrapSite.x + 40, TrapSite.y, TrapSite.z);
            Registry.Register(player);
            _sessions[player] = session;
            return player;
        }

        internal void WalkOnto(Player player, Vector3 at) => WalkOnto([player], at);

        /// <summary>Every player is first seen away from the trap, then all step onto it within one soft-trigger tick.</summary>
        internal void WalkOnto(Player[] players, Vector3 at)
        {
            Simulation.TickSoftTriggers(Playfield, 0.1);
            foreach (Player player in players)
                player.Position = new Vector3(at.x, at.y, at.z);
            Simulation.TickSoftTriggers(Playfield, 0.1);
        }

        public void Dispose()
        {
            Simulation.Dispose();
            _services.Dispose();
        }
    }

    sealed class Session : IZoneSession
    {
        public int Texts { get; private set; }
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public bool TransferToPlayfield(Playfield destination, Vector3 landing) => false;
        public void Send(byte[] packet) { }
        public void Send(MessageBody body)
        {
            // SystemText goes out as FormatFeedback 110/707.
            if (body.GetType().Name.Contains("Feedback", StringComparison.Ordinal))
                Texts++;
        }
        public void Send(MessageBody body, int sender, int receiver) => Send(body);
        public void SendInitiateCompression() { }
        public void Close() => State = SessionState.Closed;
    }

    /// <summary>Answers every call with the default value: for services the spawn path resolves but this trap never uses.</summary>
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
        public void Error(Exception exception, string message) => Console.WriteLine("ERROR " + message + " " + exception);
        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }

    sealed class Catalog : IItemTemplateCatalog
    {
        public bool TryGet(int aoid, out ItemTemplate template)
        {
            template = aoid == TrapTemplate ? TrapEvents() : null!;
            return aoid == TrapTemplate;
        }

        public ItemTemplate Require(int aoid) => TryGet(aoid, out ItemTemplate template) ? template : throw new KeyNotFoundException(aoid.ToString());
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

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality)
            => lowId == TrapTemplate ? TrapEvents() : new() { Id = lowId, Quality = quality };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }
}
