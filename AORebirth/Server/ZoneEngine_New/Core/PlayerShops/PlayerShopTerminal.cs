namespace ZoneEngine_New.Core.PlayerShops
{
    using System;

    using LostEden.Protocol.GameData;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;

    /// <summary>
    /// A "Shop for Rent" terminal: a Dynels.dat static of identity type 0xC790 (the market playfields' player shop
    /// booths). Using it opens the shop (<see cref="PlayerShopService.Open"/>) instead of running template spells.
    /// </summary>
    public sealed class PlayerShopTerminal : StaticDynel
    {
        /// <summary>The Dynels.dat identity type of a player shop terminal.</summary>
        public const IdentityType TerminalType = (IdentityType)0xC790;

        public PlayerShopTerminal(Identity identity, ItemTemplate template)
            : base(identity, template)
        {
        }

        public static bool IsPlayerShopTerminalType(IdentityType type) => type == TerminalType;

        /// <summary>Opening the shop window is immediate.</summary>
        protected override bool DelaysUse => false;

        protected override bool OnUse(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);
            if (Playfield?.GetService<PlayerShopService>() is not PlayerShopService shops)
                return false;
            shops.Open(player, this);
            return true;
        }
    }
}
