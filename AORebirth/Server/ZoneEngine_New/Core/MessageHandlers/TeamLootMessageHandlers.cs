namespace ZoneEngine_New.Core.MessageHandlers
{
    using System;

    using LostEden.Protocol.Messages;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Loot;
    using ZoneEngine_New.Core.Network;

    /// <summary>A member answers a Need or Greed roll (<see cref="LootRollChoiceMessage"/>) on their own playfield.</summary>
    public sealed class LootRollChoiceMessageHandler : IMessageHandler<LootRollChoiceMessage>
    {
        public Type MessageBodyType => typeof(LootRollChoiceMessage);

        public void Handle(MessageBody body, IZoneSession session) => Handle((LootRollChoiceMessage)body, session);

        public void Handle(LootRollChoiceMessage message, IZoneSession session)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(session);
            if (session.State != SessionState.InPlay || session.Player is not Player player || !ReferenceEquals(player.Session, session)
                || message.Identity != player.Identity)
                return;
            player.Playfield?.GetService<LootRollService>()?.Answer(player, message.RollId, message.Choice);
        }
    }
}
