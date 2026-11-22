using System.Collections.Generic;
using System.Reflection;

using ChatEngine.CoreClient;
using ChatEngine.PacketHandlers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ZoneEngine_New.Tests;

/// <summary>
/// Bug #170: anyone who sent a tell came back as a friend after relog. The client adds a tell partner as a temporary
/// buddy (status byte 0) to show their online state; the server stored every add as a friend. Only a friend the player
/// added (status 1) is stored now, and removing a friend removes the stored row.
/// </summary>
[TestClass]
[TestCategory("ChatNetwork")]
public sealed class BuddyListPersistenceTests
{
    const uint Friend = 50001;
    const uint TellPartner = 50002;

    [TestMethod]
    public void OnlyAFriendAddIsStored()
    {
        Assert.IsTrue(BuddyAdd.IsPermanent(BuddyAdd.PermanentStatus));
        Assert.IsFalse(BuddyAdd.IsPermanent(0), "a tell partner's temporary add is never stored");
    }

    [TestMethod]
    public void ATellPartnerIsTrackedForTheSessionButNotAsAStoredFriend()
    {
        var client = new Client();

        Call(client, "AddBuddyId", TellPartner, false);

        Assert.IsTrue((bool)Call(client, "HasBuddy", TellPartner)!, "online notices still reach the tell window");
        Assert.IsFalse((bool)Call(client, "IsStoredBuddy", TellPartner)!);
    }

    [TestMethod]
    public void LoggedInFriendsAreStoredAndAddingATellPartnerLaterMakesItAFriend()
    {
        var client = new Client();
        Call(client, "SetBuddies", new List<int> { (int)Friend });
        Call(client, "AddBuddyId", TellPartner, false);

        Assert.IsTrue((bool)Call(client, "IsStoredBuddy", Friend)!);
        Assert.IsFalse((bool)Call(client, "IsStoredBuddy", TellPartner)!);

        Call(client, "AddBuddyId", TellPartner, true);
        Assert.IsTrue((bool)Call(client, "IsStoredBuddy", TellPartner)!);
    }

    [TestMethod]
    public void RemovingABuddyForgetsItWhicheverKindItWas()
    {
        var client = new Client();
        Call(client, "SetBuddies", new List<int> { (int)Friend });
        Call(client, "AddBuddyId", TellPartner, false);

        Call(client, "RemoveBuddyId", Friend);
        Call(client, "RemoveBuddyId", TellPartner);

        Assert.IsFalse((bool)Call(client, "HasBuddy", Friend)!);
        Assert.IsFalse((bool)Call(client, "IsStoredBuddy", Friend)!);
        Assert.IsFalse((bool)Call(client, "HasBuddy", TellPartner)!);
    }

    static object? Call(Client client, string name, params object[] args)
        => (typeof(Client).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("No method " + name)).Invoke(client, args);
}
