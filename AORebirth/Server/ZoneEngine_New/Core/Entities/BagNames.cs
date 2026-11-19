namespace ZoneEngine_New.Core.Entities
{
    using System.Collections.Generic;
    using System.Linq;

    using ZoneEngine_New.Core.Data;

    /// <summary>
    /// The names a player gave their own bags (characterbagnames), keyed by the bag's item instance. Mutated on the
    /// playfield tick; the flush writer only ever sees a copied snapshot. A bag that leaves the player (traded away,
    /// dropped, deleted) loses its name.
    /// </summary>
    public sealed class BagNames
    {
        /// <summary>Longest name kept (the column holds 32).</summary>
        public const int MaxNameLength = 31;

        /// <summary>Most named bags one character keeps.</summary>
        public const int MaxNames = 200;

        readonly object _gate = new();
        readonly Dictionary<int, string> _names = new();
        bool _dirty;

        public bool IsDirty
        {
            get
            {
                lock (_gate)
                    return _dirty;
            }
        }

        /// <summary>1 to <see cref="MaxNameLength"/> printable ASCII characters, not only spaces.</summary>
        public static bool IsShowable(string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
                return false;
            foreach (char c in name)
            {
                if (c is < ' ' or > '~')
                    return false;
            }
            return true;
        }

        /// <summary>Every named bag, for the client after FullCharacter.</summary>
        public KeyValuePair<int, string>[] Snapshot()
        {
            lock (_gate)
                return _names.ToArray();
        }

        public bool TryGet(int bagInstanceId, out string name)
        {
            lock (_gate)
                return _names.TryGetValue(bagInstanceId, out name!);
        }

        /// <summary>Hydration: replaces the names with the stored rows without marking them dirty.</summary>
        public void Restore(IEnumerable<BagNameRecord> rows)
        {
            lock (_gate)
            {
                _names.Clear();
                foreach (BagNameRecord row in rows)
                {
                    if (row.BagInstanceId > 0 && IsShowable(row.Name))
                        _names[row.BagInstanceId] = row.Name;
                }
            }
        }

        /// <summary>Names the bag; false when the player already has <see cref="MaxNames"/> other named bags.</summary>
        public bool Set(int bagInstanceId, string name)
        {
            lock (_gate)
            {
                if (_names.TryGetValue(bagInstanceId, out string? previous))
                {
                    if (previous == name)
                        return true;
                }
                else if (_names.Count >= MaxNames)
                    return false;

                _names[bagInstanceId] = name;
                _dirty = true;
                return true;
            }
        }

        /// <summary>
        /// Forgets every name whose bag the player no longer holds (<paramref name="held"/>: every bag they hold), for a
        /// bag that left by a path that does not clear its name. Returns how many went.
        /// </summary>
        public int Prune(ISet<int> held)
        {
            lock (_gate)
            {
                int[] gone = _names.Keys.Where(id => !held.Contains(id)).ToArray();
                foreach (int id in gone)
                    _names.Remove(id);
                if (gone.Length > 0)
                    _dirty = true;
                return gone.Length;
            }
        }

        public int Count
        {
            get
            {
                lock (_gate)
                    return _names.Count;
            }
        }

        /// <summary>Forgets the bag's name; true when it had one.</summary>
        public bool Remove(int bagInstanceId)
        {
            lock (_gate)
            {
                if (!_names.Remove(bagInstanceId))
                    return false;
                _dirty = true;
                return true;
            }
        }

        /// <summary>The rows to persist, or null when nothing changed since the last take.</summary>
        public BagNameRecord[]? TakeDirty()
        {
            lock (_gate)
            {
                if (!_dirty)
                    return null;

                _dirty = false;
                return _names.OrderBy(pair => pair.Key).Select(pair => new BagNameRecord(pair.Key, pair.Value)).ToArray();
            }
        }

        /// <summary>A failed write leaves the names dirty; the next take re-reads the current rows.</summary>
        public void RestoreDirty(BagNameRecord[]? snapshot)
        {
            if (snapshot == null)
                return;

            lock (_gate)
                _dirty = true;
        }
    }
}
