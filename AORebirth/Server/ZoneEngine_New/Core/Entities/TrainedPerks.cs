namespace ZoneEngine_New.Core.Entities
{
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// The perk ids a player has trained: the persistent perk state (charactersperks). Mutated on the
    /// playfield tick; the flush writer only ever sees a copied snapshot.
    /// </summary>
    public sealed class TrainedPerks
    {
        readonly object _gate = new();
        readonly HashSet<int> _perkIds = new();
        readonly HashSet<int> _granted = new();
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

                _dirty = true;
            }
        }

        public bool Add(int perkId)
        {
            lock (_gate)
            {
                if (_granted.Contains(perkId) || !_perkIds.Add(perkId))
                    return false;
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
                _dirty = true;
                return true;
            }
        }

        /// <summary>The set to persist, or null when nothing changed since the last take. Granted perks never persist.</summary>
        public int[]? TakeDirty()
        {
            lock (_gate)
            {
                if (!_dirty)
                    return null;

                _dirty = false;
                return _perkIds.Order().ToArray();
            }
        }

        /// <summary>A failed write leaves the set dirty; the next take re-reads the current perks.</summary>
        public void RestoreDirty(int[]? snapshot)
        {
            if (snapshot == null)
                return;

            lock (_gate)
                _dirty = true;
        }
    }
}
