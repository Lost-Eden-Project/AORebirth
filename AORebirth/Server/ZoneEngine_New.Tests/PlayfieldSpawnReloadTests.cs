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
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Mobs;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.Trade;
using ZoneEngine_New.Core.WorldSimulation;

using SpawnReload = AORebirth.Modules.Content.PlayfieldSpawnReload;

/// <summary>
/// Staff spawn edits (the website's playfield editor writes Spawns.json and Districts.json into the GameData override
/// folder) reach a running playfield without tearing it down: unchanged points keep their NPCs, removed and changed
/// points' NPCs leave, added points spawn. No reload may duplicate an NPC or bring back one that was just killed.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PlayfieldSpawnReloadTests
{
    static readonly float[] KeepAt = [4, 5, 6];
    static readonly float[] GoneAt = [8, 5, 9];
    static readonly float[] EditAt = [12, 5, 3];
    static readonly float[] AddAt = [6, 5, 12];

    [TestMethod]
    public void UnchangedPointsKeepTheirNpcsRemovedAndChangedOnesLeaveAndAddedOnesSpawn()
    {
        using var content = new Content();
        content.WriteSpawns(Entry("KEEP", KeepAt), Entry("GONE", GoneAt), Entry("EDIT", EditAt));
        using var world = new World(content.Root);
        world.SpawnAll();
        NpcCharacter keep = world.Npc("KEEP");
        NpcCharacter gone = world.Npc("GONE");
        NpcCharacter edited = world.Npc("EDIT");

        content.WriteSpawns(Entry("KEEP", KeepAt), Entry("EDIT", EditAt, respawnTime: 900), Entry("NEWP", AddAt));
        SpawnReload result = world.Reload();

        Assert.IsTrue(result.Applied, string.Join("; ", result.Problems));
        Assert.AreEqual((1, 1, 1, 1, 2), (result.Kept, result.Added, result.Changed, result.Removed, result.Despawned));
        Assert.AreSame(keep, world.Npc("KEEP"), "An unchanged point keeps its NPC.");
        Assert.IsTrue(world.Registry.TryGet(keep.Identity, out _));
        Assert.IsFalse(world.Registry.TryGet(gone.Identity, out _), "A removed point's NPC leaves the world.");
        Assert.IsFalse(world.Registry.TryGet(edited.Identity, out _), "A changed point's old NPC leaves the world.");
        Assert.IsFalse(world.Points.Any(point => point.HashText == "GONE"));

        world.SpawnAll();
        Assert.AreEqual(900, world.Point("EDIT").RespawnTimeSeconds);
        Assert.AreNotEqual(edited.Identity, world.Npc("EDIT").Identity);
        world.Npc("NEWP");
        Assert.AreEqual(3, world.LivingNpcs(), "One NPC per point, none duplicated.");
        CollectionAssert.AreEquivalent(new[] { "KEEP", "EDIT", "NEWP" }, world.Points.Select(point => point.HashText).ToArray());
    }

    [TestMethod]
    public void ReloadingUnchangedFilesNeitherDuplicatesNorRespawnsAKilledNpc()
    {
        using var content = new Content();
        content.WriteSpawns(Entry("KEEP", KeepAt), Entry("GONE", GoneAt));
        using var world = new World(content.Root);
        world.SpawnAll();
        NpcCharacter alive = world.Npc("GONE");
        world.Npc("KEEP").DespawnAsDeath();
        HashSpawnPoint killed = world.Point("KEEP");
        DateTime timer = killed.NextSpawnTime;
        Assert.AreEqual(HashSpawnState.Dead, killed.State);

        for (int i = 0; i < 3; i++)
        {
            SpawnReload result = world.Reload();
            Assert.AreEqual((2, 0, 0, 0, 0), (result.Kept, result.Added, result.Changed, result.Removed, result.Despawned));
        }

        world.SpawnAll();
        Assert.AreSame(killed, world.Point("KEEP"));
        Assert.AreEqual(HashSpawnState.Dead, killed.State);
        Assert.AreEqual(timer, killed.NextSpawnTime, "A reload must not reset a killed NPC's respawn timer.");
        Assert.AreEqual(0, killed.MemberCount, "A reload must not respawn a killed NPC.");
        Assert.AreSame(alive, world.Npc("GONE"));
        Assert.AreEqual(1, world.LivingNpcs());
    }

    [TestMethod]
    public void EditingAKilledPointKeepsItWaitingAtMostItsNewRespawnTime()
    {
        using var content = new Content();
        content.WriteSpawns(Entry("EDIT", EditAt, respawnTime: 600));
        using var world = new World(content.Root);
        world.SpawnAll();
        world.Npc("EDIT").DespawnAsDeath();

        content.WriteSpawns(Entry("EDIT", EditAt, respawnTime: 300));
        DateTime before = DateTime.UtcNow;
        SpawnReload result = world.Reload();
        world.SpawnAll();

        Assert.AreEqual(1, result.Changed);
        HashSpawnPoint replaced = world.Point("EDIT");
        Assert.AreEqual(HashSpawnState.Dead, replaced.State);
        Assert.IsTrue(replaced.NextSpawnTime > before.AddSeconds(250) && replaced.NextSpawnTime <= DateTime.UtcNow.AddSeconds(300),
            "The new point waits the old timer, capped at its new respawn time: " + replaced.NextSpawnTime.ToString("o"));
        Assert.AreEqual(0, world.LivingNpcs(), "Editing a just-killed point must not bring its NPC back.");
    }

    [TestMethod]
    public void UnchangedDistrictKeepsItsNpcsAndAChangedDistrictIsReplaced()
    {
        using var content = new Content();
        content.WriteSpawns();
        content.WriteDistricts(District(0, "DIST", 2, KeepAt, GoneAt), District(1, "EDIT", 1, EditAt));
        using var world = new World(content.Root);
        world.SpawnAll();
        HashSpawnPoint[] stayed = world.Points.Where(point => point.HashText == "DIST").ToArray();
        NpcCharacter[] stayedNpcs = stayed.Select(point => point.MembersSnapshot().OfType<NpcCharacter>().Single()).ToArray();
        NpcCharacter oldEdit = world.Npc("EDIT");

        content.WriteDistricts(District(0, "DIST", 2, KeepAt, GoneAt), District(1, "EDIT", 2, EditAt, AddAt));
        SpawnReload result = world.Reload();
        world.SpawnAll();

        Assert.AreEqual((2, 2, 1, 1), (result.Kept, result.Added, result.Removed, result.Despawned));
        CollectionAssert.AreEqual(stayed, world.Points.Where(point => point.HashText == "DIST").ToArray());
        CollectionAssert.AreEqual(stayedNpcs,
            stayed.Select(point => point.MembersSnapshot().OfType<NpcCharacter>().Single()).ToArray());
        Assert.IsFalse(world.Registry.TryGet(oldEdit.Identity, out _));
        Assert.AreEqual(2, world.Points.Count(point => point.HashText == "EDIT"));
        Assert.AreEqual(4, world.LivingNpcs());
    }

    [TestMethod]
    public void OverrideFolderSpawnsAreLoadedAndReloadedOverTheShippedFile()
    {
        using var content = new Content();
        content.WriteSpawns(Entry("KEEP", KeepAt));
        string overrides = Path.Combine(content.Root, "overrides");
        string previous = Environment.GetEnvironmentVariable(GameDataPaths.OverrideEnvironmentVariableName);
        try
        {
            Environment.SetEnvironmentVariable(GameDataPaths.OverrideEnvironmentVariableName, overrides);
            content.WriteSpawns(overrides, Entry("KEEP", KeepAt), Entry("NEWP", AddAt));
            using var world = new World(content.Root);
            world.SpawnAll();
            world.Npc("NEWP");

            // The staff overlay gains the editor's point while the playfield runs.
            content.WriteSpawns(overrides, Entry("KEEP", KeepAt), Entry("NEWP", AddAt), Entry("EDIT", EditAt));
            SpawnReload result = world.Reload();
            world.SpawnAll();

            Assert.AreEqual((2, 1), (result.Kept, result.Added));
            world.Npc("EDIT");
            Assert.AreEqual(3, world.LivingNpcs());
        }
        finally
        {
            Environment.SetEnvironmentVariable(GameDataPaths.OverrideEnvironmentVariableName, previous);
        }
    }

    [TestMethod]
    public void AnEntryThatCannotSpawnIsReportedOnceAndTheRestStillApplies()
    {
        using var content = new Content();
        content.WriteSpawns(Entry("KEEP", KeepAt));
        using var world = new World(content.Root);
        world.SpawnAll();

        content.WriteSpawns(Entry("KEEP", KeepAt), Entry("NOPE", AddAt, level: 115), Entry("NEWP", GoneAt));
        SpawnReload first = world.Reload();
        SpawnReload second = world.Reload();

        Assert.IsTrue(first.Applied);
        Assert.AreEqual(1, first.Added);
        Assert.AreEqual(1, first.Problems.Count, string.Join("; ", first.Problems));
        StringAssert.StartsWith(first.Problems[0], "skipped NOPE at (6.0, 5.0, 12.0): no NpcTemplates entry");
        Assert.AreEqual(0, second.Problems.Count, "A skip already reported is not reported again.");
    }

    [TestMethod]
    public void ADungeonThatPlacesItsOwnPointsIsNotReloaded()
    {
        using var content = new Content();
        content.WriteSpawns(Entry("KEEP", KeepAt));
        using var world = new World(content.Root);
        Assert.IsTrue(world.System.AddPlacedPoint("NEWP", new AORebirth.Core.Vector.Vector3(6f, 5f, 12f), 0, null, 1));
        world.SpawnAll();
        int before = world.LivingNpcs();

        content.WriteSpawns();
        SpawnReload result = world.Reload();

        Assert.IsFalse(result.Applied);
        Assert.IsFalse(world.System.IsReloadable);
        Assert.AreEqual(before, world.LivingNpcs());
    }

    static PlayfieldSpawnEntry Entry(string hash, float[] position, int respawnTime = 600, int level = 1) => new()
    {
        HashText = hash, Position = position, Radius = 0, MinLevel = level, MaxLevel = level,
        RespawnChance = 100, RespawnTime = respawnTime
    };

    static PlayfieldDistrictEntry District(int index, string hash, int count, params float[][] locations) => new()
    {
        DistrictIndex = index, NpcMinLevel = 1, NpcMaxLevel = 1, RespawnChance = 100, RespawnTime = 10,
        SpawnInfos = [new PlayfieldDistrictSpawnInfo { HashText = hash, Count = count }],
        SpawnPoints = locations.Select(position => new PlayfieldDistrictSpawnPoint { Position = position }).ToArray()
    };

    static T Blank<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    static void Field(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    sealed class Content : IDisposable
    {
        internal const int PlayfieldId = 665;

        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "aorebirth-spawn-reload-test-" + Guid.NewGuid().ToString("N"));

        internal Content()
        {
            Directory.CreateDirectory(Root);
            string templates = string.Join(",", new[] { "KEEP", "GONE", "EDIT", "NEWP", "DIST" }.Select(hash =>
                "\"" + hash + "\":{\"Templates\":[{\"Name\":\"" + hash + " fixture\",\"Level\":1,\"Stats\":{\"54\":1,\"1\":100},\"Features\":[\"NoCombat\"]}]}"));
            File.WriteAllText(Path.Combine(Root, "NpcTemplates.json"), "{" + templates + "}");
            File.WriteAllText(Path.Combine(Root, MonsterCombatRulesCatalog.FileName), "{}");
        }

        internal void WriteSpawns(params PlayfieldSpawnEntry[] entries) => WriteSpawns(Root, entries);

        internal void WriteSpawns(string root, params PlayfieldSpawnEntry[] entries)
            => Write(root, GameDataPaths.PlayfieldSpawnsRelativePath(PlayfieldId), JsonSerializer.Serialize(new PlayfieldSpawnsData
            {
                SchemaVersion = 1, PlayfieldId = PlayfieldId, Spawns = entries
            }));

        internal void WriteDistricts(params PlayfieldDistrictEntry[] districts)
            => Write(Root, GameDataPaths.PlayfieldDistrictsRelativePath(PlayfieldId),
                JsonSerializer.Serialize(new PlayfieldDistrictsData { SchemaVersion = 1, Districts = districts }));

        static void Write(string root, string relative, string json)
        {
            string path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }

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

        internal HashSpawnPoint Point(string hash) => Points.Single(point => point.HashText == hash);

        internal NpcCharacter Npc(string hash) => Point(hash).MembersSnapshot().OfType<NpcCharacter>().Single();

        internal int LivingNpcs() => Registry.Dynels().OfType<NpcCharacter>().Count(npc => !npc.IsDead);

        /// <summary>Wakes every spawn cell a few times, as cell heat does with a player near.</summary>
        internal void SpawnAll()
        {
            MethodInfo tick = typeof(HashSpawnSystem).GetMethod("OnCellTick", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (int i = 0; i < 4; i++)
            {
                foreach (int cell in Points.Select(point => point.CellId).Distinct().ToArray())
                    tick.Invoke(System, [cell]);
            }
        }

        /// <summary>What PlayfieldManager does: the files read off the tick, then applied.</summary>
        internal SpawnReload Reload()
        {
            PlayfieldDistrictsData? districts = HashSpawnSystem.ReadDistricts(Data.RootPath, Content.PlayfieldId);
            PlayfieldSpawnsData spawns = Data.ReloadPlayfieldSpawns(Content.PlayfieldId);
            return System.ApplyReload(System.PrepareReload(spawns, districts));
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
