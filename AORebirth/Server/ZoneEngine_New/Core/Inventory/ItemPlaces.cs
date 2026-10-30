namespace ZoneEngine_New.Core.Inventory
{
    using AORebirth.Modules.Events;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Modules;

    /// <summary>
    /// Module API 1.6 item places of the engine's pages, for <see cref="ModuleHooks.ItemMoved"/>. Overflow counts as the
    /// inventory, every wear page as equipment; a bag names its bag item, loot its corpse (or chest, a world container).
    /// </summary>
    internal static class ItemPlaces
    {
        public static ItemPlaceKind KindOf(Container page) => page.Identity.Type switch
        {
            IdentityType.WeaponPage or IdentityType.ArmorPage or IdentityType.ImplantPage or IdentityType.SocialPage
                => ItemPlaceKind.Equipment,
            IdentityType.BankByRef or IdentityType.Bank => ItemPlaceKind.Bank,
            IdentityType.Container => ItemPlaceKind.Bag,
            _ => ItemPlaceKind.Inventory
        };

        public static ItemPlace Of(Container page, LootableDynel? loot = null) => loot switch
        {
            Corpse corpse => ModuleHooks.Place(ItemPlaceKind.Corpse, corpse.Identity),
            not null => ModuleHooks.Place(ItemPlaceKind.World, loot.Identity),
            _ => KindOf(page) == ItemPlaceKind.Bag
                ? ModuleHooks.Place(ItemPlaceKind.Bag, page.LinkedItem?.Identity ?? page.Identity)
                : ModuleHooks.Place(KindOf(page))
        };

        /// <summary>Raises the move unless it stays within one place kind of the player's own (a slot rearrangement).</summary>
        public static void Moved(Player player, Item item, ItemPlace from, ItemPlace to, int count = 0)
        {
            if (from.Kind == to.Kind && from.Kind is ItemPlaceKind.Inventory or ItemPlaceKind.Equipment
                    or ItemPlaceKind.Bank or ItemPlaceKind.Bag)
                return;
            ModuleHooks.ItemMoved(player, item, from, to, count);
        }
    }
}
