namespace ZoneEngine_New.Tests;

using System;
using System.IO;
using AORebirth.Core.GameData;
using AORebirth.Enums;
using LostEden.Protocol.GameData;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ZoneEngine_New.Core.Ai;
using ZoneEngine_New.Core.Characters;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.NpcScripts;
using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Live report (Lost Eden, 2026-11-23): Extended Line of Credit cast on an Alien Coccoon took it from 200 health to
/// its full 653. The drain never kills, so the cocoon sits on the caster's hate until its brain resets, and a reset
/// heals to MaxHealth; the cocoon's script has to put it back at the 200 it spawned with.
/// </summary>
[TestClass]
public sealed class AlienCoccoonResetHealthTests
{
    const int CoccoonMaxHealth = 653;
    const int CoccoonHealth = 200;

    [TestMethod]
    [DataRow("AOCO")]
    [DataRow("ACCN")]
    public void CoccoonResetAfterAHostileNanoStaysAt200Health(string hash)
    {
        NpcScriptCatalog scripts = PrivateScripts();
        var npc = new NpcCharacter(new Identity { Type = IdentityType.SimpleChar, Instance = 51001 }, new Items())
        {
            SpawnHash = hash
        };
        npc.Stats.Set(CharacterStat.MaxHealth, CoccoonMaxHealth);
        npc.Stats.Set(CharacterStat.Health, CoccoonMaxHealth);

        Assert.IsTrue(scripts.TryAttach(npc, 0, null, null), hash + " binds the cocoon script.");
        npc.Scripts!.Tick(Environment.TickCount64);
        Assert.AreEqual(CoccoonHealth, npc.Stats.GetOrZero(CharacterStat.Health), "Spawned at 200.");

        // The drain lands: hate on the caster, no damage. The cocoon cannot answer it, and its brain resets.
        NpcBrain brain = NpcBrain.Create(npc, new Vector3(0, 0, 0));
        brain.AddThreat(new Identity { Type = IdentityType.SimpleChar, Instance = 4245 }, 10f);
        brain.ResetOutOfCombat();
        npc.Scripts.Tick(Environment.TickCount64);

        Assert.AreEqual(CoccoonHealth, npc.Stats.GetOrZero(CharacterStat.Health), "A reset must not heal the cocoon to full.");
        Assert.AreEqual(CoccoonMaxHealth, npc.Stats.GetOrZero(CharacterStat.MaxHealth));

        // A second reset (cast again) holds the same.
        brain.AddThreat(new Identity { Type = IdentityType.SimpleChar, Instance = 4245 }, 10f);
        brain.ResetOutOfCombat();
        npc.Scripts.Tick(Environment.TickCount64);
        Assert.AreEqual(CoccoonHealth, npc.Stats.GetOrZero(CharacterStat.Health));
    }

    static NpcScriptCatalog PrivateScripts()
    {
        string? root = Environment.GetEnvironmentVariable("AO_REBIRTH_GAMEDATA_PATH");
        if (string.IsNullOrWhiteSpace(root)
            || !File.Exists(Path.Combine(root, NpcScriptCatalog.DirectoryName, "Mothership_AlienCoccoon.json")))
            Assert.Inconclusive("Needs the private GameData with the Mothership cocoon script (AO_REBIRTH_GAMEDATA_PATH).");

        var data = new GameDataStore(new Logger(), null, root);
        var scripts = new NpcScriptCatalog(data, new Logger());
        foreach (string problem in scripts.Result.Problems)
            Assert.IsFalse(problem.Contains("Mothership_AlienCoccoon", StringComparison.Ordinal), problem);
        return scripts;
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
        int _next = 700000;

        public Item Create(int lowId, int highId, int quality, ItemSource source, int stackCount = 1, int instanceId = 0,
            Identity? identity = null, byte[]? statsBlob = null) => new()
            {
                LowId = lowId, HighId = highId, Quality = quality, Source = source, StackCount = stackCount,
                InstanceId = instanceId, Identity = identity ?? Identity.None, Definition = CreateTemplate(lowId, highId, quality)
            };

        public Item CreateWithNewInstance(int lowId, int highId, int quality, ItemSource source, int stackCount = 1)
        {
            Item item = Create(lowId, highId, quality, source, stackCount);
            item.AssignInstanceId(++_next);
            return item;
        }

        public ItemTemplate CreateTemplate(int lowId, int highId, int quality) => new() { Id = lowId, Quality = quality };

        public bool TryFromInstanceRecord(ItemInstanceRecord row, out Item item) => throw new NotSupportedException();
    }
}
