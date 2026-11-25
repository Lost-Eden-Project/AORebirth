namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

using AORebirth.Core.GameData;
using AORebirth.Enums;

using LostEden.Protocol.GameData;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Ai;
using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Configuration;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Pets;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.WorldSimulation;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// A mission dungeon's lift (DungeonFloorTransitionDynel) carries its rider's pets along: they land in formation at the
/// rider's landing at once, a pet sent at a monster included. Left to walk, a pet had no route to the other floor and
/// only caught up when it noticed it was too far from its owner, and a fighting pet stayed on its fight until then.
/// </summary>
[TestClass]
public sealed class DungeonLiftPetTests
{
    static readonly Vector3 LiftAt = new(1, 0, 0);

    /// <summary>The landing on the floor above.</summary>
    static readonly Vector3 Landing = new(50, 64, 50);

    /// <summary>Farthest a pet's formation spot stands from its owner.</summary>
    const double FormationReach = 4.0;

    [TestMethod]
    public void TheRidersPetsLandBesideTheRiderAtOnce()
    {
        using var world = new LiftWorld();
        Player rider = world.Player(1, new Vector3(0, 0, 0));
        NpcCharacter guard = world.Pet(rider, 11, new Vector3(-2, 0, 1), PetTypes.Attack);
        NpcCharacter attacker = world.Pet(rider, 12, new Vector3(8, 0, 6), PetTypes.Heal);
        NpcCharacter monster = world.Npc(21, new Vector3(9, 0, 6));
        attacker.Pet!.Attack(monster.Identity);
        attacker.Brain!.SetCurrentTarget(monster.Identity);

        Assert.IsTrue(world.Lift.TryUse(rider), "the ride starts");

        Assert.AreEqual(Landing.y, rider.Position.y, 0.01, "the rider is on the floor above");
        foreach (NpcCharacter pet in new[] { guard, attacker })
        {
            Assert.IsTrue(Vector3.Abs(pet.Position - rider.Position) <= FormationReach,
                $"pet {pet.Identity.Instance} stayed behind at {pet.Position}");
            Assert.IsTrue(ReferenceEquals(pet.Playfield, rider.Playfield));
        }

        Assert.AreEqual(PetMode.Guard, attacker.Pet.Mode, "the attack order stays behind with its target");
        Assert.AreEqual(Identity.None, attacker.Pet.AttackTarget);
        Assert.AreEqual(Identity.None, attacker.Brain.CurrentTarget);
    }

    [TestMethod]
    public void AWaitingPetComesAlongAndFollows()
    {
        using var world = new LiftWorld();
        Player rider = world.Player(1, new Vector3(0, 0, 0));
        NpcCharacter pet = world.Pet(rider, 11, new Vector3(-6, 0, 0), PetTypes.Attack);
        pet.Pet!.Order(PetMode.Wait, pet.Position);

        Assert.IsTrue(world.Lift.TryUse(rider));

        Assert.IsTrue(Vector3.Abs(pet.Position - rider.Position) <= FormationReach, "the waiting pet stayed behind");
        Assert.AreEqual(PetMode.Follow, pet.Pet.Mode, "its post is on the floor it left");
    }

    [TestMethod]
    public void AnotherPlayersPetStaysWithItsOwner()
    {
        using var world = new LiftWorld();
        Player rider = world.Player(1, new Vector3(0, 0, 0));
        Player other = world.Player(2, new Vector3(2, 0, 0));
        var otherAt = new Vector3(3, 0, 1);
        NpcCharacter othersPet = world.Pet(other, 31, otherAt, PetTypes.Attack);

        Assert.IsTrue(world.Lift.TryUse(rider));

        Assert.AreEqual(otherAt, othersPet.Position, "another player's pet stays with its own owner");
    }

    [TestMethod]
    public void ASecondClickJustAfterTheRideTakesNoPetBack()
    {
        using var world = new LiftWorld();
        Player rider = world.Player(1, new Vector3(0, 0, 0));
        NpcCharacter pet = world.Pet(rider, 11, new Vector3(-2, 0, 1), PetTypes.Attack);
        Assert.IsTrue(world.Lift.TryUse(rider));
        Vector3 landed = new(pet.Position.x, pet.Position.y, pet.Position.z);

        // The lockout refuses the ride; nothing moves.
        rider.Position = LiftAt;
        Assert.IsFalse(world.Lift.TryUse(rider));
        Assert.AreEqual(landed, pet.Position);
    }

    sealed class LiftWorld : IDisposable
    {
        const int PlayfieldId = 0x0FF00437;

        readonly DynelRegistry _registry = new();
        readonly ServiceProvider _services;
        readonly Playfield _playfield;

        public LiftWorld()
        {
            _services = new ServiceCollection()
                .AddSingleton(_registry)
                .AddSingleton(new PlayfieldLocality(PlayfieldId, null))
                .AddSingleton(new WorldSimulationAccess())
                .AddSingleton(new GameplaySettings())
                .BuildServiceProvider();
            _playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
            Set("<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = PlayfieldId });
            Set("_serviceProvider", _services);
            Set("_pendingStatRebases", new ConcurrentDictionary<Character, byte>());
            Set("_lifecycleSync", new System.Threading.Lock());
            Set("<FightModes>k__BackingField", DistrictFightModes.None);

            Lift = new DungeonFloorTransitionDynel(_registry.AllocateStaticDynelIdentity(),
                new ItemTemplate { Id = 301143, Quality = 1 }, Landing)
            {
                Playfield = _playfield,
                Position = LiftAt
            };
            Place(Lift);
        }

        public DungeonFloorTransitionDynel Lift { get; }

        void Set(string field, object value)
            => typeof(Playfield).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_playfield, value);

        public Player Player(int id, Vector3 at)
        {
            var items = new Items();
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new Logger(), items)
            {
                FirstName = "Rider" + id,
                Session = SilentProxy<IZoneSession>.Create()
            };
            player.Inventory.Apply(new CharacterHydrationResult(), id, items);
            player.Playfield = _playfield;
            player.Position = at;
            player.Stats.Set(CharacterStat.Level, 60);
            player.Stats.Set(CharacterStat.MaxHealth, 100);
            player.Stats.Set(CharacterStat.Health, 100);
            Place(player);
            return player;
        }

        public NpcCharacter Npc(int id, Vector3 at)
        {
            var npc = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new Items())
            {
                Playfield = _playfield,
                Attackable = true,
                Position = at
            };
            npc.Stats.Set(CharacterStat.Level, 60);
            npc.Stats.Set(CharacterStat.MaxHealth, 100);
            npc.Stats.Set(CharacterStat.Health, 100);
            Place(npc);
            return npc;
        }

        public NpcCharacter Pet(Player owner, int id, Vector3 at, int type)
        {
            NpcCharacter pet = Npc(id, at);
            var controller = new PetController(owner, type, "lift-pet", 60, expiresUtc: null);
            pet.BindPet(controller);
            NpcBrain.CreatePet(pet, controller);
            owner.OwnedPets.Add(pet);
            return pet;
        }

        void Place(Dynel dynel)
        {
            _registry.Register(dynel);
            _services.GetRequiredService<PlayfieldLocality>().RegisterDynel(dynel);
        }

        public void Dispose() => _services.Dispose();
    }

    /// <summary>Answers every call with the default value: the session's sends go nowhere.</summary>
    class SilentProxy<T> : DispatchProxy where T : class
    {
        internal static T Create() => DispatchProxy.Create<T, SilentProxy<T>>();

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
