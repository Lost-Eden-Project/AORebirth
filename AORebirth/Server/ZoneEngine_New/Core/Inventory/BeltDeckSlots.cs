namespace ZoneEngine_New.Core.Inventory
{
    using AORebirth.Enums;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using ZoneEngine_New.Core.Entities;

    /// <summary>
    /// The NCU belt opens the weapon page's deck slots: deck slot N (Ncu1..Ncu6) is usable only while the character's
    /// BeltSlots stat, which the worn belt's OnWear sets (306001 Belt of Great Justice: 6), is at least N. The belt
    /// cannot leave its slot (unequip, swap, delete or drop) while any deck slot holds a deck, so decks never stay
    /// equipped past the slots their belt opens.
    /// </summary>
    internal static class BeltDeckSlots
    {
        const int FirstDeckSlot = (int)WeaponSlots.Ncu1;
        const int LastDeckSlot = (int)WeaponSlots.Ncu6;

        static bool IsBeltSlot(Container page, int slot)
            => page.Identity.Type == IdentityType.WeaponPage && slot == (int)WeaponSlots.Belt;

        static bool IsDeckSlot(Container page, int slot)
            => page.Identity.Type == IdentityType.WeaponPage && slot >= FirstDeckSlot && slot <= LastDeckSlot;

        /// <summary>True when any deck slot holds an item.</summary>
        static bool HasDeckEquipped(PlayerInventory inventory)
        {
            for (int slot = FirstDeckSlot; slot <= LastDeckSlot; slot++)
            {
                if (inventory.Equipment.Content.ContainsKey(slot))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether a move of <paramref name="item"/> from source to destination (swapping with
        /// <paramref name="destOccupant"/>, if any) keeps the belt and its deck slots consistent.
        /// </summary>
        public static bool AllowsMove(
            Player player,
            Container sourcePage,
            int sourceSlot,
            Container destPage,
            int destSlot,
            Item? destOccupant)
        {
            // A deck may go only into a slot the worn belt opens.
            if (IsDeckSlot(destPage, destSlot)
                && destSlot - FirstDeckSlot + 1 > player.Stats.Get(CharacterStat.BeltSlots))
                return false;

            // The belt leaves its slot when it is moved out, or when another item is swapped into the belt slot.
            bool beltLeaves = IsBeltSlot(sourcePage, sourceSlot) || (IsBeltSlot(destPage, destSlot) && destOccupant != null);
            return !beltLeaves || !HasDeckEquipped(player.Inventory);
        }

        /// <summary>Whether the item in <paramref name="page"/>'s <paramref name="slot"/> may be deleted or dropped.</summary>
        public static bool AllowsRemoval(Player player, Container page, int slot)
            => !IsBeltSlot(page, slot) || !HasDeckEquipped(player.Inventory);
    }
}
