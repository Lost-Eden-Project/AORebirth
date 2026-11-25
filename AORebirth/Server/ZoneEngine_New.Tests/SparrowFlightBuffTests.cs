namespace ZoneEngine_New.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Runtime.CompilerServices;

    using AORebirth.Core.GameData;
    using AORebirth.Enums;

    using LostEden.Protocol.GameData;

    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using ZoneEngine_New.Core.Characters;
    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Movement;
    using ZoneEngine_New.Core.Nanos;
    using ZoneEngine_New.Core.Playfield;
    using ZoneEngine_New.Core.Playfield.Locality;
    using ZoneEngine_New.Core.WorldSimulation;

    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// Sparrow Flight 82835 as items.dat encodes it: its CanFly (53138) runs only under
    /// "ExpansionPlayfield = 0, FlyingAllowed, And". The buff rebase must answer FlyingAllowed from the caster's
    /// playfield; left unresolved it failed closed, IsVehicle stayed 0 and the server held the Reet on the ground in
    /// Borealis (800) while the client flew.
    /// </summary>
    [TestClass]
    public sealed class SparrowFlightBuffTests
    {
        const int SparrowFlightId = 82835;
        const int SparrowNanoLine = 223;
        const int Borealis = 800;

        static readonly Identity Caster = new() { Type = IdentityType.SimpleChar, Instance = 42 };

        // Borealis 800's metadata.json: outdoors, no Shadowlands bit, the Rubi-Ka city bit and not the no-fly bit.
        static readonly PlayfieldMetaData BorealisMetaData = new()
        {
            IsIndoor = false,
            UnknownV9A = 0,
            PlayfieldTypeBits = 0x1000000
        };

        [TestMethod]
        public void SparrowFlightLetsTheCasterFlyInBorealis()
        {
            using var world = new FlightWorld(Borealis, BorealisMetaData);
            Player player = world.Player;

            Assert.AreEqual(BuffApplyDecision.Apply, player.TryApplyBuff(Sparrow(), Caster, DateTime.UtcNow, out _, out _));
            player.Rebase();
            player.Motor.RefreshFlightAuthority();

            Assert.AreEqual(1, player.Stats.GetOrZero(CharacterStat.IsVehicle));
            Assert.AreEqual((int)MovementState.Fly, player.Stats.GetOrZero(CharacterStat.CurrentMovementMode));
        }

        [TestMethod]
        public void SparrowFlightDoesNotFlyWhereThePlayfieldForbidsIt()
        {
            using var world = new FlightWorld(4582,
                new PlayfieldMetaData { IsIndoor = false, PlayfieldTypeBits = PlayfieldEntryFlags.NoFlyingMetaDataBit });
            Player player = world.Player;

            player.TryApplyBuff(Sparrow(), Caster, DateTime.UtcNow, out _, out _);
            player.Rebase();
            player.Motor.RefreshFlightAuthority();

            Assert.AreEqual(0, player.Stats.GetOrZero(CharacterStat.IsVehicle));
            Assert.AreNotEqual((int)MovementState.Fly, player.Stats.GetOrZero(CharacterStat.CurrentMovementMode));
        }

        [TestMethod]
        public void SparrowFlightDoesNotFlyInTheShadowlands()
        {
            using var world = new FlightWorld(4310,
                new PlayfieldMetaData { IsIndoor = false, UnknownV9A = PlayfieldEntryFlags.ShadowlandsMetaDataBit });
            Player player = world.Player;
            player.Stats.Set(CharacterStat.ExpansionPlayfield, 1);

            player.TryApplyBuff(Sparrow(), Caster, DateTime.UtcNow, out _, out _);
            player.Rebase();
            player.Motor.RefreshFlightAuthority();

            Assert.AreEqual(0, player.Stats.GetOrZero(CharacterStat.IsVehicle));
            Assert.AreNotEqual((int)MovementState.Fly, player.Stats.GetOrZero(CharacterStat.CurrentMovementMode));
        }

        [TestMethod]
        public void TheReetLandsWhenSparrowFlightEnds()
        {
            using var world = new FlightWorld(Borealis, BorealisMetaData);
            Player player = world.Player;
            player.TryApplyBuff(Sparrow(), Caster, DateTime.UtcNow, out _, out _);
            player.Rebase();
            player.Motor.RefreshFlightAuthority();
            Assert.AreEqual((int)MovementState.Fly, player.Stats.GetOrZero(CharacterStat.CurrentMovementMode));

            Assert.AreEqual(BuffRemovalOutcome.Removed,
                player.TryRemoveBuff(SparrowFlightId, BuffRemovalReason.Cancelled, out _));
            player.Rebase();
            player.Motor.RefreshFlightAuthority();

            Assert.AreEqual(0, player.Stats.GetOrZero(CharacterStat.IsVehicle));
            Assert.AreNotEqual((int)MovementState.Fly, player.Stats.GetOrZero(CharacterStat.CurrentMovementMode));
        }

        /// <summary>Sparrow Flight with its CanFly function and that function's requirement as items.dat carries them.</summary>
        static NanoSpell Sparrow() => NanoSpell.From(new ItemTemplate
        {
            Id = SparrowFlightId,
            Name = "Sparrow Flight",
            Quality = 8,
            Stats = new Dictionary<CharacterStat, int>
            {
                [CharacterStat.TimeExist] = 1440000,
                [CharacterStat.Level] = 10,
                [CharacterStat.MetaType] = SparrowNanoLine,
                [CharacterStat.Can] = unchecked((int)CanFlags.ApplyOnFriendly)
            },
            SpellList = new Dictionary<EventType, List<ItemSpell>>
            {
                [EventType.OnUse] =
                [
                    new ItemSpell
                    {
                        FunctionType = (int)FunctionType.CanFly,
                        Requirements =
                        [
                            new ItemRequirement { StatNumber = (int)CharacterStat.ExpansionPlayfield, Operator = (int)Operator.EqualTo, Value = 0 },
                            new ItemRequirement { Operator = (int)Operator.FlyingAllowed },
                            new ItemRequirement { Operator = (int)Operator.And }
                        ]
                    }
                ]
            }
        });

        sealed class FlightWorld : IDisposable
        {
            readonly DynelRegistry _registry = new();
            readonly ServiceProvider _services;
            readonly Playfield _playfield;

            public Player Player { get; }

            public FlightWorld(int playfieldId, PlayfieldMetaData metaData)
            {
                _services = new ServiceCollection()
                    .AddSingleton(_registry)
                    .AddSingleton(new PlayfieldLocality(playfieldId, null))
                    .AddSingleton(new WorldSimulationAccess())
                    .BuildServiceProvider();
                _playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
                Set("<Identity>k__BackingField", new Identity { Type = IdentityType.Playfield2, Instance = playfieldId });
                Set("<MetaData>k__BackingField", metaData);
                Set("_serviceProvider", _services);
                Set("_pendingStatRebases", new ConcurrentDictionary<Character, byte>());
                Set("_lifecycleSync", new System.Threading.Lock());

                var items = new Items();
                Player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = 1 }, new Logger(), items) { FirstName = "Reet" };
                Player.Inventory.Apply(new CharacterHydrationResult(), 1, items);
                Player.Playfield = _playfield;
                Player.Position = new Vector3(10, 4, 12);
                Player.Stats.Set(CharacterStat.Level, 60);
                Player.Stats.Set(CharacterStat.MaxNCU, 60);
                _registry.Register(Player);
            }

            void Set(string field, object value)
                => typeof(Playfield).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_playfield, value);

            public void Dispose() => _services.Dispose();
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
}
