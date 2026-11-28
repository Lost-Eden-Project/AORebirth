#region License

// Copyright (c) 2005-2014, CellAO Team
//
// All rights reserved.

#endregion

namespace ChatEngine.Packets
{
    using System;

    using ChatEngine.CoreClient;

    /// <summary>
    /// Line flags in a chat line's block (the length-prefixed data after the text). The block is a run of entries, a
    /// tag byte then its data: tag 0 IIISS, tag 1 a text macro BBSS, tag 2 one flags byte. Flag bit 1 makes the client
    /// show the sender's name red with " (GM)", and an ignore does not drop the line.
    /// </summary>
    public static class ChatLineFlags
    {
        /// <summary>The flags entry for a GM's line: tag 2, flags 1.</summary>
        public static byte[] GmEntry => new byte[] { 0x02, 0x01 };

        /// <summary>True when <paramref name="sender"/> is a character with a GM level above 0.</summary>
        public static bool IsGm(Client sender)
        {
            return sender != null && sender.Character != null && sender.Character.CharacterGMLevel > 0;
        }

        /// <summary>
        /// The block a relayed line carries, built from what the sender sent: its tag 0 and tag 1 entries (item and
        /// text macros) are kept, every tag 2 entry (line flags) is dropped, and the server appends the GM entry for a
        /// GM. A block that does not parse as entries is dropped whole. EXPLOIT guard: a client's own flags entry made
        /// any sender's name show red with " (GM)".
        /// </summary>
        public static byte[] SanitizeBlock(byte[] block, int offset, int length, bool gm)
        {
            byte[] kept = ParseKeptEntries(block, offset, length) ?? new byte[0];
            if (!gm)
            {
                return kept;
            }

            byte[] entry = GmEntry;
            byte[] result = new byte[kept.Length + entry.Length];
            Array.Copy(kept, 0, result, 0, kept.Length);
            Array.Copy(entry, 0, result, kept.Length, entry.Length);
            return result;
        }

        /// <summary>The tag 0 and tag 1 entries of the block, in order; null when the block is not a run of known entries.</summary>
        private static byte[] ParseKeptEntries(byte[] block, int offset, int length)
        {
            if (block == null || offset < 0 || length < 0 || offset + length > block.Length)
            {
                return null;
            }

            var kept = new System.Collections.Generic.List<byte>(length);
            int position = offset;
            int end = offset + length;
            while (position < end)
            {
                int start = position;
                byte tag = block[position++];
                switch (tag)
                {
                    case 0:
                        // IIISS: three 32-bit ints then two length-prefixed strings.
                        position += 12;
                        if (!SkipString(block, end, ref position) || !SkipString(block, end, ref position))
                        {
                            return null;
                        }
                        break;
                    case 1:
                        // BBSS: two bytes then two length-prefixed strings.
                        position += 2;
                        if (!SkipString(block, end, ref position) || !SkipString(block, end, ref position))
                        {
                            return null;
                        }
                        break;
                    case 2:
                        // One flags byte: the server decides this one.
                        position += 1;
                        if (position > end)
                        {
                            return null;
                        }
                        continue;
                    default:
                        return null;
                }

                if (position > end)
                {
                    return null;
                }

                for (int i = start; i < position; i++)
                {
                    kept.Add(block[i]);
                }
            }

            return kept.ToArray();
        }

        private static bool SkipString(byte[] block, int end, ref int position)
        {
            if (position + 2 > end)
            {
                return false;
            }

            int stringLength = (block[position] << 8) | block[position + 1];
            position += 2 + stringLength;
            return position <= end;
        }
    }

    /// <summary>
    /// A tell (PrivateMessage, 30): sender, text, then the block. The block's first byte is 00 and the line-flag
    /// entries follow it (<see cref="ChatLineFlags"/>): 00 for anyone, 00 02 01 for a GM.
    /// </summary>
    public static class MsgPrivate
    {
        public static byte[] Create(uint senderId, string message, bool gm = false)
        {
            PacketWriter writer = new PacketWriter((ushort)MessageType.PrivateMessage);
            writer.WriteUInt32(senderId);
            writer.WriteString(message ?? string.Empty);
            writer.WriteUInt16((ushort)(gm ? 3 : 1));
            writer.WriteByte(0x00);
            if (gm)
                writer.WriteBytes(ChatLineFlags.GmEntry);
            return writer.Finish();
        }
    }
}
