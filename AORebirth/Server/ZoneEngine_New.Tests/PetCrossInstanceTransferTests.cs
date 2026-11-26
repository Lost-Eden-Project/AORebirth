namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using AORebirth.Enums;
using AORebirth.World.Contracts.V1;

using LostEden.Protocol.GameData;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Nanos;
using ZoneEngine_New.Core.Pets;
using ZoneEngine_New.Core.World;

using Quaternion = AORebirth.Core.Vector.Quaternion;
using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Lost Eden v0.15.1 zone swap (2026-11-26): character 643's pet EMEN was attacking when the swap moved it, and the
/// arriving zone threw "Use Attack() for attack orders." restoring that order. Its buffs were not carried at all, so
/// every pet arrived on another zone instance unbuffed, though a move inside one instance keeps them. These tests run
/// the pet through the leaving side's envelope and the arriving side's restore, as a transfer or swap does.
/// </summary>
[TestClass]
public sealed class PetCrossInstanceTransferTests
{
    const int HealthBuffId = 930001;
    const int ShieldBuffId = 930002;
    const int ShieldGrant = 300;

    static readonly Identity Owner = new() { Type = IdentityType.SimpleChar, Instance = 643 };

    [TestMethod]
    public void AnAttackingBuffedPetArrivesWithItsBuffsShieldHealthAndPreviousOrder()
    {
        var items = new Items(HealthBuff(), ShieldBuff());
        Player owner = NewOwner(items);
        DateTime now = DateTime.UtcNow;
        (NpcCharacter leaving, PetController controller) = NewPet(owner, items, 9388872);
        owner.OwnedPets.Add(leaving);

        Apply(leaving, HealthBuff(), now);
        Apply(leaving, ShieldBuff(), now);
        // Part of the shield was spent in the fight before the move.
        leaving.Stats.Set(CharacterStat.AbsorbProjectileAC, 120, StatDetail.Base);
        int buffedMax = leaving.Stats.GetOrZero(CharacterStat.MaxHealth);
        Assert.AreEqual(1500, buffedMax, "The health buff runs on the leaving pet.");
        leaving.Stats.Set(CharacterStat.Health, 750, StatDetail.Base);
        controller.Attack(new Identity { Type = IdentityType.SimpleChar, Instance = 1001223 });

        TransferEnvelope envelope = CrossInstanceTransfer.BuildEnvelope(owner, 4311, new Vector3(0, 0, 0),
            new Quaternion(0, 0, 0, 1), ArrivalKind.ArrivalZone, 0);
        PetEnvelope carried = envelope.Pets.Single();
        Assert.AreEqual((int)PetMode.Guard, carried.Mode, "An attack order leaves as the order it interrupted.");
        CollectionAssert.AreEquivalent(new[] { HealthBuffId, ShieldBuffId }, carried.Buffs.Select(b => b.NanoId).ToArray());

        (NpcCharacter arriving, PetController arrivingController) = NewPet(owner, items, 9388900);
        EnvelopeApplier.RestorePet(arriving, carried, items, DateTime.UtcNow);

        Assert.AreEqual(PetMode.Guard, arrivingController.Mode);
        Assert.IsTrue(arriving.TryGetBuff(HealthBuffId, out Buff? health) && arriving.TryGetBuff(ShieldBuffId, out _),
            "Both buffs run on the arriving pet.");
        Assert.IsTrue(health!.ExpiresAtUtc <= now.AddSeconds(60).AddSeconds(1) && health.ExpiresAtUtc > now.AddSeconds(55),
            "A buff keeps the time it had left; the move does not refresh it.");
        Assert.AreEqual(buffedMax, arriving.Stats.GetOrZero(CharacterStat.MaxHealth), "The buff's bonus applies on arrival.");
        Assert.AreEqual(750, arriving.Stats.GetOrZero(CharacterStat.Health), "Health is half of the buffed maximum, as it left.");
        Assert.AreEqual(120, arriving.Stats.GetOrZero(CharacterStat.AbsorbProjectileAC, StatDetail.Base),
            "The shield arrives as it was spent, never refilled to its grant.");
    }

    [TestMethod]
    public void AnAttackOrderFromAnOlderZoneDoesNotFailTheRestore()
    {
        var items = new Items();
        Player owner = NewOwner(items);
        (NpcCharacter arriving, PetController controller) = NewPet(owner, items, 9388901);
        var carried = new PetEnvelope { Hash = "EMEN", Level = 60, HealthFraction = 0.5f, Mode = (int)PetMode.Attack };

        EnvelopeApplier.RestorePet(arriving, carried, items, DateTime.UtcNow);

        Assert.AreEqual(PetMode.Guard, controller.Mode);
        Assert.AreEqual(500, arriving.Stats.GetOrZero(CharacterStat.Health));
    }

