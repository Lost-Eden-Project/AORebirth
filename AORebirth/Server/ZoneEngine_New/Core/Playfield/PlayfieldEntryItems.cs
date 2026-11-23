namespace ZoneEngine_New.Core.Playfield
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    using ZoneEngine_New.Core.Configuration;
    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Logging;

    /// <summary>
    /// A playfield's "Playfield Item" (Gameplay.json <see cref="GameplaySettings.PlayfieldItems"/>): its OnUse runs on each
    /// player entering the playfield, with the player as user and target, as the RDB item's description says ("Run the
    /// nano on people entering Adonis Abyss"). The item's own criteria pick who it acts on (GMs get a message instead of
    /// the nano, ICC Shuttleport only acts above level 19). Run once the client is in play after a login, a zone change
    /// or a transfer, so what the item says and casts reaches the client; a reconnect without zoning is not an entry.
    /// </summary>
    public static class PlayfieldEntryItems
    {
        /// <summary>
        /// The item mapped to <paramref name="playfield"/> by its id, else by the resource a proxy instance copies.
        /// </summary>
        public static bool TryGetItemId(Playfield playfield, out int itemId)
        {
            ArgumentNullException.ThrowIfNull(playfield);
            itemId = 0;
            Dictionary<int, int>? map = playfield.GetService<GameplaySettings>()?.PlayfieldItems;
            if (map == null || map.Count == 0)
                return false;
            return map.TryGetValue(playfield.Identity.Instance, out itemId)
                || map.TryGetValue(playfield.ResourceId, out itemId);
        }

        /// <summary>
        /// Runs the playfield item of the player's playfield on the player. False when the playfield has none, the player
        /// is dead or not on a playfield, or nothing in the item ran.
        /// </summary>
        public static bool Run(Player player, IZoneLogger logger)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(logger);
            Playfield? playfield = player.Playfield;
            if (playfield == null || player.IsDead || !TryGetItemId(playfield, out int itemId))
                return false;

            if (playfield.GetService<IItemTemplateCatalog>() is not IItemTemplateCatalog catalog
                || !catalog.TryGet(itemId, out ItemTemplate template))
            {
                logger.Warn(string.Format(CultureInfo.InvariantCulture,
                    "Playfield item {0} for playfield {1} is not in the item catalog; nothing runs on entry",
                    itemId, playfield.Identity.Instance));
                return false;
            }

            if (playfield.GetService<IInventoryRepository>() is not IInventoryRepository inventory
                || playfield.GetService<IItemBuilder>() is not IItemBuilder items)
                return false;

            bool ran;
            try
            {
                ran = template.ExecuteOnUseSpells(player, inventory, items, source: player);
            }
            catch (Exception exception)
            {
                // An entry effect never costs the player the zone-in.
                logger.Error(exception, string.Format(CultureInfo.InvariantCulture,
                    "Playfield item {0} failed on entry character={1} playfield={2}",
                    itemId, player.Identity.Instance, playfield.Identity.Instance));
                return false;
            }

            player.FlushDirtyStats();
            return ran;
        }
    }
}
