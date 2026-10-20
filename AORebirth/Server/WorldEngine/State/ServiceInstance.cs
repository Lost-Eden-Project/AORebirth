namespace WorldEngine.State
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;

    using AORebirth.World.Contracts.V1;

    /// <summary>
    /// One registered process (a zone engine instance or ChatEngine) and its push channel. Fields other than the
    /// outbound channel and pending replies are guarded by the <see cref="WorldState"/> lock.
    /// </summary>
    public sealed class ServiceInstance
    {
        readonly ConcurrentDictionary<ulong, TaskCompletionSource<ServiceReply>> _pending = new();
        /// <summary>Pushes an instance may fall behind by before World gives up on its stream.</summary>
        public const int OutboundCapacity = 50_000;

        readonly Channel<WorldToService> _outbound = Channel.CreateBounded<WorldToService>(
            new BoundedChannelOptions(OutboundCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

        public ServiceInstance(string name, ServiceRole role, string session, long nowMs)
        {
            Name = name;
            Role = role;
            Session = session;
            LastHeartbeatMs = nowMs;
            ConnectedSinceMs = nowMs;
        }

        public string Name { get; }

        public ServiceRole Role { get; }

        public string Session { get; }

        public int Slot { get; set; }

        public Endpoint? Game { get; set; }

        public string Build { get; set; } = string.Empty;

        public long LastHeartbeatMs { get; set; }

        public long ConnectedSinceMs { get; }

        public bool Connected { get; set; } = true;

        /// <summary>Superseded by a newer registration or expired; never routed to again.</summary>
        public bool Removed { get; set; }

        public int Players { get; set; }

        public Dictionary<int, int> PlayfieldLoads { get; } = new();

        public ChannelReader<WorldToService> Outbound => _outbound.Reader;

        /// <summary>
        /// Queues a push. A full queue means the instance stopped reading: vicinity chat is dropped, anything else
        /// ends the stream so the instance reconnects and re-registers, which rebuilds what it missed.
        /// </summary>
        public bool Send(WorldToService message)
        {
            if (Removed) return false;
            if (_outbound.Writer.TryWrite(message)) return true;
            bool droppable = message.BodyCase == WorldToService.BodyOneofCase.Chat
                && message.Chat.BodyCase == ChatEvent.BodyOneofCase.Vicinity;
            if (!droppable && Interlocked.Exchange(ref _overflowed, 1) == 0) Close();
            return false;
        }

        int _overflowed;
        readonly CancellationTokenSource _closed = new();

        /// <summary>Cancelled when the instance is closed: a push stuck on a stalled stream gives up.</summary>
        public CancellationToken Closed => _closed.Token;

        /// <summary>True once the push queue overflowed and the stream was ended.</summary>
        public bool Overflowed => Volatile.Read(ref _overflowed) != 0;

        /// <summary>Ends the push stream; the registry call returns once it drains.</summary>
        public void Close()
        {
            _outbound.Writer.TryComplete();
            try { _closed.Cancel(); }
            catch (ObjectDisposedException) { }
            foreach (KeyValuePair<ulong, TaskCompletionSource<ServiceReply>> pending in _pending)
                pending.Value.TrySetCanceled();
            _pending.Clear();
        }

        /// <summary>Sends a request over the push stream and waits for its reply; null on timeout or disconnect.</summary>
        public async Task<ServiceReply?> RequestAsync(ulong requestId, ServiceRequest request, TimeSpan timeout,
            CancellationToken cancellation)
        {
            var completion = new TaskCompletionSource<ServiceReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[requestId] = completion;
            try
            {
                request.RequestId = requestId;
                if (!Send(new WorldToService { Request = request })) return null;
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeoutSource.CancelAfter(timeout);
                try
                {
                    return await completion.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
            finally
            {
                _pending.TryRemove(requestId, out _);
            }
        }

        public void CompleteRequest(ServiceReply reply)
        {
            if (_pending.TryRemove(reply.RequestId, out TaskCompletionSource<ServiceReply>? completion))
                completion.TrySetResult(reply);
        }
    }
}
