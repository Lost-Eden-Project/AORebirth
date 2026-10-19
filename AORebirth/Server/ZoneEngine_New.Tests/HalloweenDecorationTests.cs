namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

using AORebirth.Core.GameData;
using AORebirth.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SmokeLounge.AOtomation.Messaging.GameData;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
using SmokeLounge.AOtomation.Messaging.Serialization;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;

using Vector3 = AORebirth.Core.Vector.Vector3;

[TestClass]
public sealed class HalloweenDecorationTests
{
    const string PrivateDataVariable = "AO_REBIRTH_HALLOWEEN_GAMEDATA_PATH";
    static readonly int[] DecorationTemplates = [301382, 301383, 301384, 301385, 301386, 301387];
    static readonly Dictionary<string, int> DecorationTemplateByHash = new(StringComparer.Ordinal)
    {
        ["AUPU"] = 301382, ["PJUQ"] = 301383, ["CXNP"] = 301384,
        ["BRSK"] = 301385, ["SLHD"] = 301386, ["KELE"] = 301387
    };
    public TestContext TestContext { get; set; } = null!;
    static readonly Lazy<ItemTemplateCatalog> RealCatalog = new(() =>
    {
        string? root = Environment.GetEnvironmentVariable(PrivateDataVariable);
        if (string.IsNullOrWhiteSpace(root))
            Assert.Inconclusive("Set " + PrivateDataVariable + " explicitly; private item data is never copied into a fixture.");
        Assert.IsTrue(File.Exists(Path.Combine(root!, "items.dat")), "The selected private items.dat must exist.");
        return new ItemTemplateCatalog(new StubGameData(HashItemCatalog.Parse("{}"), rootPath: root), new StubLogger());
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OrdinaryHashWorldItemPublishesAndSleepsWithoutSeasonalEffects(bool enabled)
    {
        const int id = 1200;
        using var content = new Content([id], seasonal: false);
        var template = new ItemTemplate { Id = id, Name = "Ordinary fixture", Quality = 1 };
        var catalog = new StubCatalog().Add(id, 1);
        var items = new StubItemBuilder().Add(template);
        using var world = new HalloweenActivationTests.World(content.Root, enabled, itemCatalog: catalog, itemBuilder: items);
        Assert.AreEqual(1, world.Points.Count);
        Assert.IsTrue(world.Points[0].IsStatic);
        Session session = Observe(world);
        world.Tick(world.Points[0]);
        var item = (WorldItem)world.Points[0].MembersSnapshot().Single();
        Assert.AreEqual(SpawnSource.HashSpawn, item.SpawnSource);
        AssertPublished(item, session);
        world.Locality.ActivatePlayerVisibility(session.Player!);
        Assert.AreEqual(1, session.Bodies.OfType<SimpleItemFullUpdateMessage>().Count());
        Sleep(world, world.Points[0]);
        Assert.IsNull(item.Playfield);
        Assert.IsTrue(session.Bodies.OfType<DespawnMessage>().Any(body => body.Identity == item.Identity));
        world.Tick(world.Points[0]);
        var replacement = (WorldItem)world.Points[0].MembersSnapshot().Single();
        Assert.AreNotEqual(item.Identity, replacement.Identity);
        AssertPublished(replacement, session);
        Assert.AreEqual(2, session.Bodies.OfType<SimpleItemFullUpdateMessage>().Count());
    }

    [TestMethod]
    public void ClientResidentStaticKeepsExistingNoSpawnNoDespawnVisibilityBehavior()
    {
        using var content = new Content([]);
        using var world = new HalloweenActivationTests.World(content.Root, false);
        var resident = new PlayfieldStaticDynel(new Identity { Type = IdentityType.Terminal, Instance = 77 },
            new ItemTemplate { Id = 1200, Name = "Client resident fixture" })
        {
            Playfield = world.Playfield, Position = new Vector3(41, 8, 93), SpawnSource = SpawnSource.StaticDynel
        };
        world.Locality.RegisterDynel(resident);
        Session session = Observe(world);
        world.Locality.ActivatePlayerVisibility(session.Player!);
        Assert.AreEqual(0, session.Bodies.OfType<SimpleItemFullUpdateMessage>().Count());
        world.Locality.UnregisterDynel(resident);
        Assert.AreEqual(0, session.Bodies.OfType<DespawnMessage>().Count());
    }

    [TestMethod]
    public void ExplicitSeasonalBindingUsesItsExactTemplateWithoutGlobalHashAlias()
    {
        const int id = 1200;
        using var content = new Content([id], explicitBindings: true);
        var catalog = new StubCatalog().Add(id, 1);
        var items = new StubItemBuilder().Add(new ItemTemplate { Id = id, Name = "Bound fixture", Quality = 1 });
        using (var off = new HalloweenActivationTests.World(content.Root, false, itemCatalog: catalog, itemBuilder: items))
            Assert.AreEqual(0, off.Points.Count);
        using var on = new HalloweenActivationTests.World(content.Root, true, itemCatalog: catalog, itemBuilder: items);
        HashSpawnPoint point = on.Points.Single();
        Assert.IsTrue(point.IsStatic);
        Assert.AreEqual(id, point.Source.StaticTemplateId);
        Assert.IsFalse(on.Data.CanResolveItemHash(point.HashText));
        Session session = Observe(on);
        on.Tick(point);
        var item = (WorldItem)point.MembersSnapshot().Single();
        Assert.AreEqual(id, item.LowId);
        Assert.AreEqual(id, item.HighId);
        Assert.AreEqual(0, on.Registry.Dynels().OfType<NpcCharacter>().Count());
        AssertPublished(item, session);
    }

    [TestMethod]
    public void MissingExplicitStaticTemplateFailsClosedInsteadOfSpawningAnotherHashOrPlaceholder()
    {
        using var content = new Content([1200], explicitBindings: true);
        File.WriteAllText(Path.Combine(content.Root, "NpcTemplates.json"),
            "{\"AAAA\":{\"Templates\":[{\"Name\":\"Diagnostic fixture\",\"Level\":1,\"Stats\":{\"54\":1}}]}}");
        using var world = new HalloweenActivationTests.World(content.Root, true);
        Assert.AreEqual(0, world.Points.Count);
        Assert.AreEqual(0, world.Registry.Dynels().Count());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void NonpositiveExplicitStaticTemplateBindingIsRejected(int id)
    {
        using var content = new Content([id], explicitBindings: true);
        using var world = new HalloweenActivationTests.World(content.Root, true);
        Assert.AreEqual(0, world.Points.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExplicitBindingStillRespectsInactivePlacementAndUnrelatedEvent(bool unrelated)
    {
        using var content = new Content([1200], explicitBindings: true);
        string path = Path.Combine(content.Root, "Playfields", "665", "Spawns.json");
        var data = JsonSerializer.Deserialize<PlayfieldSpawnsData>(File.ReadAllText(path))!;
        if (unrelated)
        {
            data.Spawns[0].Extensions.Events = [new() { Name = "halloween_decor", Active = true }, new() { Name = "other_fixture", Active = false }];
        }
        else
        {
            data.Spawns[0].Extensions.Events[0].Active = false;
        }
        File.WriteAllText(path, JsonSerializer.Serialize(data));
        using var world = new HalloweenActivationTests.World(content.Root, true,
            itemCatalog: new StubCatalog().Add(1200, 1));
        Assert.AreEqual(0, world.Points.Count);
    }

    [TestMethod]
    public void ExplicitBindingDoesNotPropagateToAnUnboundPlacementUsingTheSameHash()
    {
        using var content = new Content([1200], explicitBindings: true);
        File.WriteAllText(Path.Combine(content.Root, "NpcTemplates.json"),
            "{\"AAAA\":{\"Templates\":[{\"Name\":\"Diagnostic fixture\",\"Level\":1,\"Stats\":{\"54\":1}}]}}");
        string path = Path.Combine(content.Root, "Playfields", "665", "Spawns.json");
        var data = JsonSerializer.Deserialize<PlayfieldSpawnsData>(File.ReadAllText(path))!;
        PlayfieldSpawnEntry unbound = JsonSerializer.Deserialize<PlayfieldSpawnEntry>(JsonSerializer.Serialize(data.Spawns[0]))!;
        unbound.StaticTemplateId = null;
        unbound.Position = [42, 8, 93];
        data.Spawns = [data.Spawns[0], unbound];
        File.WriteAllText(path, JsonSerializer.Serialize(data));
        using var world = new HalloweenActivationTests.World(content.Root, true,
            itemCatalog: new StubCatalog().Add(1200, 1),
            itemBuilder: new StubItemBuilder().Add(new ItemTemplate { Id = 1200, Quality = 1, Name = "Bound fixture" }));
        Assert.AreEqual(2, world.Points.Count);
        Assert.AreEqual(1, world.Points.Count(point => point.IsStatic));
        SpawnEveryPoint(world);
        Assert.AreEqual(1, world.Registry.Dynels().OfType<WorldItem>().Count());
        NpcCharacter placeholder = world.Registry.Dynels().OfType<NpcCharacter>().Single();
        Assert.IsTrue(placeholder.MobTemplate!.UnresolvedPlaceholder);
        Assert.IsFalse(placeholder.Attackable);
    }

    [TestMethod]
    [DataRow(540, 31)]
    [DataRow(566, 28)]
    [DataRow(655, 30)]
    [DataRow(710, 39)]
    [DataRow(800, 23)]
    [DataRow(6300, 10)]
    [DataRow(6301, 10)]
    [DataRow(6302, 10)]
    [DataRow(6303, 10)]
    [DataRow(6304, 10)]
    [DataRow(6305, 10)]
    [DataRow(6306, 10)]
    public void ActualPrivateBoundDecorationsRespectGlobalSwitchAndSpawnOnlyTheirAuthoredTemplates(int playfieldId, int expected)
    {
        ItemTemplateCatalog catalog = RealCatalog.Value;
        string root = Environment.GetEnvironmentVariable(PrivateDataVariable)!;
        string path = Path.Combine(root, "Playfields", playfieldId.ToString(), "Spawns.json");
        byte[] original = File.ReadAllBytes(path);
        PlayfieldSpawnsData content = JsonSerializer.Deserialize<PlayfieldSpawnsData>(original,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        PlayfieldSpawnEntry[] bound = content.Spawns.Where(entry => entry.StaticTemplateId.HasValue).ToArray();
        Assert.AreEqual(expected, bound.Length);
        Assert.IsTrue(bound.All(entry => DecorationTemplates.Contains(entry.StaticTemplateId!.Value)));
        Assert.IsTrue(bound.All(entry => entry.Extensions.Events.Any(item => HashSpawnSystem.IsHalloweenEvent(item.Name))));
        Assert.IsTrue(bound.All(entry => entry.Extensions.Events.All(item => item.Active)));
        var items = new ItemBuilder(catalog, new StubLogger());
        using (var off = new HalloweenActivationTests.World(root, false, playfieldId, catalog, items))
            Assert.AreEqual(0, off.Points.Count(point => point.Source.StaticTemplateId.HasValue));
        using var on = new HalloweenActivationTests.World(root, true, playfieldId, catalog, items);
        HashSpawnPoint[] points = on.Points.Where(point => point.Source.StaticTemplateId.HasValue).ToArray();
        Assert.AreEqual(expected, points.Length);
        Assert.AreEqual(expected, points.Select(point => PlacementIdentity(point.Source)).Distinct(StringComparer.Ordinal).Count());
        foreach (HashSpawnPoint point in points)
        {
            Assert.IsTrue(point.IsStatic);
            Assert.IsFalse(on.Data.CanResolveItemHash(point.HashText), "A placement binding must not add a global alias: " + point.HashText);
            // Skip only the data's probabilistic chance/timer; exercise the real static branch and registration.
            point.State = HashSpawnState.Alive;
            Assert.IsTrue((bool)typeof(HashSpawnSystem).GetMethod("TrySpawn", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(on.System, [point])!, PlacementIdentity(point.Source));
            var item = (WorldItem)point.MembersSnapshot().Single();
            Assert.AreEqual(point.Source.StaticTemplateId!.Value, item.LowId);
            Assert.AreEqual(point.Source.StaticTemplateId.Value, item.HighId);
            Assert.AreSame(on.Playfield, item.Playfield);
            Assert.AreEqual(SpawnSource.HashSpawn, item.SpawnSource);
            Assert.IsTrue(on.Registry.TryGet(item.Identity, out Dynel? registered));
            Assert.AreSame(item, registered);
            AssertSpawnBody(item, (SimpleItemFullUpdateMessage)item.BuildSpawnMessage());
        }
        Assert.AreEqual(expected, on.Registry.Dynels().OfType<WorldItem>().Count());
        Assert.AreEqual(0, on.Registry.Dynels().OfType<NpcCharacter>().Count());

        // One actual placement per playfield exercises nearby publication, wire codec and sleep/wake.
        HashSpawnPoint observedPoint = points[0];
        var observed = (WorldItem)observedPoint.MembersSnapshot().Single();
        Session session = Observe(on, observed.Position);
        AssertPublished(observed, session);
        on.Locality.ActivatePlayerVisibility(session.Player!);
        Assert.AreEqual(1, session.Bodies.OfType<SimpleItemFullUpdateMessage>().Count(body => body.Identity == observed.Identity));
        Sleep(on, observedPoint);
        Assert.AreEqual(0, observedPoint.MemberCount);
        Assert.IsNull(observed.Playfield);
        Assert.IsFalse(on.Registry.TryGet(observed.Identity, out _));
        Assert.IsTrue(session.Bodies.OfType<DespawnMessage>().Any(body => body.Identity == observed.Identity));
        Assert.AreEqual(HashSpawnState.Alive, observedPoint.State);
        Assert.IsTrue((bool)typeof(HashSpawnSystem).GetMethod("TrySpawn", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(on.System, [observedPoint])!);
        var awakened = (WorldItem)observedPoint.MembersSnapshot().Single();
        Assert.AreNotEqual(observed.Identity, awakened.Identity);
        Assert.AreEqual(observed.LowId, awakened.LowId);
        Assert.AreEqual(observed.HighId, awakened.HighId);
        AssertPublished(awakened, session);
        on.Locality.ActivatePlayerVisibility(session.Player!);
        Assert.AreEqual(1, session.Bodies.OfType<SimpleItemFullUpdateMessage>().Count(body => body.Identity == awakened.Identity));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        TestContext.WriteLine("Actual private PF{0}: explicit seasonal bindings={1}, eligible ON={1}, eligible OFF=0; all registered/body-validated, nearby wire and sleep/wake validated for one placement; no global aliases or file writes.", playfieldId, expected);
    }

    [TestMethod]
    public void ActualPrivateGlobalDecorationInventoryHasAllAuthoredBindingsAndKeepsPpoaAndAaaaSeparate()
    {
        _ = RealCatalog.Value;
        string root = Environment.GetEnvironmentVariable(PrivateDataVariable)!;
        var selected = new List<PlayfieldSpawnEntry>();
        int decorationPlacements = 0;
        int ppoa = 0;
        int aaaa = 0;
        var playfields = new Dictionary<int, int>();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "Playfields"), "Spawns.json", SearchOption.AllDirectories))
        {
            var content = JsonSerializer.Deserialize<PlayfieldSpawnsData>(File.ReadAllBytes(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            foreach (PlayfieldSpawnEntry entry in content.Spawns)
            {
                if (entry.HashText == "AAAA")
                {
                    aaaa++;
                    Assert.IsNull(entry.StaticTemplateId, "Diagnostic AAAA placements must not acquire decoration bindings.");
                }
                bool seasonal = entry.Extensions?.Events?.Any(item => item != null && HashSpawnSystem.IsHalloweenEvent(item.Name)) == true;
                if (!seasonal)
                    continue;
                if (entry.Extensions.Events.Any(item => item.Name == "halloween_decor"))
                {
                    decorationPlacements++;
                }
                if (entry.HashText == "PPOA")
                {
                    ppoa++;
                    Assert.IsNull(entry.StaticTemplateId);
                    Assert.IsTrue(entry.Extensions.Events.All(item => !item.Active), "Unsupported PPOA remains inactive.");
                }
                if (!DecorationTemplateByHash.TryGetValue(entry.HashText, out int templateId))
                    continue;
                selected.Add(entry);
                Assert.AreEqual(templateId, entry.StaticTemplateId!.Value);
                Assert.IsTrue(entry.Extensions.Events.All(item => item.Active));
                playfields[content.PlayfieldId] = playfields.GetValueOrDefault(content.PlayfieldId) + 1;
            }
        }
        Assert.AreEqual(221, selected.Count);
        Assert.AreEqual(316, decorationPlacements);
        Assert.AreEqual(12, playfields.Count);
        CollectionAssert.AreEquivalent(new[] { 31, 28, 30, 39, 23, 10, 10, 10, 10, 10, 10, 10 }, playfields.Values.ToArray());
        var expectedHashes = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["AUPU"] = 98, ["KELE"] = 48, ["BRSK"] = 20,
            ["SLHD"] = 46, ["CXNP"] = 5, ["PJUQ"] = 4
        };
        foreach ((string hash, int count) in expectedHashes)
            Assert.AreEqual(count, selected.Count(entry => entry.HashText == hash));
        Assert.AreEqual(95, ppoa, "The unsupported PPOA cohort must remain present, inactive and unbound.");
        TestContext.WriteLine("Actual private global inventory: 221 authored decorations in 12 playfields; unsupported PPOA={0} inactive/unbound; AAAA={1} retain diagnostic routing.", ppoa, aaaa);
    }

    [TestMethod]
    public void SixActualPrivateDecorationTemplatesUseExistingHashItemSpawnAndWirePath()
    {
        ItemTemplateCatalog catalog = RealCatalog.Value;
        foreach (int id in DecorationTemplates)
        {
            Assert.IsTrue(catalog.TryGet(id, out ItemTemplate template), "Actual private template missing: " + id);
            Assert.IsFalse(string.IsNullOrWhiteSpace(template.Name), "Actual item name missing: " + id);
            Assert.IsTrue(template.Stats.TryGetValue(CharacterStat.Mesh, out int mesh) && mesh > 0,
                "Actual decoration must have its authored mesh: " + id);
            TestContext.WriteLine("Actual private catalog template={0} name={1} mesh={2} ql={3}", id, template.Name, mesh, template.Quality);
        }

        // These aliases deliberately belong only to the test fixture. They do not assert a native hash/content mapping.
        using var content = new Content(DecorationTemplates);
        var items = new ItemBuilder(catalog, new StubLogger());
        using (var off = new HalloweenActivationTests.World(content.Root, false, itemCatalog: catalog, itemBuilder: items))
            Assert.AreEqual(0, off.Points.Count);
        using var on = new HalloweenActivationTests.World(content.Root, true, itemCatalog: catalog, itemBuilder: items);
        Assert.AreEqual(6, on.Points.Count);
        Assert.IsTrue(on.Points.All(point => point.IsStatic));
        Session session = Observe(on);
        SpawnEveryPoint(on);
        WorldItem[] spawned = on.Points.SelectMany(point => point.MembersSnapshot()).Cast<WorldItem>().ToArray();
        CollectionAssert.AreEquivalent(DecorationTemplates, spawned.Select(item => item.LowId).ToArray());
        Assert.AreEqual(6, spawned.Select(item => item.Identity).Distinct().Count());
        Assert.AreEqual(0, on.Registry.Dynels().OfType<NpcCharacter>().Count());
        foreach (WorldItem item in spawned)
        {
            Assert.AreEqual(SpawnSource.HashSpawn, item.SpawnSource);
            Assert.AreSame(on.Playfield, item.Playfield);
            Assert.IsTrue(on.Registry.TryGet(item.Identity, out Dynel? registered));
            Assert.AreSame(item, registered);
            AssertPublished(item, session);
        }
        on.Locality.ActivatePlayerVisibility(session.Player!);
        SpawnEveryPoint(on);
        Assert.AreEqual(6, session.Bodies.OfType<SimpleItemFullUpdateMessage>().Count());
        Assert.AreEqual(6, on.Points.Sum(point => point.MemberCount));
    }

    [TestMethod]
    public void SixActualPrivateDecorationTemplatesDespawnAndRepublishAfterCellSleep()
    {
        ItemTemplateCatalog catalog = RealCatalog.Value;
        using var content = new Content(DecorationTemplates);
        using var world = new HalloweenActivationTests.World(content.Root, true, itemCatalog: catalog,
            itemBuilder: new ItemBuilder(catalog, new StubLogger()));
        Session session = Observe(world);
        SpawnEveryPoint(world);
        WorldItem[] before = world.Points.SelectMany(point => point.MembersSnapshot()).Cast<WorldItem>().ToArray();
        Sleep(world, world.Points[0]);
        Assert.AreEqual(0, world.Points.Sum(point => point.MemberCount));
        Assert.AreEqual(0, world.Registry.Dynels().OfType<WorldItem>().Count());
        foreach (WorldItem previous in before)
        {
            Assert.IsNull(previous.Playfield);
            Assert.IsTrue(session.Bodies.OfType<DespawnMessage>().Any(body => body.Identity == previous.Identity));
        }
        SpawnEveryPoint(world);
        WorldItem[] after = world.Points.SelectMany(point => point.MembersSnapshot()).Cast<WorldItem>().ToArray();
        Assert.AreEqual(6, after.Length);
        Assert.AreEqual(0, before.Select(item => item.Identity).Intersect(after.Select(item => item.Identity)).Count());
        CollectionAssert.AreEquivalent(before.Select(item => item.LowId).ToArray(), after.Select(item => item.LowId).ToArray());
        Assert.AreEqual(12, session.Bodies.OfType<SimpleItemFullUpdateMessage>().Count());
        foreach (WorldItem item in after)
            AssertPublished(item, session);
    }

    static void AssertPublished(WorldItem item, Session session)
    {
        var body = session.Bodies.OfType<SimpleItemFullUpdateMessage>().Single(message => message.Identity == item.Identity);
        AssertSpawnBody(item, body);

        byte[] packet = session.Packets[session.Bodies.IndexOf(body)];
        using var stream = new MemoryStream(packet);
        var decoded = (SimpleItemFullUpdateMessage)new MessageSerializer().Deserialize(stream).Body;
        Assert.AreEqual(body.Identity, decoded.Identity);
        Assert.AreEqual(body.Playfield, decoded.Playfield);
        Assert.AreEqual(body.Coordinate.X, decoded.Coordinate.X);
        Assert.AreEqual(body.Coordinate.Y, decoded.Coordinate.Y);
        Assert.AreEqual(body.Coordinate.Z, decoded.Coordinate.Z);
        Assert.AreEqual(body.Heading.Y, decoded.Heading.Y);
        Assert.AreEqual(body.Heading.W, decoded.Heading.W);
        // StreamReader.ReadString removes the wire terminator from the decoded value.
        Assert.AreEqual(item.Template.Name, decoded.Name);
        CollectionAssert.AreEqual(body.Stats.Select(row => (row.Value1, row.Value2)).ToArray(),
            decoded.Stats.Select(row => (row.Value1, row.Value2)).ToArray());
    }

    static void AssertSpawnBody(WorldItem item, SimpleItemFullUpdateMessage body)
    {
        Assert.AreEqual(IdentityType.Terminal, body.Identity.Type);
        Assert.AreEqual(item.Identity, body.Identity);
        Assert.AreEqual(0, body.Identitytype, "An ownerless runtime object must transmit its own position/rotation.");
        Assert.AreEqual(0, body.Instance);
        Assert.AreEqual(Identity.None, body.Owner);
        Assert.AreEqual(item.Playfield!.Identity.Instance, body.Playfield);
        Assert.AreEqual(item.Position.xf, body.Coordinate.X);
        Assert.AreEqual(item.Position.yf, body.Coordinate.Y);
        Assert.AreEqual(item.Position.zf, body.Coordinate.Z);
        Assert.AreEqual(item.Rotation.yf, body.Heading.Y);
        Assert.AreEqual(item.Rotation.wf, body.Heading.W);
        Assert.AreEqual(item.Template.Name + "\0", body.Name);
        Dictionary<CharacterStat, uint> stats = body.Stats.ToDictionary(row => row.Value1, row => row.Value2);
        Assert.AreEqual((uint)item.LowId, stats[CharacterStat.StaticInstance]);
        Assert.AreEqual((uint)item.LowId, stats[CharacterStat.ACGItemTemplateID]);
        Assert.AreEqual((uint)item.HighId, stats[CharacterStat.ACGItemTemplateID2]);
        Assert.AreEqual((uint)item.Quality, stats[CharacterStat.ACGItemLevel]);
        uint visibleFlags = unchecked((uint)(ItemFlags.Visible | ItemFlags.TellCollision | ItemFlags.DisableStatelCollision));
        Assert.AreEqual(visibleFlags, stats[CharacterStat.Flags] & visibleFlags);

    }

    static Session Observe(HalloweenActivationTests.World world, Vector3? position = null)
    {
        Player player = TestWorld.CreatePlayer(700);
        player.Playfield = world.Playfield;
        player.Position = position ?? new Vector3(41, 8, 93);
        var session = new Session();
        player.Session = session;
        session.BindPlayer(player);
        world.Locality.RegisterDynel(player);
        world.Locality.ActivatePlayerVisibility(player);
        return session;
    }
    static void SpawnEveryPoint(HalloweenActivationTests.World world)
    {
        foreach (HashSpawnPoint point in world.Points)
            world.Tick(point);
    }
    static void Sleep(HalloweenActivationTests.World world, HashSpawnPoint point) => typeof(HashSpawnSystem)
        .GetMethod("OnCellSleep", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(world.System, [point.CellId]);
    static string PlacementIdentity(PlayfieldSpawnEntry entry) => JsonSerializer.Serialize(new
        { entry.DistrictIndex, entry.Hash, entry.HashText, entry.Position, entry.MinLevel, entry.MaxLevel });

    sealed class Content : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "aorebirth-decoration-test-" + Guid.NewGuid().ToString("N"));
        internal Content(int[] ids, bool seasonal = true, bool explicitBindings = false)
        {
            Directory.CreateDirectory(Path.Combine(Root, "Playfields", "665"));
            var hashes = ids.Select((id, index) => new { Hash = "STATIC_FIXTURE_" + index, Id = id }).ToArray();
            File.WriteAllText(Path.Combine(Root, "ItemTemplates.json"), explicitBindings ? "{}" : JsonSerializer.Serialize(hashes.ToDictionary(
                row => row.Hash, row => new { Templates = new[] { row.Id, row.Id } })));
            var entries = hashes.Select(row => new PlayfieldSpawnEntry
            {
                HashText = row.Hash, Position = [41, 8, 93], MinLevel = 1, MaxLevel = 1,
                StaticTemplateId = explicitBindings ? row.Id : null,
                Angle = 90, AngleW = 0, Radius = 0, RespawnChance = 100, RespawnTime = 30,
                Extensions = new() { Events = seasonal ? [new() { Name = "halloween_decor", Active = true }] : [] }
            }).ToArray();
            File.WriteAllText(Path.Combine(Root, "Playfields", "665", "Spawns.json"), JsonSerializer.Serialize(
                new PlayfieldSpawnsData { SchemaVersion = 1, PlayfieldId = 665, Spawns = entries }));
        }
        public void Dispose() => Directory.Delete(Root, true);
    }

    sealed class Session : IZoneSession
    {
        internal readonly List<MessageBody> Bodies = [];
        internal readonly List<byte[]> Packets = [];
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public void TransferToPlayfield(Playfield destination, Vector3 landing) => throw new NotSupportedException();
        public void Send(byte[] packet) => throw new AssertFailedException("This path should use the actual typed SimpleItemFullUpdate codec.");
        public void Send(Message message) => Send(message.Body);
        public void Send(MessageBody body)
        {
            using var stream = new MemoryStream();
            new MessageSerializer().Serialize(stream, new Message
            {
                Header = new Header { MessageId = 0xdfdf, PacketType = body.PacketType, Unknown = 1, Sender = Player!.Playfield!.Identity.Instance, Receiver = Player.Identity.Instance },
                Body = body
            });
            Bodies.Add(body);
            Packets.Add(stream.ToArray());
        }
        public void Send(MessageBody body, int sender, int receiver) => Send(body);
        public void SendInitiateCompression() => throw new NotSupportedException();
        public void Close() => State = SessionState.Closed;
    }
}
