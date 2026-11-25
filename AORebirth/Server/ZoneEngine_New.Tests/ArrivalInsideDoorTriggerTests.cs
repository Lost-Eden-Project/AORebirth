namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using AORebirth.Core.GameData;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.WorldSimulation;

using Quaternion = AORebirth.Core.Vector.Quaternion;
using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Live defect: zoning door to door could put the character down inside the destination door's walk-in trigger, and
/// the first step out of it zoned them straight back. Arriving inside a door holds it until the character has left it;
/// only walking back in uses it. Driven through the engine's own trigger pass on Jobe Platform's exit doors.
/// </summary>
[TestClass]
public sealed class ArrivalInsideDoorTriggerTests
{
    const int PlatformId = 4530;
    const float LandingY = 199.52f;
    const int ExitDoor = unchecked((int)0xC00311B2);
    const int ExitDestination = 540;

    [TestMethod]
    public void SteppingOutOfTheDoorYouArrivedInDoesNotZoneYouBack()
    {
        using var door = new DoorScene(1001);

        door.Tick(door.AtDoor);
        Assert.AreEqual(0, door.Session.Transfers.Count, "arriving inside the door does not use it");

        door.Tick(door.Beside(0.1f));
        Assert.AreEqual(0, door.Session.Transfers.Count, "shuffling inside the door does not use it");

        // Out through the side, in the door's own plane: the step's path starts inside the door.
        door.Tick(door.Beside(6f));
        Assert.AreEqual(0, door.Session.Transfers.Count, "the step out of the door is not a step into it");

        door.Tick(door.Beside(8f));
        Assert.AreEqual(0, door.Session.Transfers.Count, "nor is walking on");
    }

    [TestMethod]
    public void WalkingBackIntoTheDoorYouArrivedInUsesIt()
    {
        using var door = new DoorScene(1002);

        door.Tick(door.AtDoor);
        door.Tick(door.Beside(6f));
        Assert.AreEqual(0, door.Session.Transfers.Count);

        door.Tick(door.AtDoor);
        Assert.AreEqual(1, door.Session.Transfers.Count, "leaving and coming back uses the door, once");
        Assert.AreSame(door.Destination, door.Session.Transfers[0].World);
    }

    [TestMethod]
    public void StraightOutOfTheDoorAndBackInUsesIt()
    {
        using var door = new DoorScene(1003);

        door.Tick(door.AtDoor);
        door.Tick(door.InFront(3f));
        Assert.AreEqual(0, door.Session.Transfers.Count, "walking out the front does not use the door");

        door.Tick(door.AtDoor);
        Assert.AreEqual(1, door.Session.Transfers.Count, "walking back in does");
    }

    [TestMethod]
    public void WalkingInFromOutsideStillUsesTheDoor()
    {
        using var door = new DoorScene(1004);

        door.Tick(door.InFront(3f));
        door.Tick(door.AtDoor);
        Assert.AreEqual(1, door.Session.Transfers.Count, "a character who did not arrive in the door walks straight through");
    }

    /// <summary>One character on Jobe Platform beside one of its exit doors, with the door's destination loaded.</summary>
    sealed class DoorScene : IDisposable
    {
        readonly GameDataStore _data;
        readonly PlayfieldWorldSimulation _world;
        readonly Playfield _platform;
        readonly Player _player;
        readonly Vector3 _lateral;
        readonly Vector3 _normal;

        public DoorScene(int characterId)
        {
            _data = new GameDataStore(new StubLogger());
            DestinationsCatalog.Instance.ConfigureRoot(_data.RootPath);
            PlayfieldGeometryData geometry = _data.GetPlayfieldGeometry(PlatformId);
            var placed = geometry.Dynels?.Dynels.SingleOrDefault(d => d.IdentityInstance == ExitDoor);
            if (placed == null)
                Assert.Inconclusive("The runtime GameData has no Jobe Platform exit door " + ExitDoor.ToString("X8"));

            _world = PlayfieldWorldSimulation.Create(
                PlatformId,
                geometry,
                _data.GetPlayfieldMetaData(PlatformId),
                DestinationsCatalog.Instance,
                _data,
                new StubLogger(),
                new ItemTemplateCatalog(_data, new StubLogger()));

            var rotation = new Quaternion(placed!.Heading.X, placed.Heading.Y, placed.Heading.Z, placed.Heading.W);
            _lateral = (Vector3)Quaternion.RotateVector3(rotation, Vector3.AxisX);
            _normal = (Vector3)Quaternion.RotateVector3(rotation, Vector3.AxisZ);
            AtDoor = new Vector3(placed.Position.X, LandingY, placed.Position.Z);

            // The destination only has to be loaded and walkable; it shares the platform's surface.
            Destination = BlankPlayfield(ExitDestination, _data, _world);
            _platform = BlankPlayfield(PlatformId, _data, _world, BlankManager(Destination));

            Session = new Session();
            _player = CreatePlayer(characterId);
            _player.Session = Session;
            Session.BindPlayer(_player);
            _player.Playfield = _platform;
            _player.Position = AtDoor;
            _platform.GetRequiredService<DynelRegistry>().Register(_player);
        }

