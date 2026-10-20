namespace WorldEngine.Services
{
    using System;
    using System.Threading.Tasks;

    using AORebirth.World.Contracts.V1;

    using Grpc.Core;

    using Microsoft.Extensions.Logging;

    using WorldEngine.State;

    /// <summary>LoginEngine's calls. LoginEngine has already checked credentials and that the character is the account's.</summary>
    public sealed class SessionService(WorldState state, ILogger<SessionService> log) : Session.SessionBase
    {
        static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(8);

        public override Task<BeginLoginReply> BeginLogin(BeginLoginRequest request, ServerCallContext context)
            => Task.FromResult(new BeginLoginReply { Generation = state.BeginLogin(RequireAccount(request.Account)) });

        public override Task<SelectCharacterReply> SelectCharacter(SelectCharacterRequest request, ServerCallContext context)
        {
            RequireAccount(request.Account);
            SelectCharacterReply reply = state.SelectCharacter(request);
            if (!reply.Accepted)
                log.LogWarning("Select refused character={Character} reason={Reason}", request.CharacterId, reply.Reason);
            return Task.FromResult(reply);
        }

        public override async Task<DeleteReply> ReleaseForDelete(DeleteRequest request, ServerCallContext context)
        {
            RequireAccount(request.Account);
            WorldState.DeleteStep step = state.BeginDelete(request.CharacterId, out ServiceInstance? owner);
            if (step == WorldState.DeleteStep.AskOwner && owner != null)
            {
                ServiceReply? reply = await owner.RequestAsync(state.NextRequestId(),
                    new ServiceRequest
                    {
                        ReleaseCharacter = new ReleaseCharacter { CharacterId = request.CharacterId, Reason = ReleaseReason.ReleaseForDelete }
                    },
                    ReleaseTimeout, context.CancellationToken).ConfigureAwait(false);
                ReleaseOutcome? outcome = reply?.BodyCase == ServiceReply.BodyOneofCase.ReleaseCharacter
                    ? reply.ReleaseCharacter.Outcome
                    : null;
                step = state.FinishDeleteRelease(request.CharacterId, owner, outcome);
                if (outcome == null)
                {
                    log.LogWarning("Delete of character {Character}: {Owner} did not answer the release", request.CharacterId, owner.Name);
                    return new DeleteReply { State = DeleteState.DeleteTimeout };
                }
            }

            log.LogInformation("Delete release character={Character} result={Step}", request.CharacterId, step);
            return new DeleteReply
            {
                State = step switch
                {
                    WorldState.DeleteStep.Released => DeleteState.DeleteReleased,
                    WorldState.DeleteStep.InPlay => DeleteState.DeleteInPlay,
                    _ => DeleteState.DeleteBusy
                }
            };
        }

        public override Task<Ack> CompleteDelete(DeleteRequest request, ServerCallContext context)
        {
            RequireAccount(request.Account);
            state.CompleteDelete(request.CharacterId);
            return Task.FromResult(new Ack { Ok = true });
        }

        static string RequireAccount(string account)
        {
            if (string.IsNullOrWhiteSpace(account) || account.Length > 32)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "authenticated account required"));
            return account;
        }
    }
}
