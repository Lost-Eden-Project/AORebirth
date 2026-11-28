using System;
using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using ChatEngine.Packets;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ZoneEngine_New.Tests;

/// <summary>
/// LE-14: a channel line was relayed with the sender's own block, so a client's line-flags entry (02 01) made its name
/// show red with " (GM)". The relayed block is now server-made: the sender's macro entries kept, its flags dropped, and
/// the GM entry appended only for a GM.
/// </summary>
[TestClass]
[TestCategory("ChatNetwork")]
public sealed class ChatLineRelayGuardTests
{
    static readonly byte[] GmFlags = { 0x02, 0x01 };

    // Tag 1 text macro: two bytes then two strings.
    static readonly byte[] Macro = { 0x01, 0x05, 0x06, 0x00, 0x02, (byte)'h', (byte)'i', 0x00, 0x01, (byte)'!' };

    [TestMethod]
    public void ASendersFlagsEntryIsDroppedAndAGmGetsTheServersOwn()
    {
        CollectionAssert.AreEqual(Array.Empty<byte>(), ChatLineFlags.SanitizeBlock(GmFlags, 0, GmFlags.Length, gm: false));
        CollectionAssert.AreEqual(GmFlags, ChatLineFlags.SanitizeBlock(GmFlags, 0, GmFlags.Length, gm: true));
        CollectionAssert.AreEqual(GmFlags, ChatLineFlags.SanitizeBlock(Array.Empty<byte>(), 0, 0, gm: true));
    }

    [TestMethod]
    public void MacroEntriesSurviveAndFlagsBetweenThemDoNot()
    {
        byte[] block = Join(Macro, GmFlags, Macro);

        CollectionAssert.AreEqual(Join(Macro, Macro), ChatLineFlags.SanitizeBlock(block, 0, block.Length, gm: false));
        CollectionAssert.AreEqual(Join(Macro, Macro, GmFlags), ChatLineFlags.SanitizeBlock(block, 0, block.Length, gm: true));
    }

    [TestMethod]
    public void ABlockThatDoesNotParseIsDroppedWhole()
    {
        byte[] truncated = { 0x01, 0x05, 0x06, 0x00, 0x09, (byte)'h' };
        byte[] unknownTag = { 0x07, 0x00 };

        CollectionAssert.AreEqual(Array.Empty<byte>(), ChatLineFlags.SanitizeBlock(truncated, 0, truncated.Length, gm: false));
        CollectionAssert.AreEqual(Array.Empty<byte>(), ChatLineFlags.SanitizeBlock(unknownTag, 0, unknownTag.Length, gm: false));
        CollectionAssert.AreEqual(GmFlags, ChatLineFlags.SanitizeBlock(unknownTag, 0, unknownTag.Length, gm: true));
    }

    [TestMethod]
    public void TheRelayedChannelPacketCarriesTheSenderIdAndAServerMadeBlock()
    {
        byte[] senderId = { 0, 0x01, 0x63, 0x79 };
        byte[] sent = ChannelPacket("hello", GmFlags);

        byte[] plain = InsertSender(sent, senderId, gm: false);
        byte[] gm = InsertSender(sent, senderId, gm: true);

        CollectionAssert.AreEqual(ChannelPacket("hello", Array.Empty<byte>(), senderId), plain, "a player's flags do not survive the relay");
        CollectionAssert.AreEqual(ChannelPacket("hello", GmFlags, senderId), gm, "a GM's line carries the server's GM entry once");
        Assert.AreEqual(plain.Length - 4, BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(2, 2)));
        Assert.AreEqual(gm.Length - 4, BinaryPrimitives.ReadUInt16BigEndian(gm.AsSpan(2, 2)));
    }

    [TestMethod]
    public void APacketWhoseBlockOverrunsItsLengthIsRelayedWithAnEmptyBlock()
    {
        byte[] senderId = { 0, 0, 0, 9 };
        byte[] sent = ChannelPacket("x", GmFlags);
        sent[^3] = 0x7F; // the block claims far more bytes than follow

        byte[] relayed = InsertSender(sent, senderId, gm: false);

        CollectionAssert.AreEqual(ChannelPacket("x", Array.Empty<byte>(), senderId), relayed);
    }

    /// <summary>type, length, channel type, channel id, [sender], text S, block S.</summary>
    static byte[] ChannelPacket(string text, byte[] block, byte[]? senderId = null)
    {
        byte[] textBytes = Encoding.Latin1.GetBytes(text);
        senderId ??= Array.Empty<byte>();
        byte[] packet = new byte[9 + senderId.Length + 2 + textBytes.Length + 2 + block.Length];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), 65);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), checked((ushort)(packet.Length - 4)));
        packet[4] = 3;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(5, 4), 0x1234);
        int at = 9;
        senderId.CopyTo(packet, at);
        at += senderId.Length;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(at, 2), checked((ushort)textBytes.Length));
        textBytes.CopyTo(packet, at + 2);
        at += 2 + textBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(at, 2), checked((ushort)block.Length));
        block.CopyTo(packet, at + 2);
        return packet;
    }

    static byte[] InsertSender(byte[] packet, byte[] senderId, bool gm)
    {
        Type channelMessage = typeof(ChatLineFlags).Assembly.GetType("ChatEngine.PacketHandlers.ChannelMessage")!;
        MethodInfo method = channelMessage.GetMethod("InsertSender", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (byte[])method.Invoke(null, new object[] { packet, senderId, gm })!;
    }

    static byte[] Join(params byte[][] parts)
    {
        int length = 0;
        foreach (byte[] part in parts)
            length += part.Length;
        byte[] joined = new byte[length];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(joined, offset);
            offset += part.Length;
        }
        return joined;
    }
}
