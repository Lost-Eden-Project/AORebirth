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

namespace LoginEngine.MessageHandlers
{
    #region Usings ...

    using System;
    using System.ComponentModel.Composition;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;

    using AORebirth.Core.Components;
    using AORebirth.Database.Dao;

    using LoginEngine.CoreClient;
    using LoginEngine.Packets;

    using SmokeLounge.AOtomation.Messaging.Messages;
    using SmokeLounge.AOtomation.Messaging.Messages.SystemMessages;

    using Utility;
    using Utility.Config;

    #endregion

    /// <summary>
    /// </summary>
    [Export(typeof(IHandleMessage))]
    public class SelectCharacterHandler : IHandleMessage<SelectCharacterMessage>
    {
        #region Public Methods and Operators

        /// <summary>
        /// </summary>
        /// <param name="sender">
        /// </param>
        /// <param name="message">
        /// </param>
        public void Handle(object sender, Message message)
        {
            var client = (Client)sender;
            var selectCharacterMessage = (SelectCharacterMessage)message.Body;
            string authenticatedAccount;
            if (!client.TryGetAuthenticatedAccountName(out authenticatedAccount))
            {
                client.RejectAuthentication();
                return;
            }

            var checkLogin = new CheckLogin();
            if (checkLogin.IsCharacterOnAccount(authenticatedAccount, selectCharacterMessage.CharacterId) == false)
            {
                Colouring.Push(ConsoleColor.Green);
                Console.WriteLine(
                    "Client '" + authenticatedAccount + "' tried to log in as CharID "
                    + selectCharacterMessage.CharacterId + " but it is not on their account!");
                Colouring.Pop();

                // NV: Is this really what we want to send? Should find out sometime...
                client.Send(0x00001F83, new LoginErrorMessage { Error = LoginError.InvalidUserNamePassword });
                client.Server.DisconnectClient(client);
                return;
            }

            try
            {
                // World picks the zone instance: the one that already owns the character (link-dead, or in play on a
                // second client) or the one that runs its playfield.
                int storedPlayfield = AORebirth.Database.DatabaseDaoFactory.CreateCharacterDao()
                    .LoadById(selectCharacterMessage.CharacterId)?.Playfield ?? 0;
                var ticket = Client.World.SelectCharacter(
                    authenticatedAccount, client.ZoneHandoffGeneration, selectCharacterMessage.CharacterId, storedPlayfield,
                    client.ClientVersion);
                if (!ticket.Accepted)
                {
                    throw new InvalidOperationException("World refused the zone handoff: " + ticket.Reason);
                }

                IPAddress zoneIpAdress;
                if (!IPAddress.TryParse(ticket.Zone.Address, out zoneIpAdress)
                    || zoneIpAdress.AddressFamily != AddressFamily.InterNetwork
                    || ticket.Zone.Port == 0 || ticket.Zone.Port > ushort.MaxValue)
                {
                    throw new InvalidOperationException("World returned an unusable zone endpoint.");
                }

                var zoneRedirectionMessage = new ZoneInfoMessage
                                             {
                                                 CharacterId = selectCharacterMessage.CharacterId,
                                                 Cookie1 = ticket.Cookie1,
                                                 Cookie2 = ticket.Cookie2,
                                                 // The target zone's handoff hook may choose it; retail is 1.
                                                 EventServerType = ticket.EventServerType != 0 ? ticket.EventServerType : 1,
                                                 // The retail client stores this field but has no proven runtime consumer.
                                                 PlayerId = 0,
                                                 ServerIpAddress = zoneIpAdress,
                                                 ServerPort = (ushort)ticket.Zone.Port
                                             };
                client.MarkCharacterOnlineForHandoff(selectCharacterMessage.CharacterId);
                client.StartZoneHandoff();
                client.Send(0x0000615B, zoneRedirectionMessage);
            }
            catch (Exception exception)
            {
                client.Server.Warning(
                    client,
                    "Zone handoff failed for character {0}: {1}",
                    selectCharacterMessage.CharacterId,
                    exception.Message);
                client.FailZoneHandoff("handoff-failure");
                client.Server.DisconnectClient(client);
            }
        }

        #endregion
    }
}
