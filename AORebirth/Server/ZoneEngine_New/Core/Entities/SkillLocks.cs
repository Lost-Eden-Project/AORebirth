namespace ZoneEngine_New.Core.Entities
{
    using System;
    using System.Collections.Generic;

    using ZoneEngine_New.Core.Data;

    /// <summary>
    /// LockSkill cooldowns keyed by stat id, each with an absolute UTC expiry and the length it was locked for. Mutated
    /// on the playfield tick; the flush writer only ever sees a copied snapshot. Only the expiry is stored, so a lock
    /// restored at login counts its remaining time as its length.
    /// </summary>
    public sealed class SkillLocks
    {
        readonly object _gate = new();
        readonly Dictionary<int, DateTime> _expiresAtUtc = new();
        readonly Dictionary<int, int> _durationSeconds = new();
        bool _dirty;

        public bool IsDirty
        {
            get
            {
                lock (_gate)
                    return _dirty;
            }
        }

        /// <summary>Locks <paramref name="statId"/> for <paramref name="durationSeconds"/>. Zero or less clears it.</summary>
        public void Lock(int statId, int durationSeconds, DateTime nowUtc)
        {
            lock (_gate)
            {
                if (durationSeconds <= 0)
                {
                    _expiresAtUtc.Remove(statId);
                    _durationSeconds.Remove(statId);
                }
                else
                {
                    _expiresAtUtc[statId] = nowUtc.AddSeconds(durationSeconds);
                    _durationSeconds[statId] = durationSeconds;
                }

                _dirty = true;
            }
        }

        /// <summary>Hydration: puts a stored lock back without marking it dirty. Expired locks are ignored.</summary>
        public void Restore(int statId, DateTime expiresAtUtc, DateTime nowUtc)
        {
            if (expiresAtUtc <= nowUtc)
                return;

            lock (_gate)
            {
                _expiresAtUtc[statId] = expiresAtUtc;
                _durationSeconds[statId] = (int)Math.Ceiling((expiresAtUtc - nowUtc).TotalSeconds);
            }
        }

        public TimeSpan Remaining(int statId, DateTime nowUtc)
        {
            lock (_gate)
            {
                if (!_expiresAtUtc.TryGetValue(statId, out DateTime expiresAtUtc))
                    return TimeSpan.Zero;

                TimeSpan remaining = expiresAtUtc - nowUtc;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        public bool IsLocked(int statId, DateTime nowUtc) => Remaining(statId, nowUtc) > TimeSpan.Zero;

        /// <summary>Every unexpired lock and its remaining time.</summary>
        public List<(int Id, TimeSpan Remaining)> Active(DateTime nowUtc)
        {
            lock (_gate)
            {
                var active = new List<(int, TimeSpan)>(_expiresAtUtc.Count);
                foreach (KeyValuePair<int, DateTime> entry in _expiresAtUtc)
                {
                    if (entry.Value > nowUtc)
                        active.Add((entry.Key, entry.Value - nowUtc));
                }

                return active;
            }
        }

        /// <summary>Every unexpired lock with the seconds it was locked for and the seconds left (rounded up).</summary>
        public List<(int Id, int DurationSeconds, int RemainingSeconds)> ActiveWithDuration(DateTime nowUtc)
        {
            lock (_gate)
            {
                var active = new List<(int, int, int)>(_expiresAtUtc.Count);
                foreach (KeyValuePair<int, DateTime> entry in _expiresAtUtc)
                {
                    if (entry.Value <= nowUtc)
                        continue;

                    int remaining = (int)Math.Ceiling((entry.Value - nowUtc).TotalSeconds);
                    int duration = _durationSeconds.TryGetValue(entry.Key, out int seconds) ? seconds : remaining;
                    active.Add((entry.Key, Math.Max(duration, remaining), remaining));
                }

                return active;
            }
        }

        /// <summary>The unexpired set to persist, or null when nothing changed since the last take.</summary>
        public List<SkillLockRecord>? TakeDirty(DateTime nowUtc)
        {
            lock (_gate)
            {
                if (!_dirty)
                    return null;

                _dirty = false;
                var snapshot = new List<SkillLockRecord>(_expiresAtUtc.Count);
                foreach (KeyValuePair<int, DateTime> entry in _expiresAtUtc)
                {
                    if (entry.Value > nowUtc)
                        snapshot.Add(new SkillLockRecord { StatId = entry.Key, ExpiresAtUtcTicks = entry.Value.Ticks });
                }

                return snapshot;
            }
        }

        /// <summary>A failed write leaves the set dirty; the next take re-reads the current locks.</summary>
        public void RestoreDirty(List<SkillLockRecord>? snapshot)
        {
            if (snapshot == null)
                return;

            lock (_gate)
                _dirty = true;
        }
    }
}
