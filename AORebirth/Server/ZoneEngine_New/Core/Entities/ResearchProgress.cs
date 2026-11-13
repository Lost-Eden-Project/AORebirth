namespace ZoneEngine_New.Core.Entities
{
    using System.Collections.Generic;
    using System.Linq;

    using ZoneEngine_New.Core.Data;

    /// <summary>
    /// A player's personal research tiers in progress: research id to the research XP still needed (characterresearch).
    /// A finished tier leaves here and becomes a trained perk. Mutated on the playfield tick; the flush writer only ever
    /// sees a copied snapshot.
    /// </summary>
    public sealed class ResearchProgress
    {
        readonly object _gate = new();
        readonly Dictionary<int, int> _remaining = new();
        // Rows the next save takes out of storage: only tiers this session finished or dropped on purpose.
        readonly HashSet<int> _removed = new();
        // A GM clear: the next save makes storage exactly the rows.
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

        /// <summary>The XP <paramref name="researchId"/> still needs, when it has been started.</summary>
        public bool TryGetRemaining(int researchId, out int remaining)
        {
            lock (_gate)
                return _remaining.TryGetValue(researchId, out remaining);
        }

        /// <summary>Copy of every tier in progress.</summary>
        public KeyValuePair<int, int>[] Snapshot()
        {
            lock (_gate)
                return _remaining.OrderBy(pair => pair.Key).ToArray();
        }

        /// <summary>Hydration: replaces the progress with the stored rows without marking it dirty.</summary>
        public void Restore(IEnumerable<ResearchProgressRecord> rows)
        {
            lock (_gate)
            {
                _remaining.Clear();
                foreach (ResearchProgressRecord row in rows)
                {
                    if (row.ResearchId > 0 && row.RemainingXp > 0)
                        _remaining[row.ResearchId] = row.RemainingXp;
                }
            }
        }

        /// <summary>Sets the XP <paramref name="researchId"/> still needs; zero or less removes it (finished).</summary>
        public void Set(int researchId, int remaining)
        {
            lock (_gate)
            {
                bool changed = remaining > 0
                    ? !_remaining.TryGetValue(researchId, out int previous) || previous != remaining
                    : _remaining.ContainsKey(researchId);
                if (!changed)
                    return;

                if (remaining > 0)
                {
                    _remaining[researchId] = remaining;
                    _removed.Remove(researchId);
                }
                else
                {
                    _remaining.Remove(researchId);
                    _removed.Add(researchId);
                }

                _dirty = true;
            }
        }

        /// <summary>GM setup: forgets every tier in progress.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                if (_remaining.Count == 0)
                    return;

                _remaining.Clear();
                _removed.Clear();
                _replaceAll = true;
                _dirty = true;
            }
        }

        /// <summary>Drops progress rows for tiers that are already trained (a crash between the two writes).</summary>
        public void DropFinished(TrainedPerks trained)
        {
            lock (_gate)
            {
                foreach (int researchId in _remaining.Keys.Where(trained.Contains).ToArray())
                {
                    _remaining.Remove(researchId);
                    _removed.Add(researchId);
                    _dirty = true;
                }
            }
        }

        /// <summary>
        /// What to persist, or null when nothing changed since the last take: the rows to have in storage and the tiers
        /// to take out of it. Storage only ever loses a row this session finished or dropped, so progress that was
        /// loaded short can never erase what the character has stored.
        /// </summary>
        public ResearchChanges? TakeDirty()
        {
            lock (_gate)
            {
                if (!_dirty)
                    return null;

                var changes = new ResearchChanges(
                    _remaining.OrderBy(pair => pair.Key).Select(pair => new ResearchProgressRecord(pair.Key, pair.Value)).ToArray(),
                    _removed.Order().ToArray(),
                    _replaceAll);
                _dirty = false;
                _replaceAll = false;
                _removed.Clear();
                return changes;
            }
        }

        /// <summary>A failed write leaves the progress dirty and its removals pending; the next take re-reads the current rows.</summary>
        public void RestoreDirty(ResearchChanges? changes)
        {
            if (changes == null)
                return;

            lock (_gate)
            {
                _dirty = true;
                _replaceAll |= changes.ReplaceAll;
                foreach (int researchId in changes.Removed)
                {
                    // In progress again since the failed write: it stays.
                    if (!_remaining.ContainsKey(researchId))
                        _removed.Add(researchId);
                }
            }
        }
    }
}
