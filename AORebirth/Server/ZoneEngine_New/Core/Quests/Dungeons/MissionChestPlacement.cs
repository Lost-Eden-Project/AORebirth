namespace ZoneEngine_New.Core.Quests.Dungeons;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using AORebirth.Core.GameData;
using AORebirth.World.Collision;
using SmokeLounge.AOtomation.Messaging.GameData;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.Playfield;
using ZoneEngine_New.Core.WorldSimulation;
using AoVector3 = AORebirth.Core.Vector.Vector3;
using Vector3 = System.Numerics.Vector3;

public sealed record MissionChestPosition(int RoomIndex, float X, float Y, float Z, int YawDegrees);

/// <summary>
/// A bounded placement-time connectivity check, using the existing collision queries and frozen room/floor links.
/// It does not bake, load, or replace runtime navigation. Rejected or unproven positions are never substituted.
/// </summary>
public static class MissionChestPlacement
{
    internal const int MaximumWalkSamplesPerLink = 4096;

    internal static bool TryGetWalkSampleCount(float distance, float spacing, out int count)
    {
        count = 0;
        if (!float.IsFinite(distance) || distance < 0 || !float.IsFinite(spacing) || spacing <= 0)
            return false;
        float required = MathF.Ceiling(distance / spacing);
        if (!float.IsFinite(required) || required > MaximumWalkSamplesPerLink)
            return false;
        count = Math.Max(1, (int)required);
        return true;
    }

    public static IReadOnlyList<MissionChestPosition> Plan(DungeonWorldLayout dungeon, DungeonLayout layout,
        PlayfieldWorldSimulation simulation, IGameData gameData, IItemTemplateCatalog templates,
        IReadOnlyList<Vector3> exclusions, MissionChestPolicy policy, Random random)
    {
        ArgumentNullException.ThrowIfNull(dungeon);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(exclusions);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(random);
        int wanted = policy.RollChestCount(dungeon.Rooms.Count, random);
        if (wanted == 0 || simulation.Surface == null || policy.Placement?.IsValid != true
            || !TryGetChestSphere(gameData, templates, policy, out CollisionSphereData chest)
            || gameData.PlayerCollisionSphere is not { Radius: > 0, CenterY: > 0 } player
            || !float.IsFinite(player.Radius) || !float.IsFinite(player.CenterY))
            return [];
        return new PlacementRun(dungeon, layout, simulation, gameData, exclusions, policy.Placement,
            chest, player, random).Select(wanted);
    }

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

    sealed class PlacementRun
    {
        readonly DungeonWorldLayout _dungeon;
        readonly DungeonLayout _layout;
        readonly PlayfieldWorldSimulation _world;
        readonly IGameData _data;
        readonly IReadOnlyList<Vector3> _exclusions;
        readonly MissionChestPlacementPolicy _policy;
        readonly CollisionSphereData _chest;
        readonly CollisionSphereData _player;
        readonly Random _random;
        readonly List<Node> _nodes = [];
        readonly Dictionary<int, List<int>> _rooms = new();
        readonly Dictionary<int, List<(int Node, bool Transition)>> _links = new();
        readonly Dictionary<long, bool> _walks = new();
        readonly List<Vector3> _doorPoints = [];
        readonly List<Vector3> _selected = [];
        int _start = -1;

        internal PlacementRun(DungeonWorldLayout dungeon, DungeonLayout layout, PlayfieldWorldSimulation world,
            IGameData data, IReadOnlyList<Vector3> exclusions, MissionChestPlacementPolicy policy,
            CollisionSphereData chest, CollisionSphereData player, Random random)
        {
            _dungeon = dungeon; _layout = layout; _world = world; _data = data; _exclusions = exclusions;
            _policy = policy; _chest = chest; _player = player; _random = random;
        }

