namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;

using AORebirth.Enums;

using LostEden.Protocol.GameData;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Inventory;

/// <summary>
/// LE-01: a dropped item appeared on the ground the moment its retire was queued, and its owner could pick it up again
/// after a reload even when that retire never stored (the lane failed ahead of it): two copies of one row. The world
/// item now carries the drop's settlement and refuses pickup until the retire is stored; a world spawn carries none.
/// </summary>
[TestClass]
public sealed class WorldItemDropSettlementTests
{
    [TestMethod]
    public void ADropCanBePickedUpByItsOwnerOnlyOnceTheRetireIsStored()
    {
        Player owner = TestWorld.CreatePlayer(4100);
        Player other = TestWorld.CreatePlayer(4101);
        var settlement = new DropSettlement();
        WorldItem drop = Drop(owner, settlement);

        Assert.IsFalse(drop.CanBePickedUpBy(owner), "nothing may be picked up while the retire is only queued");
        Assert.IsFalse(drop.CanBePickedUpBy(other));

        settlement.MarkStored();

        Assert.IsTrue(drop.CanBePickedUpBy(owner), "the owner takes it back once the retire is stored");
        Assert.IsFalse(drop.CanBePickedUpBy(other), "a drop stays its owner's");
    }

    [TestMethod]
    public void AWorldSpawnCarriesNoSettlementAndCanBeTakenAtOnce()
    {
        Player anyone = TestWorld.CreatePlayer(4102);
        WorldItem spawn = new(new Identity { Type = IdentityType.None, Instance = 7 }, Carryable(), 1000, 1000, 1);

        Assert.IsTrue(spawn.CanBePickedUpBy(anyone));
    }

    static WorldItem Drop(Player owner, DropSettlement settlement) =>
        new(new Identity { Type = IdentityType.None, Instance = 8 }, Carryable(), 1000, 1000, 1)
        {
            Owner = owner.Identity,
            Settlement = settlement,
            ExpiresAtUtc = DateTime.UtcNow + WorldItemTransfers.DroppedItemLifetime
        };

    static ItemTemplate Carryable() => new()
    {
        Id = 1000,
        Name = "Dropped thing",
        Quality = 1,
        Stats = new Dictionary<CharacterStat, int> { [CharacterStat.Can] = unchecked((int)CanFlags.Carry) },
        SpellList = new Dictionary<EventType, List<ItemSpell>>()
    };
}
