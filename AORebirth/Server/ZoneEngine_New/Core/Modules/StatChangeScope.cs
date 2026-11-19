namespace ZoneEngine_New.Core.Modules
{
    using System;

    using AORebirth.Modules.Events;

    /// <summary>
    /// The <see cref="StatChangeReason"/> a <see cref="PlayerStatChangedEvent"/> carries (module API 1.16): set around the
    /// code that changes an audited stat, on the thread that changes it. The innermost scope wins, except that a GM
    /// command or a module operation stays the reason for everything it causes. Costs a thread-static read and write.
    /// </summary>
    internal readonly struct StatChangeScope : IDisposable
    {
        [ThreadStatic]
        static StatChangeReason t_current;

        readonly StatChangeReason _previous;
        readonly bool _set;

        StatChangeScope(StatChangeReason previous)
        {
            _previous = previous;
            _set = true;
        }

        public static StatChangeReason Current => t_current;

        /// <summary>Makes <paramref name="reason"/> the reason until disposed, unless a GM command or module operation is running.</summary>
        public static StatChangeScope Begin(StatChangeReason reason)
        {
            StatChangeReason previous = t_current;
            if (previous is StatChangeReason.Gm or StatChangeReason.Module)
                return default;
            t_current = reason;
            return new StatChangeScope(previous);
        }

        /// <summary>Makes <paramref name="reason"/> the reason only when no other is set (an XP award's own source).</summary>
        public static StatChangeScope Default(StatChangeReason reason)
            => t_current == StatChangeReason.Other ? Begin(reason) : default;

        public void Dispose()
        {
            if (_set)
                t_current = _previous;
        }
    }
}
