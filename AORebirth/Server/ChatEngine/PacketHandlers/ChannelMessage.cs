#region License

// Copyright (c) 2005-2014, CellAO Team
// 
// 
// All rights reserved.
// 
// 
// Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:
// 
// 
//     * Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
//     * Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
//     * Neither the name of the CellAO Team nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.
// 
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR
// CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
// EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
// PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
// PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
// LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
// NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// 

#endregion

namespace ChatEngine.PacketHandlers
{
    #region Usings ...

    using System;
    using System.Text;

    using AORebirth.ChatModules.Moderation;

    using ChatEngine.Channels;
    using ChatEngine.CoreClient;
    using ChatEngine.Modules;
    using ChatEngine.Packets;

    #endregion

    /// <summary>
    /// The channel message.
    /// </summary>
    internal static class ChannelMessage
    {
        #region Public Methods and Operators

        /// <summary>
        /// The read.
        /// </summary>
        /// <param name="client">
        /// </param>
        /// <param name="packet">
        /// </param>
        public static void Read(Client client, byte[] packet)
        {
            byte[] senderId = BitConverter.GetBytes(client.Character.CharacterId);
            Array.Reverse(senderId);

            ChannelBase channel = client.ChatServer().GetChannel(packet);

            // Every channel is membership-only: a stale Team tab must not still reach the team, a character failing a
            // ChatChannels.json channel's join conditions must not post to it by id, and an organization's channel
            // is for its members.
            if (channel == null || !client.Channels.Contains(channel))
            {
                return;
            }

            PacketReader reader = new PacketReader(ref packet);
            reader.ReadUInt16();
            reader.ReadUInt16();
            reader.ReadUInt16();
            reader.ReadUInt16();
            reader.ReadByte();
            string text = reader.ReadString();
            string channelName = channel.ChannelName;
            string logSender = client.Character.characterName;
            ChatModuleHost modules = client.ChatServer().Modules;

            // '.' and '/' lines go to the zone; a chat module's registered command (such as !level) goes to that module.
            // Either way nobody in the channel sees the line.
            if (!ProcessServerCommand(text, client)
                && modules?.TryConsumeCommand(
                    channel,
                    client.Character.CharacterId,
                    client.Character.characterName,
                    client.Character.CharacterGMLevel,
                    text) != true)
            {
                // A chat module's line filter may replace words or block the line before anyone receives it; the chat
                // log keeps what was actually said.
                ChatFilterOutcome filtered = modules == null
                    ? ChatFilterOutcome.Unchanged(text)
                    : modules.FilterPlayerLine(
                        channel,
                        VicinityChatType.Say,
                        client.Character.CharacterId,
                        client.Character.characterName,
                        client.Character.CharacterGMLevel,
                        text);
                if (filtered.Blocked)
                {
                    ChatLogger.WriteString(channelName, text, logSender + " [blocked by " + filtered.BlockedBy + "]");
                    return;
                }

                string shown = text;
                if (filtered.Replaced)
                {
                    shown = filtered.Text;
                    packet = ReplaceText(packet, shown);
                    logSender += " [filtered by " + filtered.ReplacedBy + "]";
                }

                byte[] newpacket = InsertSender(packet, senderId, ChatLineFlags.IsGm(client));

                foreach (Client recipient in client.ChatServer().ConnectedClients.Values)
                {
                    if (recipient.Channels.Contains(channel))
                    {
                        if (!recipient.KnownClients.Contains(client.Character.CharacterId)
                            && (recipient.Character.CharacterId != client.Character.CharacterId))
                        {
                            byte[] pname = PlayerName.Create(client, client.Character.CharacterId);
                            recipient.Send(pname);
                            recipient.KnownClients.Add(client.Character.CharacterId);
                        }

                        recipient.Send(newpacket);
                    }
                }

                if (client.ChatServer().BotRouter != null)
                {
                    client.ChatServer().BotRouter.PublishChannelMessage(
                        (byte)channel.channelType,
                        channel.ChannelId,
                        client.Character.CharacterId,
                        client.Character.characterName,
                        shown);
                }

                modules?.PublishPlayerMessage(
                    channel,
                    client.Character.CharacterId,
                    client.Character.characterName,
                    shown);
            }

            ChatLogger.WriteString(channelName, text, logSender);
        }

