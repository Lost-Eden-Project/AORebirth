namespace ZoneEngine_New.Core.Entities
{
    using System.Collections.Generic;
    using System.Linq;

    using ZoneEngine_New.Core.Data;

    /// <summary>
    /// A player's /pet rename names, one per pet type (characterpetnames): a newly summoned pet of that type takes its
    /// name again. Mutated on the playfield tick; the flush writer only ever sees a copied snapshot.
    /// </summary>
    public sealed class PetNames
    {
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

        /// <summary>The name pets of <paramref name="petType"/> take, when one was given.</summary>
        public bool TryGet(int petType, out string name)
        {
            lock (_gate)
                return _names.TryGetValue(petType, out name!);
        }

        /// <summary>Hydration: replaces the names with the stored rows without marking them dirty.</summary>
        public void Restore(IEnumerable<PetNameRecord> rows)
        {
            lock (_gate)
            {
                _names.Clear();
                foreach (PetNameRecord row in rows)
                {
                    if (!string.IsNullOrEmpty(row.Name))
                        _names[row.PetType] = row.Name;
                }
            }
        }

        public void Set(int petType, string name)
        {
            lock (_gate)
            {
                if (_names.TryGetValue(petType, out string? previous) && previous == name)
                    return;

                _names[petType] = name;
                _dirty = true;
            }
        }

        /// <summary>The rows to persist, or null when nothing changed since the last take.</summary>
        public PetNameRecord[]? TakeDirty()
        {
            lock (_gate)
            {
                if (!_dirty)
                    return null;

                _dirty = false;
                return _names.OrderBy(pair => pair.Key).Select(pair => new PetNameRecord(pair.Key, pair.Value)).ToArray();
            }
        }

        /// <summary>A failed write leaves the names dirty; the next take re-reads the current rows.</summary>
        public void RestoreDirty(PetNameRecord[]? snapshot)
        {
            if (snapshot == null)
                return;

            lock (_gate)
                _dirty = true;
        }
    }
}
