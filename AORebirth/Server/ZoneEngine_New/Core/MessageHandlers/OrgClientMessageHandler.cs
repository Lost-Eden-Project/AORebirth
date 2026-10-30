namespace ZoneEngine_New.Core.MessageHandlers
{
    using System;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages.OrgServerMessages;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.GameData;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// /org info: answers OrgClient Info with OrgServer/OrgInfo for the targeted player's organization
    /// (capture 20260623-084448: a self target is the requesting character). The rank is shown only when the target
    /// belongs to the requester's own organization. Other OrgClient commands are not handled.
    /// </summary>
    public sealed class OrgClientMessageHandler : IMessageHandler<OrgClientMessage>
    {
        readonly IGameData _gameData;
        readonly Lazy<PlayfieldManager> _playfieldManager;

        // Lazy: PlayfieldManager depends on the message router, which depends on the handlers.
        public OrgClientMessageHandler(IGameData gameData, Lazy<PlayfieldManager> playfieldManager)
        {
            _gameData = gameData;
            _playfieldManager = playfieldManager;
        }

        public Type MessageBodyType => typeof(OrgClientMessage);

        public void Handle(MessageBody body, IZoneSession session) => Handle((OrgClientMessage)body, session);

        public void Handle(OrgClientMessage message, IZoneSession session)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(session);

            if (message.Command != OrgClientCommand.Info
                || session.State != SessionState.InPlay || session.Player is not { } player
                || !ReferenceEquals(player.Session, session) || player.IsPersistenceQuarantined)
                return;

            Player target = player;
            if (message.Target.Instance != player.Identity.Instance
                && (message.Target.Instance == 0
                    || !_playfieldManager.Value.FindPlayer(message.Target.Instance, out target)))
                return;

            if (target.Organization is not { } org)
                return;

            bool sameOrg = player.Organization?.Id == org.Id;

            session.Send(new OrgInfoMessage
            {
                Identity = player.Identity,
                Organization = new Identity { Type = IdentityType.Organization, Instance = org.Id },
                OrganizationName = org.Name ?? string.Empty,
                Description = org.Description ?? string.Empty,
                Objective = org.Objective ?? string.Empty,
                History = org.History ?? string.Empty,
                GoverningForm = _gameData.GoverningForms.GetFormName(org.GovernmentForm),
                LeaderName = org.LeaderName ?? string.Empty,
                Rank = sameOrg
                    ? _gameData.GoverningForms.GetRankName(
                        org.GovernmentForm, target.Stats.GetOrZero(CharacterStat.ClanLevel, StatDetail.Base))
                    : string.Empty,
                Unknown3 = Array.Empty<object>()
            });
        }
    }
}
