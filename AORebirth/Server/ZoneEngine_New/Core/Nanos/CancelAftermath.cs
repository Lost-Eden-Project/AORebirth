namespace ZoneEngine_New.Core.Nanos
{
    using System;

    using ZoneEngine_New.Core.Inventory;

    /// <summary>
    /// One OnCancel function of a nano its owner cancelled, still repeating on the owner after the NCU entry left:
    /// Zazen Stance 287559 sets current nano to 1 ten times, a second apart. Lives on the owner.
    /// </summary>
    internal sealed class CancelAftermath(Buff nano, ItemSpell spell, int remaining, DateTime nextUtc)
    {
        /// <summary>The cancelled nano, which runs each repeat.</summary>
        public Buff Nano { get; } = nano;

        public ItemSpell Spell { get; } = spell;

        public int Remaining { get; set; } = remaining;

        public DateTime NextUtc { get; set; } = nextUtc;
    }
}
