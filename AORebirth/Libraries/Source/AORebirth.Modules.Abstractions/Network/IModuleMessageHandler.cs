namespace AORebirth.Modules.Network
{
    using System;

    using AORebirth.Modules.World;

    /// <summary>
    /// Handles an inbound N3 message type the engine has no reader for (module API 1.12): a message of a server's own
    /// making that its client sends. Registered with <see cref="IZoneModuleContext.RegisterMessageHandler"/> for one
    /// type; the engine refuses a type it reads itself and a type another module already handles.
    /// </summary>
    /// <remarks>
    /// <see cref="Handle"/> runs on the player's playfield tick, once the session is in play, in wire order with the
    /// player's other messages. It must return quickly and must not block. The <see cref="ModuleMessage"/> is valid only
    /// during the call: copy out what is kept. A handler that throws is logged; after ten failures in a row it is removed.
    /// </remarks>
    public interface IModuleMessageHandler
    {
        void Handle(in ModuleMessage message);
    }

    /// <summary>One inbound message for an <see cref="IModuleMessageHandler"/>, valid only during the call.</summary>
    public readonly ref struct ModuleMessage
    {
        public ModuleMessage(Guid sessionId, int characterId, int playfieldId, int messageType, EntityRef identity,
            long timestamp, ReadOnlySpan<byte> body)
        {
            SessionId = sessionId;
            CharacterId = characterId;
            PlayfieldId = playfieldId;
            MessageType = messageType;
            Identity = identity;
            Timestamp = timestamp;
            Body = body;
        }

        /// <summary>The connection (the same id as <see cref="IZoneWorld.Session"/>).</summary>
        public Guid SessionId { get; }

        /// <summary>The character the connection plays.</summary>
        public int CharacterId { get; }

        /// <summary>The playfield the character is on; the handler runs on its tick.</summary>
        public int PlayfieldId { get; }

        /// <summary>The N3 message type id.</summary>
        public int MessageType { get; }

        /// <summary>The identity in the N3 header, as the client wrote it (normally the character's).</summary>
        public EntityRef Identity { get; }

        /// <summary><c>Stopwatch.GetTimestamp()</c> when the bytes were read from the socket.</summary>
        public long Timestamp { get; }

        /// <summary>
        /// The message body after the N3 header (type, identity, one byte): the fields the client wrote, big-endian, as
        /// the message's own wire layout lays them out. Empty when the client sent none.
        /// </summary>
        public ReadOnlySpan<byte> Body { get; }
    }
}
