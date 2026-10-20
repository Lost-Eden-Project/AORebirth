namespace AORebirth.World.Contracts
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;

    using AORebirth.World.Contracts.V1;

    using Grpc.Core;

    /// <summary>
    /// A non-zone service's registry stream (ChatEngine): keeps its session alive with heartbeats, re-asserts the
    /// characters it holds after a reconnect, and surfaces World's pushes. Leases it holds expire with it.
    /// </summary>
    public sealed class WorldServiceLink : IDisposable
    {
        static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

        readonly WorldChannel _channel;
        readonly ServiceRole _role;
        readonly string _instance;
        readonly CancellationTokenSource _stop = new();
        readonly ConcurrentDictionary<int, LeaseToken> _leases = new();
        readonly TaskCompletionSource _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly Action<string> _log;
        Task? _loop;
        string _session = string.Empty;
        long _epoch;
        volatile bool _connected;

        public WorldServiceLink(string url, ServiceRole role, string instance, Action<string>? log = null)
        {
            _channel = new WorldChannel(url, role == ServiceRole.RoleChat ? "chat" : "service");
            _role = role;
            _instance = instance;
            _log = log ?? (_ => { });
        }

        public WorldChannel Channel => _channel;

        public bool IsConnected => _connected;

        /// <summary>World pushes other than heartbeat acknowledgements (chat events, team updates, presence).</summary>
        public event Action<WorldToService>? MessageReceived;

        public void Start()
        {
            _loop ??= Task.Run(() => RunAsync(_stop.Token));
        }

        public bool WaitUntilRegistered(TimeSpan timeout) => _registered.Task.Wait(timeout);

        /// <summary>A character this service holds online without a zone (a chat bot).</summary>
        public bool TryAcquire(int characterId, out string reason)
        {
            ServiceLeaseReply reply = _channel.Leases.AcquireService(new ServiceLeaseRequest { CharacterId = characterId },
                _channel.Call(CallTimeout));
            reason = reply.Reason;
            if (!reply.Accepted) return false;
            _leases[characterId] = reply.Token;
            return true;
        }

        public void Release(int characterId)
        {
            if (!_leases.TryRemove(characterId, out LeaseToken? token)) return;
            try
            {
                _channel.Leases.Release(new ReleaseRequest { CharacterId = characterId, Token = token }, _channel.Call(CallTimeout));
            }
            catch (RpcException)
            {
                // The next registration drops what is no longer asserted.
            }
        }

        /// <summary>Which of these characters anyone owns (a zone, a bot, or a delete in progress).</summary>
        public ISet<int> Owned(IEnumerable<int> characterIds)
        {
            var query = new LeaseQuery();
            query.CharacterIds.AddRange(characterIds);
            return new HashSet<int>(_channel.Leases.Query(query, _channel.Call(CallTimeout)).Owned);
        }

        async Task RunAsync(CancellationToken stop)
        {
            TimeSpan backoff = TimeSpan.FromMilliseconds(250);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await SessionAsync(stop).ConfigureAwait(false);
                    backoff = TimeSpan.FromMilliseconds(250);
                }
                catch (Exception exception) when (!stop.IsCancellationRequested)
                {
                    if (_connected) _log("World link lost: " + exception.Message);
                }
                finally
                {
                    _connected = false;
                }

                try { await Task.Delay(backoff, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromMilliseconds(Math.Min(2000, backoff.TotalMilliseconds * 2));
            }
        }

        async Task SessionAsync(CancellationToken stop)
        {
            using var session = CancellationTokenSource.CreateLinkedTokenSource(stop);
            _channel.RegistrySession = string.Empty;
            using AsyncDuplexStreamingCall<ServiceToWorld, WorldToService> call = _channel.Registry.Connect(_channel.Stream(session.Token));
            var outbound = System.Threading.Channels.Channel.CreateUnbounded<ServiceToWorld>(new UnboundedChannelOptions { SingleReader = true });
            Task writer = Task.Run(async () =>
            {
                await foreach (ServiceToWorld message in outbound.Reader.ReadAllAsync(session.Token).ConfigureAwait(false))
                    await call.RequestStream.WriteAsync(message, session.Token).ConfigureAwait(false);
            }, session.Token);

            var register = new Register { Role = _role, Instance = _instance, LastEpoch = Interlocked.Read(ref _epoch), PreviousSession = _session };
            foreach (KeyValuePair<int, LeaseToken> lease in _leases)
                register.Leases.Add(new LeaseAssertion { CharacterId = lease.Key, Token = lease.Value });
            outbound.Writer.TryWrite(new ServiceToWorld { Register = register });
            if (!await call.ResponseStream.MoveNext(session.Token).ConfigureAwait(false)
                || call.ResponseStream.Current.BodyCase != WorldToService.BodyOneofCase.Accepted)
                throw new InvalidOperationException("World did not accept the registration.");

            RegisterAccepted accepted = call.ResponseStream.Current.Accepted;
            Interlocked.Exchange(ref _epoch, accepted.Epoch);
            _session = accepted.Session;
            _channel.RegistrySession = accepted.Session;
            foreach (LeaseAssertion lease in accepted.Leases) _leases[lease.CharacterId] = lease.Token;
            foreach (LeaseAssertion lost in accepted.LostLeases) _leases.TryRemove(lost.CharacterId, out _);
            _connected = true;
            Volatile.Write(ref _outbound, outbound.Writer);
            _registered.TrySetResult();
            _log("World registered " + _instance + " epoch=" + accepted.Epoch + " leases=" + accepted.Leases.Count);

            int interval = accepted.HeartbeatMs > 0 ? accepted.HeartbeatMs : 2000;
            Task heartbeat = Task.Run(async () =>
            {
                ulong sequence = 0;
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));
                do outbound.Writer.TryWrite(new ServiceToWorld { Heartbeat = new Heartbeat { Sequence = ++sequence } });
                while (await timer.WaitForNextTickAsync(session.Token).ConfigureAwait(false));
            }, session.Token);

            try
            {
                while (await call.ResponseStream.MoveNext(session.Token).ConfigureAwait(false))
                {
                    WorldToService message = call.ResponseStream.Current;
                    if (message.BodyCase is WorldToService.BodyOneofCase.HeartbeatAck) continue;
                    if (message.BodyCase == WorldToService.BodyOneofCase.LeaseRevoked)
                        _leases.TryRemove(message.LeaseRevoked.CharacterId, out _);
                    try { MessageReceived?.Invoke(message); }
                    catch (Exception exception) { _log("World push handler failed: " + exception.Message); }
                }

                throw new InvalidOperationException("World closed the registry stream.");
            }
            finally
            {
                _connected = false;
                Interlocked.CompareExchange(ref _outbound, null, outbound.Writer);
                session.Cancel();
                outbound.Writer.TryComplete();
                try { await Task.WhenAll(writer, heartbeat).ConfigureAwait(false); }
                catch (Exception) { }
            }
        }

        public IReadOnlyCollection<int> HeldCharacters => _leases.Keys.ToList();

        ChannelWriter<ServiceToWorld>? _outbound;

        /// <summary>Chat traffic on the registry stream, in order. False while World is unreachable.</summary>
        public bool Publish(ChatEvent chat)
            => _connected && Volatile.Read(ref _outbound)?.TryWrite(new ServiceToWorld { Chat = chat }) == true;

        public void Dispose()
        {
            _stop.Cancel();
            try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
            catch (Exception) { }
            _channel.Dispose();
            _stop.Dispose();
        }
    }
}
