namespace ZoneEngine_New.Core.Quests.Dungeons;

/// <summary>Editable item references in the existing dungeon generation policy.</summary>
public sealed class DungeonFindItemPolicy
{
    public string[] ItemHashes { get; set; } = [];
    public int Quality { get; set; }
    public int QuestInstanceStat { get; set; }
    public uint? WorldFlags { get; set; }
}

/// <summary>Existing item-catalog references for a mission tool and its world target.</summary>
public sealed class DungeonRepairMachinePolicy
{
    public DungeonFindItemPolicy? Source { get; set; }
    public DungeonFindItemPolicy? Target { get; set; }
}

/// <summary>Objective candidates resolved through the existing NPC catalog.</summary>
public sealed class DungeonNpcObjectivePolicy
{
    public string[] NpcHashes { get; set; } = [];
    public string[] FindPersonNpcHashes { get; set; } = [];
    public string[] FindPersonBossModifierHashes { get; set; } = [];
}
