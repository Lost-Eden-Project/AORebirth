namespace ZoneEngine_New.Core.Playfield
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    using AORebirth.Enums;

    using LostEden.Protocol.GameData;

    using ZoneEngine_New.Core.Configuration;
    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Logging;
    using ZoneEngine_New.Core.Nanos;

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

        /// <summary>
        /// A player arriving on a playfield (by transfer, or at login with restored NCU) leaves behind every other
        /// playfield's item effect: the nanos another mapped item casts, and those they cast in turn, end without their
        /// OnTerminate (Adonis Abyss 239736: 231227 runs until removed and its fumes 274322 / watch 274323 recast each
        /// other, so they would otherwise follow the player out). This playfield's own item effects stay. Returns how
        /// many NCU entries went.
        /// </summary>
        public static int EndOtherPlayfieldsEffects(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);
            Playfield? playfield = player.Playfield;
            if (playfield == null || player.Buffs.Count == 0)
                return 0;

            Dictionary<int, int>? map = playfield.GetService<GameplaySettings>()?.PlayfieldItems;
            if (map == null || map.Count == 0
                || playfield.GetService<IItemTemplateCatalog>() is not IItemTemplateCatalog catalog)
                return 0;

            int ownItem = TryGetItemId(playfield, out int id) ? id : 0;
            HashSet<int>? own = null;
            HashSet<int>? foreign = null;
            foreach (int itemId in map.Values)
            {
                if (itemId == ownItem)
                    continue;
                CollectEffectNanos(itemId, catalog, foreign ??= new HashSet<int>());
            }

            if (foreign == null || foreign.Count == 0)
                return 0;

            if (ownItem != 0)
            {
                CollectEffectNanos(ownItem, catalog, own = new HashSet<int>());
                foreign.ExceptWith(own);
            }

            return NanoRuntime.EndWithoutTerminate(player, foreign);
        }

        /// <summary>
        /// The nanos <paramref name="itemId"/> sets going: what its OnUse casts and, through every event of each, what
        /// those cast, followed to the end of the chain (the item itself is not one).
        /// </summary>
        static void CollectEffectNanos(int itemId, IItemTemplateCatalog catalog, HashSet<int> nanos)
        {
            if (!catalog.TryGet(itemId, out ItemTemplate item)
                || !item.SpellList.TryGetValue(EventType.OnUse, out List<ItemSpell>? onUse))
                return;

            var pending = new Stack<int>();
            PushCasts(onUse, pending);
            while (pending.Count > 0)
            {
                int nanoId = pending.Pop();
                if (nanoId == itemId || !nanos.Add(nanoId) || !catalog.TryGet(nanoId, out ItemTemplate nano))
                    continue;
                foreach (List<ItemSpell> spells in nano.SpellList.Values)
                    PushCasts(spells, pending);
            }
        }

        static void PushCasts(List<ItemSpell> spells, Stack<int> pending)
        {
            foreach (ItemSpell spell in spells)
            {
                if ((FunctionType)spell.FunctionType is FunctionType.CastNano or FunctionType.CastNanoIfPossible
                        or FunctionType.NpcCastNanoIfPossible or FunctionType.TeamCastNano or FunctionType.AreaCastNano
                    && spell.TryReadInt(0, out int nanoId) && nanoId > 0)
                    pending.Push(nanoId);
            }
        }
    }
}
