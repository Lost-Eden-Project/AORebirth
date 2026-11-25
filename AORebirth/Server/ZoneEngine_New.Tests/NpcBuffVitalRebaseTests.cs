namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.Reflection;

using AORebirth.Enums;

using LostEden.Protocol.GameData;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Nanos;

/// <summary>
/// Lost Eden 2026-11-25: an idle Soft Convict in a quest dungeon showed Health 776 of MaxHealth 975 (base 457, buff bonus
/// 518) with no one fighting it. It spawned full at 457; another NPC's idle buff raised the max and the NPC rebase left
/// Health where it was, so it sat short and crept up at its natural 7 HP per 28 s. A player keeps the share of health
/// left when a buff moves the max (Character.RebaseVital); an NPC now does the same.
/// </summary>
[TestClass]
public sealed class NpcBuffVitalRebaseTests
{
    const int MaxHealthBuffId = 900001;
    const int MaxNanoBuffId = 900002;
    static readonly Identity Caster = new() { Type = IdentityType.SimpleChar, Instance = 77 };

    [TestMethod]
    public void AFullIdleNpcBuffedForMaxHealthStaysFull()
    {
        NpcCharacter npc = Spawned(maxHealth: 457, maxNano: 134);

        Apply(npc, Buff(MaxHealthBuffId, CharacterStat.MaxHealth, 518));

        Assert.AreEqual(975, npc.Stats.GetOrZero(CharacterStat.MaxHealth));
        Assert.AreEqual(975, npc.Stats.GetOrZero(CharacterStat.Health), "A full NPC stays full at the buffed max.");
        Assert.AreEqual(134, npc.Stats.GetOrZero(CharacterStat.CurrentNano), "Nano was not touched by a health buff.");
    }

    [TestMethod]
    public void AFullIdleNpcBuffedForMaxNanoStaysFull()
    {
        NpcCharacter npc = Spawned(maxHealth: 457, maxNano: 134);

        Apply(npc, Buff(MaxNanoBuffId, CharacterStat.MaxNanoEnergy, 66));

        Assert.AreEqual(200, npc.Stats.GetOrZero(CharacterStat.MaxNanoEnergy));
        Assert.AreEqual(200, npc.Stats.GetOrZero(CharacterStat.CurrentNano));
        Assert.AreEqual(457, npc.Stats.GetOrZero(CharacterStat.Health));
    }

    [TestMethod]
    public void AHurtNpcKeepsItsShareOfHealthThroughTheBuffAndItsEnd()
    {
        NpcCharacter npc = Spawned(maxHealth: 400, maxNano: 0);
        npc.Stats.Set(CharacterStat.Health, 100, StatDetail.Base, dirty: true);

        Apply(npc, Buff(MaxHealthBuffId, CharacterStat.MaxHealth, 400));
        Assert.AreEqual(800, npc.Stats.GetOrZero(CharacterStat.MaxHealth));
        Assert.AreEqual(200, npc.Stats.GetOrZero(CharacterStat.Health), "A quarter of 400 is a quarter of 800.");

        Assert.AreEqual(BuffRemovalOutcome.Removed, npc.TryRemoveBuff(MaxHealthBuffId, BuffRemovalReason.Expired, out _));
        Assert.AreEqual(400, npc.Stats.GetOrZero(CharacterStat.MaxHealth));
        Assert.AreEqual(100, npc.Stats.GetOrZero(CharacterStat.Health), "The buff ending scales it back down.");
    }

    [TestMethod]
    public void BuffingOnAndOffNeverGainsHealth()
    {
        // 333 of 1000 scaled through a 1,517 max and back must not round up.
        NpcCharacter npc = Spawned(maxHealth: 1000, maxNano: 0);
        npc.Stats.Set(CharacterStat.Health, 333, StatDetail.Base, dirty: true);

        for (int i = 0; i < 5; i++)
        {
            Apply(npc, Buff(MaxHealthBuffId, CharacterStat.MaxHealth, 517));
            Assert.AreEqual(BuffRemovalOutcome.Removed, npc.TryRemoveBuff(MaxHealthBuffId, BuffRemovalReason.Expired, out _));
            Assert.IsTrue(npc.Stats.GetOrZero(CharacterStat.Health) <= 333, "Swapping the buff on and off gained health.");
            Assert.IsTrue(npc.Stats.GetOrZero(CharacterStat.Health) > 0, "The NPC was killed by a buff ending.");
        }
    }

    [TestMethod]
    public void AnNpcWithoutVitalsGainsNone()
    {
        var npc = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = 4 }, NoItems.Instance);
        npc.Stats.Set(CharacterStat.Level, 1);

        npc.Rebase();

        Assert.IsTrue(StatCollection.IsUnset(npc.Stats.Get(CharacterStat.MaxHealth)));
        Assert.IsTrue(StatCollection.IsUnset(npc.Stats.Get(CharacterStat.Health)));
        Assert.IsTrue(StatCollection.IsUnset(npc.Stats.Get(CharacterStat.CurrentNano)));
    }

    /// <summary>The template stats, then the spawn's rebase and fill (SpawnService.Spawn).</summary>
    static NpcCharacter Spawned(int maxHealth, int maxNano)
    {
        var npc = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = 3 }, NoItems.Instance);
        npc.Stats.Set(CharacterStat.Level, 13);
        npc.Stats.Set(CharacterStat.MaxHealth, maxHealth);
        npc.Stats.Set(CharacterStat.Health, maxHealth);
        if (maxNano > 0)
        {
            npc.Stats.Set(CharacterStat.MaxNanoEnergy, maxNano);
            npc.Stats.Set(CharacterStat.CurrentNano, maxNano);
        }

        npc.Rebase();
        Assert.AreEqual(maxHealth, npc.Stats.GetOrZero(CharacterStat.Health), "Spawned full.");
        return npc;
    }

    /// <summary>
    /// An idle NPC caster's buff landing (Character.TryApplyBuff). Off a playfield the rebase runs inline; on one it is
    /// queued and Playfield.DrainRebases runs the same RebaseStats.
    /// </summary>
    static void Apply(NpcCharacter npc, NanoSpell spell)
    {
        BuffApplyDecision decision = npc.TryApplyBuff(spell, Caster, DateTime.UtcNow, out Buff? applied, out _);
        Assert.AreEqual(BuffApplyDecision.Apply, decision);
        Assert.IsNotNull(applied);
    }

    static NanoSpell Buff(int id, CharacterStat stat, int amount) => NanoSpell.From(new ItemTemplate
    {
        Id = id,
        Name = "Test max buff " + id,
        Quality = 10,
        Stats = new Dictionary<CharacterStat, int>
        {
            [CharacterStat.TimeExist] = 60000,
            [CharacterStat.Level] = 10,
            [CharacterStat.MetaType] = id,
            [CharacterStat.Can] = unchecked((int)CanFlags.ApplyOnFriendly)
        },
        SpellList = new Dictionary<EventType, List<ItemSpell>>
        {
            [EventType.OnUse] =
            [
                new ItemSpell
                {
                    FunctionType = (int)FunctionType.Modify,
                    Arguments = [(int)stat, amount]
                }
            ]
        }
    });

    /// <summary>The NPC never mints an item here.</summary>
    class NoItems : DispatchProxy
    {
        public static readonly IItemBuilder Instance = Create<IItemBuilder, NoItems>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException("No item is built in this test.");
    }
}
