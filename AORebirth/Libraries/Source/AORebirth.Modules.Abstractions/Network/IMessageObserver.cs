namespace AORebirth.Modules.Network
{
    using System;
    using System.Collections.Generic;

    using AORebirth.Modules.World;

    /// <summary>Which way a message travels (module API 1.5).</summary>
    [Flags]
    public enum MessageDirection
    {
        None = 0,

        /// <summary>Client to server.</summary>
        Inbound = 1,

        /// <summary>Server to client.</summary>
        Outbound = 2,

        Both = Inbound | Outbound
    }

    /// <summary>The N3 messages one observer receives (module API 1.5). Ids are N3 message types, see <see cref="MessageTypes"/>.</summary>
    public sealed class MessageSubscription
    {
        public IReadOnlyList<int> MessageTypes { get; init; } = Array.Empty<int>();

        public MessageDirection Directions { get; init; } = MessageDirection.Inbound;
    }

    /// <summary>
    /// Watches N3 messages on this zone instance's connections (module API 1.5). It only watches: the engine handles every
    /// message exactly as it would without an observer.
    /// <list type="bullet">
    /// <item>Inbound messages arrive on the connection's receive thread, in wire order, once the session is in play.</item>
    /// <item>Outbound messages arrive on the connection's send thread, in wire order, just before they are written.</item>
    /// </list>
    /// Neither is a playfield tick. <see cref="OnMessage"/> must not block or allocate; one that throws is logged and,
    /// after 10 failures in a row, removed. One slower than 2 ms is reported.
    /// </summary>
    public interface IMessageObserver
    {
        void OnMessage(in MessageSample sample);
    }

    /// <summary>One observed message. Valid only during <see cref="IMessageObserver.OnMessage"/>: copy what you keep.</summary>
    public readonly ref struct MessageSample
    {
        public MessageSample(Guid sessionId, int characterId, MessageDirection direction, int messageType, long timestamp,
            ReadOnlySpan<byte> body, IMessageFields fields)
        {
            SessionId = sessionId;
            CharacterId = characterId;
            Direction = direction;
            MessageType = messageType;
            Timestamp = timestamp;
            Body = body;
            Fields = fields;
        }

        /// <summary>The connection, the same id as <see cref="SessionInfo.SessionId"/>.</summary>
        public Guid SessionId { get; }

        /// <summary>The character the connection plays, or 0 before one is bound.</summary>
        public int CharacterId { get; }

        public MessageDirection Direction { get; }

        /// <summary>The N3 message type.</summary>
        public int MessageType { get; }

        /// <summary><see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> when the message was received or sent.</summary>
        public long Timestamp { get; }

        /// <summary>The plaintext message, header included (outbound: before compression).</summary>
        public ReadOnlySpan<byte> Body { get; }

        /// <summary>The message's fields by name, from the engine's own parse.</summary>
        public IMessageFields Fields { get; }
    }

    /// <summary>
    /// Reads fields of the observed message by name (module API 1.5). A name is a property path of the engine's message
    /// type: <c>"MoveType"</c>, <c>"Coordinates.X"</c>, <c>"Target[0].Instance"</c>. An enum reads as its integer, an
    /// identity as an <see cref="EntityRef"/>. A name the message does not have, or a value that does not convert, gives
    /// false; ZONE_MODULE_API.md lists the names guaranteed per message type.
    /// </summary>
    public interface IMessageFields
    {
        bool TryGetInt(string name, out int value);

        bool TryGetFloat(string name, out float value);

        bool TryGetIdentity(string name, out EntityRef value);

        bool TryGetString(string name, out string value);
    }

    /// <summary>N3 message type ids for <see cref="MessageSubscription.MessageTypes"/> (module API 1.5).</summary>
    public static class MessageTypes
    {
        public const int Attack = 0x28494070;
        public const int CastNanoSpell = 0x25314d6d;
        public const int CharacterAction = 0x5e477770;
        public const int CharDCMove = 0x54111123;
        public const int FollowTarget = 0x260f3671;
        public const int GenericCmd = 0x52526858;
        public const int LookAt = 0x2252445f;
        public const int StopFight = 0x4a41203e;
    }
}
