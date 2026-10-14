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

    using AORebirth.Database.Dao;

    using ChatEngine.CoreClient;
    using ChatEngine.Packets;

    #endregion

    /// <summary>
    /// The buddy add.
    /// </summary>
    public static class BuddyAdd
    {
        #region Public Methods and Operators

        /// <summary>
        /// Read buddy add packet
        /// </summary>
        /// <param name="client">
        /// Client sending
        /// </param>
        /// <param name="packet">
        /// Packet data
        /// </param>
        public static void Read(Client client, byte[] packet)
        {
            PacketReader reader = new PacketReader(ref packet);

            reader.ReadUInt16(); // Packet ID
            reader.ReadUInt16(); // Data length
            uint playerId = reader.ReadUInt32();
            ushort unknown1 = reader.ReadUInt16();
            byte unknown2 = reader.ReadByte();
            client.Server.Debug(
                client,
                "{0} >> BuddyAdd: PlayerID {1} Unknown1: {2} Unknown2: {3}",
                client.Character.characterName,
                playerId,
                unknown1,
                unknown2);
            reader.Finish();

            if (playerId == 0 || playerId > int.MaxValue || client.Character == null)
            {
                client.Server.Debug(
                    client,
                    "{0} << BuddyAdd rejected: invalid PlayerID {1}",
                    client.Character == null ? string.Empty : client.Character.characterName,
                    playerId);
                client.Send(PlayerIdUnknown.Create(playerId));
                return;
            }

            string buddyName = CharacterDao.Instance.GetCharacterNameById((int)playerId);
            if (string.IsNullOrWhiteSpace(buddyName))
            {
                client.Server.Debug(
                    client,
                    "{0} << BuddyAdd rejected: no character found for PlayerID {1}",
                    client.Character.characterName,
                    playerId);
                client.Send(PlayerIdUnknown.Create(playerId));
                return;
            }

            bool persisted = CharacterDao.Instance.AddBuddy(
                (int)client.Character.CharacterId, (int)playerId);
            if (!persisted)
            {
                client.Server.Debug(
                    client,
                    "{0} << BuddyAdd persistence verification failed: OwnerCharacterID {1} BuddyID {2}",
                    client.Character.characterName,
                    client.Character.CharacterId,
                    playerId);
                return;
            }

            client.Send(PlayerName.Create(playerId, buddyName));
            if (!client.KnownClients.Contains(playerId))
            {
                client.KnownClients.Add(playerId);
            }

            uint online = 0;
            Client connectedBuddy;
            if (client.ChatServer().ConnectedClients.TryGetValue(playerId, out connectedBuddy)
                && connectedBuddy != null)
            {
                online = 1;
            }
            else
            {
                try
                {
                    online = CharacterDao.Instance.IsOnline((int)playerId) > 0 ? 1u : 0u;
                }
                catch
                {
                    online = 0;
                }
            }

            byte[] requestFlags =
            {
                (byte)(unknown1 >> 8),
                (byte)unknown1,
                unknown2
            };
            client.SetBuddyStatusFlags(playerId, requestFlags);
            client.Send(BuddyOnlineStatus.Create(playerId, online, requestFlags));
            client.Server.Debug(
                client,
                "{0} << BuddyAdd processed: OwnerCharacterID {1} PlayerID {2} Name {3} Online {4} Persisted {5} Flags {6:X4}/{7:X2}",
                client.Character.characterName,
                client.Character.CharacterId,
                playerId,
                buddyName,
                online,
                persisted,
                unknown1,
                unknown2);
        }

        #endregion
    }
}
