namespace ZoneEngine_New.Core.Nanos
{
    using System;
    using System.Collections.Generic;

    public enum BuffApplyDecision
    {
        /// <summary>Nothing in NCU conflicts; add the buff.</summary>
        Apply,

        /// <summary>Existing entries must leave first (recast, or weaker nanos on a shared nano line).</summary>
        Replace,

        /// <summary>The nano has no duration, so it never enters NCU.</summary>
        RefusedNotABuff,

        /// <summary>A nano sharing a line with a higher stacking order is already up.</summary>
        RefusedStrainStronger,

        /// <summary>The target does not have enough free NCU.</summary>
        RefusedNotEnoughNcu,
    }

    /// <summary>
    /// Strain, stacking and NCU decision for landing one nano on one target. Pure: it reads the
    /// current NCU list and answers what should happen, and never mutates anything.
    /// </summary>
    public static class BuffApplyRules
    {
        /// <summary>
        /// Decides whether <paramref name="spell"/> can land. Friendly buffs must fit
        /// <paramref name="maxNcu"/> unless they ignore NCU; hostile nanos never consume it.
        /// A nano conflicts with every running buff it shares a nano line with (<see cref="NanoSpell.StackingLines"/>):
        /// one with a higher stacking order refuses it, and the rest all leave when it lands, so a composite
        /// replaces each single-line nano it covers. The buff that launches the nano (<see cref="NanoSpell.LaunchesNano"/>)
        /// is left out of that: Refresh Outfit (Team) 223241 (line 588, order 11) team-casts its nano-heal 216580 (line
        /// 588, order 10) on the land and every 8 s, and on the caster, where the parent runs, the child must land beside
        /// it rather than be refused by it or replace it. <paramref name="replaced"/> lists the leaving buffs on
        /// <see cref="BuffApplyDecision.Replace"/>; otherwise it is empty.
        /// </summary>
        public static BuffApplyDecision Evaluate(
            NanoSpell spell,
            IReadOnlyList<Buff> active,
            int maxNcu,
            out IReadOnlyList<Buff> replaced)
        {
            ArgumentNullException.ThrowIfNull(spell);
            ArgumentNullException.ThrowIfNull(active);

            replaced = [];
            if (!spell.IsBuff)
                return BuffApplyDecision.RefusedNotABuff;

            List<Buff>? leaving = null;
            int usedNcu = 0;
            int freedNcu = 0;
            for (int i = 0; i < active.Count; i++)
            {
                Buff buff = active[i];
                if (buff.ConsumesNcu)
                    usedNcu += buff.NcuCost;

                // A recast refreshes itself and never loses the stacking comparison.
                if (buff.Id != spell.Id)
                {
                    if (!spell.SharesStackingLine(buff) || buff.LaunchesNano(spell.Id))
                        continue;

                    if (buff.StackingOrder > spell.StackingOrder)
                        return BuffApplyDecision.RefusedStrainStronger;
                }

                (leaving ??= new List<Buff>(1)).Add(buff);
                if (buff.ConsumesNcu)
                    freedNcu += buff.NcuCost;
            }

            if (spell.ConsumesNcu && usedNcu - freedNcu + spell.NcuCost > maxNcu)
                return BuffApplyDecision.RefusedNotEnoughNcu;

            if (leaving == null)
                return BuffApplyDecision.Apply;

            replaced = leaving;
            return BuffApplyDecision.Replace;
        }
    }
}
