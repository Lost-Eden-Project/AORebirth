namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using AORebirth.Core.GameData;

using LostEden.Protocol.GameData;
using LostEden.Protocol.Messages;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Ai;
using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Pets;
using ZoneEngine_New.Core.Playfield;

using Quaternion = AORebirth.Core.Vector.Quaternion;
using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Live report: a heal pet on a heal order stayed at its owner's side, so the tank walked out of its heals the moment
/// the MP stopped to loot. A heal pet on a heal order now walks with the one it heals, out to
/// <see cref="PetAi.HealLeashMeters"/> from its owner; past that it gives the order up and comes back.
/// </summary>
[TestClass]
public sealed class HealPetLeashTests
{
    [TestMethod]
    public void HealPetOnAHealOrderWalksWithItsTarget()
    {
        using var world = new PetWorld();
        Player owner = world.Player(1, new Vector3(0, 0, 0));
        Player tank = world.Player(4, new Vector3(30, 0, 0));
        (NpcCharacter _, PetController controller, NpcBrain brain) = world.Pet(2, owner, PetTypes.Heal, new Vector3(2, 0, 0));
        controller.Heal(tank.Identity);

        Assert.AreSame(tank, PetAi.Leader(brain));
        Vector3 spot = PetAi.FollowPoint(brain, PetAi.Leader(brain), 0, 0);
        Assert.IsTrue(Vector3.Abs(spot - tank.Position) < 6, "the follow spot is beside the heal target");

        controller.EndHeal();
        Assert.AreSame(owner, PetAi.Leader(brain), "without the order the owner leads again");
    }

    [TestMethod]
    public void OtherPetsAndOverEquippedHealPetsFollowTheirOwner()
    {
        using var world = new PetWorld();
        Player owner = world.Player(1, new Vector3(0, 0, 0));
        Player tank = world.Player(4, new Vector3(30, 0, 0));

        (NpcCharacter _, PetController attack, NpcBrain attackBrain) = world.Pet(2, owner, PetTypes.Attack, new Vector3(2, 0, 0));
        attack.Heal(tank.Identity);
        Assert.AreSame(owner, PetAi.Leader(attackBrain));

        (NpcCharacter _, PetController heal, NpcBrain healBrain) = world.Pet(3, owner, PetTypes.Heal, new Vector3(2, 0, 2));
        heal.Heal(tank.Identity);
        heal.IsOverEquipped = true;
        Assert.AreSame(owner, PetAi.Leader(healBrain));
    }

    [TestMethod]
    public void HealPetStaysWithItsTargetPastTheNormalLeash()
    {
        using var world = new PetWorld();
        Player owner = world.Player(1, new Vector3(0, 0, 0));
        double far = (PetService.MaxPlayerPetDistance + PetAi.HealLeashMeters) / 2;
        Player tank = world.Player(4, new Vector3(far + 2, 0, 0));
        (NpcCharacter _, PetController controller, NpcBrain brain) = world.Pet(2, owner, PetTypes.Heal, new Vector3(far, 0, 0));
        controller.Heal(tank.Identity);

        Assert.IsFalse(PetAi.ShouldRejoinOwner(brain));
        Assert.IsFalse(PetAi.PickEnemy(brain), "a heal pet does not fight on its own");
        Assert.AreEqual(tank.Identity, controller.HealTarget);
        Assert.AreEqual(0, world.Feedback(owner), "nothing to warn about inside the leash");
    }

    [TestMethod]
    public void HealPetPastTheHealLeashGivesTheOrderUpAndRejoins()
    {
        using var world = new PetWorld();
        Player owner = world.Player(1, new Vector3(0, 0, 0));
        Player tank = world.Player(4, new Vector3(PetAi.HealLeashMeters + 7, 0, 0));
        (NpcCharacter pet, PetController controller, NpcBrain brain) =
            world.Pet(2, owner, PetTypes.Heal, new Vector3(PetAi.HealLeashMeters + 5, 0, 0));
        controller.Heal(tank.Identity);

        Assert.IsTrue(PetAi.ShouldRejoinOwner(brain));
        Assert.AreEqual(Identity.None, controller.HealTarget, "the order ends, so the pet does not walk back out");
        Assert.AreEqual(1, world.Feedback(owner), "the owner is told once");
        Assert.AreSame(owner, PetAi.Leader(brain));

        PetRejoinOwnerNode.Rejoin(brain);
        Assert.IsTrue(Vector3.Abs(pet.Position - owner.Position) < 6, "the pet is back beside its owner");
        Assert.IsFalse(PetAi.ShouldRejoinOwner(brain));
        Assert.AreEqual(1, world.Feedback(owner));
    }

