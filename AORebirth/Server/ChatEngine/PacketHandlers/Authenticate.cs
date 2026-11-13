#region License

// Copyright (c) 2005-2014, CellAO Team
//
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:
//
//     * Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
//     * Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer.
//     * Neither the name of the CellAO Team nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED.
// IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES.
//

#endregion

namespace ChatEngine.PacketHandlers
{
    #region Usings ...

    using System;

    using AO.Core.Encryption;

    using AORebirth.Database.Dao;
    using AORebirth.Database.Entities;

    using ChatEngine.CoreClient;
    using ChatEngine.Packets;

    using Utility;

    #endregion

    /// <summary>
    /// The game client's chat login: character id, account name and the encrypted login key answering this
    /// connection's seed.
    /// </summary>
    internal static class Authenticate
    {
        private const int LoginAllowedFlag = 0;

        private const int MaxUserNameLength = 64;

        private const int MaxLoginKeyLength = 4096;

        // TEMPORARY - RE-ENABLE (set to true) WHEN THE FIXED LOST EDEN CLIENT IS DEPLOYED.
        // Released Lost Eden clients encrypt the chat login key for Funcom's public key instead of ours
        // (ChatLoginKey.cs ignores -server-key), so the server cannot decrypt it. While false, a game client whose
        // key does not verify is still admitted on account name + character ownership alone: anyone who knows an
        // account name and character id can chat as that character. Chat bots always need a valid key.
        private const bool RequireGameClientLoginKey = false;

        #region Public Methods and Operators

        public static void Read(Client client, ref byte[] packet)
        {
            // type(2) length(2) unknown(4) character id(4) name(S) key(S)
            string userName;
            string loginKey;
            uint characterId;
            if (!TryReadCredentials(packet, 12, out userName, out loginKey))
            {
                Refuse(client, "malformed login request");
                return;
            }

            characterId = ((uint)packet[8] << 24) | ((uint)packet[9] << 16) | ((uint)packet[10] << 8) | packet[11];

            if (characterId == 0)
            {
                Refuse(client, "no character");
                return;
            }

            string accountName;
            string refusal;
            if (!TryVerifyAccount(
                    client, userName, loginKey, out accountName, out refusal, RequireGameClientLoginKey))
            {
                Refuse(client, refusal);
                return;
            }

            if (!CharacterDao.Instance.IsCharacterOnAccount(accountName, characterId))
            {
                Refuse(client, "character " + characterId + " is not on account '" + ToLogValue(userName) + "'");
                return;
            }

            client.Character = new Character(characterId, client);
            if (!client.ChatServer().RegisterClient(client))
            {
                client.Server.DisconnectClient(client);
                return;
            }

            client.MarkChatReady();
            client.Send(LoginOk.Create());

            client.KnownClients.Add(client.Character.CharacterId);
            client.Send(PlayerName.Create(client, client.Character.CharacterId));

            client.Send(MsgAnonymousVicinity.Create(
                string.Empty,
                string.Format(
                    client.ChatServer().MessageOfTheDay,
                    AssemblyInfoclass.RevisionName + " " + AssemblyInfoclass.AssemblyVersion),
                string.Empty));

            client.ChatServer().InitializeBuddyPresence(client);
            client.ChatServer().AddClientToChannels(client);
            client.ChatServer().RestorePrivateGroupInvitations(client);
        }

        #endregion

        #region Methods

        /// <summary>
        /// Reads the name and key strings (each a big-endian 16-bit length and ASCII bytes) starting at
        /// <paramref name="offset"/>, refusing lengths that overrun the packet or exceed what a client sends.
        /// </summary>
        internal static bool TryReadCredentials(byte[] packet, int offset, out string userName, out string loginKey)
        {
            userName = null;
            loginKey = null;
            return packet != null
                && TryReadString(packet, ref offset, MaxUserNameLength, out userName)
                && TryReadString(packet, ref offset, MaxLoginKeyLength, out loginKey);
        }

        /// <summary>
        /// Proves the connection holds the account's password: the login key must decrypt to this account name, the
        /// seed sent to this connection, and the stored password. The account must also be allowed to log in.
        /// </summary>
        internal static bool TryVerifyAccount(
            Client client, string userName, string loginKey, out string accountName, out string refusal,
            bool requireKey = true)
        {
            accountName = null;
            if (FailedLogins.IsBlocked(client.AcceptedAddress))
            {
                refusal = "too many failed logins from this address";
                return false;
            }

            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(loginKey)
                || string.IsNullOrEmpty(client.ServerSalt))
            {
                refusal = "missing credentials";
                return false;
            }

