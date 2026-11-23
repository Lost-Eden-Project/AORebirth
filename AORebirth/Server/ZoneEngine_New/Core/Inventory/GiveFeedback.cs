namespace ZoneEngine_New.Core.Inventory
{
    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages.N3Messages;

    using ZoneEngine_New.Core.Entities;

    /// <summary>
    /// The healer's and the healed one's combat-window lines for health or nano one character gives another. The client
    /// prints them from CharacterAction (Gamecode CharacterActionIIR_t 1005d61f): the message's character is the
    /// receiving client's own, Target the other character, Parameter2 the amount. The line always speaks as "you", so each
    /// goes only to that player's session, never to the cell. A self-heal sends none (its line is HealthDamage's).
    /// </summary>
    internal static class GiveFeedback
    {
        /// <summary>Health <paramref name="amount"/> a player actually raised on <paramref name="receiver"/>.</summary>
        internal static void HealthGiven(Character giver, Character receiver, int amount)
            => Send(giver, receiver, amount, CharacterActionType.Healed, CharacterActionType.HealedBy);

        /// <summary>CurrentNano <paramref name="amount"/> a player actually raised on <paramref name="receiver"/>.</summary>
        internal static void NanoGiven(Character giver, Character receiver, int amount)
            => Send(giver, receiver, amount, CharacterActionType.NanoIncreased, CharacterActionType.NanoIncreasedBy);

        static void Send(Character giver, Character receiver, int amount, CharacterActionType gave, CharacterActionType got)
        {
            if (amount <= 0 || ReferenceEquals(giver, receiver) || giver is not Player giverPlayer)
                return;

            giverPlayer.Session?.Send(Message(giverPlayer, receiver, gave, amount));
            if (receiver is Player receiverPlayer)
                receiverPlayer.Session?.Send(Message(receiverPlayer, giverPlayer, got, amount));
        }

        static CharacterActionMessage Message(Player owner, Character other, CharacterActionType action, int amount) => new()
        {
            Identity = owner.Identity,
            Action = action,
            Target = other.Identity,
            Parameter1 = 0,
            Parameter2 = amount
        };
    }
}
