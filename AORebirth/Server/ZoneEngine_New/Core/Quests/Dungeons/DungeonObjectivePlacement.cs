namespace ZoneEngine_New.Core.Quests.Dungeons;

using System;
using System.Collections.Generic;
using System.Linq;
using AORebirth.World.Collision;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Playfield;
using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>
/// Chooses a reproducible objective point from the original district positions in connected placed rooms: the boss
/// room when the layout reserves one (a multi-floor dungeon's top floor), else the room farthest from the entrance, or
/// one of the <see cref="DungeonLayout.ObjectiveRoomChoices"/> farthest.
/// </summary>
internal static class DungeonObjectivePlacement
{
    internal static bool TrySelect(string gameDataRoot, DungeonLayout layout, IZoneLogger logger,
        out int roomIndex, out Vector3 position)
    {
        var dungeon = DungeonPlayfieldBinder.TryBuild(gameDataRoot, 0, layout.Generator, logger);
        if (dungeon == null)
        {
            roomIndex = -1;
            position = new Vector3();
            return false;
        }
        return TrySelect(dungeon, gameDataRoot, layout, logger, out roomIndex, out position);
    }

    /// <summary>
    /// The same choice on geometry the caller already built: it depends only on the layout's seed and the style's
    /// districts, so a dungeon derives its objective point when it builds instead of freezing it at acceptance.
    /// </summary>
    internal static bool TrySelect(DungeonWorldLayout dungeon, string gameDataRoot, DungeonLayout layout, IZoneLogger logger,
        out int roomIndex, out Vector3 position)
    {
        roomIndex = -1;
        position = new Vector3();
        var districts = QuestDungeonPlayfield.LoadStyleDistricts(gameDataRoot, layout.Generator.Style, logger);
        if (districts?.Districts == null)
            return false;

        int[] distance = RoomDistances(dungeon, layout);
        var byIndex = new Dictionary<int, AORebirth.Core.GameData.PlayfieldDistrictEntry>();
        foreach (var district in districts.Districts)
            if (district != null)
                byIndex[district.DistrictIndex] = district;
        var candidates = new List<(int Room, int Distance, System.Numerics.Vector3 Position)>();
        foreach (var placement in dungeon.Placements)
        {
            if (distance[placement.Index] <= 0)
                continue;
            int zone = placement.Template.InstanceId;
            int districtIndex = districts.ZoneToDistrictMap is { } map && zone >= 0 && zone < map.Length ? map[zone] : zone;
            if (!byIndex.TryGetValue(districtIndex, out var district))
                continue;
            foreach (var point in district.SpawnPoints ?? [])
            {
                if (point?.Position is not { Length: >= 3 } raw)
                    continue;
                var placed = placement.TransformTemplatePoint(new System.Numerics.Vector3(raw[0], raw[1], raw[2]));
                if (float.IsFinite(placed.X) && float.IsFinite(placed.Y) && float.IsFinite(placed.Z))
                    candidates.Add((placement.Index, distance[placement.Index], placed));
            }
        }
        if (candidates.Count == 0)
            return false;
        // The boss room is one lift past its floor's departure room, so a long wing on a lower floor can be more hops
        // from the entrance; the objective still belongs on the boss floor.
        (int Room, int Distance, System.Numerics.Vector3 Position)[] choices;
        if (layout.BossRoomIndex is int bossRoom && candidates.Any(row => row.Room == bossRoom))
            choices = candidates.Where(row => row.Room == bossRoom).ToArray();
        else
        {
            if (layout.BossRoomIndex is int unreachable)
                logger.Warn("Dungeon boss room " + unreachable + " has no reachable spawn point; objective falls back to the farthest room");
            if (layout.ObjectiveRoomChoices > 1)
                return SelectAmongFarthest(candidates, layout, out roomIndex, out position);
            int farthest = candidates.Max(row => row.Distance);
            choices = candidates.Where(row => row.Distance == farthest).ToArray();
        }
        var selected = choices[new Random(unchecked(layout.Seed * 15485863)).Next(choices.Length)];
        roomIndex = selected.Room;
        position = new Vector3(selected.Position.X, selected.Position.Y, selected.Position.Z);
        return true;
    }

    /// <summary>
    /// One of the <see cref="DungeonLayout.ObjectiveRoomChoices"/> rooms farthest from the entrance (ties in room order),
    /// then one of its points, both from the layout seed.
    /// </summary>
    static bool SelectAmongFarthest(List<(int Room, int Distance, System.Numerics.Vector3 Position)> candidates, DungeonLayout layout,
        out int roomIndex, out Vector3 position)
    {
        int[] rooms = candidates.Select(row => (row.Room, row.Distance)).Distinct()
            .OrderByDescending(row => row.Distance).ThenBy(row => row.Room)
            .Take(layout.ObjectiveRoomChoices).Select(row => row.Room).ToArray();
        var random = new Random(unchecked(layout.Seed * 15485863));
        int room = rooms[random.Next(rooms.Length)];
        var points = candidates.Where(row => row.Room == room).ToArray();
        var selected = points[random.Next(points.Length)];
        roomIndex = selected.Room;
        position = new Vector3(selected.Position.X, selected.Position.Y, selected.Position.Z);
        return true;
    }

    internal static int[] RoomDistances(DungeonWorldLayout dungeon, DungeonLayout layout)
    {
        int count = dungeon.Placements.Count;
        var links = new List<int>[count];
        for (int i = 0; i < count; i++)
            links[i] = [];
        foreach (var door in dungeon.Doors)
        {
            if (door.IsExit || door.Room < 0 || door.Room >= count || door.LinkedRoom < 0 || door.LinkedRoom >= count)
                continue;
            links[door.Room].Add(door.LinkedRoom);
            links[door.LinkedRoom].Add(door.Room);
        }
        foreach (var transition in layout.FloorTransitions)
        {
            links[transition.From.RoomIndex].Add(transition.To.RoomIndex);
            links[transition.To.RoomIndex].Add(transition.From.RoomIndex);
        }
        var distance = new int[count];
        Array.Fill(distance, -1);
        if (count == 0)
            return distance;
        distance[0] = 0;
        var queue = new Queue<int>();
        queue.Enqueue(0);
        while (queue.Count > 0)
        {
            int room = queue.Dequeue();
            foreach (int next in links[room])
            {
                if (distance[next] >= 0)
                    continue;
                distance[next] = distance[room] + 1;
                queue.Enqueue(next);
            }
        }
        return distance;
    }
}
