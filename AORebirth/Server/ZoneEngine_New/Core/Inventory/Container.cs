namespace ZoneEngine_New.Core.Inventory
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages.N3Messages;

    [Flags]
    public enum ContainerFlags
    {
        None = 0,
        CanAdd = 1 << 0,
        CanRemove = 1 << 1,
        Backpack = 1 << 2,
        Inventory = 1 << 3,
        Bank = 1 << 4,
        Contract = 1 << 5,
    }

    /// <summary>
    /// Item container with slot-indexed content (inventory page, backpack, bank, etc.).
    /// </summary>
    public class Container
    {
        public Container(IdentityType type, int offset, int capacity, int instanceId = 0)
        {
            Identity = new Identity { Type = type, Instance = instanceId };
            Offset = offset;
            Capacity = capacity;
        }

        public Identity Identity { get; }

        public ContainerFlags Flags { get; set; }

        public Dictionary<int, Item> Content { get; } = new();

        public int Offset { get; }

        public int Capacity { get; }

        public bool IsOpen { get; set; }

        public bool IsHydrated { get; set; } = true;

        public int InventoryHandle { get; set; }

        public Item? LinkedItem { get; set; }

        public Identity ParentSlot { get; set; } = Identity.None;

        public bool Add(int slot, Item item)
        {
            if (Content.ContainsKey(slot))
                return false;

            if (slot < Offset || slot >= Offset + Capacity)
                return false;

            Content.Add(slot, item);
            return true;
        }

        public Item? Remove(int slot)
        {
            if (!Content.TryGetValue(slot, out Item? item))
                return null;

            if (item.Locked)
                return null;

            Content.Remove(slot);
            return item;
        }

        /// <summary>
        /// Occupied slots in ascending slot order. The slot number is the client placement, which
        /// callers such as appearance rebuilds need to resolve a mesh position.
        /// </summary>
        public IEnumerable<KeyValuePair<int, Item>> EnumerateSlots()
        {
            int last = Offset + Capacity;
            // A large, mostly empty container (an NPC's 999-slot equipment) walks only its occupied slots, in slot order.
            if (Content.Count * 8 < Capacity)
            {
                int[] slots = new int[Content.Count];
                Content.Keys.CopyTo(slots, 0);
                Array.Sort(slots);
                foreach (int slot in slots)
                {
                    if (slot >= Offset && slot < last && Content.TryGetValue(slot, out Item? held) && held != null)
                        yield return new KeyValuePair<int, Item>(slot, held);
                }

                yield break;
            }

            for (int slot = Offset; slot < last; slot++)
            {
                if (Content.TryGetValue(slot, out Item? item) && item != null)
                    yield return new KeyValuePair<int, Item>(slot, item);
            }
        }

        public int FindFreeSlot()
        {
            for (int slot = Offset; slot < Offset + Capacity; slot++)
            {
                if (!Content.ContainsKey(slot))
                    return slot;
            }

            return -1;
        }

        public ChestFullUpdateMessage BuildChestItemFullUpdate(
            Identity owner,
            int playfieldId,
            Identity sourceInventorySlot)
        {
            Item bag = LinkedItem
                ?? throw new InvalidOperationException("Container has no LinkedItem for ChestItemFullUpdate.");

            return new ChestFullUpdateMessage
            {
                Identity = Identity,
                Unknown = 0,
                Unknown1 = 0x0b,
                OwnerType = (int)owner.Type,
                OwnerInstance = owner.Instance,
                PlayfieldId = playfieldId,
                StateMachine = new Identity { Type = (IdentityType)1000015, Instance = 0 },
                Unknown5 = (short)(0x0100 | (sourceInventorySlot.Instance & 0xff)),
                Stats =
                [
                    StatTuple(CharacterStat.Flags, bag.Flags),
                    StatTuple(CharacterStat.StaticInstance, bag.HighId),
                    StatTuple(CharacterStat.ACGItemLevel, bag.Quality),
                    StatTuple(CharacterStat.ACGItemTemplateID, bag.LowId),
                    StatTuple(CharacterStat.ACGItemTemplateID2, bag.HighId),
                    StatTuple(CharacterStat.MultipleCount, Math.Max(1, bag.StackCount))
                ],
                UnknownData = [],
                Unknown7 = 2,
                Unknown8 = 50,
                UnknownArray = [],
                Unknown9 = 3
            };
        }

        /// <summary>
        /// Builds an open/update packet for this container's current contents.
        /// </summary>
        public InventoryUpdateMessage BuildInventoryUpdateMessage(
            Identity recipient,
            Identity bagIdentity,
            int slotNumberInMainInventory,
            int unknown1 = 2,
            int unknown2 = 1)
        {
            var entries = new InventorySlot[Content.Count];
            int index = 0;
            foreach (KeyValuePair<int, Item> slot in Content.OrderBy(static pair => pair.Key))
            {
                Item item = slot.Value;
                short count = (short)Math.Clamp(Math.Max(1, item.StackCount), 1, short.MaxValue);

                entries[index++] = new InventorySlot
                {
                    Placement = slot.Key,
                    Flags = item.ToInventoryPacketFlags(),
                    Count = count,
                    Identity = item.Identity,
                    ItemLowId = item.LowId,
                    ItemHighId = item.HighId,
                    Quality = item.Quality,
                    Unknown = 0
                };
            }

            return new InventoryUpdateMessage
            {
                Identity = recipient,
                Unknown = 1,
                Unknown1 = Capacity,
                Unknown2 = unknown1,
                Items = entries,
                InventoryIdentity = bagIdentity,
                Handle = slotNumberInMainInventory,
                Unknown3 = unknown2
            };
        }

        static GameTuple<Stat, int> StatTuple(CharacterStat stat, int value) => new((Stat)stat, value);
    }
}