        internal IReadOnlyList<MissionChestPosition> Select(int wanted)
        {
            AddAnchors();
            if (_start < 0)
                return [];
            AddCandidates();
            bool[] initial = Flood();
            int[] candidates = Enumerable.Range(0, _nodes.Count).Where(index => _nodes[index].Candidate && initial[index]).ToArray();
            _random.Shuffle(candidates);
            var result = new List<MissionChestPosition>();
            foreach (int index in candidates)
            {
                if (result.Count >= wanted)
                    break;
                Node node = _nodes[index];
                if (_selected.Any(point => Near(point, node.Point, MathF.Max(_policy.ChestClearance, 2 * _chest.Radius + _policy.CollisionPadding))))
                    continue;
                _selected.Add(node.Point);
                bool[] reachable = Flood();
                if (!_selected.All(point => HasApproach(point, reachable)))
                {
                    _selected.RemoveAt(_selected.Count - 1);
                    continue;
                }
                result.Add(new MissionChestPosition(node.Room, node.Point.X, node.Point.Y, node.Point.Z, _random.Next(360)));
            }
            return result;
        }

        void AddAnchors()
        {
            _start = Add(0, ToNumerics(_layout.Spawn));
            var districts = QuestDungeonPlayfield.LoadStyleDistricts(_data.RootPath, _layout.Generator.Style, new NLogZoneLogger());
            if (districts?.Districts != null)
            {
                var byIndex = districts.Districts.Where(value => value != null).ToDictionary(value => value.DistrictIndex);
                foreach (DungeonRoomPlacement placed in _dungeon.Placements)
                {
                    int zone = placed.Template.InstanceId;
                    int mapped = districts.ZoneToDistrictMap is { } map && zone >= 0 && zone < map.Length ? map[zone] : zone;
                    if (!byIndex.TryGetValue(mapped, out PlayfieldDistrictEntry? district))
                        continue;
                    foreach (PlayfieldDistrictSpawnPoint point in district.SpawnPoints ?? [])
                        if (point?.Position is { Length: >= 3 } raw)
                            Add(placed.Index, placed.TransformTemplatePoint(new Vector3(raw[0], raw[1], raw[2])));
                }
            }
            foreach (DungeonDoorPlacement door in _dungeon.Doors)
            {
                _doorPoints.Add(door.Position);
                if (door.IsExit)
                    continue;
                float radians = door.YawDegrees * MathF.PI / 180;
                var offset = new Vector3(MathF.Sin(radians), 0, MathF.Cos(radians))
                    * MathF.Max(1, _player.Radius + _policy.CollisionPadding * 2);
                int first = DoorApproach(door.Room, door.Position, offset);
                int second = DoorApproach(door.LinkedRoom, door.Position, offset);
                Link(first, second, transition: false);
            }
            foreach (var transition in _layout.FloorTransitions)
            {
                _doorPoints.Add(transition.From.Position);
                _doorPoints.Add(transition.To.Position);
                Link(Add(transition.From.RoomIndex, transition.From.Position),
                    Add(transition.To.RoomIndex, transition.To.Position), transition: true);
            }
        }

        int DoorApproach(int room, Vector3 position, Vector3 offset)
        {
            if (room < 0 || room >= _dungeon.Rooms.Count)
                return -1;
            Vector3 first = position + offset;
            Vector3 second = position - offset;
            return Add(room, _dungeon.Rooms[room].Contains(first) ? first : second);
        }