    [TestMethod]
    public void ARepeatedMoveNeverRefillsAPetsShield()
    {
        var items = new Items(ShieldBuff());
        Player owner = NewOwner(items);
        (NpcCharacter pet, _) = NewPet(owner, items, 9388902);
        owner.OwnedPets.Add(pet);
        Apply(pet, ShieldBuff(), DateTime.UtcNow);
        pet.Stats.Set(CharacterStat.AbsorbProjectileAC, 40, StatDetail.Base);

        for (int hop = 0; hop < 3; hop++)
        {
            PetEnvelope carried = CrossInstanceTransfer.BuildEnvelope(owner, 4311, new Vector3(0, 0, 0),
                new Quaternion(0, 0, 0, 1), ArrivalKind.ArrivalZone, 0).Pets.Single();
            owner.OwnedPets.Remove(pet);
            (pet, _) = NewPet(owner, items, 9388903 + hop);
            owner.OwnedPets.Add(pet);
            EnvelopeApplier.RestorePet(pet, carried, items, DateTime.UtcNow);
            Assert.AreEqual(40, pet.Stats.GetOrZero(CharacterStat.AbsorbProjectileAC, StatDetail.Base), "hop " + hop);
        }
    }

    static void Apply(NpcCharacter pet, NanoSpell spell, DateTime now)
        => Assert.AreEqual(BuffApplyDecision.Apply, pet.TryApplyBuff(spell, Owner, now, out _, out _));

    static NanoSpell HealthBuff()
        => Nano(HealthBuffId, line: 501, new ItemSpell
        {
            FunctionType = (int)FunctionType.Modify,
            Target = (int)ItemTarget.Wearer,
            Arguments = [(int)CharacterStat.MaxHealth, 500]
        });

    static NanoSpell ShieldBuff()
        => Nano(ShieldBuffId, line: 502, new ItemSpell
        {
            FunctionType = (int)FunctionType.ChangeVariable,
            Target = (int)ItemTarget.Wearer,
            Arguments = [(int)CharacterStat.AbsorbProjectileAC, ShieldGrant]
        });

    static NanoSpell Nano(int id, int line, ItemSpell modifier)
        => NanoSpell.From(new ItemTemplate
        {
            Id = id,
            Name = "Nano " + id,
            Quality = 1,
            Flags = 2,
            Stats = new Dictionary<CharacterStat, int>
            {
                [CharacterStat.TimeExist] = 6000,
                [CharacterStat.Level] = 1,
                [CharacterStat.MetaType] = line,
                [CharacterStat.StackingOrder] = 1,
                [CharacterStat.Can] = unchecked((int)CanFlags.ApplyOnFriendly)
            },
            SpellList = new Dictionary<EventType, List<ItemSpell>> { [EventType.OnUse] = [modifier] },
            CanCancel = true
        });

    static Player NewOwner(Items items)
    {
        var player = new Player(Owner, new Logger(), items) { FirstName = "Owner" };
        player.Inventory.Apply(new CharacterHydrationResult(), Owner.Instance, items);
        player.Stats.Set(CharacterStat.Level, 60);
        return player;
    }

    static (NpcCharacter, PetController) NewPet(Player owner, Items items, int instance)
    {
        var pet = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = instance }, items);
        pet.Stats.Set(CharacterStat.Level, 60, StatDetail.Base);
        pet.Stats.Set(CharacterStat.MaxHealth, 1000, StatDetail.Base);
        pet.Stats.Set(CharacterStat.Health, 1000, StatDetail.Base);
        pet.Stats.Set(CharacterStat.MaxNCU, 100, StatDetail.Base);
        var controller = new PetController(owner, PetTypes.Attack, "EMEN", 60, expiresUtc: null);
        pet.BindPet(controller);
        return (pet, controller);
    }

    sealed class Logger : IZoneLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(Exception exception, string message) { }
        public IZoneLogger CreateForPlayfield(int playfieldId) => this;
    }

    sealed class Items : IItemBuilder
    {
        readonly Dictionary<int, ItemTemplate> _templates;
        int _next = 51600000;

        internal Items(params NanoSpell[] nanos)
            => _templates = nanos.ToDictionary(nano => nano.Id, nano => (ItemTemplate)nano);

        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null) => new()
            {
                LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount,
                InstanceId = instanceId, Identity = identity ?? Identity.None, Definition = CreateTemplate(lowId, highId, quality)
            };

        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
        {
            Item item = Create(lowId, highId, quality, source, stackCount);
            item.AssignInstanceId(Interlocked.Increment(ref _next));
            return item;
        }

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality)
            => _templates.TryGetValue(lowId, out ItemTemplate? template) ? template : new ItemTemplate { Id = lowId, Quality = quality };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }
}
