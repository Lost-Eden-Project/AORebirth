namespace ZoneEngine_New.Tests;

using LoginEngine.Packets;

using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// LE-16: the character slot limit only shaped the list reply; creation never counted. The rule the creation
/// transaction now applies (with the account row locked): the account's own allowance or the default, whichever is
/// higher, and one more character only while fewer than that exist.
/// </summary>
[TestClass]
public sealed class CharacterSlotPolicyTests
{
    [TestMethod]
    public void TheAccountsAllowanceRaisesTheDefaultButNeverLowersIt()
    {
        Assert.AreEqual(50, CharacterSlotPolicy.Allowed(accountAllowedCharacters: 8, defaultAllowedCharacters: 50));
        Assert.AreEqual(60, CharacterSlotPolicy.Allowed(accountAllowedCharacters: 60, defaultAllowedCharacters: 50));
        Assert.AreEqual(50, CharacterSlotPolicy.Allowed(accountAllowedCharacters: 0, defaultAllowedCharacters: 50));
    }

    [TestMethod]
    public void OneMoreCharacterIsAllowedOnlyBelowTheLimit()
    {
        Assert.IsTrue(CharacterSlotPolicy.CanCreate(existingCharacters: 0, allowedCharacters: 50));
        Assert.IsTrue(CharacterSlotPolicy.CanCreate(existingCharacters: 49, allowedCharacters: 50));
        Assert.IsFalse(CharacterSlotPolicy.CanCreate(existingCharacters: 50, allowedCharacters: 50));
        Assert.IsFalse(CharacterSlotPolicy.CanCreate(existingCharacters: 51, allowedCharacters: 50), "an account already over its limit creates nothing more");
        Assert.IsFalse(CharacterSlotPolicy.CanCreate(existingCharacters: -1, allowedCharacters: 50), "a count that cannot be trusted refuses");
        Assert.IsFalse(CharacterSlotPolicy.CanCreate(existingCharacters: 0, allowedCharacters: 0), "no slots, no character");
    }
}
