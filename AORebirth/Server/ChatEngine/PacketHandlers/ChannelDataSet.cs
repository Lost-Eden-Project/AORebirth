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
    using AORebirth.Database.Dao;
    using ChatEngine.Channels;
    using ChatEngine.CoreClient;

    #endregion

    /// <summary>
    /// The channel data set.
    /// </summary>
    public class ChannelDataSet
    {
        #region Fields

        /// <summary>
        /// The channelId.
        /// </summary>
        private byte[] channelId = new byte[] { };

        /// <summary>
        /// The data.
        /// </summary>
        private string data = string.Empty;

        /// <summary>
        /// The mute.
        /// </summary>
        private ushort mute;

        #endregion

        #region Public Methods and Operators

        /// <summary>
        /// Channel Data packet read
        /// </summary>
        /// <param name="client">
        /// Client sending
        /// </param>
        /// <param name="packet">
        /// Packet data
        /// </param>
        public void Read(Client client, byte[] packet)
        {
            PacketReader reader = new PacketReader(ref packet);

            reader.ReadUInt16(); // Packet ID
            reader.ReadUInt16(); // Data length
            this.channelId = reader.ReadBytes(5);
            this.mute = reader.ReadUInt16();
            this.data = reader.ReadString();
            client.Server.Debug(
                client,
                "{0} >> ChannelDataSet: Channel {1} {2} Mute: {3} Data: {4}",
                client.Character.characterName,
                this.channelId[0],
                BitConverter.ToUInt32(this.channelId, 1),
                this.mute,
                this.data);
            if (this.channelId.Length == 5 && this.channelId[0] == (byte)ChannelType.Raid)
            {
                uint raidId = BitConverter.ToUInt32(this.channelId, 1);
                bool eligible = client.IsBot && client.Character.CharacterId != 0;
                bool channelFound = false;
                bool added = false;
                bool member = false;
                foreach (ChannelBase channel in client.ChatServer().Channels)
                {
                    if (channel.channelType == ChannelType.Raid && channel.ChannelId == raidId)
                    {
                        channelFound = true;
                        if (true)
                        {
                            added = channel.AddClient(client);
                            member = client.Channels.Contains(channel);
                        }

                        break;
                    }
                }
            }
            else if (this.channelId.Length == 5 && this.channelId[0] == (byte)ChannelType.Organization
                && client.IsBot && client.Character.CharacterId != 0)
            {
                // Same bot join as the raid, for an existing org channel. The id is accepted little-endian
                // (as the raid id is) or big-endian (as the client sends it in group messages).
                uint littleEndianId = BitConverter.ToUInt32(this.channelId, 1);
                uint bigEndianId = (uint)System.Net.IPAddress.NetworkToHostOrder((int)littleEndianId);
                ChannelBase orgChannel = null;
                lock (client.ChatServer().Channels)
                {
                    foreach (ChannelBase channel in client.ChatServer().Channels)
                    {
                        if (channel.channelType == ChannelType.Organization
                            && (channel.ChannelId == littleEndianId || channel.ChannelId == bigEndianId))
                        {
                            orgChannel = channel;
                            break;
                        }
                    }

                    // The bot may connect before any member: create the channel like AddClientToChannels does,
                    // but only for an organization that exists.
                    if (orgChannel == null)
                    {
                        foreach (uint candidate in new[] { littleEndianId, bigEndianId })
                        {
                            if (candidate != 0 && candidate < int.MaxValue
                                && OrganizationDao.Instance.Get((int)candidate) != null)
                            {
                                orgChannel = new OrganizationChannel((int)candidate);
                                client.ChatServer().Channels.Add(orgChannel);
                                break;
                            }
                        }
                    }
                }

                if (orgChannel != null)
                {
                    orgChannel.AddClient(client);
                }
            }
            reader.Finish();
        }

        #endregion
    }
}
