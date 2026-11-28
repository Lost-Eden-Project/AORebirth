namespace ZoneEngine_New.Tests;

using System;
using System.Net;

using LoginEngine.CoreServer;

using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// LE-15: a wrong password only disconnected, so a guessing run was limited by nothing but the connection count and
/// every guess cost a 200,000-iteration hash. The throttle locks the address and the account out after a few failures,
/// for a growing time, before the hash is computed; a right password clears both.
/// </summary>
[TestClass]
public sealed class LoginAttemptThrottleTests
{
    static readonly IPAddress Attacker = IPAddress.Parse("203.0.113.7");
    static readonly IPAddress Victim = IPAddress.Parse("198.51.100.9");

    [TestMethod]
    public void AFewFailuresAreFreeThenTheAddressAndAccountAreLockedOutForAGrowingTime()
    {
        var clock = new Clock();
        var throttle = new LoginAttemptThrottle(clock.Now);

        for (int i = 0; i < LoginAttemptThrottle.FreeFailures; i++)
        {
            Assert.IsTrue(throttle.Allow(Attacker, "Mike"), "failure " + i + " is still free");
            throttle.RecordFailure(Attacker, "Mike");
        }
        Assert.IsTrue(throttle.Allow(Attacker, "Mike"), "the free failures alone lock nothing");

        throttle.RecordFailure(Attacker, "Mike");
        Assert.IsFalse(throttle.Allow(Attacker, "Mike"), "the next failure locks out");
        Assert.IsFalse(throttle.Allow(Attacker, "someone-else"), "the address is locked out for every account");
        Assert.IsFalse(throttle.Allow(Victim, "mike"), "the account is locked out from every address, whatever its case");
        Assert.IsTrue(throttle.Allow(Victim, "someone-else"), "another address and account are untouched");

        clock.Advance(LoginAttemptThrottle.FirstLockout);
        Assert.IsTrue(throttle.Allow(Attacker, "Mike"), "the first lockout is over");
        throttle.RecordFailure(Attacker, "Mike");
        Assert.IsFalse(throttle.Allow(Attacker, "Mike"));
        clock.Advance(LoginAttemptThrottle.FirstLockout);
        Assert.IsFalse(throttle.Allow(Attacker, "Mike"), "the second lockout is twice as long");
        clock.Advance(LoginAttemptThrottle.FirstLockout);
        Assert.IsTrue(throttle.Allow(Attacker, "Mike"));
    }

    [TestMethod]
    public void TheAccountLockoutIsCappedShortSoAGuesserCannotKeepItsOwnerOut()
    {
        var clock = new Clock();
        var throttle = new LoginAttemptThrottle(clock.Now);
        for (int i = 0; i < 40; i++)
            throttle.RecordFailure(Attacker, "Mike");

        clock.Advance(LoginAttemptThrottle.AccountLockoutCap);
        Assert.IsTrue(throttle.Allow(Victim, "Mike"), "the owner logs in from their own address once the account cap passed");
        Assert.IsFalse(throttle.Allow(Attacker, "Mike"), "the guessing address stays locked out longer");
        clock.Advance(LoginAttemptThrottle.AddressLockoutCap);
        Assert.IsTrue(throttle.Allow(Attacker, "Mike"), "the address lockout never exceeds its cap");
    }

    [TestMethod]
    public void ARightPasswordClearsTheAddressAndTheAccount()
    {
        var clock = new Clock();
        var throttle = new LoginAttemptThrottle(clock.Now);
        for (int i = 0; i <= LoginAttemptThrottle.FreeFailures; i++)
            throttle.RecordFailure(Attacker, "Mike");
        Assert.IsFalse(throttle.Allow(Attacker, "Mike"));

        throttle.RecordSuccess(Attacker, "Mike");

        Assert.IsTrue(throttle.Allow(Attacker, "Mike"));
        Assert.IsTrue(throttle.Allow(Attacker, "someone-else"));
        Assert.IsTrue(throttle.Allow(Victim, "Mike"));
    }

    [TestMethod]
    public void AnUnknownAddressStillCountsAgainstTheAccount()
    {
        var clock = new Clock();
        var throttle = new LoginAttemptThrottle(clock.Now);
        for (int i = 0; i <= LoginAttemptThrottle.FreeFailures; i++)
            throttle.RecordFailure(null!, "Mike");

        Assert.IsFalse(throttle.Allow(null!, "Mike"));
        Assert.IsFalse(throttle.Allow(Victim, "Mike"));
        Assert.IsTrue(throttle.Allow(null!, "someone-else"));
    }

    [TestMethod]
    public void LockoutsDoubleFromTheFirstAndStopAtTheCap()
    {
        Assert.AreEqual(TimeSpan.Zero, LoginAttemptThrottle.LockoutFor(LoginAttemptThrottle.FreeFailures, LoginAttemptThrottle.AddressLockoutCap));
        Assert.AreEqual(LoginAttemptThrottle.FirstLockout, LoginAttemptThrottle.LockoutFor(LoginAttemptThrottle.FreeFailures + 1, LoginAttemptThrottle.AddressLockoutCap));
        Assert.AreEqual(LoginAttemptThrottle.FirstLockout + LoginAttemptThrottle.FirstLockout,
            LoginAttemptThrottle.LockoutFor(LoginAttemptThrottle.FreeFailures + 2, LoginAttemptThrottle.AddressLockoutCap));
        Assert.AreEqual(LoginAttemptThrottle.AddressLockoutCap, LoginAttemptThrottle.LockoutFor(1000, LoginAttemptThrottle.AddressLockoutCap));
        Assert.AreEqual(LoginAttemptThrottle.AccountLockoutCap, LoginAttemptThrottle.LockoutFor(1000, LoginAttemptThrottle.AccountLockoutCap));
    }

    [TestMethod]
    public void TheTableStaysBoundedUnderAFloodOfAddresses()
    {
        var clock = new Clock();
        var throttle = new LoginAttemptThrottle(clock.Now);
        for (int i = 0; i < LoginAttemptThrottle.MaxKeys + 500; i++)
            throttle.RecordFailure(new IPAddress(new byte[] { 10, (byte)(i >> 16), (byte)(i >> 8), (byte)i }), "acct" + i);

        // Every failure adds two keys, so the table was trimmed many times over; the latest failure is still known.
        Assert.IsTrue(throttle.Allow(Victim, "fresh"));
        Assert.AreEqual("1 failed attempt(s)", throttle.Describe("acct" + (LoginAttemptThrottle.MaxKeys + 499)));
    }

    sealed class Clock
    {
        DateTime _now = new(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
        public DateTime Now() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
