namespace ZoneEngine_New.Core.Network
{
    using System;

    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages.N3Messages;

    /// <summary>
    /// Stat messages from the engine's stat pairs: the engine keeps a stat's value as an int, the message carries the
    /// same 32 bits as a uint.
    /// </summary>
    internal static class StatMessages
    {
        /// <summary>A Stat message about <paramref name="identity"/> carrying <paramref name="stats"/>.</summary>
        public static StatMessage For(Identity identity, ReadOnlySpan<GameTuple<Stat, int>> stats, byte unknown = 1)
        {
            var message = new StatMessage { Identity = identity, Unknown = unknown };
            message.Stats.EnsureCapacity(stats.Length);
            foreach (GameTuple<Stat, int> stat in stats)
                message.Stats.Add(new GameTuple<Stat, uint>(stat.Value1, unchecked((uint)stat.Value2)));
            return message;
        }

        /// <summary>One stat pair as a Stat message carries it.</summary>
        public static GameTuple<Stat, uint> Pair(CharacterStat stat, int value) => new((Stat)stat, unchecked((uint)value));
    }
}
