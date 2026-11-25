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
/// A hash-spawned Unicorn Laser Fence (278035, Sector 10 URLF) kills the alien that walks into it and nothing else. Live
/// defect: walk-in pads were only ever tested against players, so a Kyr'Ozch walked straight through the fence. Its
/// OnEnter rows run on the entering character (ApplyOn Wearer) under npcfamily == 220: Set Health 0 and SetFlag
/// NPCFlags bit 14. A player or a non-alien NPC in the fence is untouched, and the fence stays up after a kill.
/// </summary>
[TestClass]
public sealed class NpcWalkInFenceTests
{
    const string Hash = "FNCE";
    const int FenceTemplate = 900002;
    const int FenceMesh = 279351;
    const int AlienFamily = 220;
    const int FenceFlagBit = 14;
    static readonly Vector3 FenceSite = new(20f, 0f, 20f);

    /// <summary>
    /// Sector 10 live: the fence's mesh sphere is on file (radius 5.558, centre 0.35 below its base) but the hash-spawned
    /// fence has no placement, so the pad fell back to a 1 m disc at the fence's centre point; Delmus stood 2.3 m from
    /// it with a spawned alien and nothing happened. The pad must be the fence's own mesh sphere.
    /// </summary>
    [TestMethod]
    public void AnAlienTwoMetresFromTheFenceCentreIsStillInsideTheFence()
    {
        using var content = new Content();
        using var world = new World(content.Root);
        WorldItem fence = world.SpawnFence();

        NpcCharacter alien = world.AddNpc(5001, AlienFamily);
        world.WalkOnto(alien, new Vector3(FenceSite.x - 0.8, FenceSite.y, FenceSite.z + 2.2));

        Assert.IsTrue(alien.IsDead, "The fence's sphere reaches 5.8 m from its centre, not 1 m.");
        Assert.AreSame(world.Playfield, fence.Playfield);
    }

    [TestMethod]
    public void AnAlienWalkingIntoTheFenceDiesThereWithNoSpoils()
    {
        using var content = new Content();
        using var world = new World(content.Root);
        WorldItem fence = world.SpawnFence();
        Assert.AreEqual(1, world.Simulation.VicinityTriggerCount, "The fence's OnEnter list gets a walk-in pad.");

        NpcCharacter alien = world.AddNpc(5001, AlienFamily);
        world.WalkOnto(alien, fence.Position);

        Assert.IsTrue(alien.IsDead, "The fence kills the alien.");
        Assert.AreEqual(0, alien.Stats.GetOrZero(CharacterStat.Health));
        Assert.AreEqual(1 << FenceFlagBit, alien.Stats.GetOrZero(CharacterStat.NPCFlags) & (1 << FenceFlagBit), "Its SetFlag row ran too.");
        Assert.IsFalse(alien.PlayersEarnedSpoils(), "Nobody fought it: no XP, no loot.");
        Assert.AreSame(world.Playfield, fence.Playfield, "The fence stays up.");
        Assert.AreEqual(1, world.Simulation.VicinityTriggerCount);
    }

    [TestMethod]
    public void ANonAlienNpcAndAPlayerWalkThroughUnharmed()
    {
        using var content = new Content();
        using var world = new World(content.Root);
        WorldItem fence = world.SpawnFence();

        NpcCharacter trooper = world.AddNpc(5002, npcFamily: 36);
        world.WalkOnto(trooper, fence.Position);
        Player player = world.AddPlayer(1);
        world.WalkOnto(player, fence.Position);

        Assert.IsFalse(trooper.IsDead);
        Assert.AreEqual(100, trooper.Stats.GetOrZero(CharacterStat.Health));
        Assert.AreEqual(0, trooper.Stats.GetOrZero(CharacterStat.NPCFlags));
        Assert.IsFalse(player.IsDead);
        Assert.AreEqual(100, player.Stats.GetOrZero(CharacterStat.Health));
        Assert.AreEqual(0, player.Stats.GetOrZero(CharacterStat.NPCFlags));
    }