            DBLoginData loginData = LoginDataDao.Instance.GetByUsername(userName);
            if (loginData == null || string.IsNullOrWhiteSpace(loginData.Username)
                || string.IsNullOrWhiteSpace(loginData.Password))
            {
                FailedLogins.Record(client.AcceptedAddress);
                refusal = "unknown account '" + ToLogValue(userName) + "'";
                return false;
            }

            // The seed is single use: a failed or replayed attempt needs a new connection.
            string serverSalt = client.ServerSalt;
            client.ServerSalt = string.Empty;

            if (!new LoginEncryption().IsValidLogin(loginKey, serverSalt, userName, loginData.Password))
            {
                if (!requireKey)
                {
                    client.Server.Info(
                        client,
                        "Chat login key NOT verified for account '{0}' (RequireGameClientLoginKey is off)",
                        ToLogValue(userName));
                    return IsAllowed(loginData, userName, out accountName, out refusal);
                }

                FailedLogins.Record(client.AcceptedAddress);
                refusal = "login key rejected for account '" + ToLogValue(userName) + "'";
                return false;
            }

            return IsAllowed(loginData, userName, out accountName, out refusal);
        }

        private static bool IsAllowed(DBLoginData loginData, string userName, out string accountName, out string refusal)
        {
            accountName = null;
            if (loginData.Flags != LoginAllowedFlag || LoginDataDao.Instance.IsSanctioned(loginData.Username))
            {
                refusal = "account '" + ToLogValue(userName) + "' is not allowed to log in";
                return false;
            }

            accountName = loginData.Username;
            refusal = null;
            return true;
        }

        internal static void Refuse(Client client, string reason)
        {
            // Info, not debug: a rejected chat login is what an operator looks for when chat stops connecting.
            client.Server.Info(client, "Chat login refused: {0}", reason ?? string.Empty);
            client.Send(LoginError.Create("Invalid login"));
            client.Server.DisconnectClient(client);
        }

        private static bool TryReadString(byte[] packet, ref int offset, int maxLength, out string value)
        {
            value = null;
            if (offset < 0 || offset + 2 > packet.Length)
            {
                return false;
            }

            int length = (packet[offset] << 8) | packet[offset + 1];
            offset += 2;
            if (length > maxLength || offset + length > packet.Length)
            {
                return false;
            }

            value = System.Text.Encoding.ASCII.GetString(packet, offset, length);
            offset += length;
            return true;
        }

        private static string ToLogValue(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            char[] safe = value.Length > 64 ? value.Substring(0, 64).ToCharArray() : value.ToCharArray();
            for (int i = 0; i < safe.Length; i++)
            {
                if (char.IsControl(safe[i]) || safe[i] == '{' || safe[i] == '}')
                {
                    safe[i] = '?';
                }
            }

            return new string(safe);
        }

        #endregion

        /// <summary>Slows password guessing: an address with too many recent failures is refused for a while.</summary>
        private static class FailedLogins
        {
            private const int MaxFailures = 10;

            private const int MaxTrackedAddresses = 50000;

            private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

            private static readonly System.Collections.Generic.Dictionary<System.Net.IPAddress, Entry> Entries =
                new System.Collections.Generic.Dictionary<System.Net.IPAddress, Entry>();

            internal static bool IsBlocked(System.Net.IPAddress address)
            {
                if (address == null || System.Net.IPAddress.IsLoopback(address))
                {
                    return false;
                }

                lock (Entries)
                {
                    Entry entry;
                    if (!Entries.TryGetValue(address, out entry))
                    {
                        return false;
                    }

                    if (DateTime.UtcNow - entry.WindowStartUtc >= Window)
                    {
                        Entries.Remove(address);
                        return false;
                    }

                    return entry.Failures >= MaxFailures;
                }
            }

            internal static void Record(System.Net.IPAddress address)
            {
                if (address == null || System.Net.IPAddress.IsLoopback(address))
                {
                    return;
                }

                DateTime now = DateTime.UtcNow;
                lock (Entries)
                {
                    Entry entry;
                    if (Entries.TryGetValue(address, out entry) && now - entry.WindowStartUtc < Window)
                    {
                        entry.Failures++;
                        return;
                    }

                    if (Entries.Count >= MaxTrackedAddresses)
                    {
                        // Bounded memory beats a perfect record; live offenders are re-counted within one window.
                        Entries.Clear();
                    }

                    Entries[address] = new Entry { WindowStartUtc = now, Failures = 1 };
                }
            }

            private sealed class Entry
            {
                internal DateTime WindowStartUtc;

                internal int Failures;
            }
        }
    }
}