        void AddCandidates()
        {
            foreach (DungeonRoomBounds room in _dungeon.Rooms)
            {
                float margin = _chest.Radius + _policy.CollisionPadding;
                float width = room.Max.X - room.Min.X - 2 * margin;
                float depth = room.Max.Z - room.Min.Z - 2 * margin;
                if (width <= 0 || depth <= 0)
                    continue;
                float referenceY = _dungeon.Placements.FirstOrDefault(placed => placed.Index == room.Index)?.Placed.Y ?? room.Min.Y + 1;
                // A cave's template origin can lie below its walkable surface. Existing transformed, floor-checked
                // room anchors supply the elevation; the original origin remains the fallback when there are none.
                if (_rooms.TryGetValue(room.Index, out List<int>? anchors) && anchors.Count > 0)
                {
                    float[] heights = anchors.Select(index => _nodes[index].Point.Y).Order().ToArray();
                    referenceY = heights[heights.Length / 2];
                }
                int added = 0;
                for (int attempt = 0; attempt < _policy.CandidateAttemptsPerRoom && added < _policy.CandidatesPerRoom; attempt++)
                {
                    var point = new Vector3(room.Min.X + margin + (float)_random.NextDouble() * width,
                        referenceY, room.Min.Z + margin + (float)_random.NextDouble() * depth);
                    if (!Project(point, out point) || !room.Contains(point) || !Allowed(point) || !Occupies(point, _chest))
                        continue;
                    int node = Add(room.Index, point, candidate: true);
                    if (node < 0)
                        continue;
                    added++;
                    float approach = _chest.Radius + _player.Radius + 2 * _policy.CollisionPadding;
                    foreach (Vector3 direction in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ })
                        Add(room.Index, point + direction * approach);
                }
            }
        }

        bool Allowed(Vector3 point)
            => !Near(point, ToNumerics(_layout.Spawn), _policy.EntryClearance)
                && !_exclusions.Any(other => Near(point, other, _policy.ObjectiveClearance))
                && !_doorPoints.Any(other => Near(point, other, _policy.DoorClearance));

        int Add(int room, Vector3 point, bool candidate = false)
        {
            if (room < 0 || room >= _dungeon.Rooms.Count || !Project(point, out point)
                || !_dungeon.Rooms[room].Contains(point) || !Occupies(point, _player))
                return -1;
            int id = _nodes.Count;
            _nodes.Add(new Node(room, point, candidate));
            if (!_rooms.TryGetValue(room, out List<int>? nodes))
            {
                nodes = [];
                _rooms.Add(room, nodes);
            }
            nodes.Add(id);
            return id;
        }

        void Link(int first, int second, bool transition)
        {
            if (first < 0 || second < 0)
                return;
            if (!_links.TryGetValue(first, out List<(int, bool)>? from))
            {
                from = [];
                _links.Add(first, from);
            }
            if (!_links.TryGetValue(second, out List<(int, bool)>? to))
            {
                to = [];
                _links.Add(second, to);
            }
            from.Add((second, transition));
            to.Add((first, transition));
        }

