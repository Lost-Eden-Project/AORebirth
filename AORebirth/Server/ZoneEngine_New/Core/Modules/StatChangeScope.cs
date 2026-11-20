namespace ZoneEngine_New.Core.Modules
{
    using System;

    using AORebirth.Modules.Events;

    /// <summary>
    /// The <see cref="StatChangeReason"/> (module API 1.16) and <see cref="StatChangeSource"/> (1.17) a
    /// <see cref="PlayerStatChangedEvent"/> carries: set around the code that changes an audited stat, on the thread that
    /// changes it. The innermost scope wins, reason and source together, except that a GM command or a module operation
    /// stays the reason, with its own source, for everything it causes. Costs a thread-static read and write.
    /// </summary>
    internal readonly struct StatChangeScope : IDisposable
    {
        [ThreadStatic]
        static StatChangeReason t_current;

        [ThreadStatic]
        static StatChangeSource t_source;

        readonly StatChangeReason _previous;
        readonly StatChangeSource _previousSource;
        readonly bool _set;

        StatChangeScope(StatChangeReason previous, StatChangeSource previousSource)
        {
            _previous = previous;
            _previousSource = previousSource;
            _set = true;
        }

        public static StatChangeReason Current => t_current;

        public static StatChangeSource CurrentSource => t_source;

        /// <summary>Makes <paramref name="reason"/> the reason until disposed, unless a GM command or module operation is running.</summary>
        public static StatChangeScope Begin(StatChangeReason reason) => Begin(reason, default);

        /// <summary>
        /// Makes <paramref name="reason"/> the reason and <paramref name="source"/> its source until disposed, unless a GM
        /// command or module operation is running.
        /// </summary>
        public static StatChangeScope Begin(StatChangeReason reason, in StatChangeSource source)
        {
            StatChangeReason previous = t_current;
            if (previous is StatChangeReason.Gm or StatChangeReason.Module)
                return default;
            var scope = new StatChangeScope(previous, t_source);
            t_current = reason;
            t_source = source;
            return scope;
        }

        /// <summary>Makes <paramref name="reason"/> the reason only when no other is set (an XP award's own source).</summary>
        public static StatChangeScope Default(StatChangeReason reason) => Default(reason, default);

        /// <summary>Makes <paramref name="reason"/> and <paramref name="source"/> current only when no other reason is set.</summary>
        public static StatChangeScope Default(StatChangeReason reason, in StatChangeSource source)
            => t_current == StatChangeReason.Other ? Begin(reason, source) : default;

        public void Dispose()
        {
            if (!_set)
                return;
            t_current = _previous;
            t_source = _previousSource;
        }
    }
}
