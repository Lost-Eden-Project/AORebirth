namespace ZoneEngine_New.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Net;

    using AORebirth.World.Contracts.V1;

    using Microsoft.Extensions.Logging.Abstractions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using WorldEngine;
    using WorldEngine.State;

    /// <summary>
    /// World moves the duty (the database-wide sweeps and the modules' primary-only work) from one zone to another, as a
    /// zone swap's drain does. Two zones must never hold it at once: a zone that confirms letting go ("duty-ack/1") is
    /// waited for, up to a timeout or its stream dropping; an older build is not.
    /// </summary>
    [TestClass]
    public sealed class WorldDutyHandoverTests
    {
        long _now = 1_000_000;

        [TestMethod]
        public void DutyMovesOnlyAfterTheOldHolderConfirms()
        {
            (WorldState world, ServiceInstance a, ServiceInstance b) = TwoZones(ack: true);

            a.Draining = true;
            world.Sweep();

            CollectionAssert.AreEqual(new[] { false }, Duty(a), "The draining holder is told to let go.");
            CollectionAssert.AreEqual(Array.Empty<bool>(), Duty(b), "Nobody gets the duty before the old holder confirms.");
            Assert.IsNull(Holder(world));

            world.Sweep();
            CollectionAssert.AreEqual(Array.Empty<bool>(), Duty(b), "A sweep inside the timeout keeps waiting.");

            world.DutyReleased(a);
            CollectionAssert.AreEqual(new[] { true }, Duty(b));
            Assert.AreEqual("zone-b", Holder(world));
            CollectionAssert.AreEqual(Array.Empty<bool>(), Duty(a), "The old holder is told once.");
        }

        [TestMethod]
        public void DutyMovesAfterTheTimeoutWithoutConfirmation()
        {
            (WorldState world, ServiceInstance a, ServiceInstance b) = TwoZones(ack: true);
            a.Draining = true;
            world.Sweep();
            Duty(a);

            _now += (long)WorldState.DutyReleaseTimeout.TotalMilliseconds - 1;
            KeepAlive(world, a, b);
            world.Sweep();
            CollectionAssert.AreEqual(Array.Empty<bool>(), Duty(b));

            _now += 2;
            KeepAlive(world, a, b);
            world.Sweep();
            CollectionAssert.AreEqual(new[] { true }, Duty(b));
            Assert.AreEqual("zone-b", Holder(world));
        }

        [TestMethod]
        public void DutyMovesAtOnceWhenTheOldHoldersStreamDrops()
        {
            (WorldState world, ServiceInstance a, ServiceInstance b) = TwoZones(ack: true);
            a.Draining = true;
            world.Sweep();
            Duty(a);

            // A zone drops its duty when its registry stream ends.
            world.Disconnected(a);
            CollectionAssert.AreEqual(new[] { true }, Duty(b));
            Assert.AreEqual("zone-b", Holder(world));
        }

        [TestMethod]
        public void AnOlderBuildIsNotWaitedFor()
        {
            (WorldState world, ServiceInstance a, ServiceInstance b) = TwoZones(ack: false);
            a.Draining = true;
            world.Sweep();

            CollectionAssert.AreEqual(new[] { false }, Duty(a));
            CollectionAssert.AreEqual(new[] { true }, Duty(b), "A build that never confirms gets the old immediate hand-over.");
        }

        [TestMethod]
        public void AConfirmationFromAnotherInstanceIsIgnored()
        {
            (WorldState world, ServiceInstance a, ServiceInstance b) = TwoZones(ack: true);
            a.Draining = true;
            world.Sweep();
            Duty(a);

            world.DutyReleased(b);
            CollectionAssert.AreEqual(Array.Empty<bool>(), Duty(b));
            Assert.IsNull(Holder(world));

            world.DutyReleased(a);
            world.DutyReleased(a);
            CollectionAssert.AreEqual(new[] { true }, Duty(b), "A repeated confirmation grants nothing twice.");
        }

        (WorldState World, ServiceInstance A, ServiceInstance B) TwoZones(bool ack)
        {
            var options = new WorldOptions
            {
                ListenAddress = IPAddress.Loopback,
                Port = 7600,
                StatusPort = 0,
                LeaseTtl = TimeSpan.FromSeconds(60),
                LeaseGrace = TimeSpan.FromSeconds(5),
                Heartbeat = TimeSpan.FromSeconds(1),
                HandoffLifetime = TimeSpan.FromSeconds(30),
                Token = string.Empty
            };
            var world = new WorldState(options, NullLogger<WorldState>.Instance, () => _now);
            ServiceInstance a = Zone(world, "zone-a", 7501, ack);
            Assert.AreEqual("zone-a", Holder(world));
            CollectionAssert.AreEqual(new[] { true }, Duty(a));
            ServiceInstance b = Zone(world, "zone-b", 7502, ack);
            CollectionAssert.AreEqual(Array.Empty<bool>(), Duty(b), "The lower slot keeps the duty.");
            return (world, a, b);
        }

        ServiceInstance Zone(WorldState world, string name, uint port, bool ack)
        {
            var instance = new ServiceInstance(name, ServiceRole.RoleZone, Guid.NewGuid().ToString("N"), _now);
            var register = new Register
            {
                Role = ServiceRole.RoleZone,
                Instance = name,
                Game = new Endpoint { Address = "127.0.0.1", Port = port },
                Build = "build",
                GameData = "gamedata",
                Ready = true
            };
            register.Capabilities.Add("swap/1");
            if (ack) register.Capabilities.Add(WorldState.DutyAckCapability);
            world.Register(instance, register);
            world.AfterRegister();
            return instance;
        }

        void KeepAlive(WorldState world, params ServiceInstance[] instances)
        {
            foreach (ServiceInstance instance in instances)
                world.Heartbeat(instance, new Heartbeat { Ready = true });
        }

        static string? Holder(WorldState world)
            => (string?)world.Snapshot().GetType().GetProperty("dutyHolder")!.GetValue(world.Snapshot());

        /// <summary>The duty assignments pushed to the instance since the last call.</summary>
        static bool[] Duty(ServiceInstance instance)
        {
            var duty = new List<bool>();
            while (instance.Outbound.TryRead(out WorldToService? message))
                if (message.BodyCase == WorldToService.BodyOneofCase.Duty)
                    duty.Add(message.Duty.Holder);
            return duty.ToArray();
        }
    }
}
