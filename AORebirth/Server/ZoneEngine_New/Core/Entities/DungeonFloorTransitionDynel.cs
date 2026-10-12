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

    protected override bool OnUse(Player player)
    {
        if (player.IsDead || player.IsPersistenceQuarantined || player.Session == null
            || Playfield == null || !ReferenceEquals(player.Playfield, Playfield)
            || !CanBeginUse(player)) return false;
        player.TeleportWithinPlayfield(Landing);
        return true;
    }
}
