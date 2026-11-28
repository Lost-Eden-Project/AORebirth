namespace ZoneEngine_New.Tests;

using System;
using System.Collections.Generic;
using System.Reflection;

using AORebirth.Enums;

using LostEden.Protocol.GameData;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Nanos;

/// <summary>
/// Team request 39: stock NPCs run other professions' nanos they cast themselves (The Reck 2026-11-26: Nano-Technician
/// Arachnobots on Enfraam's Fortification 150628, an Engineer buff). An NPC's cast of a nano it has uploaded passes the
/// nano's caster profession check; a player's cast keeps it. The NPC caster (NpcNanoCaster) asks CanStartCast.
/// </summary>
[TestClass]
public sealed class NpcOtherProfessionNanoCastTests
{
    const int NanoTechnician = 11;
    const int Engineer = 6;
    const int EngineerBuffId = 150628;

    [TestMethod]
    public void AnNpcCastsAnUploadedNanoOfAnotherProfession()
    {
        NpcCharacter npc = Npc(NanoTechnician);
        NanoSpell spell = Buff(EngineerBuffId, Leaf(CharacterStat.Profession, Operator.EqualTo, Engineer));
        Assert.IsTrue(npc.TryAddUploadedNano(EngineerBuffId));

        Assert.IsTrue(NanoRuntime.CanStartCast(npc, spell, npc, DateTime.UtcNow),
            "A Nano-Technician NPC holding the Engineer crystal casts it, as stock NPCs do.");
    }

    [TestMethod]
    public void AVisualProfessionCheckOrAnOrOfProfessionsPassesForAnUploadedNano()
    {
        NpcCharacter npc = Npc(NanoTechnician);
        NanoSpell visual = Buff(EngineerBuffId, Leaf(CharacterStat.VisualProfession, Operator.EqualTo, Engineer));
        NanoSpell either = Buff(EngineerBuffId + 1,
            Leaf(CharacterStat.Profession, Operator.EqualTo, Engineer),
            Leaf(CharacterStat.Profession, Operator.EqualTo, 9),
            new ItemRequirement { Operator = (int)Operator.Or });
        npc.TryAddUploadedNano(visual.Id);
        npc.TryAddUploadedNano(either.Id);

        Assert.IsTrue(NanoRuntime.CanStartCast(npc, visual, npc, DateTime.UtcNow));
        Assert.IsTrue(NanoRuntime.CanStartCast(npc, either, npc, DateTime.UtcNow));
    }

    [TestMethod]
    public void AnNpcIsStillRefusedANanoItHasNotUploaded()
    {
        NpcCharacter npc = Npc(NanoTechnician);
        NanoSpell spell = Buff(EngineerBuffId, Leaf(CharacterStat.Profession, Operator.EqualTo, Engineer));

        Assert.IsFalse(NanoRuntime.CanStartCast(npc, spell, npc, DateTime.UtcNow),
            "Only an uploaded nano skips the profession check.");
    }

    [TestMethod]
    public void AnUploadedNanoKeepsItsOtherRequirements()
    {
        NpcCharacter npc = Npc(NanoTechnician);
        NanoSpell spell = Buff(EngineerBuffId,
            Leaf(CharacterStat.Profession, Operator.EqualTo, Engineer),
            Leaf(CharacterStat.Level, Operator.GreaterThan, 100),
            new ItemRequirement { Operator = (int)Operator.And });
        npc.TryAddUploadedNano(EngineerBuffId);

        Assert.IsFalse(NanoRuntime.CanStartCast(npc, spell, npc, DateTime.UtcNow), "A level 13 NPC is under level 100.");

        npc.Stats.Set(CharacterStat.Level, 150);
        Assert.IsTrue(NanoRuntime.CanStartCast(npc, spell, npc, DateTime.UtcNow));
    }

    [TestMethod]
    public void AProfessionCheckOnTheTargetStillApplies()
    {
        NpcCharacter npc = Npc(NanoTechnician);
        NanoSpell spell = Buff(EngineerBuffId,
            new ItemRequirement { Operator = (int)Operator.OnTarget },
            Leaf(CharacterStat.Profession, Operator.EqualTo, Engineer));
        npc.TryAddUploadedNano(EngineerBuffId);

        Assert.IsFalse(NanoRuntime.CanStartCast(npc, spell, npc, DateTime.UtcNow),
            "The target is a Nano-Technician, not an Engineer.");

        npc.Stats.Set(CharacterStat.Profession, Engineer);
        Assert.IsTrue(NanoRuntime.CanStartCast(npc, spell, npc, DateTime.UtcNow));
    }

    [TestMethod]
    public void APlayersCastKeepsTheProfessionCheck()
    {
        var player = new Player(new Identity { Type = IdentityType.SimpleChar, Instance = 50 }, Proxy<IZoneLogger>.Instance,
            Proxy<IItemBuilder>.Instance);
        player.Stats.Set(CharacterStat.Level, 13);
        player.Stats.Set(CharacterStat.Profession, NanoTechnician);
        player.Stats.Set(CharacterStat.MaxHealth, 500);
        player.Stats.Set(CharacterStat.Health, 500);
        player.Stats.Set(CharacterStat.MaxNCU, 100);
        NanoSpell spell = Buff(EngineerBuffId, Leaf(CharacterStat.Profession, Operator.EqualTo, Engineer));
        Assert.IsTrue(player.TryAddUploadedNano(EngineerBuffId));

        Assert.IsFalse(NanoRuntime.CanStartCast(player, spell, player, DateTime.UtcNow),
            "A Nano-Technician player cannot cast an Engineer nano, uploaded or not.");

        player.Stats.Set(CharacterStat.Profession, Engineer);
        Assert.IsTrue(NanoRuntime.CanStartCast(player, spell, player, DateTime.UtcNow));
    }

    static NpcCharacter Npc(int profession)
    {
        var npc = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = 3 }, Proxy<IItemBuilder>.Instance);
        npc.Stats.Set(CharacterStat.Level, 13);
        npc.Stats.Set(CharacterStat.Profession, profession);
        npc.Stats.Set(CharacterStat.MaxHealth, 500);
        npc.Stats.Set(CharacterStat.Health, 500);
        return npc;
    }

    static ItemRequirement Leaf(CharacterStat stat, Operator op, int value)
        => new() { StatNumber = (int)stat, Operator = (int)op, Value = value };

    static NanoSpell Buff(int id, params ItemRequirement[] toUse) => NanoSpell.From(new ItemTemplate
    {
        Id = id,
        Name = "Test other-profession buff " + id,
        Quality = 10,
        Stats = new Dictionary<CharacterStat, int>
        {
            [CharacterStat.TimeExist] = 60000,
            [CharacterStat.Level] = 10,
            [CharacterStat.Can] = unchecked((int)(CanFlags.ApplyOnFriendly | CanFlags.ApplyOnSelf))
        },
        Actions = [new ItemAction { ActionType = (int)ActionType.ToUse, Requirements = [.. toUse] }],
        SpellList = new Dictionary<EventType, List<ItemSpell>>
        {
            [EventType.OnUse] =
            [
                new ItemSpell { FunctionType = (int)FunctionType.Modify, Arguments = [(int)CharacterStat.MaxHealth, 10] }
            ]
        }
    });

    /// <summary>Nothing is built or logged here; every call returns its default.</summary>
    class Proxy<T> : DispatchProxy where T : class
    {
        public static readonly T Instance = Create<T, Proxy<T>>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.ReturnType is { IsValueType: true } type && type != typeof(void)
                ? Activator.CreateInstance(type)
                : null;
    }
}
