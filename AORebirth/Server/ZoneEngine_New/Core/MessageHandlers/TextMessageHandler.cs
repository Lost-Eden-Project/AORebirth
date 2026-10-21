namespace ZoneEngine_New.Core.MessageHandlers
{
    using System;

    using SmokeLounge.AOtomation.Messaging.Messages;

    using ZoneEngine_New.Core.Chat;
    using ZoneEngine_New.Core.Commands;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Network;

    public sealed class TextMessageHandler : IMessageHandler<TextMessage>
    {
        private readonly GmCommandDispatcher _commands;
        private readonly VicinityChatRelay _vicinity;

        public TextMessageHandler(GmCommandDispatcher commands, VicinityChatRelay vicinity)
        {
            ArgumentNullException.ThrowIfNull(commands);
            ArgumentNullException.ThrowIfNull(vicinity);
            _commands = commands;
            _vicinity = vicinity;
        }

        public Type MessageBodyType => typeof(TextMessage);

        public void Handle(MessageBody body, IZoneSession session)
        {
            Handle((TextMessage)body, session);
        }

        public void Handle(TextMessage message, IZoneSession session)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(session);

            if (session.State != SessionState.InPlay)
                return;

            Player? player = session.Player;
            if (player == null)
                return;

            string? text = message.Message?.Text;
            if (string.IsNullOrEmpty(text))
                return;

            if (!_commands.TryExecuteText(session, player, text))
                _vicinity.Relay(player, message);
        }
    }
}
