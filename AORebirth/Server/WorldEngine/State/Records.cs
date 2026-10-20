namespace WorldEngine.State
{
    using AORebirth.World.Contracts.V1;

    public enum LeaseState
    {
        /// <summary>The owner simulates the character (in play or link-dead).</summary>
        InPlay,

        /// <summary>The source saved and let go; the destination has not claimed yet.</summary>
        Transferring
    }

    /// <summary>Who owns a character. Guarded by the <see cref="WorldState"/> lock.</summary>
    public sealed class CharacterLease
    {
        public CharacterLease(int characterId, string owner, LeaseToken token, string account)
        {
            CharacterId = characterId;
            Owner = owner;
            Token = token;
            Account = account;
        }

        public int CharacterId { get; }

        /// <summary>Instance name. During a transfer, the source until the destination claims.</summary>
        public string Owner { get; set; }

        public LeaseToken Token { get; set; }

        public string Account { get; set; }

        public LeaseState State { get; set; } = LeaseState.InPlay;

        public string? TransferTarget { get; set; }

        public string Name { get; set; } = string.Empty;

        public int PlayfieldId { get; set; }

        public bool LinkDead { get; set; }

        public int Level { get; set; }

        public int Profession { get; set; }

        /// <summary>The inspect card and team row the owning zone last reported.</summary>
        public PlayerCard? Card { get; set; }
    }

    public enum TicketKind
    {
        Login,
        Transfer
    }

    /// <summary>The cookies a client presents to one zone instance, once. Guarded by the <see cref="WorldState"/> lock.</summary>
    public sealed class HandoffTicket
    {
        public required int CharacterId { get; init; }

        public required byte[] CookieHash { get; init; }

        public required string Account { get; init; }

        /// <summary>The login generation it was issued under; empty for transfers.</summary>
        public required string Generation { get; init; }

        public required string Target { get; init; }

        public required TicketKind Kind { get; init; }

        public required long ExpiresMs { get; init; }

        public TransferEnvelope? Envelope { get; init; }
    }

    /// <summary>Which instance runs a playfield. Guarded by the <see cref="WorldState"/> lock.</summary>
    public sealed class PlayfieldAssignment
    {
        public PlayfieldAssignment(int playfieldId, string instance, ulong assignment, long nowMs)
        {
            PlayfieldId = playfieldId;
            Instance = instance;
            Assignment = assignment;
            LastRoutedMs = nowMs;
        }

        public int PlayfieldId { get; }

        public string Instance { get; set; }

        public ulong Assignment { get; set; }

        /// <summary>Last time a ticket or transfer was routed here; a recently routed playfield is not released.</summary>
        public long LastRoutedMs { get; set; }
    }
}
