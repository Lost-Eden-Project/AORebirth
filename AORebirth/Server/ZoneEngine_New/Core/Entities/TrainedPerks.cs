namespace ZoneEngine_New.Core.Entities
{
    using System.Collections.Generic;
    using System.Linq;

    using ZoneEngine_New.Core.Data;

    /// <summary>
    /// The perk ids a player has trained: the persistent perk state (charactersperks). Mutated on the
    /// playfield tick; the flush writer only ever sees a copied snapshot.
    /// </summary>
    public sealed class TrainedPerks
    {
        readonly object _gate = new();
        readonly HashSet<int> _perkIds = new();
        readonly HashSet<int> _granted = new();
        // What the next save takes out of storage: only perks this session removed on purpose (untrain, reset).
        readonly HashSet<int> _removed = new();
        // A GM replace: the next save makes storage exactly the set.
        bool _replaceAll;
        bool _dirty;

        public bool IsDirty
        {
            get
            {
                lock (_gate)
                    return _dirty;
            }
        }

        public bool Contains(int perkId)
        {
            lock (_gate)
                return _perkIds.Contains(perkId) || _granted.Contains(perkId);
        }

        /// <summary>Sorted copy of the trained perk ids, granted ones included.</summary>
        public int[] Snapshot()
        {
            lock (_gate)
                return _perkIds.Union(_granted).Order().ToArray();
        }

        /// <summary>
        /// HACK (<see cref="GameData.PerkCatalog.GrantedToAll"/>): perks every player owns at runtime because we do not
        /// support Global Research. They count as trained everywhere (HasPerk, the client's perk map, perk
        /// templates), but they are never saved, can never be untrained, and survive <see cref="Restore"/> and
        /// <see cref="Replace"/>.
        /// </summary>
        public void Grant(IEnumerable<int> perkIds)
        {
            lock (_gate)
            {
                _granted.Clear();
                foreach (int perkId in perkIds)
                {
                    if (perkId > 0)
                        _granted.Add(perkId);
                }
            }
        }

        /// <summary>Hydration: replaces the set with the stored perks without marking it dirty.</summary>
        public void Restore(IEnumerable<int> perkIds)
        {
            lock (_gate)
            {
                _perkIds.Clear();
                foreach (int perkId in perkIds)
                {
                    if (perkId > 0)
                        _perkIds.Add(perkId);
                }
            }
        }

        /// <summary>GM setup: replaces the whole set, research included, and marks it dirty.</summary>
        public void Replace(IEnumerable<int> perkIds)
        {
            lock (_gate)
            {
                _perkIds.Clear();
                foreach (int perkId in perkIds)
                {
                    if (perkId > 0)
                        _perkIds.Add(perkId);
                }

                _removed.Clear();
                _replaceAll = true;
                _dirty = true;
            }
        }

        public bool Add(int perkId)
        {
            lock (_gate)
            {
                if (_granted.Contains(perkId) || !_perkIds.Add(perkId))
                    return false;
                _removed.Remove(perkId);
                _dirty = true;
                return true;
            }
        }

        public bool Remove(int perkId)
        {
            lock (_gate)
            {
                if (!_perkIds.Remove(perkId))
                    return false;
                _removed.Add(perkId);
                _dirty = true;
                return true;
            }
        }

        /// <summary>
        /// What to persist, or null when nothing changed since the last take: the trained perks to have in storage and
        /// the perks to take out of it. Storage only ever loses a perk this session removed on purpose, so a set that
        /// was loaded short can never erase what the character owns. Granted perks never persist.
        /// </summary>
        public TrainedPerkChanges? TakeDirty()
        {
            lock (_gate)
            {
                if (!_dirty)
                    return null;

                var changes = new TrainedPerkChanges(_perkIds.Order().ToArray(), _removed.Order().ToArray(), _replaceAll);
                _dirty = false;
                _replaceAll = false;
                _removed.Clear();
                return changes;
            }
        }

        /// <summary>A failed write leaves the set dirty and its removals pending; the next take re-reads the current perks.</summary>
        public void RestoreDirty(TrainedPerkChanges? changes)
        {
            if (changes == null)
                return;

            lock (_gate)
            {
                _dirty = true;
                _replaceAll |= changes.ReplaceAll;
                foreach (int perkId in changes.Removed)
                {
                    // Trained again since the failed write: it stays.
                    if (!_perkIds.Contains(perkId))
                        _removed.Add(perkId);
                }
            }
        }
    }
}
