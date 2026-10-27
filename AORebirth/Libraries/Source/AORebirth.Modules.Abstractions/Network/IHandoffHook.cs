namespace AORebirth.Modules.Network
{
    /// <summary>
    /// Decides on login handoffs to this zone (module API 1.4). Called once per character select whose target is this
    /// zone, on the World link's thread, before World issues the handoff ticket. Must return quickly: the player's
    /// login waits on it, and World refuses the handoff if the answer takes longer than a few seconds.
    /// </summary>
    public interface IHandoffHook
    {
        HandoffDecision Decide(HandoffRequest request);
    }

    public sealed class HandoffRequest
    {
        public HandoffRequest(string account, int characterId, string clientVersion)
        {
            Account = account;
            CharacterId = characterId;
            ClientVersion = clientVersion;
        }

        public string Account { get; }

        public int CharacterId { get; }

        /// <summary>The version string the client sent at login, exactly as received (may carry trailing NULs).</summary>
        public string ClientVersion { get; }
    }

    public readonly struct HandoffDecision
    {
        HandoffDecision(bool admitted, uint eventServerType, string reason)
        {
            Admitted = admitted;
            EventServerType = eventServerType;
            Reason = reason;
        }

        public bool Admitted { get; }

        /// <summary>ZoneInfo's EventServerType for this handoff; 0 leaves the retail value (1).</summary>
        public uint EventServerType { get; }

        /// <summary>Why the client was refused, for the logs. Not shown to the player.</summary>
        public string Reason { get; }

        public static HandoffDecision Admit() => new(true, 0, string.Empty);

        public static HandoffDecision Admit(uint eventServerType) => new(true, eventServerType, string.Empty);

        public static HandoffDecision Refuse(string reason) => new(false, 0, reason ?? string.Empty);
    }
}
