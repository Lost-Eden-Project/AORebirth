namespace ZoneEngine_New.Tests;

using System;
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
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.Playfield.Locality;
using ZoneEngine_New.Core.WorldSimulation;
using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// The out-of-sight leash (Gameplay.json leash.outOfSightSeconds): a monster that has not seen the player it is
/// chasing for that long gives up and walks home, though a route to the player is still there. A closed door stands
/// between them as the wall does in a dungeon corridor.
/// </summary>
[TestClass]
public sealed class NpcLineOfSightLeashTests
{
    static readonly Vector3 Home = new(10, 0, -8);

    /// <summary>Straight behind the door from <see cref="Home"/>, 22 m off: hidden and not close.</summary>
    static readonly Vector3 BehindTheDoor = new(10, 0, 14);

    /// <summary>17 m off, past the door's edge: in plain sight.</summary>
    static readonly Vector3 InTheOpen = new(22, 0, 4);

    [TestMethod]
    public void MonsterGivesUpOnAPlayerItHasNotSeenForTheOutOfSightTime()
    {
        using var rig = new Rig();
        Player player = rig.Player(41, BehindTheDoor);
        // Chased a few metres from home toward the door, and hurt.
        rig.Npc.Position = new Vector3(10, 0, -4);
        rig.Npc.Stats.Set(CharacterStat.Health, 400);
        Assert.IsFalse(rig.Npc.HasLineOfSightTo(player), "fixture: the door hides the player");
        rig.Brain.AddThreat(player.Identity, 50f);

        // Just hated: the whole window to find the player round the corner.
        rig.Tick();
        Assert.IsFalse(rig.Brain.IsEvading, "a player just lost from sight is still chased");
        Assert.AreEqual(player.Identity, rig.Npc.FightingTarget);

        // The player has stayed behind the wall past the out-of-sight time.
        rig.Brain.Hate.MarkSeen(player.Identity, DateTime.UtcNow.AddSeconds(-(GameplayLeash.Default.OutOfSightSeconds + 1)));
        // The chase drops the player on this think; the leash runs on the next, as on a live playfield.
        rig.Tick();
        rig.Tick();

        Assert.IsTrue(rig.Brain.IsEvading, "the monster gives up and walks home");
        Assert.IsTrue(rig.Brain.Hate.IsEmpty);
        Assert.AreEqual(Identity.None, rig.Npc.FightingTarget);
        Assert.AreEqual(1000, rig.Npc.Stats.GetOrZero(CharacterStat.Health), "a leash resets the monster");
    }

    [TestMethod]
    public void MonsterKeepsChasingAPlayerItCanSee()
    {
        using var rig = new Rig();
        Player player = rig.Player(42, InTheOpen);
        rig.Brain.AddThreat(player.Identity, 50f);
        rig.Brain.Hate.MarkSeen(player.Identity, DateTime.UtcNow.AddSeconds(-(GameplayLeash.Default.OutOfSightSeconds + 1)));

        rig.Tick();

        Assert.IsFalse(rig.Brain.IsEvading);
        Assert.AreEqual(player.Identity, rig.Npc.FightingTarget);
        Assert.IsTrue((DateTime.UtcNow - rig.Brain.Hate.LastSeenUtc(player.Identity)).TotalSeconds < 5,
            "the sighting starts the count again");
    }

    [TestMethod]
    public void APlayerHiddenCloseByIsNotGivenUpOn()
    {
        using var rig = new Rig();
        // Behind the same door, but 4 m off: ducked behind cover next to the monster.
        Player player = rig.Player(43, new Vector3(10, 0, -4));
        rig.Npc.Position = new Vector3(10, 0, 0.5);
        Assert.IsFalse(rig.Npc.HasLineOfSightTo(player), "fixture: the door hides the player");
        rig.Brain.AddThreat(player.Identity, 50f);
        rig.Brain.Hate.MarkSeen(player.Identity, DateTime.UtcNow.AddSeconds(-(GameplayLeash.Default.OutOfSightSeconds + 1)));

        rig.Tick();

        Assert.IsFalse(rig.Brain.IsEvading);
        Assert.IsTrue(rig.Brain.Hate.Contains(player.Identity));
    }