    [TestMethod]
    public void TheFenceKillsEachAlienThatCrossesItAndLeavesTheLingererAlone()
    {
        using var content = new Content();
        using var world = new World(content.Root);
        WorldItem fence = world.SpawnFence();

        NpcCharacter trooper = world.AddNpc(5002, npcFamily: 36);
        world.WalkOnto(trooper, fence.Position);
        // The trooper stands in the fence while two aliens come through one after the other.
        NpcCharacter first = world.AddNpc(5003, AlienFamily);
        world.WalkOnto(first, fence.Position);
        NpcCharacter second = world.AddNpc(5004, AlienFamily);
        world.WalkOnto(second, fence.Position);
        world.Tick();

        Assert.IsTrue(first.IsDead);
        Assert.IsTrue(second.IsDead);
        Assert.IsFalse(trooper.IsDead);
        Assert.AreEqual(100, trooper.Stats.GetOrZero(CharacterStat.Health));
    }

    [TestMethod]
    public void OnlyWalkInListsWithNpcCriteriaFireOnNpcs()
    {
        static ItemRequirement AlienLeaf() => new()
        {
            StatNumber = (int)CharacterStat.NPCFamily, Operator = (int)Operator.EqualTo, Value = AlienFamily
        };
        static ItemSpell Kill(List<ItemRequirement>? leaves) => new()
        {
            FunctionType = (int)FunctionType.Set, Target = (int)ItemTarget.Wearer, TickCount = 1,
            Arguments = [(int)CharacterStat.Health, 0], Requirements = leaves ?? []
        };

        Assert.IsTrue(DynelEventSpells.TargetsNpcs([Kill([AlienLeaf()])]), "A row on the entering character testing NPCFamily.");
        Assert.IsFalse(DynelEventSpells.TargetsNpcs([Kill(null)]), "A trap with no NPC criteria stays player-only.");
        Assert.IsFalse(DynelEventSpells.TargetsNpcs([new ItemSpell
        {
            FunctionType = (int)FunctionType.Set, Target = (int)ItemTarget.User, TickCount = 1,
            Arguments = [(int)CharacterStat.Health, 0], Requirements = [AlienLeaf()]
        }]), "A row on the owner itself does not make the pad an NPC pad.");
        Assert.IsFalse(DynelEventSpells.TargetsNpcs([Kill([AlienLeaf()]), new ItemSpell
        {
            FunctionType = (int)FunctionType.AreaTrigger, Target = (int)ItemTarget.Wearer, TickCount = 1, Arguments = [0]
        }]), "A scheduled static event is a player's.");
    }

