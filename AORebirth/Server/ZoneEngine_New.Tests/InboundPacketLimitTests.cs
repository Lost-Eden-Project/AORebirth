namespace ZoneEngine_New.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;

    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages.N3Messages;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.MessageHandlers;
    using ZoneEngine_New.Core.Network;

    /// <summary>
    /// The inbound packet budget: normal play is never limited, and a /born (or any other) packet flood is dropped
    /// before it reaches the playfield tick and then disconnected, instead of stalling the zone for everyone.
    /// </summary>
    [TestClass]
    public sealed class InboundPacketLimitTests
    {
        const long Frequency = 1_000_000;

        static long At(double seconds) => (long)(seconds * Frequency);

        static int SendAt(InboundPacketLimiter limiter, double seconds, int count, InboundPacketVerdict? expected = null)
        {
            int accepted = 0;
            for (int i = 0; i < count; i++)
            {
                InboundPacketVerdict verdict = limiter.Consume(At(seconds));
                if (expected != null)
                    Assert.AreEqual(expected, verdict, $"packet {i} at {seconds:F3}s");
                if (verdict == InboundPacketVerdict.Accept)
                    accepted++;
            }
            return accepted;
        }

        [TestMethod]
        public void ZoneInBurstThenHeavyMovementAndMacrosAreNeverLimited()
        {
            var limiter = new InboundPacketLimiter(Frequency);

            // Zone-in: a full burst all at once.
            SendAt(limiter, 0, InboundPacketLimiter.Burst, InboundPacketVerdict.Accept);

            // A minute of constant mouse-turning while running (20/s), with a 50-line macro fired every 2 seconds.
            for (int tick = 1; tick <= 60 * 20; tick++)
            {
                double t = 1 + (tick / 20.0);
                SendAt(limiter, t, 1, InboundPacketVerdict.Accept);
                if (tick % 40 == 0)
                    SendAt(limiter, t, 50, InboundPacketVerdict.Accept);
            }

            Assert.AreEqual(0, limiter.Dropped);
        }

        [TestMethod]
        public void SustainedRateJustUnderTheRefillIsNeverLimited()
        {
            var limiter = new InboundPacketLimiter(Frequency);
            for (int i = 0; i < 99 * 120; i++)
                SendAt(limiter, i / 99.0, 1, InboundPacketVerdict.Accept);
            Assert.AreEqual(0, limiter.Dropped);
        }

        [TestMethod]
        public void LagSpikeDeliveringBufferedPacketsDropsBrieflyButNeverDisconnects()
        {
            var limiter = new InboundPacketLimiter(Frequency);

            // A stalled connection delivers 800 buffered packets at once: the bucket covers 500, the rest are dropped.
            int accepted = SendAt(limiter, 0, 800);
            Assert.AreEqual(InboundPacketLimiter.Burst, accepted);
            Assert.AreEqual(300, limiter.Dropped);

            // Normal play resumes: the bucket refills and nothing is dropped again or disconnected, even long after.
            for (int i = 1; i <= 20 * 30; i++)
                Assert.AreEqual(InboundPacketVerdict.Accept, limiter.Consume(At(0.5 + (i / 20.0))));
            Assert.AreEqual(300, limiter.Dropped);
        }

        [TestMethod]
        public void SustainedFloodIsDroppedThenDisconnectedAfterTheGracePeriod()
        {
            var limiter = new InboundPacketLimiter(Frequency);
            double firstDrop = -1;
            double disconnect = -1;
            int firstDrops = 0;
            int accepted = 0;

            // 2000 packets a second, a step every millisecond.
            for (int ms = 0; ms < 10_000 && disconnect < 0; ms++)
            {
                double t = ms / 1000.0;
                for (int i = 0; i < 2; i++)
                {
                    switch (limiter.Consume(At(t)))
                    {
                        case InboundPacketVerdict.Accept: accepted++; break;
                        case InboundPacketVerdict.FirstDrop: firstDrops++; firstDrop = t; break;
                        case InboundPacketVerdict.Disconnect: disconnect = t; break;
                    }
                    if (disconnect >= 0)
                        break;
                }
            }

            Assert.AreEqual(1, firstDrops, "The flood is logged once, not per packet.");
            Assert.IsTrue(firstDrop >= 0 && firstDrop < 0.3, $"first drop at {firstDrop}");
            Assert.IsTrue(disconnect >= firstDrop + InboundPacketLimiter.DisconnectAfter.TotalSeconds - 0.001,
                $"disconnected at {disconnect}, first drop at {firstDrop}");
            Assert.IsTrue(disconnect < firstDrop + InboundPacketLimiter.DisconnectAfter.TotalSeconds + 0.01,
                $"disconnected at {disconnect}, first drop at {firstDrop}");

            // Only the burst plus the refill ever reached the game.
            int allowed = InboundPacketLimiter.Burst + (int)Math.Ceiling(disconnect * InboundPacketLimiter.RefillPerSecond) + 1;
            Assert.IsTrue(accepted <= allowed, $"accepted {accepted} > {allowed}");
            Assert.IsTrue(limiter.PeakPerSecond >= 1900, $"peak {limiter.PeakPerSecond}");
        }

        [TestMethod]
        public void FloodInBurstsWithShortPausesStillDisconnects()
        {
            // Pausing a quarter second between bursts does not reset the grace period.
            var limiter = new InboundPacketLimiter(Frequency);
            InboundPacketVerdict last = InboundPacketVerdict.Accept;
            for (int burst = 0; burst < 40 && last != InboundPacketVerdict.Disconnect; burst++)
            {
                for (int i = 0; i < 600 && last != InboundPacketVerdict.Disconnect; i++)
                    last = limiter.Consume(At(burst * 0.25));
            }
            Assert.AreEqual(InboundPacketVerdict.Disconnect, last);
        }

        [TestMethod]
        [Timeout(30_000)]
        public async Task BornSpamOverTheWireIsCutOffAndDisconnectedWhileNormalUseGoesThrough()
        {
            byte[] born = BornPacket();

            // Normal use: a 400-packet burst, then 60 /born a second for two seconds. Every one reaches dispatch.
            await using (var peer = await Peer.ConnectAsync())
            {
                var clock = Stopwatch.StartNew();
                await peer.SendAsync(born, 400);
                for (int i = 1; i <= 120; i++)
                {
                    double due = i / 60.0;
                    while (clock.Elapsed.TotalSeconds < due)
                        await Task.Delay(1);
                    await peer.SendAsync(born, 1);
                }

                await peer.WaitForDispatchedAsync(520);
                Assert.AreEqual(520, peer.Log.Dispatched);
                Assert.AreEqual(0, peer.Log.Count("Inbound packet limit reached"));
                Assert.AreNotEqual(SessionState.Closed, peer.Session.State);
            }

            // Flood: /born as fast as the socket takes it. Dropped past the budget, then the connection is closed.
            await using (var peer = await Peer.ConnectAsync())
            {
                var clock = Stopwatch.StartNew();
                long sent = 0;
                while (peer.Session.State != SessionState.Closed && clock.Elapsed < TimeSpan.FromSeconds(15))
                {
                    try { await peer.SendAsync(born, 50); }
                    catch (Exception exception) when (exception is SocketException or System.IO.IOException) { break; }
                    sent += 50;
                }

                await peer.WaitForClosedAsync();
                double seconds = clock.Elapsed.TotalSeconds;
                Assert.AreEqual(SessionState.Closed, peer.Session.State);
                Assert.IsTrue(seconds >= InboundPacketLimiter.DisconnectAfter.TotalSeconds, $"closed after {seconds:F2}s");
                Assert.AreEqual(1, peer.Log.Count("Inbound packet limit reached"));
                Assert.AreEqual(1, peer.Log.Count("Inbound packet flood: closing connection"));

                int allowed = InboundPacketLimiter.Burst + (int)Math.Ceiling(seconds * InboundPacketLimiter.RefillPerSecond) + 1;
                Assert.IsTrue(peer.Log.Dispatched <= allowed, $"dispatched {peer.Log.Dispatched} of {sent} sent, allowed {allowed}");
                Assert.IsTrue(sent > allowed * 2, $"the flood only sent {sent}");
            }
        }

        /// <summary>What the client sends for /born: CharacterAction 0x86 (Gamecode.dll 0x10041e4a).</summary>
        static byte[] BornPacket()
        {
            var self = new Identity { Type = (IdentityType)50000, Instance = 123456 };
            var message = new CharacterActionMessage
            {
                Identity = self,
                Action = (CharacterActionType)0x86,
                Target = Identity.None,
                Unknown2 = string.Empty
            };
            byte[] packet = new ZoneMessageCodec().Serialize(message, 123456, 1);
            int padding = (4 - (packet.Length % 4)) % 4;
            Array.Resize(ref packet, packet.Length + padding);
            return packet;
        }

        sealed class Peer : IAsyncDisposable
        {
            readonly TcpListener _listener;
            readonly TcpClient _client;
            readonly CancellationTokenSource _lifetime = new();
            readonly Task _run;

            Peer(TcpListener listener, TcpClient client, ZoneSession session, CountingLogger log)
            {
                _listener = listener;
                _client = client;
                Session = session;
                Log = log;
                _run = session.RunAsync(_lifetime.Token);
            }

            public ZoneSession Session { get; }

            public CountingLogger Log { get; }

            public static async Task<Peer> ConnectAsync()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
                var endpoint = (IPEndPoint)listener.LocalEndpoint;
                await client.ConnectAsync(endpoint.Address, endpoint.Port);
                Socket accepted = await listener.AcceptSocketAsync();
                var log = new CountingLogger();
                // No login: every admitted packet reaches the dispatcher, which drops it with one "Dropped inbound" line
                // because the session is not in play. That line counts exactly the packets the limiter let through.
                var login = (ZoneLoginHandler)RuntimeHelpers.GetUninitializedObject(typeof(ZoneLoginHandler));
                var session = new ZoneSession(Guid.NewGuid(), accepted, new ZoneMessageCodec(), new ZoneMessageDispatcher(login, log), log);
                return new Peer(listener, client, session, log);
            }

            public async Task SendAsync(byte[] packet, int count)
            {
                byte[] batch = new byte[packet.Length * count];
                for (int i = 0; i < count; i++)
                    Buffer.BlockCopy(packet, 0, batch, i * packet.Length, packet.Length);
                await _client.GetStream().WriteAsync(batch);
            }

            public async Task WaitForDispatchedAsync(int count)
            {
                var deadline = Stopwatch.StartNew();
                while (Log.Dispatched < count && deadline.Elapsed < TimeSpan.FromSeconds(5))
                    await Task.Delay(5);
            }

            public async Task WaitForClosedAsync()
            {
                var deadline = Stopwatch.StartNew();
                while (Session.State != SessionState.Closed && deadline.Elapsed < TimeSpan.FromSeconds(5))
                    await Task.Delay(5);
            }

            public async ValueTask DisposeAsync()
            {
                Session.Close();
                _lifetime.Cancel();
                _client.Dispose();
                try { await _run.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { }
                _listener.Stop();
                _lifetime.Dispose();
            }
        }

        sealed class CountingLogger : IZoneLogger
        {
            readonly ConcurrentQueue<string> _warnings = new();
            int _dispatched;

            public int Dispatched => Volatile.Read(ref _dispatched);

            public int Count(string fragment) => _warnings.Count(line => line.Contains(fragment, StringComparison.Ordinal));

            public void Debug(string message) { }

            public void Info(string message) { }

            public void Warn(string message)
            {
                if (message.StartsWith("Dropped inbound CharacterActionMessage", StringComparison.Ordinal))
                    Interlocked.Increment(ref _dispatched);
                else
                    _warnings.Enqueue(message);
            }

            public void Error(string message) => _warnings.Enqueue("ERROR " + message);

            public void Error(Exception exception, string message) => _warnings.Enqueue("ERROR " + message + " " + exception);

            public IZoneLogger CreateForPlayfield(int playfieldId) => this;
        }
    }
}
