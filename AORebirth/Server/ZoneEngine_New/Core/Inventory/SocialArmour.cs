namespace ZoneEngine_New.Core.Inventory
{
    using System;

    using AORebirth.Enums;

    /// <summary>
    /// Social armour (item flag <see cref="ItemFlags.ItemSocialArmour"/>: battle suits 88067-88070, Grid Armor 155173 and
    /// the other whole-body suits) is worn alone on the armor page. The client's two rules (Gamecode.dll), applied only to
    /// what goes onto the armor page: weapons and utilities are checked by the wield path (FUN_10081cb4), which has no such
    /// rule, and the social page passes it by.
    /// <list type="bullet">
    /// <item>FUN_10081b3c, "Feedback_SocialArmorAndOtherArmor": while a social armour item is worn (character flag 2, set
    /// by FUN_10080da0 on equip and cleared by FUN_10080ff7), nothing else goes on unless it carries Can
    /// <see cref="CanFlags.CanBeWornWithSocialArmor"/> or is an implant or spirit.</item>
    /// <item>FUN_100808dc, "Feedback_SocialArmorNoOtherArmor": social armour goes on only when every other item on the
    /// armor page (slots 0x10-0x1F) is the same template (ACGItemTemplateID) or carries that Can flag.</item>
    /// </list>
    /// </summary>
    internal static class SocialArmour
    {
        public static bool IsSocialArmour(Item item) => (item.Flags & (int)ItemFlags.ItemSocialArmour) != 0;

        static bool CanBeWornWith(Item item) => item.Can(CanFlags.CanBeWornWithSocialArmor);

        /// <summary>
        /// True when <paramref name="item"/> may go onto <paramref name="wearPage"/> beside the social armour rules.
        /// <paramref name="item"/> and <paramref name="swappedWith"/> are the items changing place, so neither counts as
        /// worn: a suit swapped out for body armor no longer blocks it.
        /// </summary>
        public static bool Allows(PlayerInventory inventory, Item item, Container wearPage, Item? swappedWith)
        {
            ArgumentNullException.ThrowIfNull(inventory);
            ArgumentNullException.ThrowIfNull(item);
            if (!ReferenceEquals(wearPage, inventory.Armor))
                return true;

            bool Changing(Item worn) => ReferenceEquals(worn, item) || ReferenceEquals(worn, swappedWith);

            // The client also lets implants and spirits through; neither fits the armor page.
            if (!CanBeWornWith(item)
                && (WearsSocialArmour(inventory.Armor, Changing) || WearsSocialArmour(inventory.Equipment, Changing)))
                return false;

            if (!IsSocialArmour(item))
                return true;

            foreach (Item worn in inventory.Armor.Content.Values)
            {
                if (!Changing(worn) && worn.LowId != item.LowId && !CanBeWornWith(worn))
                    return false;
            }

            return true;
        }

        static bool WearsSocialArmour(Container page, Func<Item, bool> changing)
        {
            foreach (Item worn in page.Content.Values)
            {
                if (!changing(worn) && IsSocialArmour(worn))
                    return true;
            }

            return false;
        }
    }
}
