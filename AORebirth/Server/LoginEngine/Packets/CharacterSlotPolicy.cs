namespace LoginEngine.Packets
{
    using System;

    /// <summary>
    /// How many characters an account may hold, and whether one more may be created. The account's own allowance can
    /// raise the default, never lower it (the same rule the character list applies). Counting happens inside the
    /// creation transaction with the account row locked (<see cref="CharacterName"/>), so two creations racing for the
    /// last slot cannot both pass. Self-contained so the zone test project can exercise it.
    /// </summary>
    public static class CharacterSlotPolicy
    {
        /// <summary>The slots an account gets: its own allowance or the default, whichever is higher.</summary>
        public static int Allowed(int accountAllowedCharacters, int defaultAllowedCharacters)
        {
            return Math.Max(accountAllowedCharacters, defaultAllowedCharacters);
        }

        /// <summary>True while the account holds fewer characters than it is allowed.</summary>
        public static bool CanCreate(long existingCharacters, int allowedCharacters)
        {
            return existingCharacters >= 0 && allowedCharacters > 0 && existingCharacters < allowedCharacters;
        }
    }
}