        bool[] Flood()
        {
            var visited = new bool[_nodes.Count];
            if (Blocked(_nodes[_start].Point))
                return visited;
            var queue = new Queue<int>();
            visited[_start] = true;
            queue.Enqueue(_start);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (int next in _rooms[_nodes[current].Room])
                    Visit(next, transition: false);
                if (_links.TryGetValue(current, out var links))
                    foreach ((int next, bool transition) in links)
                        Visit(next, transition);

                void Visit(int next, bool transition)
                {
                    if (visited[next] || Blocked(_nodes[next].Point))
                        return;
                    Vector3 a = _nodes[current].Point, b = _nodes[next].Point;
                    if (!transition && (BlockedSegment(a, b) || !CanWalk(current, next)))
                        return;
                    visited[next] = true;
                    queue.Enqueue(next);
                }
            }
            return visited;
        }

        bool CanWalk(int first, int second)
        {
            long key = ((long)Math.Min(first, second) << 32) | (uint)Math.Max(first, second);
            if (_walks.TryGetValue(key, out bool result))
                return result;
            Vector3 from = _nodes[first].Point, to = _nodes[second].Point;
            if (!TryGetWalkSampleCount(Vector3.Distance(from, to), _policy.WalkSampleSpacing, out int steps))
            {
                _walks.Add(key, false);
                return false;
            }
            Vector3 previous = from;
            result = true;
            for (int step = 1; step <= steps; step++)
            {
                Vector3 expected = Vector3.Lerp(from, to, step / (float)steps);
                if (!Project(expected, out Vector3 point) || MathF.Abs(point.Y - expected.Y) > _policy.MaxWalkStep
                    || MathF.Abs(point.Y - previous.Y) > _policy.MaxWalkStep || !ClearWalkSegment(previous, point))
                {
                    result = false;
                    break;
                }
                previous = point;
            }
            _walks.Add(key, result);
            return result;
        }

        bool HasApproach(Vector3 chest, bool[] reachable)
        {
            Vector3 centre = chest + Vector3.UnitY * _chest.CenterY;
            for (int index = 0; index < _nodes.Count; index++)
            {
                Vector3 point = _nodes[index].Point;
                if (reachable[index] && Vector3.Distance(point, chest) <= LootableDynel.OpenRange
                    && _world.HasLineOfSight(ToAo(point + Vector3.UnitY * _player.CenterY), ToAo(centre)))
                    return true;
            }
            return false;
        }

        bool Project(Vector3 source, out Vector3 floor)
        {
            floor = default;
            if (!float.IsFinite(source.X) || !float.IsFinite(source.Y) || !float.IsFinite(source.Z)
                || !_world.TryRaycastDown(ToAo(source + Vector3.UnitY * _policy.FloorProbeLift),
                    _policy.FloorProbeLift + _policy.MaxFloorDrop, out AoVector3 hit))
                return false;
            floor = ToNumerics(hit);
            return true;
        }

        bool Occupies(Vector3 feet, CollisionSphereData sphere)
        {
            float radius = sphere.Radius + _policy.CollisionPadding;
            Vector3 centre = feet + Vector3.UnitY * (sphere.CenterY + 2 * _policy.CollisionPadding);
            foreach (Vector3 axis in new[] { Vector3.UnitX, Vector3.UnitZ })
                if (!CastClear(centre - axis * radius, centre + axis * radius, radius))
                    return false;
            foreach (Vector3 offset in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ })
                if (!Project(feet + offset * sphere.Radius, out Vector3 support) || MathF.Abs(support.Y - feet.Y) > _policy.MaxWalkStep)
                    return false;
            return true;
        }

        bool ClearWalkSegment(Vector3 from, Vector3 to)
            => CastClear(from + Vector3.UnitY * (_player.CenterY + _policy.CollisionPadding),
                to + Vector3.UnitY * (_player.CenterY + _policy.CollisionPadding), _player.Radius);

        bool CastClear(Vector3 from, Vector3 to, float radius)
            => !_world.TrySphereCast(ToAo(from), ToAo(to), radius, out _, out _)
                && !_world.TrySphereCast(ToAo(to), ToAo(from), radius, out _, out _);

        bool Blocked(Vector3 point)
            => _selected.Any(chest => Near(chest, point, _chest.Radius + _player.Radius + _policy.CollisionPadding));

        bool BlockedSegment(Vector3 from, Vector3 to)
        {
            foreach (Vector3 chest in _selected)
            {
                if (MathF.Abs(chest.Y - from.Y) > _chest.Radius * 2 + _policy.MaxWalkStep
                    && MathF.Abs(chest.Y - to.Y) > _chest.Radius * 2 + _policy.MaxWalkStep)
                    continue;
                var a = new Vector2(from.X, from.Z);
                var b = new Vector2(to.X, to.Z);
                var c = new Vector2(chest.X, chest.Z);
                Vector2 segment = b - a;
                float factor = segment.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(c - a, segment) / segment.LengthSquared(), 0, 1) : 0;
                if (Vector2.Distance(c, a + segment * factor) < _chest.Radius + _player.Radius + _policy.CollisionPadding)
                    return true;
            }
            return false;
        }

        bool Near(Vector3 first, Vector3 second, float distance)
            => MathF.Abs(first.Y - second.Y) <= _chest.Radius * 2 + _policy.MaxWalkStep
                && Vector2.DistanceSquared(new Vector2(first.X, first.Z), new Vector2(second.X, second.Z)) < distance * distance;

        sealed record Node(int Room, Vector3 Point, bool Candidate);
    }

    static AoVector3 ToAo(Vector3 point) => new(point.X, point.Y, point.Z);
    static Vector3 ToNumerics(AoVector3 point) => new(point.xf, point.yf, point.zf);
}
