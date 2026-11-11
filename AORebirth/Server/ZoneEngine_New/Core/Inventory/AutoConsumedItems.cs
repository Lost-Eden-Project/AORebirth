namespace ZoneEngine_New.Core.Inventory
{
    using System;

    using AORebirth.Enums;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;

    /// <summary>
    /// Items used on whoever gains them instead of being held: live's virtual items (Daily Mission Mini-XP Reward 286989)
    /// carry the item flag <see cref="ItemFlags.Null"/> together with Can <see cref="CanFlags.Consume"/>. Their OnUse runs
    /// on the gaining player once per count.
    /// </summary>
    public static class AutoConsumedItems
    {
        static readonly NLog.Logger Log = NLog.LogManager.GetLogger("AutoConsumedItems");

        /// <summary>Flag <see cref="ItemFlags.Null"/> and Can <see cref="CanFlags.Consume"/>.</summary>
        public static bool IsAutoConsumed(ItemTemplate? template)
            => template != null
                && (template.Flags & (int)ItemFlags.Null) != 0
                && template.Stats.TryGetValue(CharacterStat.Can, out int can)
                && ((uint)can & (uint)CanFlags.Consume) != 0;

        public static bool IsAutoConsumed(Item? item) => IsAutoConsumed(item?.Definition);

        /// <summary>
        /// Uses an auto-consumed <paramref name="item"/> the player is about to gain, before it is placed anywhere: true
        /// when it was used (the caller then places nothing), false for any other item or when the player has no
        /// playfield to use it in (the caller then gives it as usual).
        /// </summary>
        public static bool TryConsume(Player player, Item item)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(item);
            if (!IsAutoConsumed(item))
                return false;

            Playfield.Playfield? playfield = player.Playfield;
            if (playfield?.GetService<IInventoryRepository>() is not IInventoryRepository inventory
                || playfield.GetService<IItemBuilder>() is not IItemBuilder items)
                return false;

            int uses = Math.Max(1, item.StackCount);
            for (int use = 0; use < uses; use++)
                item.Definition.ExecuteOnUseSpells(player, inventory, items, source: player);
            player.FlushDirtyStats();
            Log.Info("Auto-consumed char=" + player.Identity.Instance + " item=" + item.Name + " " + item.LowId + "/"
                + item.HighId + " ql=" + item.Quality + " x" + uses);
            return true;
        }

        /// <summary>
        /// An auto-consumed item that already landed in the main inventory (taken from loot, which the client places
        /// itself): its row is retired, the client is told to delete it from <paramref name="slot"/>, and it is used.
        /// </summary>
        public static bool ConsumeLanded(Player player, int slot, Item item, InventoryActionService actions)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(actions);
            if (!IsAutoConsumed(item))
                return false;

            var slotIdentity = new Identity { Type = IdentityType.Inventory, Instance = slot };
            if (!actions.TryDelete(player, slotIdentity, item.InstanceId))
            {
                Log.Warn("Auto-consume could not retire char=" + player.Identity.Instance + " item=" + item.LowId
                    + " slot=" + slot + "; left in inventory");
                return false;
            }

            player.Session?.Send(new CharacterActionMessage
            {
                Identity = player.Identity, Action = CharacterActionType.DeleteItem, Target = slotIdentity
            });
            return TryConsume(player, item);
        }
    }
}
