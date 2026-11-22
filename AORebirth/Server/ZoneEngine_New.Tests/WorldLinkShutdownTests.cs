namespace ZoneEngine_New.Tests
{
    using System;
    using System.Diagnostics;
    using System.Reflection;
    using System.Threading.Channels;

    using AORebirth.World.Contracts.V1;

    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.World;

    /// <summary>
    /// The zone's World link at shutdown and when World takes the duty away. Shutdown used to end with exit code 3: the
    /// release flush asked the single-reader channel for a count it cannot give (NotSupportedException), so lease releases
    /// were never waited for, and the service container disposed the link a second time (ObjectDisposedException).
    /// </summary>
    [TestClass]
    public sealed class WorldLinkShutdownTests
    {
        [ClassInitialize]
        public static void Configure(TestContext context)
        {
            if (ZoneInstanceOptions.CurrentOrNull == null)
                ZoneInstanceOptions.Configure(new[] { "--instance", "zone-test", "--zone-port", "7501" });
        }

        [TestMethod]
        public void FlushReleasesWaitsForQueuedReleasesWithoutThrowing()
        {
            using var link = new WorldLink(new SilentLogger());
            link.FlushReleases(TimeSpan.FromMilliseconds(50));
            Assert.AreEqual(0, link.PendingReleases);

            Queue(link, 42);
            Assert.AreEqual(1, link.PendingReleases);
            var timer = Stopwatch.StartNew();
            // Nothing sends it (the link was never started): the flush waits its timeout, then returns.
            link.FlushReleases(TimeSpan.FromMilliseconds(200));
            Assert.IsTrue(timer.ElapsedMilliseconds >= 150, "The flush waits while a release is still queued.");
            Assert.AreEqual(1, link.PendingReleases);
        }

        [TestMethod]
        public void DisposingTwiceDoesNotThrow()
        {
            var link = new WorldLink(new SilentLogger());
            link.Dispose();
            link.Dispose();
        }

        [TestMethod]
        public void LosingTheDutyIsConfirmedToWorld()
        {
            using var link = new WorldLink(new SilentLogger());
            var outbound = Channel.CreateUnbounded<ServiceToWorld>();

            Handle(link, new WorldToService { Duty = new DutyAssignment { Holder = true } }, outbound.Writer);
            Assert.IsTrue(link.IsDutyHolder);
            Assert.IsFalse(outbound.Reader.TryRead(out _), "Gaining the duty sends nothing back.");

            Handle(link, new WorldToService { Duty = new DutyAssignment { Holder = false } }, outbound.Writer);
            Assert.IsFalse(link.IsDutyHolder);
            Assert.IsTrue(outbound.Reader.TryRead(out ServiceToWorld? reply));
            Assert.AreEqual(ServiceToWorld.BodyOneofCase.DutyReleased, reply!.BodyCase);
            Assert.IsFalse(outbound.Reader.TryRead(out _));
        }

        static void Queue(WorldLink link, int characterId)
            => typeof(WorldLink).GetMethod("QueueRelease", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(link, new object[] { characterId, new LeaseToken { Epoch = 1, Generation = 1 } });

        static void Handle(WorldLink link, WorldToService message, ChannelWriter<ServiceToWorld> outbound)
            => typeof(WorldLink).GetMethod("Handle", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(link, new object[] { message, outbound });

        sealed class SilentLogger : IZoneLogger
        {
            public void Debug(string message)
            {
            }

            public void Info(string message)
            {
            }

            public void Warn(string message)
            {
            }

            public void Error(string message)
            {
            }

            public void Error(Exception exception, string message)
            {
            }

            public IZoneLogger CreateForPlayfield(int playfieldId) => this;
        }
    }
}
