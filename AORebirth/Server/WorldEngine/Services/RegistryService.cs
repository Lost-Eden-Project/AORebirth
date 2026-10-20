namespace WorldEngine.Services
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    using AORebirth.World.Contracts.V1;

    using Grpc.Core;

    using Microsoft.Extensions.Logging;

    using WorldEngine.State;

    /// <summary>
    /// The long-lived stream each zone instance and ChatEngine holds: first message registers, then heartbeats and
    /// replies flow up while World pushes requests and broadcasts down.
    /// </summary>
    public sealed class RegistryService(WorldState state, WorldEngine.Teams.TeamRegistry teams, ILogger<RegistryService> log) : Registry.RegistryBase
    {
        public override async Task Connect(IAsyncStreamReader<ServiceToWorld> requestStream,
            IServerStreamWriter<WorldToService> responseStream, ServerCallContext context)
        {
            CancellationToken cancellation = context.CancellationToken;
            if (!await requestStream.MoveNext(cancellation).ConfigureAwait(false)
                || requestStream.Current.BodyCase != ServiceToWorld.BodyOneofCase.Register)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "first message must register"));

            Register register = requestStream.Current.Register;
            string name = (register.Instance ?? string.Empty).Trim();
            if (name.Length == 0 || name.Length > 64)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "instance name required"));
            if (register.Role == ServiceRole.RoleZone && (register.Game == null || string.IsNullOrEmpty(register.Game.Address)
                || register.Game.Port == 0 || register.Game.Port > 65535))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "zone game endpoint required"));

            var instance = new ServiceInstance(name, register.Role, Guid.NewGuid().ToString("N"), Environment.TickCount64);
            RegisterAccepted accepted;
            try
            {
                accepted = state.Register(instance, register);
            }
            catch (InvalidOperationException exception)
            {
                throw new RpcException(new Status(StatusCode.AlreadyExists, exception.Message));
            }

            instance.Send(new WorldToService { Accepted = accepted });
            state.AfterRegister();
            // Teams: after a World restart the zones' mirrors are taken back; after a reconnect each mirror is
            // brought up to date, since pushes sent while the stream was down are gone.
            if (register.Role == ServiceRole.RoleZone) teams.Reconcile(instance, register.Teams, accepted.Recovering);
            // When World retires the instance (superseded or expired) the push ends and so does the call.
            using var call = CancellationTokenSource.CreateLinkedTokenSource(cancellation, instance.Closed);
            Task pump = PumpAsync(instance, responseStream, call.Token);
            _ = pump.ContinueWith(_ =>
            {
                try { call.Cancel(); }
                catch (ObjectDisposedException) { }
            }, TaskScheduler.Default);
            try
            {
                while (await requestStream.MoveNext(call.Token).ConfigureAwait(false))
                {
                    ServiceToWorld message = requestStream.Current;
                    switch (message.BodyCase)
                    {
                        case ServiceToWorld.BodyOneofCase.Heartbeat:
                            state.Heartbeat(instance, message.Heartbeat);
                            instance.Send(new WorldToService { HeartbeatAck = new HeartbeatAck { Sequence = message.Heartbeat.Sequence } });
                            break;
                        case ServiceToWorld.BodyOneofCase.Reply:
                            instance.CompleteRequest(message.Reply);
                            break;
                        case ServiceToWorld.BodyOneofCase.Chat:
                            state.PublishChat(instance, message.Chat);
                            break;
                        case ServiceToWorld.BodyOneofCase.Presence:
                            foreach (PresenceUpdate update in message.Presence.Updates)
                                state.UpdatePresence(instance, update);
                            break;
                    }
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException
                || (exception is RpcException rpc && rpc.StatusCode == StatusCode.Cancelled))
            {
            }
            finally
            {
                if (instance.Overflowed)
                    log.LogWarning("{Instance} fell {Count} pushes behind; its stream was ended so it re-registers", name,
                        ServiceInstance.OutboundCapacity);
                if (!instance.Removed) state.Disconnected(instance);
                instance.Close();
                try { await pump.ConfigureAwait(false); }
                catch (Exception exception) { log.LogDebug(exception, "Registry push for {Instance} ended", name); }
            }
        }

        static async Task PumpAsync(ServiceInstance instance, IServerStreamWriter<WorldToService> stream, CancellationToken cancellation)
        {
            await foreach (WorldToService message in instance.Outbound.ReadAllAsync(cancellation).ConfigureAwait(false))
                await stream.WriteAsync(message, cancellation).ConfigureAwait(false);
        }
    }
}
