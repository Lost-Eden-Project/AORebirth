namespace ZoneEngine_New.Tests;

using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Helpers;
using SmokeLounge.AOtomation.Messaging.GameData;

[TestClass]
public sealed class ItemBehaviorContentTests
{
    const string Fixture = """{"ProtectedItems":[10]}""";

    [TestMethod]
    public void ProtectedItemsLoadFromContent()
    {
        var content = ItemBehaviorContent.Parse(Fixture);
        Assert.IsTrue(content.IsProtected(new Item { LowId = 10, HighId = 10 }));
        Assert.IsFalse(content.IsProtected(new Item { LowId = 20, HighId = 20 }));
    }

    [TestMethod]
    public void InvalidProtectedItemFailsValidation()
        => Assert.ThrowsExactly<InvalidDataException>(() => ItemBehaviorContent.Parse(Fixture.Replace("[10]", "[0]")));

    [TestMethod]
    public void FistAssignmentChangesFromContentWithSameBinary()
    {
        const string source = """{"FistWeapons":[{"Tier":1,"LowId":100,"HighId":101}]}""";
        var first = MartialArtsFistResolver.Resolve(Profession.MartialArtist, 300, ItemBehaviorContent.Parse(source));
        var second = MartialArtsFistResolver.Resolve(Profession.MartialArtist, 300,
            ItemBehaviorContent.Parse(source.Replace("\"LowId\":100", "\"LowId\":200")));
        Assert.AreEqual(100, first.LowId); Assert.AreEqual(200, second.LowId);
        Assert.AreEqual(first.Quality, second.Quality);
    }
}
