namespace LoginEngine.CoreServer
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Net;

    /// <summary>
    /// Slows repeated failed passwords. Every failure counts against the connecting address and against the account
    /// named; past <see cref="FreeFailures"/> each further failure locks that key out for twice as long as the last,
    /// up to a cap, and a success clears both keys. The lockout is checked before the password hash is computed, so a
    /// guessing run costs no CPU once it is locked out. The account cap is short so that someone guessing at another
    /// player's name cannot keep that player out for long; the address cap is longer. Self-contained (no engine
    /// types) so it can be exercised by the zone test project.
    /// </summary>
    public sealed class LoginAttemptThrottle
    {
        /// <summary>Failures a key may make before a lockout starts.</summary>
        public const int FreeFailures = 5;

        /// <summary>The first lockout; each further failure doubles it up to the key's cap.</summary>
        public static readonly TimeSpan FirstLockout = TimeSpan.FromSeconds(2);

        public static readonly TimeSpan AddressLockoutCap = TimeSpan.FromMinutes(5);

        public static readonly TimeSpan AccountLockoutCap = TimeSpan.FromSeconds(30);

        /// <summary>Keys tracked at most; past it the expired and then the oldest entries are dropped.</summary>
        public const int MaxKeys = 100000;

        /// <summary>The engine's one throttle, over the real clock.</summary>
        public static readonly LoginAttemptThrottle Shared = new LoginAttemptThrottle(() => DateTime.UtcNow);

        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        private readonly Func<DateTime> clock;

        private readonly object sync = new object();

        public LoginAttemptThrottle(Func<DateTime> clock)
        {
            if (clock == null)
            {
                throw new ArgumentNullException("clock");
            }

            this.clock = clock;
        }

        /// <summary>True when neither the address nor the account is locked out now.</summary>
        public bool Allow(IPAddress address, string account)
        {
            DateTime now = this.clock();
            lock (this.sync)
            {
                return !this.LockedOut(AddressKey(address), now) && !this.LockedOut(AccountKey(account), now);
            }
        }

        /// <summary>A wrong password for <paramref name="account"/> from <paramref name="address"/>.</summary>
        public void RecordFailure(IPAddress address, string account)
        {
            DateTime now = this.clock();
            lock (this.sync)
            {
                this.Fail(AddressKey(address), now, AddressLockoutCap);
                this.Fail(AccountKey(account), now, AccountLockoutCap);
                this.Trim(now);
            }
        }

        /// <summary>A correct password: the address and the account start over.</summary>
        public void RecordSuccess(IPAddress address, string account)
        {
            lock (this.sync)
            {
                string addressKey = AddressKey(address);
                string accountKey = AccountKey(account);
                if (addressKey != null)
                {
                    this.entries.Remove(addressKey);
                }

                if (accountKey != null)
                {
                    this.entries.Remove(accountKey);
                }
            }
        }

        /// <summary>How long <paramref name="failures"/> failures in a row lock a key out, under <paramref name="cap"/>.</summary>
        public static TimeSpan LockoutFor(int failures, TimeSpan cap)
        {
            int over = failures - FreeFailures;
            if (over <= 0)
            {
                return TimeSpan.Zero;
            }

            double seconds = FirstLockout.TotalSeconds * Math.Pow(2, Math.Min(over - 1, 30));
            return seconds >= cap.TotalSeconds ? cap : TimeSpan.FromSeconds(seconds);
        }

        private static string AddressKey(IPAddress address)
        {
            return address == null ? null : "a:" + address.ToString();
        }

        private static string AccountKey(string account)
        {
            return string.IsNullOrWhiteSpace(account) ? null : "u:" + account.Trim().ToLowerInvariant();
        }

        private bool LockedOut(string key, DateTime now)
        {
            Entry entry;
            return key != null && this.entries.TryGetValue(key, out entry) && entry.LockedUntil > now;
        }

        private void Fail(string key, DateTime now, TimeSpan cap)
        {
            if (key == null)
            {
                return;
            }

            Entry entry;
            if (!this.entries.TryGetValue(key, out entry))
            {
                entry = new Entry();
                this.entries[key] = entry;
            }

            entry.Failures++;
            entry.LastFailure = now;
            TimeSpan lockout = LockoutFor(entry.Failures, cap);
            entry.LockedUntil = lockout > TimeSpan.Zero ? now.Add(lockout) : DateTime.MinValue;
        }

        /// <summary>Keeps the table bounded: entries whose lockout is over and that have been quiet for the address cap go first.</summary>
        private void Trim(DateTime now)
        {
            if (this.entries.Count <= MaxKeys)
            {
                return;
            }

            var stale = new List<string>();
            string oldestKey = null;
            DateTime oldest = DateTime.MaxValue;
            foreach (KeyValuePair<string, Entry> pair in this.entries)
            {
                if (pair.Value.LockedUntil <= now && pair.Value.LastFailure.Add(AddressLockoutCap) <= now)
                {
                    stale.Add(pair.Key);
                }

                if (pair.Value.LastFailure < oldest)
                {
                    oldest = pair.Value.LastFailure;
                    oldestKey = pair.Key;
                }
            }

            foreach (string key in stale)
            {
                this.entries.Remove(key);
            }

            if (this.entries.Count > MaxKeys && oldestKey != null)
            {
                this.entries.Remove(oldestKey);
            }
        }

        /// <summary>For logs: failures recorded against the account so far.</summary>
        public string Describe(string account)
        {
            lock (this.sync)
            {
                Entry entry;
                string key = AccountKey(account);
                return key != null && this.entries.TryGetValue(key, out entry)
                    ? entry.Failures.ToString(CultureInfo.InvariantCulture) + " failed attempt(s)"
                    : "no failed attempts";
            }
        }

        private sealed class Entry
        {
            public int Failures;

            public DateTime LastFailure;

            public DateTime LockedUntil;
        }
    }
}
