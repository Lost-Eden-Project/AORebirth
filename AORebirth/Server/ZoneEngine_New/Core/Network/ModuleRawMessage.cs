namespace ZoneEngine_New.Core.Network
{
    using System;
    using System.Buffers.Binary;

    using LostEden.Protocol;
    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages;

    /// <summary>
    /// An N3 message of a type the engine has no reader for, kept as bytes for the module that handles it (module API
    /// 1.12). Built by hand from the packet on the receive thread and routed like any other body to
    /// <see cref="MessageHandlers.ModuleRawMessageHandler"/> on the playfield tick. The same layout, written back, is how
    /// a module's outbound message is sent (<see cref="Encode"/>).
    /// </summary>
    public sealed class ModuleRawMessage : N3Message
    {
        /// <summary>The packet header, the N3 type, the identity (type and instance) and one byte.</summary>
        public const int N3HeaderLength = MessageHeader.Size + 4 + 8 + 1;

        /// <summary>The most a module may put in one message's body: the session's packet limit less the headers.</summary>
        public const int MaxBodyLength = 8192 - N3HeaderLength;

        public ModuleRawMessage()
            : base(default)
        {
        }

        /// <summary>The body after the N3 header, as the client wrote it.</summary>
        public byte[] Payload { get; private set; } = Array.Empty<byte>();

        /// <summary><c>Stopwatch.GetTimestamp()</c> when the packet was read from the socket.</summary>
        public long ReceivedAt { get; private set; }

        /// <summary>
        /// The message and header for <paramref name="packet"/> when it is an N3 packet of a type <paramref name="handlers"/>
        /// wants; false otherwise (the ordinary codec then reads it).
        /// </summary>
        public static bool TryCreate(byte[] packet, Modules.ModuleMessageHandlers handlers, long receivedAt,
            out ModuleRawMessage message, out MessageHeader header)
        {
            message = null!;
            header = default;
            if (packet == null || packet.Length < N3HeaderLength || !MessageHeader.TryRead(packet, out header)
                || header.PacketType != PacketType.N3Message)
                return false;

            int type = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(MessageHeader.Size));
            if (!handlers.Wants(type))
                return false;

            int size = header.Length;
            int end = size >= N3HeaderLength && size <= packet.Length ? size : packet.Length;
            message = new ModuleRawMessage
            {
                N3MessageType = (N3MessageType)type,
                Identity = new Identity(
                    (IdentityType)BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(MessageHeader.Size + 4)),
                    BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(MessageHeader.Size + 8))),
                Unknown = packet[MessageHeader.Size + 12],
                Payload = packet.AsSpan(N3HeaderLength, end - N3HeaderLength).ToArray(),
                ReceivedAt = receivedAt
            };
            return true;
        }

        protected override void ReadBody(ref SpanReader reader) => Payload = reader.ReadByteArray(reader.Remaining);

        protected override void WriteBody(PacketWriter writer) => writer.WriteBytes(Payload);

        /// <summary>
        /// A whole outbound packet for a module's message: the header as the codec writes one (0xDFDF, N3, 1, the size,
        /// sender, receiver), the type, <paramref name="identity"/>, the byte 1, then <paramref name="body"/> as given.
        /// </summary>
        public static byte[] Encode(int messageType, Identity identity, int sender, int receiver, ReadOnlySpan<byte> body)
        {
            int length = N3HeaderLength + body.Length;
            if (length > short.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(body), "A module message body must leave the packet under 32 KB.");
            byte[] packet = new byte[length];
            Span<byte> span = packet;
            BinaryPrimitives.WriteUInt16BigEndian(span, ZoneMessageCodec.PacketMessageId);
            BinaryPrimitives.WriteInt16BigEndian(span.Slice(2), (short)PacketType.N3Message);
            BinaryPrimitives.WriteInt16BigEndian(span.Slice(4), 0x0001);
            BinaryPrimitives.WriteInt16BigEndian(span.Slice(6), (short)length);
            BinaryPrimitives.WriteInt32BigEndian(span.Slice(8), sender);
            BinaryPrimitives.WriteInt32BigEndian(span.Slice(12), receiver);
            BinaryPrimitives.WriteInt32BigEndian(span.Slice(16), messageType);
            BinaryPrimitives.WriteInt32BigEndian(span.Slice(20), (int)identity.Type);
            BinaryPrimitives.WriteInt32BigEndian(span.Slice(24), identity.Instance);
            span[28] = 0x01;
            body.CopyTo(span.Slice(N3HeaderLength));
            return packet;
        }
    }
}
