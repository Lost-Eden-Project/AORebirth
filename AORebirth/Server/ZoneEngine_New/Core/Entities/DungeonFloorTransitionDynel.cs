namespace ZoneEngine_New.Core.Entities;

using System;
using SmokeLounge.AOtomation.Messaging.GameData;
using ZoneEngine_New.Core.Inventory;
using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>A configured dungeon platform/button using the existing world-use and intrazone teleport mechanics.</summary>
public sealed class DungeonFloorTransitionDynel : StaticDynel
{
    public DungeonFloorTransitionDynel(Identity identity, ItemTemplate template, Vector3 landing)
        : base(identity, template)
    {
        Landing = landing ?? throw new ArgumentNullException(nameof(landing));
        Stats.Set(CharacterStat.ACGItemLevel, template.Quality);
        Stats.Set(CharacterStat.MultipleCount, Math.Max(1, template.MultipleCount));
    }

    public Vector3 Landing { get; }

    protected override bool DelaysUse => false;

    /// <summary>
    /// The retail lift spawn carries only these six stats (capture_mangohip_lifts, Platform 1 and Button packets):
    /// the template's flags and identity, not its mesh/placement/animation stats.
    /// </summary>
    protected override GameTuple<CharacterStat, uint>[] BuildStats()
    {
        int flags = Template.Stats.TryGetValue(CharacterStat.Flags, out int templateFlags) ? templateFlags : 0;
        return
        [
            Stat(CharacterStat.Flags, flags),
            Stat(CharacterStat.StaticInstance, Template.Id),
            Stat(CharacterStat.ACGItemLevel, Stats.Get(CharacterStat.ACGItemLevel)),
            Stat(CharacterStat.ACGItemTemplateID, Template.Id),
            Stat(CharacterStat.ACGItemTemplateID2, Template.Id),
            Stat(CharacterStat.MultipleCount, Stats.Get(CharacterStat.MultipleCount))
        ];
    }

    static GameTuple<CharacterStat, uint> Stat(CharacterStat stat, int value)
        => new() { Value1 = stat, Value2 = (uint)value };

    protected override bool OnUse(Player player)
    {
        if (player.IsDead || player.IsPersistenceQuarantined || player.Session == null
            || Playfield == null || !ReferenceEquals(player.Playfield, Playfield)
            || !CanBeginUse(player)) return false;
        player.TeleportWithinPlayfield(Landing);
        return true;
    }
}
