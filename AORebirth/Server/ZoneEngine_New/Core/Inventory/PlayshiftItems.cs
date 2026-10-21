namespace ZoneEngine_New.Core.Inventory
{
    using System.Collections.Generic;

    using AORebirth.Enums;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using ZoneEngine_New.Core.Entities;

    /// <summary>
    /// Worn items whose PlayShiftRequirements (action 136) the wearer does not meet where it stands are disabled: they
    /// stay equipped but add no wear bonuses, functions or procs (<see cref="Item.PlayshiftSuppressed"/>), and the
    /// client gets <see cref="ItemFlags.Disabled"/> (0x40) in the item's slot flags so it stops counting it too. A
    /// Yalmaha (117322: playfield not indoors, no expansion playfield, MonsterData 0, flying allowed) stops flying in a
    /// playfield that forbids it, and works again in one that allows it. Re-checked on every playfield arrival, before
    /// the FullCharacter that carries the slot flags.
    /// </summary>
    public static class PlayshiftItems
    {
        /// <summary>
        /// Re-checks every worn item and rebases when one changed. Call after the character's playfield-entry stats are
        /// set and before its FullCharacter is built. True when anything changed.
        /// </summary>
        public static bool Refresh(Player player)
        {
            if (!player.Inventory.IsHydrated)
                return false;

            bool changed = false;
            foreach (Item item in player.Inventory.WornItems(includeIneffective: true))
            {
                ItemTemplate? definition = item.Definition;
                if (definition == null)
                    continue;

                bool meets = definition.MeetsActionRequirements(
                    stat => WithoutOwnBonus(player, item, stat),
                    ActionType.PlayShiftRequirements,
                    player.ResolveRequirement);
                if (item.PlayshiftSuppressed != meets)
                    continue;

                item.PlayshiftSuppressed = !meets;
                changed = true;
            }

            if (changed)
                player.Rebase();
            return changed;
        }

        /// <summary>
        /// <paramref name="stat"/> as it would read without <paramref name="item"/>'s own wear bonus: a Yalmaha's
        /// MonsterShape must not fail its own MonsterData = 0 check.
        /// </summary>
        static int WithoutOwnBonus(Player player, Item item, CharacterStat stat)
        {
            int full = player.Stats.Get(stat);
            if (item.PlayshiftSuppressed)
                return full;

            int baseValue = StatCollection.Normalize(player.Stats.Get(stat, StatDetail.Base));
            var scratch = new StatCollection();
            scratch.Set(stat, baseValue);
            WearBonusApplier.ApplyItem(item, includeWield: true, scratch);
            int own = StatCollection.Normalize(scratch.Get(stat)) - baseValue;
            return own == 0 ? full : StatCollection.Normalize(full) - own;
        }
    }
}
