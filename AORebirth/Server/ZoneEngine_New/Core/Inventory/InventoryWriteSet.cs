namespace ZoneEngine_New.Core.Inventory
{
    using System;
    using System.Collections.Generic;
    using LostEden.Protocol.GameData;
    using ZoneEngine_New.Core.Data;

    /// <summary>
    /// The rows one queued inventory transaction writes for one character: the plan the tick decided (places and
    /// retires, with whether each item had a row then) folded with the dirty inventory taken when the transaction runs
    /// in the lane. A dirty entry made after the plan (<see cref="PlayerInventory.DirtySequence"/>) is newer and wins
    /// over the plan's place for that item; one made before it is overridden by the plan. Kept by the transaction
    /// across retries: dirty entries taken on a failed attempt are not given back, they are folded again with whatever
    /// was taken since.
    /// </summary>
    /// <remarks>
    /// An item whose insert is queued counts as persisted from the moment it is queued (<see cref="Item.IsPersisted"/>),
    /// so a dirty entry made for it afterwards resolves to a location update; here that update re-homes the queued
    /// insert instead of updating a row that does not exist yet.
    /// </remarks>
    internal sealed class InventoryWriteSet
    {
        readonly Dictionary<int, ItemInstanceRecord> _inserts = new();
        readonly Dictionary<int, ItemLocationUpdate> _locations = new();
        readonly Dictionary<int, long> _sequences = new();
        readonly HashSet<int> _nanos = new();

        public IReadOnlyCollection<ItemInstanceRecord> Inserts => _inserts.Values;

        public IReadOnlyCollection<ItemLocationUpdate> Locations => _locations.Values;

        public int[] UploadedNanos => [.. _nanos];

        /// <summary>Folds a dirty flush taken after everything already folded: its entries win over earlier folds.</summary>
        public void Fold(PlayerInventory.InventoryDirtyFlush? newer, IReadOnlyList<int> newerNanos)
        {
            foreach (int nano in newerNanos)
                _nanos.Add(nano);
            if (newer == null)
                return;
            foreach (ItemInstanceRecord insert in newer.Inserts)
            {
                _locations.Remove(insert.InstanceId);
                _inserts[insert.InstanceId] = insert;
                _sequences[insert.InstanceId] = newer.SequenceOf(insert.InstanceId);
            }
            foreach (ItemLocationUpdate update in newer.Updates)
            {
                if (_inserts.TryGetValue(update.InstanceId, out ItemInstanceRecord? queued))
                    _inserts[update.InstanceId] = ReHome(queued, update.ContainerType, update.ContainerInstance, update.ContainerPlacement, update.StackCount);
                else
                    _locations[update.InstanceId] = update;
                _sequences[update.InstanceId] = newer.SequenceOf(update.InstanceId);
            }
        }

        /// <summary>
        /// The plan (made at <paramref name="planSequence"/>) puts <paramref name="item"/> at a place with
        /// <paramref name="count"/>. A dirty entry made for it after the plan is newer and stays; otherwise an item that
        /// had no row when the plan was made is inserted there, one that had a row is moved there.
        /// </summary>
        public void Place(Item item, bool hadRow, Identity container, int placement, int count, long planSequence)
        {
            ArgumentNullException.ThrowIfNull(item);
            int id = item.InstanceId;
            bool newerFolded = _sequences.TryGetValue(id, out long sequence) && sequence > planSequence;
            if (hadRow)
            {
                if (!newerFolded)
                {
                    _inserts.Remove(id);
                    _locations[id] = new ItemLocationUpdate(id, (int)container.Type, container.Instance, placement, count);
                }
                return;
            }

            if (newerFolded)
            {
                // Moved since the plan: its queued insert goes where the move put it (Fold re-homed a queued insert;
                // an update folded before any insert was queued becomes the insert here).
                if (_locations.Remove(id, out ItemLocationUpdate moved))
                    _inserts[id] = ReHome(item.ToRecord(container, placement, count), moved.ContainerType, moved.ContainerInstance,
                        moved.ContainerPlacement, moved.StackCount);
                return;
            }
            _locations.Remove(id);
            _inserts[id] = item.ToRecord(container, placement, count);
        }

        /// <summary>
        /// The plan retires <paramref name="item"/>: nothing folded for it is written; its row, when it had one, moves to
        /// <paramref name="graveyard"/> at <paramref name="placement"/>.
        /// </summary>
        public void Retire(Item item, bool hadRow, Identity graveyard, int placement)
        {
            ArgumentNullException.ThrowIfNull(item);
            int id = item.InstanceId;
            _inserts.Remove(id);
            _locations.Remove(id);
            if (hadRow)
                _locations[id] = new ItemLocationUpdate(id, (int)graveyard.Type, graveyard.Instance, placement);
        }

        /// <summary>The plan gives the item no row at all (a never-stored item merged away, or one another character now holds).</summary>
        public void Drop(int instanceId)
        {
            _inserts.Remove(instanceId);
            _locations.Remove(instanceId);
        }

        static ItemInstanceRecord ReHome(ItemInstanceRecord queued, int containerType, int containerInstance, int placement, int? count) => new()
        {
            InstanceId = queued.InstanceId,
            ContainerType = containerType,
            ContainerInstance = containerInstance,
            ContainerPlacement = placement,
            ItemType = queued.ItemType,
            LowId = queued.LowId,
            HighId = queued.HighId,
            Quality = queued.Quality,
            StackCount = count ?? queued.StackCount,
            Source = queued.Source
        };
    }
}
