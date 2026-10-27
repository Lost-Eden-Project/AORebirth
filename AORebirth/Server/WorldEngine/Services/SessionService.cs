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

        /// <summary>
        /// How long a select waits on the target zone's handoff hook. Must stay well under login's deadline for the whole
        /// call (WorldSessionClient.CallTimeout, 5 s), so a slow zone gets World's clean refusal rather than a deadline
        /// error on the login server.
        /// </summary>
        static readonly TimeSpan HandoffHookTimeout = TimeSpan.FromSeconds(3);

        public override Task<BeginLoginReply> BeginLogin(BeginLoginRequest request, ServerCallContext context)
            => Task.FromResult(new BeginLoginReply { Generation = state.BeginLogin(RequireAccount(request.Account)) });

        public override async Task<SelectCharacterReply> SelectCharacter(SelectCharacterRequest request, ServerCallContext context)
        {
            RequireAccount(request.Account);
            SelectCharacterReply reply = state.SelectCharacter(request, out ServiceInstance? target);
            if (reply.Accepted && target is { HasHandoffHook: true })
                reply = await AskHandoffHookAsync(request, reply, target, context).ConfigureAwait(false);
            if (!reply.Accepted)
                log.LogWarning("Select refused character={Character} reason={Reason}", request.CharacterId, reply.Reason);
            return reply;
        }

        /// <summary>
        /// The target zone decides on the handoff. No answer refuses it (a zone that installed a hook is guarding
        /// something, so it fails closed); a refusal withdraws the ticket just issued.
        /// </summary>
        async Task<SelectCharacterReply> AskHandoffHookAsync(
            SelectCharacterRequest request, SelectCharacterReply issued, ServiceInstance target, ServerCallContext context)
        {
            ServiceReply? answer = await target.RequestAsync(state.NextRequestId(),
                new ServiceRequest
                {
                    AdmitClient = new AdmitClient
                    {
                        Account = request.Account,
                        CharacterId = request.CharacterId,
                        ClientVersion = request.ClientVersion
                    }
                },
                HandoffHookTimeout, context.CancellationToken).ConfigureAwait(false);

            if (answer?.BodyCase != ServiceReply.BodyOneofCase.AdmitClient)
            {
                state.WithdrawHandoff(request.CharacterId, issued.Cookie1, issued.Cookie2);
                log.LogWarning("Handoff hook on {Zone} did not answer for character {Character}", target.Name, request.CharacterId);
                return new SelectCharacterReply { Accepted = false, Reason = "zone_unavailable" };
            }

            if (!answer.AdmitClient.Admitted)
            {
                state.WithdrawHandoff(request.CharacterId, issued.Cookie1, issued.Cookie2);
                log.LogInformation("Handoff hook on {Zone} refused character {Character}: {Reason}",
                    target.Name, request.CharacterId, answer.AdmitClient.Reason);
                return new SelectCharacterReply { Accepted = false, Reason = "refused_by_zone" };
            }

            issued.EventServerType = answer.AdmitClient.EventServerType;
            return issued;
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