        #endregion

        #region Methods

        /// <summary>
        /// The client's channel packet with its text string (at offset 9, after type, length, channel type and channel id)
        /// replaced by <paramref name="text"/>; the block after it is kept as sent and the length header is rewritten.
        /// </summary>
        internal static byte[] ReplaceText(byte[] packet, string text)
        {
            const int TextOffset = 9;
            if (packet == null || packet.Length < TextOffset + 2)
            {
                return packet;
            }

            int restStart = TextOffset + 2 + ((packet[TextOffset] << 8) | packet[TextOffset + 1]);
            if (restStart > packet.Length)
            {
                return packet;
            }

            byte[] bytes = Encoding.Latin1.GetBytes(text ?? string.Empty);
            byte[] result = new byte[TextOffset + 2 + bytes.Length + (packet.Length - restStart)];
            Array.Copy(packet, 0, result, 0, TextOffset);
            result[TextOffset] = (byte)(bytes.Length >> 8);
            result[TextOffset + 1] = (byte)bytes.Length;
            Array.Copy(bytes, 0, result, TextOffset + 2, bytes.Length);
            Array.Copy(packet, restStart, result, TextOffset + 2 + bytes.Length, packet.Length - restStart);
            int bodyLength = result.Length - 4;
            result[2] = (byte)(bodyLength >> 8);
            result[3] = (byte)bodyLength;
            return result;
        }

        /// <summary>
        /// The client's channel packet (type, length, channel type, channel id, text S, block S) relayed with the sender
        /// id after the channel id and a server-made block: the sender's macro entries kept, its line-flags entries
        /// dropped, and the GM entry 02 01 appended for a GM (<see cref="ChatLineFlags.SanitizeBlock"/>). A packet whose
        /// text or block does not fit its length is relayed with an empty block.
        /// </summary>
        internal static byte[] InsertSender(byte[] packet, byte[] senderId, bool gm)
        {
            const int TextOffset = 9;
            int textEnd = packet.Length;
            byte[] block = ChatLineFlags.SanitizeBlock(packet, 0, 0, gm);
            if (packet.Length >= TextOffset + 2)
            {
                int blockLengthOffset = TextOffset + 2 + ((packet[TextOffset] << 8) | packet[TextOffset + 1]);
                if (blockLengthOffset + 2 <= packet.Length)
                {
                    textEnd = blockLengthOffset;
                    int blockLength = (packet[blockLengthOffset] << 8) | packet[blockLengthOffset + 1];
                    if (blockLengthOffset + 2 + blockLength <= packet.Length)
                        block = ChatLineFlags.SanitizeBlock(packet, blockLengthOffset + 2, blockLength, gm);
                }
            }

            byte[] newpacket = new byte[textEnd + senderId.Length + 2 + block.Length];
            Array.Copy(packet, 0, newpacket, 0, TextOffset);
            Array.Copy(senderId, 0, newpacket, TextOffset, senderId.Length);
            int shift = senderId.Length;
            Array.Copy(packet, TextOffset, newpacket, TextOffset + shift, textEnd - TextOffset);
            int blockOffset = textEnd + shift;
            newpacket[blockOffset] = (byte)(block.Length >> 8);
            newpacket[blockOffset + 1] = (byte)block.Length;
            Array.Copy(block, 0, newpacket, blockOffset + 2, block.Length);

            int bodyLength = newpacket.Length - 4;
            newpacket[2] = (byte)(bodyLength >> 8);
            newpacket[3] = (byte)bodyLength;
            return newpacket;
        }

        /// <summary>
        /// </summary>
        /// <param name="text">
        /// </param>
        /// <param name="client">
        /// </param>
        /// <returns>
        /// </returns>
        private static bool ProcessServerCommand(string text, Client client)
        {
            if (text.StartsWith(".") || text.StartsWith("/"))
            {
                ChatEngine.CoreServer.ChatServer.SendToZone((int)client.Character.CharacterId, text);
                return true;
            }

            return false;
        }

        #endregion
    }
}
