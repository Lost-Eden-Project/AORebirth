namespace WorldEngine.Services
{
    using System.Threading.Tasks;

    using AORebirth.World.Contracts.V1;

    using Grpc.Core;

    using Microsoft.Extensions.Logging;

    using WorldEngine.State;

    public sealed class LeasesService(WorldState state, ILogger<LeasesService> log) : Leases.LeasesBase
    {
        public override Task<ClaimReply> Claim(ClaimRequest request, ServerCallContext context)
        {
            ServiceInstance instance = CallerSession.Require(state, context);
            ClaimReply reply = state.Claim(instance, request);
            if (reply.Accepted)
                log.LogInformation("Claim character={Character} by {Instance} kind={Kind}", request.CharacterId, instance.Name, reply.Kind);
            else
                log.LogWarning("Claim refused character={Character} by {Instance} reason={Reason}", request.CharacterId, instance.Name, reply.Reason);
            return Task.FromResult(reply);
        }

        public override Task<Ack> Release(ReleaseRequest request, ServerCallContext context)
        {
            ServiceInstance instance = CallerSession.Require(state, context);
            Ack ack = state.Release(instance, request);
            log.LogInformation("Release character={Character} by {Instance} result={Reason}", request.CharacterId, instance.Name, ack.Reason);
            return Task.FromResult(ack);
        }

        public override Task<ServiceLeaseReply> AcquireService(ServiceLeaseRequest request, ServerCallContext context)
            => Task.FromResult(state.AcquireService(CallerSession.Require(state, context), request));

        public override Task<LeaseQueryReply> Query(LeaseQuery request, ServerCallContext context)
            => Task.FromResult(state.Query(request));

        public override Task<TransferReply> BeginTransfer(TransferRequest request, ServerCallContext context)
        {
            ServiceInstance instance = CallerSession.Require(state, context);
            TransferReply reply = state.BeginTransfer(instance, request);
            if (!reply.Accepted)
                log.LogWarning("Transfer refused character={Character} by {Instance} reason={Reason}", request.CharacterId, instance.Name, reply.Reason);
            return Task.FromResult(reply);
        }

        public override Task<Ack> UpdatePresence(PresenceUpdate request, ServerCallContext context)
            => Task.FromResult(state.UpdatePresence(CallerSession.Require(state, context), request));
    }

    public sealed class PlayfieldsService(WorldState state) : Playfields.PlayfieldsBase
    {
        public override Task<ResolveReply> Resolve(ResolveRequest request, ServerCallContext context)
        {
            CallerSession.Require(state, context);
            return Task.FromResult(state.Resolve(request));
        }

        public override Task<Ack> Release(PlayfieldReleaseRequest request, ServerCallContext context)
            => Task.FromResult(state.ReleasePlayfield(CallerSession.Require(state, context), request));
    }

    public sealed class ChatService(WorldState state) : AORebirth.World.Contracts.V1.Chat.ChatBase
    {
        public override Task<Ack> Publish(ChatEvent request, ServerCallContext context)
        {
            ServiceInstance sender = CallerSession.Require(state, context);
            bool delivered = state.PublishChat(sender, request);
            return Task.FromResult(new Ack { Ok = delivered, Reason = delivered ? "delivered" : "no_target" });
        }
    }

    public sealed class TeamsService(WorldState state, WorldEngine.Teams.TeamRegistry teams) : AORebirth.World.Contracts.V1.Teams.TeamsBase
    {
        public override Task<TeamCommandReply> Command(TeamCommand request, ServerCallContext context)
            => Task.FromResult(teams.Command(CallerSession.Require(state, context), request));
    }

    public sealed class DirectoryService(WorldState state) : Directory.DirectoryBase
    {
        public override Task<Ack> Route(InstanceCommand request, ServerCallContext context)
        {
            ServiceInstance sender = CallerSession.Require(state, context);
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 64 || request.Arguments.Count > 256)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid command"));
            bool delivered = state.Route(sender, request);
            return Task.FromResult(new Ack { Ok = delivered, Reason = delivered ? "routed" : "no_target" });
        }

        public override async Task<LocateReply> Locate(LocateRequest request, ServerCallContext context)
        {
            LocateReply reply = state.Locate(request);
            if (!reply.Found) return reply;
            // Position lives in the owning zone, not in World.
            ServiceInstance? owner = state.OwnerOf(reply.CharacterId);
            if (owner == null) return new LocateReply { Found = false };
            ServiceReply? answer = await owner.RequestAsync(state.NextRequestId(),
                new ServiceRequest { LocateCharacter = new LocateCharacter { CharacterId = reply.CharacterId } },
                System.TimeSpan.FromSeconds(3), context.CancellationToken).ConfigureAwait(false);
            if (answer?.BodyCase != ServiceReply.BodyOneofCase.LocateCharacter || !answer.LocateCharacter.Found) return reply;
            reply.PlayfieldId = answer.LocateCharacter.PlayfieldId;
            reply.X = answer.LocateCharacter.X;
            reply.Y = answer.LocateCharacter.Y;
            reply.Z = answer.LocateCharacter.Z;
            return reply;
        }

        public override Task<ListOnlineReply> ListOnline(ListOnlineRequest request, ServerCallContext context)
            => Task.FromResult(state.ListOnline());

        public override Task<Ack> ShiftGameClock(ShiftGameClockRequest request, ServerCallContext context)
        {
            CallerSession.Require(state, context);
            long offset = state.ShiftGameClock(request.DeltaGameSeconds, request.Reset);
            return Task.FromResult(new Ack { Ok = true, Reason = offset.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        public override Task<Ack> Announce(Announcement request, ServerCallContext context)
        {
            CallerSession.Require(state, context);
            state.Broadcast(new WorldToService { Announcement = request }, ServiceRole.RoleZone);
            return Task.FromResult(new Ack { Ok = true });
        }
    }
}
