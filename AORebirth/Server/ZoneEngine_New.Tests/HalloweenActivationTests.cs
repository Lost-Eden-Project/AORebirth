namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

using AORebirth.Core.GameData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SmokeLounge.AOtomation.Messaging.GameData;
using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Configuration;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Mobs;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.Trade;
using ZoneEngine_New.Core.WorldSimulation;

[TestClass]
public sealed class HalloweenActivationTests
{
    const string PrivateDataVariable = "AO_REBIRTH_HALLOWEEN_GAMEDATA_PATH";
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [TestMethod]
    public void MissingEventsDefaultsOffAndExplicitEnabledLoadsFromExistingGameplayConfig()
    {
        using var content = new Content();
        string path = Path.Combine(content.Root, "Gameplay.json");
        var config = JsonSerializer.SerializeToNode(Settings(false))!.AsObject();
        config.Remove("Events");
        File.WriteAllText(path, config.ToJsonString());
        Assert.IsFalse(GameplaySettings.Load(path).Events.Halloween.Enabled);
        config["events"] = new JsonObject { ["halloween"] = new JsonObject { ["enabled"] = true } };
        File.WriteAllText(path, config.ToJsonString());
        Assert.IsTrue(GameplaySettings.Load(path).Events.Halloween.Enabled);
        Assert.IsFalse(new GameplaySettings().Events.Halloween.Enabled);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("{\"halloween\":null}")]
    public void NullSeasonalConfigurationFailsValidation(string eventsJson)
    {
        using var content = new Content();
        string path = Path.Combine(content.Root, "Gameplay.json");
        var config = JsonSerializer.SerializeToNode(Settings(false))!.AsObject();
        config["Events"] = JsonNode.Parse(eventsJson);
        File.WriteAllText(path, config.ToJsonString());
        Assert.ThrowsException<InvalidDataException>(() => GameplaySettings.Load(path));
    }

    [TestMethod]
    [DataRow("halloween_all")]
    [DataRow("halloween2019_all")]
    [DataRow("halloween_uph")]
    [DataRow("halloween_decor")]
    [DataRow("halloween_pvp_uph")]
    [DataRow("halloween_pvp_uph_boss")]
    [DataRow("halloween_quests")]
    [DataRow("halloween_fc_shop")]
    [DataRow("halloween_buttons")]
    public void EveryRecognizedTagIsBlockedOffAndPermittedOnlyWhenItsPlacementIsActive(string tag)
    {
        using var content = new Content(Entry(Event(tag, true)));
        Assert.IsTrue(HashSpawnSystem.IsHalloweenEvent(tag));
        using (var off = new World(content.Root, false))
            Assert.AreEqual(0, off.Points.Count);
        using (var on = new World(content.Root, true))
            Assert.AreEqual(1, on.Points.Count);
        content.Write(Entry(Event(tag, false)));
        using var inactive = new World(content.Root, true);
        Assert.AreEqual(0, inactive.Points.Count, "The global switch must not override an inactive placement.");
    }

