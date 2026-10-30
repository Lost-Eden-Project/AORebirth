namespace ZoneEngine_New.Core.Modules
{
    using System;
    using System.Collections.Generic;

    using AORebirth.Modules.Events;
    using AORebirth.Modules.World;

    using ZoneEngine_New.Core.Entities;

    /// <summary>
    /// The locks running on a player now, for <see cref="AORebirth.Modules.World.IZoneWorld.Cooldowns"/> (module API 1.6):
    /// skill locks, the nano recharge, nano line locks and perk locks, restored ones included. Runs on the player's
    /// playfield tick (snapshot build or a read on that tick).
    /// </summary>
    internal static class ModuleCooldowns
    {
        public static CooldownInfo[] Read(Player player)
        {
            ArgumentNullException.ThrowIfNull(player);
            DateTime nowUtc = DateTime.UtcNow;
            var locks = new List<CooldownInfo>();
            Add(locks, CooldownKind.SkillLock, player.SkillLocks, nowUtc);
            Add(locks, CooldownKind.NanoLine, player.NanoLineLocks, nowUtc);
            Add(locks, CooldownKind.PerkLock, player.PerkLocks, nowUtc);
            if (player.IsInNanoRecharge(nowUtc))
                locks.Add(new CooldownInfo(new CooldownKey(CooldownKind.NanoRecharge, 0), player.NanoRechargeLength,
                    ModuleClock.At(player.NanoRechargeUntilUtc)));
            return locks.Count == 0 ? Array.Empty<CooldownInfo>() : locks.ToArray();
        }

        static void Add(List<CooldownInfo> locks, CooldownKind kind, SkillLocks source, DateTime nowUtc)
        {
            foreach ((int id, int durationSeconds, DateTime expiresAtUtc) in source.ActiveUntil(nowUtc))
                locks.Add(new CooldownInfo(new CooldownKey(kind, id), TimeSpan.FromSeconds(durationSeconds), ModuleClock.At(expiresAtUtc)));
        }

        // ---- the action a lock comes from ----

        [ThreadStatic]
        static bool _active;

        [ThreadStatic]
        static PlayerActionKind _kind;

        [ThreadStatic]
        static int _id;

        /// <summary>
        /// Marks the action whose effects run until the scope ends, so a lock set by those effects (LockSkill, LockPerk
        /// functions) reports it as CooldownEvent.SourceKind / SourceId. Two thread-static writes.
        /// </summary>
        public static SourceScope During(PlayerActionKind kind, int id)
        {
            var scope = new SourceScope(_active, _kind, _id);
            _active = true;
            _kind = kind;
            _id = id;
            return scope;
        }

        /// <summary>The action running now (see <see cref="During"/>), or Other.</summary>
        public static PlayerActionKind SourceKind => _active ? _kind : PlayerActionKind.Other;

        /// <summary>The ability id of the action running now, or 0.</summary>
        public static int SourceId => _active ? _id : 0;

        public readonly struct SourceScope : IDisposable
        {
            readonly bool _wasActive;
            readonly PlayerActionKind _previousKind;
            readonly int _previousId;

            internal SourceScope(bool wasActive, PlayerActionKind previousKind, int previousId)
            {
                _wasActive = wasActive;
                _previousKind = previousKind;
                _previousId = previousId;
            }

            public void Dispose()
            {
                _active = _wasActive;
                _kind = _previousKind;
                _id = _previousId;
            }
        }
    }
}
