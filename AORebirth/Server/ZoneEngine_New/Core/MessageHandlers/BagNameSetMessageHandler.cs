namespace ZoneEngine_New.Core.MessageHandlers
{
    using System;
    using System.Globalization;

    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Network;

    /// <summary>
    /// A player names one of their own bags (<see cref="BagNameSetMessage"/>): a bag in their inventory, overflow or
    /// (when loaded) bank. The name is kept server side (characterbagnames) and answered with a
    /// <see cref="BagNameMessage"/> holding what was kept: the name, or empty when it was cleared or refused.
    /// </summary>
    public sealed class BagNameSetMessageHandler(InventoryFlushService flush) : IMessageHandler<BagNameSetMessage>
    {
        public Type MessageBodyType => typeof(BagNameSetMessage);

        public void Handle(MessageBody body, IZoneSession session) => Handle((BagNameSetMessage)body, session);

        public void Handle(BagNameSetMessage message, IZoneSession session)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(session);
            if (session.State != SessionState.InPlay || session.Player is not Player player || !ReferenceEquals(player.Session, session)
                || message.Identity != player.Identity || player.IsPersistenceQuarantined || !player.Inventory.IsHydrated)
                return;

            if (message.Bag.Type != IdentityType.Container || !TryFindOwnBag(player, message.Bag, out Item bag))
            {
                player.Logger.Warn(string.Format(CultureInfo.InvariantCulture,
                    "Bag name refused char={0} bag={1}: not one of the player's bags", player.Identity.Instance, message.Bag));
                Reply(player, message.Bag, string.Empty);
                return;
            }

            string name = message.Name?.Trim() ?? string.Empty;
            bool changed;
            if (name.Length == 0)
                changed = player.BagNames.Remove(bag.InstanceId);
            else if (!BagNames.IsShowable(name))
            {
                RequirementFeedback.SendText(player,
                    "A bag name is 1 to " + BagNames.MaxNameLength + " letters, digits, spaces or punctuation.");
                Reply(player, message.Bag, player.BagNames.TryGet(bag.InstanceId, out string kept) ? kept : string.Empty);
                return;
            }
            else if (!TrySet(player, bag.InstanceId, name))
            {
                RequirementFeedback.SendText(player, "You cannot name more than " + BagNames.MaxNames + " bags.");
                Reply(player, message.Bag, string.Empty);
                return;
            }
            else
                changed = true;

            if (changed)
                flush.NotifyDirty(player);
            Reply(player, message.Bag, name);
        }

        /// <summary>
        /// Names the bag. At the cap, names of bags the player no longer holds are dropped first; that needs the bank
        /// loaded, or a banked bag's name would be taken for a stale one.
        /// </summary>
        static bool TrySet(Player player, int bagInstanceId, string name)
        {
            if (player.BagNames.Set(bagInstanceId, name))
                return true;
            if (player.Inventory.Bank is not { } bank || !bank.IsHydrated)
                return false;
            var held = new System.Collections.Generic.HashSet<int>();
            foreach (Item item in player.Inventory.EnumerateHeldItems())
                if (InventoryMoveService.IsBagItem(item))
                    held.Add(item.InstanceId);
            foreach (Item item in bank.Content.Values)
                if (InventoryMoveService.IsBagItem(item))
                    held.Add(item.InstanceId);
            return player.BagNames.Prune(held) > 0 && player.BagNames.Set(bagInstanceId, name);
        }

        /// <summary>A bag the player holds: on a carried page, in overflow, or in the bank once it has been loaded.</summary>
        static bool TryFindOwnBag(Player player, Identity bagIdentity, out Item bag)
        {
            foreach (Item item in player.Inventory.EnumerateHeldItems())
            {
                if (item.Identity == bagIdentity && InventoryMoveService.IsBagItem(item))
                {
                    bag = item;
                    return true;
                }
            }

            if (player.Inventory.Bank is { } bank)
            {
                foreach (Item item in bank.Content.Values)
                {
                    if (item.Identity == bagIdentity && InventoryMoveService.IsBagItem(item))
                    {
                        bag = item;
                        return true;
                    }
                }
            }

            bag = null!;
            return false;
        }

        static void Reply(Player player, Identity bag, string name)
            => player.Session?.Send(new BagNameMessage { Identity = player.Identity, Bag = bag, Name = name });
    }
}