    [TestMethod]
    public void HealPetWithoutAHealOrderKeepsTheNormalLeash()
    {
        using var world = new PetWorld();
        Player owner = world.Player(1, new Vector3(0, 0, 0));
        double far = (PetService.MaxPlayerPetDistance + PetAi.HealLeashMeters) / 2;
        (NpcCharacter _, PetController _, NpcBrain brain) = world.Pet(2, owner, PetTypes.Heal, new Vector3(far, 0, 0));

        Assert.IsTrue(PetAi.ShouldRejoinOwner(brain));
    }

    [TestMethod]
    public void HealOrderOnAGoneTargetFallsBackToTheOwner()
    {
        using var world = new PetWorld();
        Player owner = world.Player(1, new Vector3(0, 0, 0));
        Player tank = world.Player(4, new Vector3(60, 0, 0));
        (NpcCharacter _, PetController controller, NpcBrain brain) = world.Pet(2, owner, PetTypes.Heal, new Vector3(58, 0, 0));
        controller.Heal(tank.Identity);
        Assert.IsFalse(PetAi.ShouldRejoinOwner(brain));

        world.Remove(tank);

        Assert.AreSame(owner, PetAi.Leader(brain));
        Assert.AreEqual(Identity.None, controller.HealTarget);
        Assert.IsTrue(PetAi.ShouldRejoinOwner(brain), "past the normal leash with no one to heal, the pet comes back");
    }

    sealed class PetWorld : IDisposable
    {
        readonly ServiceProvider _services;
        readonly DynelRegistry _registry = new();
        readonly Dictionary<int, Session> _sessions = [];

        public Playfield Playfield { get; }

        public PetWorld()
        {
            _services = new ServiceCollection()
                .AddSingleton(_registry)
                .AddSingleton(new ZoneEngine_New.Core.WorldSimulation.WorldSimulationAccess())
                .AddSingleton<IInventoryRepository>(new StubInventoryRepository())
                .AddSingleton<IItemBuilder>(new StubItemBuilder())
                .BuildServiceProvider();
            var playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
            Set(playfield, "<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield, Instance = 4582 });
            Set(playfield, "<ResourceId>k__BackingField", 4582);
            Set(playfield, "_lifecycleSync", new Lock());
            Set(playfield, "_logger", new StubLogger());
            Set(playfield, "_serviceProvider", _services);
            Playfield = playfield;
        }

        public Player Player(int id, Vector3 at)
        {
            var builder = new StubItemBuilder();
            var session = new Session();
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new StubLogger(), builder)
            {
                FirstName = "Healee" + id,
                Session = session
            };
            player.Inventory.Apply(new CharacterHydrationResult(), id, builder);
            player.Playfield = Playfield;
            player.Position = at;
            player.Stats.Set(CharacterStat.Level, 60);
            player.Stats.Set(CharacterStat.MaxHealth, 100);
            player.Stats.Set(CharacterStat.Health, 50);
            _registry.Register(player);
            _sessions[id] = session;
            return player;
        }

        public (NpcCharacter Pet, PetController Controller, NpcBrain Brain) Pet(int id, Player owner, int type, Vector3 at)
        {
            var pet = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = 1000 + id }, new StubItemBuilder())
            {
                Playfield = Playfield,
                Position = at,
                Name = "Pet" + id
            };
            pet.Stats.Set(CharacterStat.Level, 60);
            pet.Stats.Set(CharacterStat.MaxHealth, 100);
            pet.Stats.Set(CharacterStat.Health, 100);
            _registry.Register(pet);
            var controller = new PetController(owner, type, "synthetic-pet", 60, expiresUtc: null);
            pet.BindPet(controller);
            return (pet, controller, NpcBrain.CreatePet(pet, controller));
        }

        /// <summary>The character leaves the playfield (zoned, logged out, despawned).</summary>
        public void Remove(Character character) => _registry.UnregisterExact(character);

        /// <summary>Feedback lines sent to <paramref name="player"/> so far.</summary>
        public int Feedback(Player player) => _sessions[player.Identity.Instance].FeedbackCount;

        public void Dispose() => _services.Dispose();
    }

    static void Set(object target, string name, object value)
        => (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException(target.GetType().Name + " has no field " + name)).SetValue(target, value);

    sealed class Session : IZoneSession
    {
        public int FeedbackCount { get; private set; }
        public SessionState State { get; set; } = SessionState.InPlay;
        public Player? Player { get; private set; }
        public void BindPlayer(Player player) => Player = player;
        public void UnbindPlayer() => Player = null;
        public bool TransferToPlayfield(Playfield destination, Vector3 landing) => false;
        public bool TransferToPlayfield(Playfield destination, Vector3 landing, Quaternion heading) => false;
        public void Send(byte[] packet) { }
        public void Send(MessageBody body) => Count(body);
        public void Send(MessageBody body, int sender, int receiver) => Count(body);
        public void SendInitiateCompression() { }
        public void Close() => State = SessionState.Closed;

        void Count(MessageBody body)
        {
            if (body.GetType().Name == "FormatFeedbackMessage")
                FeedbackCount++;
        }
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
