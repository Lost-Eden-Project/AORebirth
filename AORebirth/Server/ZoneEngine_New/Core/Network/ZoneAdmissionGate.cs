namespace ZoneEngine_New.Core.Network;

using System;
using System.Threading.Tasks;

using AORebirth.Interfaces.Persistence.Characters;
using AORebirth.World.Contracts.V1;

using SmokeLounge.AOtomation.Messaging.Messages.SystemMessages;

using ZoneEngine_New.Core.World;

public enum ZoneAdmissionKind
{
    /// <summary>A fresh login: hydrate from the database.</summary>
    Login,

    /// <summary>The character is already owned here (link-dead or a second client): take it over.</summary>
    Reconnect,

    /// <summary>A playfield change within this instance.</summary>
    Redirect,

    /// <summary>Arriving from another instance: hydrate, then apply the envelope.</summary>
    Transfer
}

public sealed record ZoneAdmission(bool Accepted, string Reason, ZoneAdmissionKind Kind = ZoneAdmissionKind.Login,
    TransferEnvelope? Envelope = null)
{
    public static ZoneAdmission Refused(string reason) => new(false, reason);
}

public interface IZoneAdmissionGate
{
    Task<ZoneAdmission> ClaimAsync(ZoneLoginMessage message);
}

/// <summary>
/// Checks a client's cookies: a same-instance redirect locally, everything else with World, which grants the lease.
/// Never hydrates or spawns.
/// </summary>
public sealed class ZoneAdmissionGate(ICharacterDao characters, WorldLink world, ZoneRedirects redirects) : IZoneAdmissionGate
{
    public async Task<ZoneAdmission> ClaimAsync(ZoneLoginMessage message)
    {
        if (message.CharacterId <= 0 || (message.Cookie1 == 0 && message.Cookie2 == 0))
            return ZoneAdmission.Refused("missing_handoff");
        if (redirects.TryConsume(message.CharacterId, message.Cookie1, message.Cookie2))
            return new ZoneAdmission(true, "claimed_redirect", ZoneAdmissionKind.Redirect);

        ClaimReply reply = await world.ClaimAsync(message.CharacterId, message.Cookie1, message.Cookie2).ConfigureAwait(false);
        if (!reply.Accepted) return ZoneAdmission.Refused(reply.Reason);

        // The ticket's account must still own the character row (it may have been deleted or moved since). A ticket
        // that does not say whose it is never gets in.
        string? owner = await Task.Run(() => characters.LoadById(message.CharacterId)?.AccountUsername).ConfigureAwait(false);
        if (reply.Account.Length == 0 || !string.Equals(owner, reply.Account, StringComparison.OrdinalIgnoreCase))
        {
            world.Abandon(message.CharacterId);
            return ZoneAdmission.Refused(reply.Account.Length == 0 ? "unknown_account" : "account_mismatch");
        }

        ZoneAdmissionKind kind = reply.Kind switch
        {
            ClaimKind.ClaimReconnect => ZoneAdmissionKind.Reconnect,
            ClaimKind.ClaimTransfer => ZoneAdmissionKind.Transfer,
            _ => ZoneAdmissionKind.Login
        };
        return new ZoneAdmission(true, reply.Reason, kind, reply.Envelope);
    }
}
