namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

using AORebirth.Core.GameData;
using AORebirth.DungeonGenerator;
using AORebirth.World.Collision;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages.N3Messages;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.Quests.Dungeons;
using ZoneEngine_New.Core.WorldSimulation;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Bug #686: the device a Rubi-Ka mission sends you to repair or use floated in the air. The dungeon placed it at its
/// district spawn point, and those points sit about 0.4 m above the floor; it now stands on the floor under the point,
/// as a mission chest does. Runs the dungeon's own objective spawn against the runtime GameData (AO_REBIRTH_GAMEDATA_PATH).
/// </summary>
[TestClass]
public sealed class MissionObjectiveFloorTests
{
    const int PlayfieldId = 0x0FF00321;

    static readonly Lazy<GameDataStore?> Runtime = new(() =>
    {
        string? root = Environment.GetEnvironmentVariable(GameDataPaths.EnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(Path.Combine(root, "Dungeons")))
            return null;
        return new GameDataStore(new Logger(), null, root);
    });

    [TestMethod]
    [DataRow(321)]
    [DataRow(324)]
    [DataRow(341)]
    [DataRow(351)]
    public void TheMissionDeviceStandsOnTheFloorUnderItsPoint(int style)
    {
        GameDataStore data = Runtime.Value ?? throw Inconclusive();
        var generator = new DungeonGenerator(data.RootPath);
        int checkedSeeds = 0;
        for (int seed = 1; seed <= 3; seed++)
        {
            DungeonGenerationResult result = generator.Generate(new DungeonGenerationRequest
            {
                StyleId = style, Seed = seed, FloorCount = 1, FloorSize = 6, FloorHeight = 128, BossFloor = false,
                UseFloorTransitions = false, LockedDoorChancePercent = 0, BuildingInstance = unchecked((int)0xC00010F4),
                Layout = DungeonLayoutKind.Planned
            });
            var layout = new DungeonLayout(result.Generator, result.Identity, result.GeneratorBlob(),
                new Vector3(result.Spawn.X, result.Spawn.Y, result.Spawn.Z), seed, null)
            {
                FloorTransitions = result.Transitions, BossRoomIndex = result.BossRoomIndex
            };
            DungeonWorldLayout dungeon = DungeonCollisionBuilder.BuildLayout(data.RootPath, result.Generator);
            if (!DungeonObjectivePlacement.TrySelect(dungeon, data.RootPath, layout, new Logger(), out int room, out Vector3 point))
                continue;
            using PlayfieldWorldSimulation world = PlayfieldWorldSimulation.Create(PlayfieldId,
                new PlayfieldGeometryData { Collision = dungeon.Collision }, null, DestinationsCatalog.Instance, data, new Logger());

            var binding = new MissionItemObjectiveBinding
            {
                QuestId = "GOBJECTIVE", OwnerId = 77, MissionType = 2,
                IdentityType = (int)IdentityType.Terminal, Instance = 1234567,
                LowId = 100341, HighId = 100341, Quality = 1, RoomIndex = room,
                X = point.xf, Y = point.yf, Z = point.zf, QuestInstanceStat = 1
            };
            var registry = new DynelRegistry();
            var locality = new PlayfieldLocality(PlayfieldId, null);
            locality.ApplyDungeonRooms(dungeon);
            using ServiceProvider services = new ServiceCollection()
                .AddSingleton(registry)
                .AddSingleton(new WorldSimulationAccess { Instance = world })
                .AddSingleton(Fake<IItemBuilder>())
                .BuildServiceProvider();
            QuestDungeonPlayfield playfield = Dungeon(layout, binding, services);

            typeof(QuestDungeonPlayfield).GetMethod("SpawnObjectiveItem", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(playfield, [dungeon, locality]);

            MissionObjectiveItem device = registry.Dynels().OfType<MissionObjectiveItem>().Single();
            Assert.IsTrue(world.TrySnapToFloor(new Vector3(binding.X, binding.Y, binding.Z), out Vector3 floor),
                "No floor under the objective point (style " + style + ", seed " + seed + ").");
            // The defect: the device stood at the point, above the floor, and was drawn in the air.
            Assert.IsTrue(binding.Y - floor.yf > 0.05f, "The objective point is " + (binding.Y - floor.yf) + " m over its floor.");
            Assert.AreEqual(binding.X, device.Position.xf);
            Assert.AreEqual(floor.yf, device.Position.yf, "The device floats (style " + style + ", seed " + seed + ").");
            Assert.AreEqual(binding.Z, device.Position.zf);
            var update = (SimpleItemFullUpdateMessage)device.BuildSpawnMessage();
            Assert.AreEqual(floor.yf, update.Position!.Value.Y, "The client is told the floor position.");
            // The accepted binding keeps the point it was accepted with; only where the device stands changes.
            Assert.AreEqual(point.yf, binding.Y);
            checkedSeeds++;
        }
        Assert.IsTrue(checkedSeeds > 0, "No objective was placed for style " + style + ".");
    }

    static Exception Inconclusive()
    {
        Assert.Inconclusive("Set AO_REBIRTH_GAMEDATA_PATH to the private GameData (Dungeons).");
        return new InvalidOperationException();
    }

    /// <summary>A quest dungeon holding only what its objective spawn reads: the layout, the binding and its services.</summary>
    static QuestDungeonPlayfield Dungeon(DungeonLayout layout, MissionItemObjectiveBinding binding, ServiceProvider services)
    {
        var playfield = (QuestDungeonPlayfield)RuntimeHelpers.GetUninitializedObject(typeof(QuestDungeonPlayfield));
        Set(typeof(Playfield), playfield, "<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = PlayfieldId });
        Set(typeof(Playfield), playfield, "_serviceProvider", services);
        Set(typeof(Playfield), playfield, "_pendingStatRebases", new ConcurrentDictionary<Character, byte>());
        Set(typeof(Playfield), playfield, "_lifecycleSync", new System.Threading.Lock());
        Set(typeof(Playfield), playfield, "<FightModes>k__BackingField", DistrictFightModes.None);
        Set(typeof(AcgDungeonPlayfield), playfield, "<Layout>k__BackingField", layout);
        Set(typeof(QuestDungeonPlayfield), playfield, "_objectiveItem", binding);
        Set(typeof(QuestDungeonPlayfield), playfield, "_collectObjective", (Func<Player, MissionObjectiveItem, bool>)((_, _) => false));
        return playfield;
    }

    static void Set(Type type, object target, string field, object value)
        => (type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(type.Name, field)).SetValue(target, value);

    public class ItemsProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod!.Name == nameof(IItemBuilder.CreateTemplate)
                ? new ItemTemplate()
                : throw new AssertFailedException("Unexpected call " + targetMethod.Name);
    }

    static T Fake<T>() where T : class => DispatchProxy.Create<T, ItemsProxy>();

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
