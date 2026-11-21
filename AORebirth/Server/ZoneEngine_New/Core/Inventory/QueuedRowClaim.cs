namespace ZoneEngine_New.Core.Inventory
{
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// The items whose rows one queued lane transaction decides, claimed on the tick in the same section that plans the
    /// rows and queues the transaction (see <see cref="PlayerInventory.NoteRowQueued"/>). While the claim stands, no lane
    /// work queued ahead of the transaction takes those items' dirty entries. The transaction releases it once, under the
    /// persistence gate, right before it takes the dirty inventory itself (or, for a commit that only inserts, once the
    /// insert is stored). A release that runs again on a retry does nothing.
    /// </summary>
    internal sealed class QueuedRowClaim
    {
        readonly PlayerInventory[] _inventories;
        readonly int[] _instanceIds;
        bool _released;

        public QueuedRowClaim(IEnumerable<PlayerInventory> inventories, IEnumerable<int> instanceIds)
        {
            _inventories = inventories.ToArray();
            _instanceIds = instanceIds.Where(id => id > 0).Distinct().ToArray();
            foreach (PlayerInventory inventory in _inventories)
                foreach (int id in _instanceIds)
                    inventory.NoteRowQueued(id);
        }

        public void Release()
        {
            if (_released)
                return;
            _released = true;
            foreach (PlayerInventory inventory in _inventories)
                foreach (int id in _instanceIds)
                    inventory.NoteRowCommitted(id);
        }
    }
}
