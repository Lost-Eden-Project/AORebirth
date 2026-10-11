namespace ZoneEngine_New.Core.Quests.Dungeons;

using System;
using System.Collections.Generic;
using System.Linq;
using AORebirth.World.Collision;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Playfield;
using Vector3 = AORebirth.Core.Vector.Vector3;

/// <summary>Chooses a reproducible objective point from the original district positions in connected placed rooms.</summary>
internal static class DungeonObjectivePlacement
{
    internal static bool TrySelect(string gameDataRoot, DungeonLayout layout, IZoneLogger logger,
        out int roomIndex, out Vector3 position)
    {
        roomIndex = -1;
        position = new Vector3();
        var dungeon = DungeonPlayfieldBinder.TryBuild(gameDataRoot, 0, layout.Generator, logger);
        var districts = QuestDungeonPlayfield.LoadStyleDistricts(gameDataRoot, layout.Generator.Style, logger);
        if (dungeon == null || districts?.Districts == null)
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
        int farthest = candidates.Max(row => row.Distance);
        var choices = candidates.Where(row => row.Distance == farthest).ToArray();
        var selected = choices[new Random(unchecked(layout.Seed * 15485863)).Next(choices.Length)];
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
