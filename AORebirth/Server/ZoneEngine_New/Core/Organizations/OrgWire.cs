namespace ZoneEngine_New.Core.Organizations
{
    using System;
    using System.Buffers.Binary;
    using System.Text;

    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages;
    using LostEden.Protocol.Messages.N3Messages;

    using ZoneEngine_New.Core.Network;

    /// <summary>OrgServer messages (N3 0x64582A07): the organization info window and the invitation dialog.</summary>
    public static class OrgWire
    {
        public static Identity OrganizationIdentity(int organizationId)
            => new() { Type = IdentityType.Organization, Instance = organizationId };

        /// <summary>
        /// OrgServer 0x02 for <paramref name="recipient"/>. <paramref name="rank"/> is shown only to members of the same
        /// organization (capture 20260623-084448). A null <paramref name="state"/> is the empty info retail sends when a
        /// character leaves, so the client clears its organization window.
        /// </summary>
        public static OrgServerMessage Info(Identity recipient, OrgState? state, string rank, string governingForm)
            => new()
            {
                Identity = recipient,
                OrgServerMessageType = OrgServerMessageType.OrgInfo,
                Organization = OrganizationIdentity(state?.Id ?? 0),
                IOrgServerMessage = new OrganizationInfo
                {
                    OrganizationName = state?.Name ?? string.Empty,
                    Description = state?.Description ?? string.Empty,
                    Objective = state?.Objective ?? string.Empty,
                    History = state?.History ?? string.Empty,
                    GoverningForm = governingForm,
                    LeaderName = state?.LeaderName ?? string.Empty,
                    Rank = rank,
                    ControlledAreas = []
                }
            };

        /// <summary>
        /// OrgServer 0x05, the invitation dialog ("Do you want to join the organization "%s"?", 501/67156085): the
        /// common OrgServer header (type, two zero ints, the 0xDEAA organization identity), the organization name as a
        /// 16-bit length and its bytes, then a zero int. The legacy SmokeLounge contract and the retail OrgInfo capture
        /// put the name in that header for every type; LostEden.Protocol 0.4.1's OrgInvite has no name field, so this
        /// one is written here. The client answers with OrgClient Join (0x0F) naming the organization.
        /// </summary>
        public static byte[] Invite(Identity invitee, int clientPlayfieldId, int organizationId, string organizationName)
        {
            byte[] name = Encoding.ASCII.GetBytes(OrgText.Printable(organizationName, OrganizationRules.NameColumnLength));
            byte[] body = new byte[1 + 4 + 4 + 8 + 2 + name.Length + 4];
            Span<byte> span = body;
            span[0] = (byte)OrgServerMessageType.OrgInvite;
            BinaryPrimitives.WriteInt32BigEndian(span.Slice(1), 0);
            BinaryPrimitives.WriteInt32BigEndian(span.Slice(5), 0);
            BinaryPrimitives.WriteInt32BigEndian(span.Slice(9), (int)IdentityType.Organization);
            BinaryPrimitives.WriteInt32BigEndian(span.Slice(13), organizationId);
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(17), (ushort)name.Length);
            name.CopyTo(span.Slice(19));
            BinaryPrimitives.WriteInt32BigEndian(span.Slice(19 + name.Length), 0);

            byte[] packet = ModuleRawMessage.Encode((int)N3MessageType.OrgServer, invitee, clientPlayfieldId, invitee.Instance, body);
            // Retail OrgServer packets carry 0 in the N3 header byte (capture 20260623-084448), as the codec's OrgInfo does.
            packet[ModuleRawMessage.N3HeaderLength - 1] = 0;
            return packet;
        }
    }
}
