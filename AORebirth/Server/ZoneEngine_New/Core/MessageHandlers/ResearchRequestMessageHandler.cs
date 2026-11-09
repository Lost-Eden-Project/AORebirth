namespace ZoneEngine_New.Core.MessageHandlers
{
    using System;

    using SmokeLounge.AOtomation.Messaging.Messages;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Network;

    /// <summary>
    /// The client's global research refresh (ResearchRequestIIR, N3Msg_UpdateGlobalResearch Gamecode.dll 0x10026eb8).
    /// HACK: we do not support Global Research and might never support it, so the answer is only the GrantedToAll
    /// research, all completed (<see cref="Player.SendGrantedGlobalResearch"/>).
    /// </summary>
    public sealed class ResearchRequestMessageHandler : IMessageHandler<ResearchRequestMessage>
    {
        public Type MessageBodyType => typeof(ResearchRequestMessage);

        public void Handle(MessageBody body, IZoneSession session)
        {
            Handle((ResearchRequestMessage)body, session);
        }

        public void Handle(ResearchRequestMessage message, IZoneSession session)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(session);

            if (session.State != SessionState.InPlay)
                return;

            session.Player?.SendGrantedGlobalResearch();
        }
    }
}
