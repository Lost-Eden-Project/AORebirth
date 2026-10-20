namespace ZoneEngine_New.Core.Characters
{
    using System;
    using System.Threading;

    /// <summary>
    /// The most credits a character may hold (Gameplay.json inventory.maxCredits). No gain takes a character past it:
    /// trades and shop sales that would are refused, and loot, rewards and GM sets stop at it. Set once at startup.
    /// </summary>
    public static class CashLimit
    {
        /// <summary>
        /// The client's own limit: it ignores a Cash value above 1,000,000,000 (Gamecode.dll, the character stat setter
        /// for stat 61), so a larger cap works on the server but the credits window stops updating past it.
        /// </summary>
        public const int Default = 1_000_000_000;

        static int _max = Default;

        public static int Max => Volatile.Read(ref _max);

        public static void Configure(int max)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
            Volatile.Write(ref _max, max);
        }

        /// <summary><paramref name="cash"/> held to 0..<see cref="Max"/>.</summary>
        public static int Clamp(long cash) => cash < 0 ? 0 : cash > Max ? Max : (int)cash;

        /// <summary>How many more credits a character holding <paramref name="current"/> can take.</summary>
        public static int Room(int current) => Math.Max(0, Max - Math.Max(0, current));
    }
}