    /// <summary>The Unicorn Laser Fence rows as the item catalog maps them (ApplyOn 2 = Wearer; Criteria npcfamily == 220).</summary>
    static ItemTemplate FenceEvents()
    {
        static ItemRequirement Alien() => new()
        {
            StatNumber = (int)CharacterStat.NPCFamily, Operator = (int)Operator.EqualTo, Value = AlienFamily
        };

        var template = new ItemTemplate { Id = FenceTemplate, Quality = 1, Name = "Unicorn Laser Fence" };
        template.Stats[CharacterStat.Mesh] = FenceMesh;
        template.SpellList[EventType.OnEnter] =
        [
            new ItemSpell
            {
                FunctionType = (int)FunctionType.Set, Target = (int)ItemTarget.Wearer, TickCount = 1,
                Arguments = [(int)CharacterStat.Health, 0],
                Requirements = [Alien()]
            },
            new ItemSpell
            {
                FunctionType = (int)FunctionType.SetFlag, Target = (int)ItemTarget.Wearer, TickCount = 1,
                Arguments = [(int)CharacterStat.NPCFlags, FenceFlagBit],
                Requirements = [Alien()]
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
        internal const int PlayfieldId = 4374;
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "aorebirth-npc-fence-test-" + Guid.NewGuid().ToString("N"));

        internal Content()
        {
            string spawns = Path.Combine(Root, "Playfields", PlayfieldId.ToString(), "Spawns.json");
            Directory.CreateDirectory(Path.GetDirectoryName(spawns)!);
            File.WriteAllText(Path.Combine(Root, "NpcTemplates.json"),
                "{\"AAAA\":{\"Templates\":[{\"Name\":\"Diagnostic fixture\",\"Level\":1,\"Stats\":{\"54\":1}}]}}");
            File.WriteAllText(Path.Combine(Root, "ItemTemplates.json"),
                "{\"" + Hash + "\":{\"Templates\":[" + FenceTemplate + "]}}");
            // The live DynelCollision.json rows that matter: the player's sphere and the fence mesh's; no Templates row.
            File.WriteAllText(Path.Combine(Root, "DynelCollision.json"),
                "{\"SchemaVersion\":1,\"PlayerSphere\":{\"Radius\":0.25075,\"CenterY\":0.25075},\"Templates\":{},"
                + "\"Meshes\":{\"" + FenceMesh + "\":{\"Radius\":5.558,\"CenterY\":-0.352058}}}");
            File.WriteAllText(spawns, JsonSerializer.Serialize(new PlayfieldSpawnsData
            {
                SchemaVersion = 1,
                PlayfieldId = PlayfieldId,
                Spawns =
                [
                    new PlayfieldSpawnEntry
                    {
                        HashText = Hash, Position = [FenceSite.xf, FenceSite.yf, FenceSite.zf], Radius = 0.001f, MinLevel = 1,
                        MaxLevel = 1, RespawnChance = 100, RespawnTime = 660
                    }
                ]
            }));
        }

        public void Dispose() => Directory.Delete(Root, true);
    }

    /// <summary>An outdoor playfield with hash spawning, its locality grid and its soft triggers, as <c>VicinityTrapSelfDestructTests</c> builds one.</summary>
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

        internal WorldItem SpawnFence()
        {
            HashSpawnPoint point = ((List<HashSpawnPoint>)GetField(System, "_allPoints")).Single();
            Invoke(System, "OnCellTick", point.CellId);
            WorldItem fence = point.MembersSnapshot().OfType<WorldItem>().Single();
            Assert.IsTrue(Registry.TryGet(fence.Identity, out _));
            return fence;
        }

        internal Player AddPlayer(int characterId)
        {
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = characterId }, new Logger(), _items)
            {
                FirstName = "Fence" + characterId
            };
            player.Stats.Set(CharacterStat.Level, 110);
            player.Stats.Set(CharacterStat.MaxHealth, 100);
            player.Stats.Set(CharacterStat.Health, 100);
            var session = new Session();
            session.BindPlayer(player);
            player.Session = session;
            Place(player);
            return player;
        }

        internal NpcCharacter AddNpc(int instance, int npcFamily)
        {
            var npc = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = instance }, _items)
            {
                Name = npcFamily == AlienFamily ? "Ensign - Ankari'Sinuh" : "Unicorn Trooper"
            };
            npc.Stats.Set(CharacterStat.Level, 110);
            npc.Stats.Set(CharacterStat.MaxHealth, 100);
            npc.Stats.Set(CharacterStat.Health, 100);
            npc.Stats.Set(CharacterStat.NPCFamily, npcFamily);
            Place(npc);
            return npc;
        }

        /// <summary>Starts a character well away from the fence, in the world and on the locality grid.</summary>
        void Place(Character character)
        {
            character.Playfield = Playfield;
            character.Position = new Vector3(FenceSite.x + 40, FenceSite.y, FenceSite.z);
            Registry.Register(character);
            Locality.RegisterDynel(character);
        }

        /// <summary>The character is first seen away from the fence, then steps onto it and the playfield ticks.</summary>
        internal void WalkOnto(Character character, Vector3 at)
        {
            Tick();
            character.Position = new Vector3(at.x, at.y, at.z);
            Tick();
        }

        /// <summary>
        /// One playfield tick's worth of what matters here: soft triggers, then the locality grid (NPCs only: the heat
        /// scheduler's player tick wants a full playfield behind the blank one; the player pass reads positions directly).
        /// </summary>
        internal void Tick()
        {
            Simulation.TickSoftTriggers(Playfield, 0.1);
            if (!Registry.HasPlayers())
                Locality.Tick(0.1);
        }

        public void Dispose()
        {
            Simulation.Dispose();
            _services.Dispose();
        }
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

    /// <summary>Answers every call with the default value: for services the spawn path resolves but this fence never uses.</summary>
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
            template = aoid == FenceTemplate ? FenceEvents() : null!;
            return aoid == FenceTemplate;
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
            => lowId == FenceTemplate ? FenceEvents() : new() { Id = lowId, Quality = quality };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }
}
