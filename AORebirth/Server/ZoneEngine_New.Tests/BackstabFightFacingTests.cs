namespace ZoneEngine_New.Tests
{
    using System;
    using System.Reflection;
    using System.Runtime.CompilerServices;

    using AORebirth.Enums;

    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using LostEden.Protocol.GameData;

    using ZoneEngine_New.Core.Characters;
    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Helpers;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Playfield;

    using Quaternion = AORebirth.Core.Vector.Quaternion;
    using Vector3 = AORebirth.Core.Vector.Vector3;

    /// <summary>
    /// A mob standing in a fight turns to face its target on the server as every client turns it (the client's
    /// fight facing), so Backstab's "behind" is judged against the heading the players see.
    /// </summary>
    [TestClass]
    public sealed class BackstabFightFacingTests
    {
        const double Tick = 0.1;

        [TestMethod]
        public void MobTurnsToFaceItsTankSoTheRogueOppositeIsBehindIt()
        {
            using ServiceProvider services = new ServiceCollection().AddSingleton(new DynelRegistry()).BuildServiceProvider();
            Playfield playfield = Playfield(services);
            Player tank = PlayerAt(18);
            Player rogue = PlayerAt(19);
            NpcCharacter mob = Npc(1);
            Place(playfield, services, tank, rogue, mob);

            // The mob faces +Z; the tank walked round to its back (-Z) and the rogue stands at its front (+Z).
            mob.Position = new Vector3(0, 0, 0);
            mob.Rotation = Yaw(0f);
            tank.Position = new Vector3(0, 0, -2);
            rogue.Position = new Vector3(0, 0, 2);
            mob.SetFightingTarget(tank.Identity);

            Assert.AreEqual(ClientFeedback.SpecialMustBeBehind, BackstabBlocker(rogue, mob));

            TickFacing(mob, seconds: 2.0);

            Assert.AreEqual(MathF.PI, MathF.Abs(YawOf(mob)), 0.01f, "the mob faces its tank");
            Assert.IsTrue(SpecialAttacks.IsBehind(rogue, mob));
            Assert.IsNull(BackstabBlocker(rogue, mob), "Backstab goes off from behind the turned mob");
        }

        [TestMethod]
        public void TurnWaitsForTheCheckAndTakesTheShortWayAtTheClientRate()
        {
            using ServiceProvider services = new ServiceCollection().AddSingleton(new DynelRegistry()).BuildServiceProvider();
            Playfield playfield = Playfield(services);
            Player tank = PlayerAt(18);
            NpcCharacter mob = Npc(1);
            Place(playfield, services, tank, mob);

            mob.Position = new Vector3(0, 0, 0);
            mob.Rotation = Yaw(0f);
            tank.Position = new Vector3(-2, 0, 0); // a quarter turn to the mob's -X side
            mob.SetFightingTarget(tank.Identity);

            TickFacing(mob, seconds: 0.6);
            Assert.AreEqual(0f, YawOf(mob), 1e-4f, "nothing turns before the 0.67 s check");

            TickFacing(mob, seconds: 0.1); // the check fires and starts the turn
            TickFacing(mob, seconds: 0.1); // one step at 7.2 rad/s
            Assert.AreEqual(-0.72f, YawOf(mob), 0.01f, "the short way, at the client's turn rate");

            TickFacing(mob, seconds: 0.5);
            Assert.AreEqual(-MathF.PI / 2f, YawOf(mob), 0.01f);
        }

        [TestMethod]
        public void SmallOffsetsCastingAndNoTargetLeaveTheHeading()
        {
            using ServiceProvider services = new ServiceCollection().AddSingleton(new DynelRegistry()).BuildServiceProvider();
            Playfield playfield = Playfield(services);
            Player tank = PlayerAt(18);
            NpcCharacter mob = Npc(1);
            Place(playfield, services, tank, mob);

            mob.Position = new Vector3(0, 0, 0);
            mob.Rotation = Yaw(0f);

            // Inside the 0.26 rad slack.
            tank.Position = new Vector3(0.4, 0, 2);
            mob.SetFightingTarget(tank.Identity);
            TickFacing(mob, seconds: 2.0);
            Assert.AreEqual(0f, YawOf(mob), 1e-4f);

            // Behind it, but casting.
            tank.Position = new Vector3(0, 0, -2);
            for (double t = 0; t < 2.0; t += Tick)
                mob.Motor.TickFightFacing(Tick, tank, casting: true);
            Assert.AreEqual(0f, YawOf(mob), 1e-4f);

            // Out of the fight.
            mob.SetFightingTarget(Identity.None);
            TickFacing(mob, seconds: 2.0);
            Assert.AreEqual(0f, YawOf(mob), 1e-4f);
        }

        [TestMethod]
        public void PlayerFightingAnotherPlayerTurnsSoABackstabFromTheOtherSideLands()
        {
            using ServiceProvider services = new ServiceCollection().AddSingleton(new DynelRegistry()).BuildServiceProvider();
            Playfield playfield = Playfield(services);
            Player fighter = PlayerAt(18);
            Player opponent = PlayerAt(19);
            Player rogue = PlayerAt(20);
            Place(playfield, services, fighter, opponent, rogue);

            // PvP: the fighter faces +Z, its opponent is at its back and the rogue at its front.
            fighter.Position = new Vector3(0, 0, 0);
            fighter.Rotation = Yaw(0f);
            opponent.Position = new Vector3(0, 0, -2);
            rogue.Position = new Vector3(0, 0, 2);
            fighter.SetFightingTarget(opponent.Identity);

            Assert.AreEqual(ClientFeedback.SpecialMustBeBehind, BackstabBlocker(rogue, fighter));

            TickFacing(fighter, seconds: 2.0);

            Assert.AreEqual(MathF.PI, MathF.Abs(YawOf(fighter)), 0.01f, "the player faces who they fight");
            Assert.IsNull(BackstabBlocker(rogue, fighter));
        }

        [TestMethod]
        public void FlyingPlayerTurnKeepsItsPitch()
        {
            using ServiceProvider services = new ServiceCollection().AddSingleton(new DynelRegistry()).BuildServiceProvider();
            Playfield playfield = Playfield(services);
            Player player = PlayerAt(18);
            NpcCharacter mob = Npc(1);
            Place(playfield, services, player, mob);

            // Facing +Z, nosed 0.3 rad down (pitch about X), with the mob a quarter turn to the +X side.
            player.Position = new Vector3(0, 0, 0);
            player.Rotation = new Quaternion(MathF.Sin(0.15f), 0, 0, MathF.Cos(0.15f));
            mob.Position = new Vector3(2, 0, 0);
            player.SetFightingTarget(mob.Identity);

            TickFacing(player, seconds: 2.0);

            var forward = (Vector3)player.Rotation.RotateVector3(Vector3.AxisZ);
            Assert.AreEqual(1.0, forward.x / Math.Sqrt((forward.x * forward.x) + (forward.z * forward.z)), 0.01, "turned to the mob");
            Assert.AreEqual(-MathF.Sin(0.3f), forward.y, 0.01, "pitch kept");
        }

        // Character.Tick's own call: the resolved fight target and whether a nano is being cast.
        static void TickFacing(Character character, double seconds)
        {
            for (double t = 0; t < seconds - 1e-9; t += Tick)
            {
                Character? target = character.FightingTarget.Instance != 0 ? character.TryResolveFightingTarget() : null;
                character.Motor.TickFightFacing(Tick, target, character.IsCastingNano);
            }
        }

        static int? BackstabBlocker(Character attacker, Character target)
            => (int?)typeof(SpecialAttacks)
                .GetMethod("StealthStrikeBlocker", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [attacker, target, CharacterStat.Backstab]);

        static Quaternion Yaw(float radians) => new(0, MathF.Sin(radians / 2f), 0, MathF.Cos(radians / 2f));

        static float YawOf(Character character)
        {
            Quaternion q = character.Rotation;
            return MathF.Atan2(2f * (float)((q.w * q.y) + (q.x * q.z)), 1f - (2f * (float)((q.y * q.y) + (q.z * q.z))));
        }

        static NpcCharacter Npc(int instance)
            => new(new Identity { Type = IdentityType.SimpleChar, Instance = 900000 + instance }, new Items());

        static Player PlayerAt(int instance)
            => new(new Identity { Type = IdentityType.SimpleChar, Instance = instance }, new Logger(), new Items()) { FirstName = "Test" };

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
            public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
                Identity? identity = null, byte[]? statsBlob = null) => throw new NotSupportedException();

            public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
                => throw new NotSupportedException();

            public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };

            public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
        }

        static void Place(Playfield playfield, ServiceProvider services, params Character[] characters)
        {
            DynelRegistry registry = services.GetRequiredService<DynelRegistry>();
            foreach (Character character in characters)
            {
                character.Playfield = playfield;
                registry.Register(character);
            }
        }

        static Playfield Playfield(ServiceProvider services)
        {
            var playfield = (Playfield)RuntimeHelpers.GetUninitializedObject(typeof(Playfield));
            typeof(Playfield).GetField("<Identity>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(playfield, new Identity { Type = IdentityType.Playfield2, Instance = 800 });
            typeof(Playfield).GetField("_serviceProvider", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(playfield, services);
            return playfield;
        }
    }
}
