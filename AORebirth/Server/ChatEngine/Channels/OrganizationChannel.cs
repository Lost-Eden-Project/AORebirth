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

namespace ChatEngine.Channels
{
    using System.Data;
    using System.Linq;

    using AORebirth.Database;

    using Dapper;

    /// <summary>
    /// One organization's chat channel (id = organization id). Membership comes from org_members, the only authority;
    /// the zone that changes it moves characters in and out of channels (World chat events).
    /// </summary>
    public class OrganizationChannel : ChannelBase
    {
        public OrganizationChannel(int organizationId, string name)
            : base(
                ChannelFlags.NoInternational | ChannelFlags.GroupOnName,
                ChannelType.Organization,
                (uint)organizationId,
                name ?? string.Empty)
        {
        }

        /// <summary>The character's organization id and name; (0, null) when it belongs to none. Blocking.</summary>
        public static (int OrganizationId, string Name) MembershipOf(int characterId)
        {
            if (characterId <= 0)
            {
                return (0, null);
            }

            using (IDbConnection connection = Connector.GetConnection())
            {
                var row = connection.Query<(int, string)>(
                    "SELECT o.OrganizationId, o.Name FROM org_members m INNER JOIN org_organizations o "
                    + "ON o.OrganizationId=m.OrganizationId WHERE m.CharacterId=@Id",
                    new { Id = characterId }).FirstOrDefault();
                return row.Item1 > 0 ? row : (0, null);
            }
        }

        /// <summary>The organization's name, or null when it does not exist. Blocking.</summary>
        public static string NameOf(int organizationId)
        {
            if (organizationId <= 0)
            {
                return null;
            }

            using (IDbConnection connection = Connector.GetConnection())
            {
                return connection.Query<string>(
                    "SELECT Name FROM org_organizations WHERE OrganizationId=@Id", new { Id = organizationId }).FirstOrDefault();
            }
        }
    }
}
