namespace ChatEngine.Modules
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;

    using AORebirth.ChatModules.Channels;

    using NLog;

    /// <summary>
    /// One module's delivery of channel lines and consumed commands: a bounded queue the network threads add to without
    /// waiting, drained by the module's own thread. A slow or stuck module therefore delays only itself, never chat
    /// traffic or other modules. The thread starts with the first subscriber or registered command.
    /// </summary>
    internal sealed class ChatModuleDelivery
    {
        internal const int Capacity = 4096;

        internal const int MaxConsecutiveFailures = 10;

        private readonly string moduleId;

        private readonly Logger log;

        /// <summary>Holds <see cref="ChatChannelMessage"/> and <see cref="PendingCommand"/> items, in arrival order.</summary>
        private readonly BlockingCollection<object> queue =
            new BlockingCollection<object>(new ConcurrentQueue<object>(), Capacity);

        private readonly object sync = new object();

        private Subscriber[] subscribers = Array.Empty<Subscriber>();

        private Thread thread;

        private bool stopped;

        private long dropped;

        public ChatModuleDelivery(string moduleId, Logger log)
        {
            this.moduleId = moduleId;
            this.log = log;
        }

        public string ModuleId => this.moduleId;

        public bool HasSubscribers => Volatile.Read(ref this.subscribers).Length > 0;

        public void Subscribe(Action<ChatChannelMessage> handler)
        {
            if (handler == null)
            {
                return;
            }

            lock (this.sync)
            {
                if (this.stopped)
                {
                    return;
                }

                Subscriber[] current = this.subscribers;
                var next = new Subscriber[current.Length + 1];
                Array.Copy(current, next, current.Length);
                next[current.Length] = new Subscriber(handler);
                Volatile.Write(ref this.subscribers, next);
                this.StartLocked();
            }
        }

        /// <summary>Starts the delivery thread for a module that registered commands, subscribed or not.</summary>
        public void EnsureStarted()
        {
            lock (this.sync)
            {
                if (!this.stopped)
                {
                    this.StartLocked();
                }
            }
        }

        public void Unsubscribe(Action<ChatChannelMessage> handler)
        {
            if (handler == null)
            {
                return;
            }

            lock (this.sync)
            {
                Subscriber[] current = this.subscribers;
                int index = Array.FindLastIndex(current, x => x.Handler == handler);
                if (index >= 0)
                {
                    this.RemoveAt(current, index);
                }
            }
        }

        /// <summary>Queues the line for this module; never waits. Called from network and bot threads.</summary>
        public void Enqueue(ChatChannelMessage message)
        {
            if (!this.HasSubscribers)
            {
                return;
            }

            this.TryAdd(message);
        }

        /// <summary>
        /// Queues a consumed command; never waits. <paramref name="run"/> calls the module's handler and
        /// <paramref name="failed"/> tells the issuer when it throws. False when the queue is full or stopped.
        /// </summary>
        public bool EnqueueCommand(string name, Action run, Action failed)
        {
            return this.TryAdd(new PendingCommand(name, run, failed));
        }

        /// <summary>Delivers what is already queued, then stops the thread, waiting at most <paramref name="timeout"/>.</summary>
        public void Stop(TimeSpan timeout)
        {
            Thread running;
            lock (this.sync)
            {
                if (this.stopped)
                {
                    return;
                }

                this.stopped = true;
                running = this.thread;
            }

            this.queue.CompleteAdding();
            if (running != null && !running.Join(timeout))
            {
                this.log.Warn("[ChatModule " + this.moduleId + "] delivery thread did not stop within "
                    + timeout.TotalSeconds + " s.");
            }
        }

        private bool TryAdd(object item)
        {
            bool added;
            try
            {
                added = this.queue.TryAdd(item);
            }
            catch (InvalidOperationException)
            {
                // Stopped: the engine is shutting down.
                return false;
            }

            if (!added)
            {
                long count = Interlocked.Increment(ref this.dropped);
                if (count == 1 || count % 1000 == 0)
                {
                    this.log.Warn("[ChatModule " + this.moduleId + "] is " + Capacity + " lines behind; dropped "
                        + count + " channel line(s) or command(s) so far.");
                }
            }

            return added;
        }

        private void StartLocked()
        {
            if (this.thread == null)
            {
                this.thread = new Thread(this.Run)
                {
                    IsBackground = true,
                    Name = "ChatModule " + this.moduleId,
                };
                this.thread.Start();
            }
        }

        private void Run()
        {
            foreach (object item in this.queue.GetConsumingEnumerable())
            {
                if (item is PendingCommand command)
                {
                    this.RunCommand(command);
                    continue;
                }

                var message = (ChatChannelMessage)item;
                foreach (Subscriber subscriber in Volatile.Read(ref this.subscribers))
                {
                    try
                    {
                        subscriber.Handler(message);
                        subscriber.Failures = 0;
                    }
                    catch (Exception exception)
                    {
                        subscriber.Failures++;
                        this.log.Error(exception, "[ChatModule " + this.moduleId + "] channel message handler failed ("
                            + subscriber.Failures + " in a row).");
                        if (subscriber.Failures >= MaxConsecutiveFailures)
                        {
                            this.log.Warn("[ChatModule " + this.moduleId + "] channel message handler unsubscribed after "
                                + MaxConsecutiveFailures + " failures in a row.");
                            lock (this.sync)
                            {
                                Subscriber[] current = this.subscribers;
                                int index = Array.IndexOf(current, subscriber);
                                if (index >= 0)
                                {
                                    this.RemoveAt(current, index);
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>A failing command stays registered: the issuer is told and every failure is logged.</summary>
        private void RunCommand(PendingCommand command)
        {
            try
            {
                command.Run();
            }
            catch (Exception exception)
            {
                this.log.Error(exception, "[ChatModule " + this.moduleId + "] command " + command.Name + " failed.");
                try
                {
                    command.Failed();
                }
                catch (Exception notifyException)
                {
                    this.log.Error(notifyException, "[ChatModule " + this.moduleId + "] could not report the failure of "
                        + command.Name + ".");
                }
            }
        }

        private void RemoveAt(Subscriber[] current, int index)
        {
            var next = new Subscriber[current.Length - 1];
            Array.Copy(current, 0, next, 0, index);
            Array.Copy(current, index + 1, next, index, current.Length - index - 1);
            Volatile.Write(ref this.subscribers, next);
        }

        private sealed class PendingCommand
        {
            public PendingCommand(string name, Action run, Action failed)
            {
                this.Name = name;
                this.Run = run;
                this.Failed = failed;
            }

            public string Name { get; }

            public Action Run { get; }

            public Action Failed { get; }
        }

        private sealed class Subscriber
        {
            public Subscriber(Action<ChatChannelMessage> handler)
            {
                this.Handler = handler;
            }

            public Action<ChatChannelMessage> Handler { get; }

            /// <summary>Touched only on the delivery thread.</summary>
            public int Failures { get; set; }
        }
    }
}
