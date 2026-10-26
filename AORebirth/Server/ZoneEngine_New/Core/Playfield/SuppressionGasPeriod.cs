namespace ZoneEngine_New.Core.Playfield
{
    using System;

    using ZoneEngine_New.Core.Entities;

    /// <summary>
    /// A player's PvP grace and disgrace, as the client runs them (Gamecode.dll 0x1003f449, on every change of the
    /// district's fight mode). Moving from no PvP (100% / 75% gas, or into the playfield) into PvP gas starts a grace:
    /// for <see cref="DistrictFightModes.GraceSecondsAt"/> the player keeps the mode it came from, so other players
    /// cannot attack it yet. Moving from PvP gas into no PvP starts a disgrace: for <see cref="DisgraceSeconds"/> it
    /// keeps the PvP mode it left and can still be attacked. One period at a time: entering PvP while disgraced ends
    /// the disgrace with no grace, and leaving PvP while graced ends the grace with no disgrace. Attacking anyone or
    /// casting a hostile nano on them ends a grace early (0x10069f8e, 0x10051a78). The client shows the countdowns
    /// itself.
    /// </summary>
    public sealed class SuppressionGasPeriod
    {
        /// <summary>The disgrace after leaving PvP gas (0x1003ef25).</summary>
        public const int DisgraceSeconds = 60;

        Playfield? _playfield;
        FightMode? _district;
        FightMode _held;
        bool _grace;
        bool _disgrace;
        DateTime _endsAtUtc;

        public bool InGrace => _grace;

        public bool InDisgrace => _disgrace;

        /// <summary>The mode other players judge the player by: the held mode during a period, else the district's.</summary>
        public FightMode Effective(FightMode district) => _grace || _disgrace ? _held : district;

        /// <summary>Follows the player's district; call every tick.</summary>
        public void Update(Player player, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(player);
            if ((_grace || _disgrace) && nowUtc >= _endsAtUtc)
                _grace = _disgrace = false;

            Playfield? playfield = player.Playfield;
            if (!ReferenceEquals(playfield, _playfield))
            {
                // A new playfield is entered from no district at all.
                _playfield = playfield;
                _district = null;
            }

            if (playfield == null)
                return;

            int zone = playfield.ZoneOf(player);
            FightMode district = playfield.FightModes.At(zone);
            if (_district == district)
                return;

            FightMode? previous = _district;
            _district = district;
            _held = previous ?? FightMode.NoFight;
            bool wasPvp = previous is FightMode old && IsPvp(old);
            bool isPvp = IsPvp(district);
            if (wasPvp && !isPvp)
            {
                if (_grace)
                    _grace = false;
                else
                    Begin(ref _disgrace, nowUtc, DisgraceSeconds);
            }
            else if (!wasPvp && isPvp)
            {
                if (_disgrace)
                    _disgrace = false;
                else
                    Begin(ref _grace, nowUtc, playfield.FightModes.GraceSecondsAt(zone));
            }
        }

        /// <summary>The player attacked, or aimed a hostile nano at, someone: a grace ends.</summary>
        public void EndGrace() => _grace = false;

        void Begin(ref bool period, DateTime nowUtc, int seconds)
        {
            period = true;
            _endsAtUtc = nowUtc.AddSeconds(seconds);
        }

        static bool IsPvp(FightMode mode) => mode >= FightMode.Side;
    }
}
