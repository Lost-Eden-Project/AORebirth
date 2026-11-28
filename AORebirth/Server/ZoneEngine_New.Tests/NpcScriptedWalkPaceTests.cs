namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading;

using AORebirth.Core.GameData;
using AORebirth.Enums;

using LostEden.Protocol.GameData;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Ai;
using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Movement;
using ZoneEngine_New.Core.NpcScripts;

using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// A scripted NPC that finishes a walking WalkTo goes back to running pace (team request 38). Live: The Beast's adds
/// (Pandemonium_TheBeastAdds, pf 4331) walked to the room centre, then chased players at walking pace for the rest of
/// their lives: arrival left the motor in walk, and neither the script's ReleaseMovement (a no-op once the walk is
/// over) nor SetStat CurrentMovementMode 3 (a stat write only) put it back.
/// </summary>
[TestClass]
public sealed class NpcScriptedWalkPaceTests
{
    static readonly Vector3 Start = new(0, 0, 0);
    static readonly Vector3 Centre = new(20, 0, 0);
    static readonly Vector3 Beyond = new(20, 0, 20);

    [TestMethod]
    [Timeout(5000)]
    public void TheBeastsAddsRunAgainOnceTheirWalkInArrives()
    {
        // The Beast's adds' Spawned handler, as the private GameData has it (without the opening Wait).
        var rig = new Rig("""
            [ { "WalkTo": [20, 0, 0] }, { "SetHome": "Here" }, { "SetVar": { "Var": 0, "Value": 1 } },
              { "ReleaseMovement": true }, { "SetStat": { "Stat": "CurrentMovementMode", "Value": 3 } } ]
            """);

        rig.RunScript();
        Assert.IsTrue(rig.Brain.ScriptHoldsMovement, "WalkTo takes the movement lease.");
        rig.Brain.Tick(1);
        Assert.AreEqual(MovementState.Walk, rig.Npc.Motor.State, "WalkTo walks.");

        rig.StepOnto(Centre);
        rig.Brain.Tick(1);
        Assert.IsFalse(rig.Brain.ScriptHoldsMovement, "The walk arrived.");
        Assert.AreNotEqual(MovementState.Walk, rig.Npc.Motor.State, "Arrival leaves walking pace.");

        // The program reads the arrival and runs SetHome, ReleaseMovement and SetStat.
        rig.RunScript();
        rig.Brain.Tick(1);
        Assert.AreEqual(MovementState.Run, rig.Npc.Motor.State, "The add is at running pace for the fight that follows.");
        Assert.IsTrue(rig.Brain.Home is Vector3 home && Math.Abs(home.x - rig.Npc.Position.x) < 0.01,
            "SetHome Here ran after the arrival.");
    }

    [TestMethod]
    [Timeout(5000)]
    public void AWalkLegAfterAnArrivedLegStillWalks()
    {
        var rig = new Rig("""[ { "WalkTo": [20, 0, 0] }, { "WalkTo": [20, 0, 20] } ]""");

        rig.RunScript();
        rig.Brain.Tick(1);
        rig.StepOnto(Centre);
        rig.Brain.Tick(1);
        Assert.AreNotEqual(MovementState.Walk, rig.Npc.Motor.State);

        rig.RunScript();
        Assert.IsTrue(rig.Brain.ScriptHoldsMovement, "The second leg took the lease.");
        rig.Brain.Tick(1);
        Assert.AreEqual(MovementState.Walk, rig.Npc.Motor.State, "The second WalkTo walks again.");

        rig.StepOnto(Beyond);
        rig.Brain.Tick(1);
        Assert.AreNotEqual(MovementState.Walk, rig.Npc.Motor.State, "And runs once it arrives.");
    }

    [TestMethod]
    [Timeout(5000)]
    public void ARunToLegArrivesAtRunningPace()
    {
        var rig = new Rig("""[ { "RunTo": [20, 0, 0] } ]""");

        rig.RunScript();
        rig.Brain.Tick(1);
        Assert.AreNotEqual(MovementState.Walk, rig.Npc.Motor.State);

        rig.StepOnto(Centre);
        rig.Brain.Tick(1);
        Assert.AreNotEqual(MovementState.Walk, rig.Npc.Motor.State);
    }

    sealed class Rig
    {
        internal readonly NpcCharacter Npc;
        internal readonly NpcBrain Brain;
        readonly NpcScriptRunner _runner;
        long _nowMs = Environment.TickCount64;

        internal Rig(string spawnedOps)
        {
            Npc = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = 38001 }, new Items())
            {
                Name = "Bile of the Beast"
            };
            Npc.Stats.Set(CharacterStat.Level, 200);
            Npc.Stats.Set(CharacterStat.MaxHealth, 1000);
            Npc.Stats.Set(CharacterStat.Health, 1000);
            Npc.Position = new Vector3(Start.x, Start.y, Start.z);
            Brain = NpcBrain.Create(Npc, Npc.Position);

            string json = """{ "Id": "Test_WalkPace", "Handlers": [ { "On": "Spawned", "Do": """ + spawnedOps + """ } ] }""";
            using JsonDocument document = JsonDocument.Parse(json);
            var problems = new List<string>();
            NpcScriptProgram? program = new NpcScriptCompiler(RootOnlyGameData.Create(System.IO.Path.GetTempPath()))
                .CompileProgram("Test_WalkPace.json", document.RootElement, problems);
            Assert.IsNotNull(program, string.Join("; ", problems));

            _runner = new NpcScriptRunner(program, ScriptArgs.Empty, Npc);
            Npc.AttachScript(_runner);
            _runner.Raise(NpcScriptTrigger.Spawned);
        }

        /// <summary>One script tick, past any arrival poll the runner set.</summary>
        internal void RunScript()
        {
            _nowMs += 1000;
            _runner.Tick(Math.Max(_nowMs, Environment.TickCount64 + 1000));
        }

        /// <summary>The NPC's body reaches this NPC's spread of <paramref name="destination"/> (no navmesh here).</summary>
        internal void StepOnto(Vector3 destination) => Npc.Position = NpcSpread.Destination(Npc, destination);
    }

    /// <summary>An <see cref="IGameData"/> with only <see cref="IGameData.RootPath"/>; the movement verbs touch nothing else.</summary>
    public class RootOnlyGameData : DispatchProxy
    {
        string _root = string.Empty;

        public static IGameData Create(string root)
        {
            IGameData proxy = Create<IGameData, RootOnlyGameData>();
            ((RootOnlyGameData)(object)proxy)._root = root;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == "get_" + nameof(IGameData.RootPath)
                ? _root
                : throw new NotSupportedException("Compiling touched IGameData." + targetMethod?.Name);
    }

    sealed class Items : IItemBuilder
    {
        int _next = 38100000;

        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null) => new()
            {
                LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount,
                InstanceId = instanceId, Identity = identity ?? Identity.None, Definition = CreateTemplate(lowId, highId, quality)
            };

        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
        {
            Item item = Create(lowId, highId, quality, source, stackCount);
            item.AssignInstanceId(Interlocked.Increment(ref _next));
            return item;
        }

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }
}
