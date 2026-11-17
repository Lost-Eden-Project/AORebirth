namespace ZoneEngine_New.Core.MessageHandlers
{
    using System;
    using System.Globalization;

    using AORebirth.Modules.Network;

    using LostEden.Protocol.Messages;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Modules;
    using ZoneEngine_New.Core.Network;

    /// <summary>
    /// Hands a <see cref="ModuleRawMessage"/> (an N3 type a module registered for, module API 1.12) to that module's
    /// <see cref="IModuleMessageHandler"/> on the player's playfield tick. A module that threw is counted by the handler
    /// table and never closes the connection.
    /// </summary>
    public sealed class ModuleRawMessageHandler(ZoneModuleHost? modules, IZoneLogger logger) : IMessageHandler<ModuleRawMessage>
    {
        public Type MessageBodyType => typeof(ModuleRawMessage);

        public void Handle(MessageBody body, IZoneSession session) => Handle((ModuleRawMessage)body, session);

        public void Handle(ModuleRawMessage message, IZoneSession session)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(session);

            if (session.State != SessionState.InPlay || session.Player is not Player player || player.Playfield == null)
                return;

            ModuleMessageHandlers? handlers = modules?.MessageHandlers;
            Guid sessionId = session is ZoneSession zoneSession ? zoneSession.Id : Guid.Empty;
            if (handlers == null || !handlers.Handle(new ModuleMessage(sessionId, player.Identity.Instance,
                    player.Playfield.Identity.Instance, (int)message.N3MessageType, ModuleHooks.Entity(message.Identity),
                    message.ReceivedAt, message.Payload)))
            {
                logger.Warn(string.Format(CultureInfo.InvariantCulture,
                    "Module message 0x{0:x8} has no handler any more character={1}", (int)message.N3MessageType, player.Identity.Instance));
            }
        }
    }
}