    [TestMethod]
    public void AHiddenPlayerIsDroppedForOneInSightWithoutAReset()
    {
        using var rig = new Rig();
        Player hidden = rig.Player(44, BehindTheDoor);
        Player seen = rig.Player(45, InTheOpen);
        rig.Brain.AddThreat(hidden.Identity, 100f);
        rig.Brain.AddThreat(seen.Identity, 10f);
        rig.Tick();
        Assert.AreEqual(hidden.Identity, rig.Npc.FightingTarget, "top threat first");

        rig.Brain.Hate.MarkSeen(hidden.Identity, DateTime.UtcNow.AddSeconds(-(GameplayLeash.Default.OutOfSightSeconds + 1)));
        rig.Tick();

        Assert.IsFalse(rig.Brain.IsEvading, "someone is still in sight: no reset, no heal");
        Assert.AreEqual(seen.Identity, rig.Npc.FightingTarget);
        Assert.IsTrue(rig.Brain.Hate.Contains(hidden.Identity), "kept on the hate list: seen again, it is fought again");
    }

    [TestMethod]
    public void ZeroOutOfSightSecondsTurnsTheGiveUpOff()
    {
        using var rig = new Rig(new GameplaySettings { Leash = new GameplayLeash { OutOfSightSeconds = 0 } });
        Player player = rig.Player(46, BehindTheDoor);
        rig.Brain.AddThreat(player.Identity, 50f);
        rig.Brain.Hate.MarkSeen(player.Identity, DateTime.UtcNow.AddSeconds(-600));

        rig.Tick();

        Assert.IsFalse(rig.Brain.IsEvading);
        Assert.AreEqual(player.Identity, rig.Npc.FightingTarget);
    }

    sealed class Rig : IDisposable
    {
        readonly ServiceProvider _services;
        readonly PlayfieldWorldSimulation _world;
        readonly Playfield _playfield;

        internal NpcCharacter Npc { get; }
        internal NpcBrain Brain { get; }

        internal Rig(GameplaySettings? gameplay = null)
        {
            var logger = new StubLogger();
            _world = (PlayfieldWorldSimulation)Activator.CreateInstance(typeof(PlayfieldWorldSimulation),
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
                args: new object?[] { 1, null, new PlayfieldGeometryData(), null, new GameDataStore(logger), logger }, culture: null)!;
            // A closed door across the corridor between the monster's home and the far side: 5.2 m wide, 3 m high.
            _world.Doors.Add(10, new Vector3(10, 0, 0), 0f, 1f, 3f, 1.5f, flags: 0);

            _services = new ServiceCollection()
                .AddSingleton(new DynelRegistry())
                .AddSingleton(new WorldSimulationAccess { Instance = _world })
                .AddSingleton(new PlayfieldLocality(1, null))
                .AddSingleton(gameplay ?? new GameplaySettings())
                .BuildServiceProvider();
            _playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
            typeof(Playfield).GetField("<Identity>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_playfield, new Identity { Type = IdentityType.Playfield2, Instance = 800 });
            typeof(Playfield).GetField("_serviceProvider", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_playfield, _services);
            typeof(Playfield).GetField("<FightModes>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_playfield, DistrictFightModes.None);

            Npc = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = 1001 }, new Items());
            Npc.Stats.Set(CharacterStat.MaxHealth, 1000);
            Npc.Stats.Set(CharacterStat.Health, 1000);
            Npc.Position = Home;
            Place(Npc);
            Brain = NpcBrain.Create(Npc, Home);
        }

        internal Player Player(int id, Vector3 at)
        {
            var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = id }, new StubLogger(), new Items());
            player.Stats.Set(CharacterStat.MaxHealth, 100);
            player.Stats.Set(CharacterStat.Health, 100);
            player.Position = at;
            Place(player);
            return player;
        }

        void Place(Character character)
        {
            character.Playfield = _playfield;
            _services.GetRequiredService<DynelRegistry>().Register(character);
            _services.GetRequiredService<PlayfieldLocality>().RegisterDynel(character);
        }

        /// <summary>One brain think, as the playfield tick runs it.</summary>
        internal void Tick() => Npc.Tick(NpcAiRules.BrainThinkSeconds + 0.1);

        public void Dispose()
        {
            _world.Dispose();
            _services.Dispose();
        }
    }

    sealed class Items : IItemBuilder
    {
        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null)
            => new()
            {
                LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount, InstanceId = instanceId,
                Identity = identity ?? Identity.None, Definition = CreateTemplate(lowId, highId, quality)
            };

        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
            => Create(lowId, highId, quality, source, stackCount);

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }

    sealed class StubLogger : IZoneLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(Exception exception, string message) { }
        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }
}