        public Session Session { get; }

        public Playfield Destination { get; }

        public Vector3 AtDoor { get; }

        /// <summary>Along the doorway, in the door's own plane.</summary>
        public Vector3 Beside(float distance)
            => new(AtDoor.x + (_lateral.x * distance), AtDoor.y, AtDoor.z + (_lateral.z * distance));

        /// <summary>Straight out of the doorway.</summary>
        public Vector3 InFront(float distance)
            => new(AtDoor.x + (_normal.x * distance), AtDoor.y, AtDoor.z + (_normal.z * distance));

        public void Tick(Vector3 position)
        {
            _player.Position = position;
            _world.TickSoftTriggers(_platform, 0.1);
        }

        public void Dispose()
        {
            _platform.GetRequiredService<DynelRegistry>().UnregisterExact(_player);
            _world.Dispose();
        }
    }

    static Playfield BlankPlayfield(int id, IGameData data, PlayfieldWorldSimulation world, PlayfieldManager? manager = null)
    {
        var playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
        Set(playfield, "<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield, Instance = id });
        Set(playfield, "<ResourceId>k__BackingField", id);
        Set(playfield, "_lifecycleSync", new Lock());
        Set(playfield, "_logger", new StubLogger());
        IServiceCollection services = new ServiceCollection()
            .AddSingleton(data)
            .AddSingleton(new WorldSimulationAccess { Instance = world })
            .AddSingleton(new DynelRegistry())
            .AddSingleton<IInventoryRepository>(new StubInventoryRepository())
            .AddSingleton<IItemBuilder>(new StubItemBuilder());
        if (manager != null)
            services.AddSingleton(manager);

        Set(playfield, "_serviceProvider", services.BuildServiceProvider());
        return playfield;
    }

    static PlayfieldManager BlankManager(Playfield destination)
    {
        var manager = (PlayfieldManager)RuntimeHelpers.GetUninitializedObject(typeof(PlayfieldManager));
        Set(manager, "_sync", new Lock());
        Set(manager, "_playfields", new Dictionary<int, Playfield> { [destination.Identity.Instance] = destination });
        return manager;
    }

    static void Set(object target, string name, object value)
        => (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException(target.GetType().Name + " has no field " + name)).SetValue(target, value);

    static Player CreatePlayer(int instance)
    {
        var builder = new StubItemBuilder();
        var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = instance }, new StubLogger(), builder)
        {
            FirstName = "Doorstep"
        };
        player.Inventory.Apply(new CharacterHydrationResult(), instance, builder);
        return player;
    }

    sealed class Session : IZoneSession
    {
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        internal readonly List<(Playfield World, Vector3 Position)> Transfers = [];
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public bool TransferToPlayfield(Playfield destination, Vector3 landing) { Transfers.Add((destination, landing)); return true; }
        public bool TransferToPlayfield(Playfield destination, Vector3 landing, Quaternion heading) => TransferToPlayfield(destination, landing);
        public void Send(byte[] packet) { }
        public void Send(MessageBody body) { }
        public void Send(MessageBody body, int sender, int receiver) { }
        public void SendInitiateCompression() { }
        public void Close() => State = SessionState.Closed;
    }

    sealed class StubLogger : IZoneLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(Exception exception, string message) => throw new AssertFailedException(message + ": " + exception, exception);
        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }

    sealed class StubItemBuilder : IItemBuilder
    {
        public Item Create(int lowId, int highId, int quality, AORebirth.Enums.ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null)
            => new()
            {
                LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount, InstanceId = instanceId,
                Identity = identity ?? Identity.None, Definition = CreateTemplate(lowId, highId, quality)
            };

        public Item CreateWithNewInstance(int lowId, int highId, int quality, AORebirth.Enums.ItemSource source, int stackCount = 1)
            => Create(lowId, highId, quality, source, stackCount);

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }

    /// <summary>Every call throws: walking through a door reaches no item rows.</summary>
    sealed class StubInventoryRepository : IInventoryRepository
    {
        public IReadOnlyList<ItemInstanceRecord> GetCarriedItems(int characterId) => throw new NotSupportedException();
        public IReadOnlyList<ItemInstanceRecord> GetBankItems(int characterId) => throw new NotSupportedException();
        public IReadOnlyList<ItemInstanceRecord> GetContainerItems(int containerInstanceId) => throw new NotSupportedException();
        public int LeaseInstanceIdBlock(int count) => throw new NotSupportedException();
        public ItemInstanceRecord Insert(ItemInstanceRecord item) => throw new NotSupportedException();
        public void UpdateLocation(int instanceId, int containerType, int containerInstance, int containerPlacement) => throw new NotSupportedException();
        public void UpdateLocations(IReadOnlyList<ItemLocationUpdate> locations) => throw new NotSupportedException();
        public void PersistNewAndUpdateLocations(IReadOnlyList<ItemInstanceRecord> inserts, IReadOnlyList<ItemLocationUpdate> updates) => throw new NotSupportedException();
    }
}
