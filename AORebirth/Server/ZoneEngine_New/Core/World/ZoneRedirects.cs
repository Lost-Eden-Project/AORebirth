namespace ZoneEngine_New.Core.World
{
    using System;
    using System.Collections.Concurrent;
    using System.Security.Cryptography;

    /// <summary>
    /// Same-instance playfield changes. The client reconnects for every zone change with the cookies it logged in
    /// with; when the destination runs on this instance the lease never moves, so the authorization stays here:
    /// one live redirect per character, consumed once, valid for the handoff lifetime.
    /// </summary>
    public sealed class ZoneRedirects
    {
        readonly ConcurrentDictionary<int, Entry> _entries = new();
        readonly Func<long> _clock;

        public ZoneRedirects(Func<long>? clock = null)
        {
            _clock = clock ?? (() => Environment.TickCount64);
        }

        public static TimeSpan Lifetime { get; } = TimeSpan.FromSeconds(ResolveLifetimeSeconds());

        public void Authorize(int characterId, uint cookie1, uint cookie2)
        {
            if (characterId <= 0 || (cookie1 == 0 && cookie2 == 0))
                throw new InvalidOperationException("A claimed handoff is required for a redirect.");
            _entries[characterId] = new Entry(Hash(cookie1, cookie2), _clock() + (long)Lifetime.TotalMilliseconds);
        }

        /// <summary>True once for cookies that match a live authorization.</summary>
        public bool TryConsume(int characterId, uint cookie1, uint cookie2)
        {
            if (!_entries.TryGetValue(characterId, out Entry? entry)) return false;
            if (_clock() >= entry.ExpiresMs)
            {
                _entries.TryRemove(new(characterId, entry));
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(entry.Hash, Hash(cookie1, cookie2))
                && _entries.TryRemove(new(characterId, entry));
        }

        public void Revoke(int characterId) => _entries.TryRemove(characterId, out _);

        static byte[] Hash(uint first, uint second)
        {
            Span<byte> bytes = stackalloc byte[8];
            BitConverter.TryWriteBytes(bytes[..4], first);
            BitConverter.TryWriteBytes(bytes[4..], second);
            return SHA256.HashData(bytes);
        }

        static int ResolveLifetimeSeconds()
            => int.TryParse(Environment.GetEnvironmentVariable("AO_REBIRTH_LOGIN_HANDOFF_TIMEOUT_SECONDS"), out int seconds)
                && seconds >= 5 && seconds <= 120 ? seconds : 30;

        sealed record Entry(byte[] Hash, long ExpiresMs);
    }
}