    [TestMethod]
    public void OverlappingActiveSeasonalTagsRegisterOnePointAndInitializationIsIdempotent()
    {
        using var content = new Content(Entry(Event("halloween_all", true), Event("halloween_uph", true)));
        byte[] original = File.ReadAllBytes(content.SpawnsPath);
        using var world = new World(content.Root, true);
        Assert.AreEqual(1, world.Points.Count);
        world.System.Initialize(world.Locality);
        Assert.AreEqual(1, world.Points.Count);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(content.SpawnsPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnrelatedInactiveEventStillBlocksAnOverlappingSeasonalPlacement(bool enabled)
    {
        using var content = new Content(Entry(Event("halloween_all", true), Event("winter_fixture", false)));
        using var world = new World(content.Root, enabled);
        Assert.AreEqual(0, world.Points.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OrdinaryEventsAndEmptyEventNamesRetainTheirExistingSemantics(bool enabled)
    {
        using var content = new Content(
            Entry(), Entry(Event("ordinary_fixture", true)), Entry(Event("ordinary_fixture", false)),
            Entry(Event("halloween_future_fixture", true)), Entry(Event("", false)));
        using var world = new World(content.Root, enabled);
        Assert.AreEqual(4, world.Points.Count);
        Assert.IsFalse(HashSpawnSystem.IsHalloweenEvent("halloween_future_fixture"));
        Assert.IsFalse(HashSpawnSystem.IsHalloweenEvent("HALLOWEEN_ALL"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InvalidPlacementRemainsRejectedByExistingValidation(bool enabled)
    {
        var entry = Entry(Event("halloween_all", true));
        using var content = new Content();
        entry.Radius = -1;
        content.Write(entry);
        using var world = new World(content.Root, enabled);
        Assert.AreEqual(0, world.Points.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OrdinaryAaaaDiagnosticPlaceholderRetainsItsTemplateAndRegistration(bool enabled)
    {
        var entry = Entry();
        entry.HashText = "AAAA";
        using var content = new Content(entry);
        using var world = new World(content.Root, enabled);
        Assert.AreEqual(1, world.Points.Count);
        Assert.IsTrue(world.Data.TryResolveMobTemplate("AAAA", 1, out MobTemplate template));
        Assert.IsTrue(template.UnresolvedPlaceholder);
        Assert.IsTrue(template.Has(NpcFeature.NoCombat));
        world.Tick(world.Points[0]);
        var npc = world.Points[0].MembersSnapshot().OfType<NpcCharacter>().Single();
        Assert.IsFalse(npc.Attackable);
        Assert.AreEqual("AAAA", npc.SpawnHash);
    }

    [TestMethod]
    public void ConfigFileEditsTakeEffectOnlyForNewlyLoadedSettingsAndNewSpawnSystem()
    {
        using var content = new Content(Entry(Event("halloween_all", true)));
        string path = Path.Combine(content.Root, "Gameplay.json");
        File.WriteAllText(path, JsonSerializer.Serialize(Settings(false)));
        GameplaySettings loaded = GameplaySettings.Load(path);
        using var running = new World(content.Root, loaded);
        Assert.AreEqual(0, running.Points.Count);
        File.WriteAllText(path, JsonSerializer.Serialize(Settings(true)));
        Assert.IsFalse(loaded.Events.Halloween.Enabled);
        running.System.Initialize(running.Locality);
        Assert.AreEqual(0, running.Points.Count);
        using var restarted = new World(content.Root, GameplaySettings.Load(path));
        Assert.AreEqual(1, restarted.Points.Count);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void EligibleNpcSpawnsSleepsWakesAndRespawnsAfterDespawnAsDeath(bool enabled, bool seasonal)
    {
        using var content = new Content(seasonal ? Entry(Event("halloween_all", true)) : Entry());
        using var world = new World(content.Root, enabled);
        HashSpawnPoint point = world.Points.Single();
        world.Tick(point);
        var first = point.MembersSnapshot().OfType<NpcCharacter>().Single();
        Assert.AreSame(world.Playfield, first.Playfield);
        Assert.IsTrue(world.Registry.TryGet(first.Identity, out var registered));
        Assert.AreSame(first, registered);
        Assert.IsTrue(world.System.TryGetSpawnPoint(first, out var owner));
        Assert.AreSame(point, owner);
        world.Tick(point);
        Assert.AreSame(first, point.MembersSnapshot().Single(), "An occupied point must not duplicate its NPC.");

        Invoke(world.System, "OnCellSleep", point.CellId);
        Assert.AreEqual(0, point.MemberCount);
        Assert.IsNull(first.Playfield);
        Assert.IsFalse(world.Registry.TryGet(first.Identity, out _));
        Assert.AreEqual(HashSpawnState.Alive, point.State);
        world.Tick(point);
        var awakened = point.MembersSnapshot().OfType<NpcCharacter>().Single();
        Assert.AreNotEqual(first.Identity, awakened.Identity);

        awakened.DespawnAsDeath();
        Assert.AreEqual(HashSpawnState.Dead, point.State);
        Assert.AreEqual(0, point.MemberCount);
        Assert.IsNull(awakened.Playfield);
        Assert.IsFalse(world.System.TryGetSpawnPoint(awakened, out _));
        Assert.IsFalse(world.Registry.TryGet(awakened.Identity, out _));
        Assert.IsTrue(point.NextSpawnTime > DateTime.UtcNow);
        world.Tick(point);
        Assert.AreEqual(0, point.MemberCount, "The existing respawn delay must remain effective.");
        point.NextSpawnTime = DateTime.UtcNow.AddSeconds(-1);
        world.Tick(point);
        var respawned = point.MembersSnapshot().OfType<NpcCharacter>().Single();
        Assert.AreNotEqual(awakened.Identity, respawned.Identity);
        Assert.AreSame(world.Playfield, respawned.Playfield);
        Assert.AreEqual(1, world.Registry.Dynels().OfType<NpcCharacter>().Count());
    }

    [TestMethod]
    public void PrivatePf665RetainsExactPopulationWithoutMutationOrDuplicates()
    {
        string? root = Environment.GetEnvironmentVariable(PrivateDataVariable);
        if (string.IsNullOrWhiteSpace(root))
            Assert.Inconclusive("Set " + PrivateDataVariable + " explicitly to validate private GameData; it is never copied into a fixture.");
        string path = Path.Combine(root!, "Playfields", "665", "Spawns.json");
        byte[] before = File.ReadAllBytes(path);
        PlayfieldSpawnsData catalog = JsonSerializer.Deserialize<PlayfieldSpawnsData>(before, JsonOptions)!;
        PlayfieldSpawnEntry[] seasonal = catalog.Spawns.Where(IsSeasonal).ToArray();
        Assert.AreEqual(612, catalog.Spawns.Length);
        Assert.AreEqual(93, seasonal.Length);
        Assert.AreEqual(73, seasonal.Count(entry => entry.HashText == "UNPU"));
        Assert.AreEqual(10, seasonal.Count(entry => entry.HashText == "GRUC"));
        Assert.AreEqual(10, seasonal.Count(entry => entry.HashText == "PPOA"));
        Assert.AreEqual(83, seasonal.Count(entry => entry.Extensions.Events.All(item => item.Active)));
        Assert.IsTrue(seasonal.Where(entry => entry.HashText == "PPOA").All(entry => entry.Extensions.Events.All(item => !item.Active)));
        Assert.AreEqual(93, seasonal.Select(PlacementIdentity).Distinct(StringComparer.Ordinal).Count());

        using (var off = new World(root!, false, 665))
            Assert.AreEqual(0, off.Points.Count(point => IsSeasonal(point.Source)));
        using (var on = new World(root!, true, 665))
        {
            HashSpawnPoint[] eligible = on.Points.Where(point => IsSeasonal(point.Source)).ToArray();
            Assert.AreEqual(83, eligible.Length);
            Assert.AreEqual(73, eligible.Count(point => point.HashText == "UNPU"));
            Assert.AreEqual(10, eligible.Count(point => point.HashText == "GRUC"));
            Assert.AreEqual(0, eligible.Count(point => point.HashText == "PPOA"));
            Assert.AreEqual(83, eligible.Select(point => PlacementIdentity(point.Source)).Distinct(StringComparer.Ordinal).Count());
        }
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }

    static bool IsSeasonal(PlayfieldSpawnEntry entry) => entry.Extensions?.Events?.Any(item => item != null && HashSpawnSystem.IsHalloweenEvent(item.Name)) == true;
    static string PlacementIdentity(PlayfieldSpawnEntry entry) => JsonSerializer.Serialize(new { entry.DistrictIndex, entry.Hash, entry.HashText, entry.Position, entry.MinLevel, entry.MaxLevel });
    static GameplaySettings Settings(bool enabled) => new()
    {
        StuckLocation = new GameplayLocation { Playfield = 566, X = 1, Y = 2, Z = 3 },
        Events = new GameplayEvents { Halloween = new GameplayHalloween { Enabled = enabled } }
    };
    static PlayfieldHashSpawnExtensionEvent Event(string name, bool active) => new() { Name = name, Active = active };
    static PlayfieldSpawnEntry Entry(params PlayfieldHashSpawnExtensionEvent[] events) => new()
    {
        HashText = "TEST", Position = [4, 5, 6], Radius = 0, MinLevel = 1, MaxLevel = 1,
        RespawnChance = 100, RespawnTime = 30, Extensions = new() { Events = events }
    };
    static T Blank<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    static void Field(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    static void Invoke(object target, string name, object argument) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, [argument]);

    sealed class Content : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "aorebirth-halloween-test-" + Guid.NewGuid().ToString("N"));
        internal string SpawnsPath => Path.Combine(Root, "Playfields", "665", "Spawns.json");
        internal Content(params PlayfieldSpawnEntry[] entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SpawnsPath)!);
            File.WriteAllText(Path.Combine(Root, "NpcTemplates.json"), "{\"TEST\":{\"Templates\":[{\"Name\":\"Activation fixture\",\"Level\":1,\"Stats\":{\"54\":1,\"1\":100},\"Features\":[\"NoCombat\"]}]},\"AAAA\":{\"Templates\":[{\"Name\":\"Diagnostic fixture\",\"Level\":1,\"Stats\":{\"54\":1}}]}}");
            Write(entries);
        }
        internal void Write(params PlayfieldSpawnEntry[] entries) => File.WriteAllText(SpawnsPath, JsonSerializer.Serialize(new PlayfieldSpawnsData { SchemaVersion = 1, PlayfieldId = 665, Spawns = entries }));
        public void Dispose() => Directory.Delete(Root, true);
    }

    internal sealed class World : IDisposable
    {
        readonly ServiceProvider _services;
        internal Playfield Playfield { get; } = Blank<Playfield>();
        internal DynelRegistry Registry { get; } = new();
        internal PlayfieldLocality Locality { get; }
        internal HashSpawnSystem System { get; }
        internal GameDataStore Data { get; }
        internal SpawnService Spawns { get; }
        internal List<HashSpawnPoint> Points => (List<HashSpawnPoint>)typeof(HashSpawnSystem).GetField("_allPoints", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(System)!;
        internal World(string root, bool enabled, int id = 665, IItemTemplateCatalog? itemCatalog = null, IItemBuilder? itemBuilder = null)
            : this(root, Settings(enabled), id, itemCatalog, itemBuilder) { }
        internal World(string root, GameplaySettings settings, int id = 665, IItemTemplateCatalog? itemCatalog = null, IItemBuilder? itemBuilder = null)
        {
            Field(Playfield, "<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = id });
            Field(Playfield, "_pendingStatRebases", new ConcurrentDictionary<Character, byte>());
            Field(Playfield, "_lifecycleSync", new Lock());
            Data = new GameDataStore(new StubLogger(), null, root);
            Locality = new PlayfieldLocality(id, Data.GetPlayfieldMetaData(id));
            IItemBuilder items = itemBuilder ?? new StubItemBuilder();
            IItemTemplateCatalog catalog = itemCatalog ?? new StubCatalog();
            var minter = new HashItemMinter(Data, catalog, items);
            var moves = new InventoryMoveService(new StubLogger(), Blank<InventoryFlushService>(), Blank<InventoryActionService>());
            var activation = new NpcContentActivationService(Playfield, Registry, Locality, items, catalog, Data);
            _services = new ServiceCollection()
                .AddSingleton(Registry).AddSingleton(Locality).AddSingleton<IGameData>(Data)
                .AddSingleton<IInventoryRepository>(new StubInventoryRepository())
                .AddSingleton<IItemBuilder>(items).AddSingleton<IItemTemplateCatalog>(catalog).AddSingleton(new WorldSimulationAccess())
                .AddSingleton(moves).AddSingleton(activation).AddSingleton(settings)
                .BuildServiceProvider();
            Field(Playfield, "_serviceProvider", _services);
            Spawns = new SpawnService(_services, Registry, new StubLogger(), Playfield, Blank<PlayfieldManager>(),
                Data, items, minter, Blank<InventoryFlushService>(), Blank<TradeService>(), Blank<CharacterSnapshotService>());
            System = new HashSpawnSystem(Playfield, Spawns, Data, new StubLogger(), settings);
            System.Initialize(Locality);
        }
        internal void Tick(HashSpawnPoint point) => Invoke(System, "OnCellTick", point.CellId);
        public void Dispose() => _services.Dispose();
    }
}
