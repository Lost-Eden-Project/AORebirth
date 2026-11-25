namespace ZoneEngine_New.Core.Entities
{
    using System;

    using LostEden.Protocol.GameData;

    using ZoneEngine_New.Core.Inventory;

    /// <summary>
    /// A city controller (CityController 0xC418, item 249767) placed by Dynels.dat with the identity the client's RDB 1000039
    /// record names: <c>0xC0000000 | (index &lt;&lt; 16) | playfield</c>. The client resolves the controller dynel by that
    /// identity (Gamecode 101224a0, without a null check), so it keeps it. Using it opens the district's window
    /// (<see cref="Cities.CityService.OpenWindow"/>).
    /// </summary>
    public sealed class CityController : StaticDynel
    {
        public CityController(Identity identity, ItemTemplate template)
            : base(identity, template)
        {
        }

        public static bool IsCityControllerType(IdentityType type) => type == IdentityType.CityController;

        /// <summary>The window opens at once.</summary>
        protected override bool DelaysUse => false;

        protected override bool OnUse(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);
            return Playfield?.GetService<Cities.CityService>() is { } cities && cities.OpenWindow(player, this);
        }
    }
}
