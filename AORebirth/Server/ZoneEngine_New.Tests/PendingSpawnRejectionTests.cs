namespace ZoneEngine_New.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Threading;

    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages;

    using ZoneEngine_New.Core.Characters;
    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;

    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// Lost Eden 2026-11-25, pf 4605: a GM character whose Body Development was set to 999,999,999 overflowed max health,
    /// came out of the rebase with Health unset, and the spawn guard threw out of the inbound drain. The whole playfield
    /// tick failed and the client sat on the loading screen. The spawn must still be refused, as that player's failed
    /// login only.
    /// </summary>
    [TestClass]
    public sealed class PendingSpawnRejectionTests
    {
        [TestMethod]
        public void Rejected_spawn_closes_that_login_and_the_rest_of_the_tick_still_runs()
        {
            var logger = new RecordingLogger();
            Playfield playfield = BlankPlayfield(4605);
            SpawnService spawns = Spawns(logger, playfield);
            var queue = new PlayfieldInboundQueue();
            var session = new LoadingSession();
            bool laterItemRan = false;

            Assert.IsTrue(queue.TryEnqueue(new PendingSpawnInboundItem { Session = session, Hydration = RejectedHydration() }));
            Assert.IsTrue(queue.TryEnqueue(new PlayfieldActionInboundItem { Action = () => laterItemRan = true }));

            queue.Drain(null!, spawns, playfield);

            Assert.AreEqual(SessionState.Closed, session.State, "the rejected login is closed, not left loading");
            Assert.IsNull(session.Player, "no player is published for a rejected spawn");
            Assert.AreEqual(0, session.Sent, "nothing reaches the wire for a rejected spawn");
            Assert.IsTrue(laterItemRan, "the items queued after the rejected spawn still run in the same tick");
            Assert.AreEqual(1, logger.Errors.Count);
            StringAssert.Contains(logger.Errors[0], "ZoneLogin rejected character=9951 playfield=4605");
        }

        static CharacterHydrationResult RejectedHydration() => new()
        {
            Character = new CharacterRecord { Id = 9951, Name = "RejectedSpawn", Playfield = 4605, X = 100, Y = 0, Z = 100, HeadingW = 1 },
            // Health and every other required spawn stat are missing: SpawnPlayer refuses it.
            Stats = Array.Empty<StatRecord>(),
        };

        static SpawnService Spawns(IZoneLogger logger, Playfield playfield)
        {
            var manager = (PlayfieldManager)RuntimeHelpers.GetUninitializedObject(typeof(PlayfieldManager));
            Set(manager, "_sync", new Lock());
            Set(manager, "_playersByCharacterId", new Dictionary<int, Player>());
            var spawns = (SpawnService)RuntimeHelpers.GetUninitializedObject(typeof(SpawnService));
            Set(spawns, "_logger", logger);
            Set(spawns, "_playfield", playfield);
            Set(spawns, "_playfieldManager", manager);
            return spawns;
        }

        static Playfield BlankPlayfield(int id)
        {
            var playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
            typeof(Playfield).GetField("<Identity>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(playfield, new Identity { Type = IdentityType.Playfield2, Instance = id });
            return playfield;
        }

        static void Set(object target, string field, object value)
            => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

        sealed class LoadingSession : IZoneSession
        {
            public int Sent;
            public SessionState State { get; set; } = SessionState.Loading;
            public Player? Player { get; private set; }
            public void BindPlayer(Player player) => Player = player;
            public void UnbindPlayer() => Player = null;
            public void Close() => State = SessionState.Closed;
            public bool TransferToPlayfield(Playfield destination, Vector3 landing) => throw new AssertFailedException();
            public void Send(byte[] packet) => Sent++;
            public void Send(MessageBody body) => Sent++;
            public void Send(MessageBody body, int sender, int receiver) => Sent++;
            public void SendInitiateCompression() => Sent++;
        }

        sealed class RecordingLogger : IZoneLogger
        {
            public readonly List<string> Errors = new();
            public void Debug(string message) { }
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message) => Errors.Add(message);
            public void Error(Exception exception, string message) => Errors.Add(message);
            public IZoneLogger CreateForPlayfield(int playfieldId) => this;
        }
    }
}
