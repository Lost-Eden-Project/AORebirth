namespace ZoneEngine_New.Core.Network
{
    using System;
    using System.Buffers.Binary;

    using LostEden.Protocol;
    using LostEden.Protocol.Messages;

    /// <summary>
    /// The engine's face on the LostEden.Protocol codec: one packet in, its header and body out; a body with a sender
    /// and receiver back to the exact bytes of one packet. One instance serves every session: the codec keeps no state
    /// between calls while message reuse stays off, and it must stay off, since a decoded body is queued to a playfield
    /// tick and kept.
    /// </summary>
    public sealed class ZoneMessageCodec
    {
        /// <summary>The message id every zone packet starts with.</summary>
        public const ushort PacketMessageId = 0xDFDF;

        // The server's own messages (team loot, loot rolls, bag names) decode like any other before a codec exists.
        static ZoneMessageCodec() => ExtendedMessages.Register();

        private readonly MessageCodec _codec = new MessageCodec();

        [ThreadStatic]
        private static PacketWriter? _writer;

        /// <summary>
        /// The packet's header and body. A null body is a type without a reader; <see cref="DecodedMessage.ReadFailed"/>
        /// is a body the reader rejected (short, or a count past its limit), which the client would drop too.
        /// </summary>
        public DecodedMessage Deserialize(byte[] packet)
        {
            ArgumentNullException.ThrowIfNull(packet);

            return _codec.Decode(packet);
        }

        /// <summary>
        /// The packet for <paramref name="body"/>, exactly as long as its header says: the session pads plaintext
        /// frames itself and the zlib stream carries no padding.
        /// </summary>
        public byte[] Serialize(MessageBody body, int sender, int receiver)
        {
            ArgumentNullException.ThrowIfNull(body);

            PacketWriter writer = _writer ??= new PacketWriter(4096);
            writer.Reset();
            _codec.Encode(body, new MessageHeader(body.PacketType, sender, receiver, PacketMessageId), writer);
            int length = BinaryPrimitives.ReadUInt16BigEndian(writer.WrittenSpan.Slice(MessageHeader.SizeOffset));
            return writer.WrittenSpan.Slice(0, length).ToArray();
        }
    }
}
