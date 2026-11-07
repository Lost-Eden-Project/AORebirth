namespace ZoneEngine_New.Core.Quests.Dungeons;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using AORebirth.Core.GameData;
using AORebirth.DungeonGenerator;
using AORebirth.World.Collision;
using SmokeLounge.AOtomation.Messaging.GameData;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;

/// <summary>A place a chest can stand: a style district spawn point in an eligible room that nothing else uses.</summary>
public sealed record MissionChestSite(int RoomIndex, AORebirth.Core.Vector.Vector3 Position);

/// <summary>
/// Chest placement on the style's authored district spawn points. The points are on the floor and reachable by
/// construction, the same way the NPC population is placed, so no geometry probing is needed. Everything here is a
/// pure function of the layout seed, so a rebuilt dungeon puts its chests back where they were.
/// </summary>
public static class MissionChestPlacement
{
    /// <summary>Rooms a chest may stand in: not the entrance, start, main hall or a floor-transition room, and not a room the policy excludes by name.</summary>
    public static HashSet<int> EligibleRooms(DungeonWorldLayout dungeon, DungeonLayout layout, string gameDataRoot, MissionChestPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(dungeon);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(policy);
        StyleRoomSet styles = StyleRoomSet.Load(gameDataRoot, layout.Generator.Style);
        var shapes = new Dictionary<int, RoomShape>();
        foreach (RoomShape shape in styles.All)
            shapes[shape.RoomId] = shape;
        var transitions = new HashSet<int>();
        foreach (var link in layout.FloorTransitions)
        {
            transitions.Add(link.From.RoomIndex);
            transitions.Add(link.To.RoomIndex);
        }
        string[] excluded = policy.Placement?.ExcludedRoomNameParts ?? [];
        var eligible = new HashSet<int>();
        foreach (DungeonRoomPlacement placed in dungeon.Placements)
        {
            if (!shapes.TryGetValue(placed.Template.InstanceId, out RoomShape? shape)
                || shape.Role is RoomRole.Entrance or RoomRole.Start || styles.IsMainHall(shape) || transitions.Contains(placed.Index))
                continue;
            if (excluded.Any(part => shape.Name.Contains(part, StringComparison.OrdinalIgnoreCase)))
                continue;
            eligible.Add(placed.Index);
        }
        return eligible;
    }

    /// <summary>
    /// Picks the chests' sites: the policy's count from the number of eligible rooms, spread one per room before any
    /// room gets a second, in the seeded <paramref name="random"/>'s order.
    /// </summary>
    public static IReadOnlyList<MissionChestSite> Plan(IReadOnlyList<MissionChestSite> sites, MissionChestPolicy policy, Random random)
    {
        ArgumentNullException.ThrowIfNull(sites);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(random);
        var byRoom = new List<List<MissionChestSite>>();
        var roomOrder = new Dictionary<int, List<MissionChestSite>>();
        foreach (MissionChestSite site in sites)
        {
            if (!roomOrder.TryGetValue(site.RoomIndex, out List<MissionChestSite>? room))
            {
                room = [];
                roomOrder[site.RoomIndex] = room;
                byRoom.Add(room);
            }
            room.Add(site);
        }
        var result = new List<MissionChestSite>();
        int wanted = policy.RollChestCount(byRoom.Count, random);
        if (wanted <= 0 || byRoom.Count == 0)
            return result;
        random.Shuffle(CollectionsMarshal.AsSpan(byRoom));
        foreach (List<MissionChestSite> room in byRoom)
            random.Shuffle(CollectionsMarshal.AsSpan(room));
        for (int pass = 0; result.Count < wanted; pass++)
        {
            bool any = false;
            foreach (List<MissionChestSite> room in byRoom)
            {
                if (pass >= room.Count)
                    continue;
                result.Add(room[pass]);
                any = true;
                if (result.Count >= wanted)
                    break;
            }
            if (!any)
                break;
        }
        return result;
    }

    /// <summary>The chest dynel's collision sphere from its spawn definition's template, mesh and scale.</summary>
    public static bool TryGetChestSphere(IGameData gameData, IItemTemplateCatalog templates,
        MissionChestPolicy policy, out CollisionSphereData sphere)
    {
        sphere = null!;
        ChestSpawnDefinition? appearance = policy.SpawnDefinition;
        if (appearance == null)
            return false;
        appearance.Validate();
        uint rawId = appearance.Stats.Single(stat => stat.StatId == (int)CharacterStat.StaticInstance).Value;
        if (rawId == 0 || rawId > int.MaxValue || !templates.TryGet((int)rawId, out ItemTemplate template))
            return false;
        int Stat(CharacterStat stat, int fallback)
        {
            ChestSpawnStat? placed = appearance.Stats.FirstOrDefault(value => value.StatId == (int)stat);
            return placed != null ? unchecked((int)placed.Value)
                : template.Stats.TryGetValue(stat, out int value) ? value : fallback;
        }
        if (!gameData.TryGetDynelCollisionSphere((int)rawId, Stat(CharacterStat.Mesh, 0), Stat(CharacterStat.Scale, 100),
                out float radius, out float centerY)
            || !float.IsFinite(radius) || !float.IsFinite(centerY) || radius <= 0 || centerY <= 0)
            return false;
        sphere = new CollisionSphereData { Radius = radius, CenterY = centerY };
        return true;
    }
}
