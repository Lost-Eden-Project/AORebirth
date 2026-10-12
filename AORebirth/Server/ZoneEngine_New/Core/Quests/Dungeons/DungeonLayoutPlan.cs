namespace ZoneEngine_New.Core.Quests.Dungeons;

/// <summary>Generation choices frozen in the existing accepted-quest JSON. Changes to the algorithm require a new version.</summary>
public sealed class DungeonLayoutPlan
{
    public int StyleId { get; set; }
    public int FloorCount { get; set; }
    public int FloorSize { get; set; }
    public int FloorHeight { get; set; }
    public bool DescendingFloors { get; set; }
    public bool BossFloor { get; set; }
    public int LockedDoorChancePercent { get; set; }

    public bool IsValid => StyleId > 0 && FloorCount is >= 1 and <= 8 && FloorSize is >= 3 and <= 40
        && FloorHeight is > 0 and <= 255 && LockedDoorChancePercent is >= 0 and <= 100
        && (!BossFloor || FloorCount >= 2);
}
