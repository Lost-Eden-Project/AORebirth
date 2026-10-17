namespace ZoneEngine_New.Core.Quests.Dungeons;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using AORebirth.Core.GameData;
using AORebirth.DungeonGenerator;
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
        readonly List<int> _chosen = [];
        readonly HashSet<int> _requiredAnchors = [];
        readonly Dictionary<int, HashSet<int>> _roomLinks = new();
        readonly Dictionary<CollisionTriangleMesh, (Vector3 Min, Vector3 Max)> _meshBounds = new();
        readonly List<(Vector3 Source, int Landing)> _floorTravel = [];
        int _start = -1;

        internal PlacementRun(DungeonWorldLayout dungeon, DungeonLayout layout, PlayfieldWorldSimulation world,
            IGameData data, IReadOnlyList<Vector3> exclusions, MissionChestPlacementPolicy policy,
            CollisionSphereData chest, CollisionSphereData player, Random random)
        {
            _dungeon = dungeon; _layout = layout; _world = world; _data = data; _exclusions = exclusions;
            _policy = policy; _chest = chest; _player = player; _random = random;
            foreach (CollisionTriangleMesh mesh in dungeon.Collision.SurfaceMeshes)
            {
                Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
                foreach (Vector3 vertex in mesh.Vertices)
                {
                    min = Vector3.Min(min, vertex);
                    max = Vector3.Max(max, vertex);
                }
                _meshBounds[mesh] = (min, max);
            }
        }

        internal IReadOnlyList<MissionChestPosition> Select(int wanted)
        {
            AddAnchors();
            if (_start < 0)
                return [];
            AddWalkAnchors();
            AddCandidates();
            bool[] initial = Flood();
            int[] candidates = Enumerable.Range(0, _nodes.Count)
                .Where(index => _nodes[index].Candidate && initial[_nodes[index].Approach]).ToArray();
            _random.Shuffle(candidates);
            var rooms = candidates.GroupBy(index => _nodes[index].Room)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(index => _nodes[index].Corner).ToArray());
            foreach (var pair in _dungeon.RoomLinks)
                foreach (int linked in pair.Value)
                    RoomLink(pair.Key, linked);
            foreach (var transition in _layout.FloorTransitions)
                RoomLink(transition.From.RoomIndex, transition.To.RoomIndex);
            var result = new List<MissionChestPosition>();
            var selectedRooms = new List<int>();
            var floorCounts = new Dictionary<int, int>();
            var distances = new List<Dictionary<int, int>>();
            while (rooms.Count > 0 && result.Count < wanted)
            {
                // Random first room / tie breaks, then spread over floors and the existing room-link graph.
                int room = rooms.Keys.OrderBy(room => selectedRooms.Any(other =>
                        _roomLinks.TryGetValue(room, out var links) && links.Contains(other)))
                    .ThenBy(room => floorCounts.GetValueOrDefault(Floor(room)))
                    .ThenByDescending(room => distances.Count == 0 ? 0 : distances.Min(map => map.GetValueOrDefault(room, 0)))
                    .ThenByDescending(room => _selected.Count == 0 ? 0 : _selected.Min(point =>
                        Vector3.DistanceSquared(point, _nodes[rooms[room][0]].Point)))
                    .ThenBy(_ => _random.Next()).First();
                int[] options = rooms[room];
                rooms.Remove(room); // A room is considered once, even when every candidate is rejected.
                foreach (int index in options)
                {
                    Node node = _nodes[index];
                    if (_selected.Any(point => Near(point, node.Point,
                            MathF.Max(_policy.ChestClearance, 2 * _chest.Radius + _policy.CollisionPadding))))
                        continue;
                    _selected.Add(node.Point);
                    _chosen.Add(index);
                    bool[] reachable = Flood();
                    bool routesClear = _requiredAnchors.All(anchor => !initial[anchor] || reachable[anchor]);
                    if (!routesClear || !_chosen.All(chosen => HasApproach(chosen, reachable)))
                    {
                        _selected.RemoveAt(_selected.Count - 1);
                        _chosen.RemoveAt(_chosen.Count - 1);
                        continue;
                    }
                    int yaw = ((int)MathF.Round(MathF.Atan2(node.Inward.X, node.Inward.Z) * 180 / MathF.PI) + 360) % 360;
                    result.Add(new MissionChestPosition(room, node.Point.X, node.Point.Y, node.Point.Z, yaw));
                    selectedRooms.Add(room);
                    floorCounts[Floor(room)] = floorCounts.GetValueOrDefault(Floor(room)) + 1;
                    distances.Add(RoomDistances(room));
                    break;
                }
            }
            return result;
        }

        int Floor(int room) => _layout.Generator.Rooms[room].Floor;

        void RoomLink(int first, int second)
        {
            if (!_roomLinks.TryGetValue(first, out var links))
            {
                _roomLinks[first] = links = [];
            }
            links.Add(second);
            if (!_roomLinks.TryGetValue(second, out links))
            {
                _roomLinks[second] = links = [];
            }
            links.Add(first);
        }

        Dictionary<int, int> RoomDistances(int start)
        {
            var distances = new Dictionary<int, int> { [start] = 0 };
            var queue = new Queue<int>();
            queue.Enqueue(start);
            while (queue.TryDequeue(out int room))
                if (_roomLinks.TryGetValue(room, out var links))
                {
                    foreach (int next in links)
                        if (distances.TryAdd(next, distances[room] + 1))
                        {
                            queue.Enqueue(next);
                        }
                }
            return distances;
        }

        void AddAnchors()
        {
            _start = Add(0, ToNumerics(_layout.Spawn));
            if (_start >= 0)
            {
                _requiredAnchors.Add(_start);
            }
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
                int[] first = DoorApproaches(door.Room, door.Position, offset);
                int[] second = DoorApproaches(door.LinkedRoom, door.Position, offset);
                foreach (int a in first)
                    foreach (int b in second)
                        Link(a, b, transition: false);
                foreach (int node in first.Concat(second))
                {
                    _requiredAnchors.Add(node);
                    _doorPoints.Add(new Vector3(door.Position.X, _nodes[node].Point.Y, door.Position.Z));
                }
            }
            foreach (var transition in _layout.FloorTransitions)
            {
                _doorPoints.Add(transition.From.Position);
                _doorPoints.Add(transition.To.Position);
                int from = Landing(transition.From.RoomIndex, transition.From.Position);
                int to = Landing(transition.To.RoomIndex, transition.To.Position);
                // Runtime platforms are used from interaction range and land at the opposite endpoint.
                // Never substitute a nearby destination approach for that supported landing.
                if (to >= 0)
                {
                    _floorTravel.Add((transition.From.Position, to));
                }
                if (from >= 0)
                {
                    _floorTravel.Add((transition.To.Position, from));
                }
                if (from >= 0)
                {
                    _requiredAnchors.Add(from);
                }
                if (to >= 0)
                {
                    _requiredAnchors.Add(to);
                }
            }
        }

        int Landing(int room, Vector3 target)
            => Project(target, out Vector3 supported) && MathF.Abs(supported.Y - target.Y) <= _policy.MaxWalkStep
                ? Add(room, supported) : -1;

        void AddWalkAnchors()
        {
            // Keep the existing sampled connectivity check useful in bent rooms and halls. These
            // supported interior points are walking anchors only; they can never become chest positions.
            foreach (var group in _dungeon.Collision.SurfaceMeshes.Where(mesh =>
                         mesh.Source?.StartsWith("ground-room", StringComparison.Ordinal) == true && mesh.CellId.HasValue)
                         .GroupBy(mesh => mesh.CellId!.Value))
            {
                var triangles = group.SelectMany(mesh => mesh.Triangles.Select(triangle =>
                    (A: mesh.Vertices[triangle.A], B: mesh.Vertices[triangle.B], C: mesh.Vertices[triangle.C]))).ToArray();
                int added = 0;
                for (int attempt = 0; triangles.Length > 0 && attempt < _policy.CandidateAttemptsPerRoom
                     && added < _policy.CandidatesPerRoom * 5; attempt++)
                {
                    var triangle = triangles[_random.Next(triangles.Length)];
                    float u = (float)_random.NextDouble(), v = (float)_random.NextDouble();
                    if (u + v > 1) { u = 1 - u; v = 1 - v; }
                    Vector3 point = triangle.A + u * (triangle.B - triangle.A) + v * (triangle.C - triangle.A);
                    if (Add(group.Key, point) >= 0)
                    {
                        added++;
                    }
                }
            }
        }

        int[] DoorApproaches(int room, Vector3 position, Vector3 offset)
        {
            if (room < 0 || room >= _dungeon.Rooms.Count)
                return [];
            var result = new List<int>();
            Vector3 side = Vector3.Normalize(new Vector3(offset.Z, 0, -offset.X)) * (2 * _player.Radius);
            foreach (float depth in new[] { 0.5f, 1f, 2f })
                foreach (float sign in new[] { -1f, 1f })
                    foreach (float lateral in new[] { 0f, -1f, 1f })
                    {
                        Vector3 source = position + offset * depth * sign + side * lateral;
                        if (!TryOwnFloor(room, source, out Vector3 ground, checkHeight: false) || !Project(ground, out Vector3 supported))
                            continue;
                        int node = Add(room, supported);
                        if (node >= 0)
                        {
                            result.Add(node);
                        }
                    }
            return result.ToArray();
        }

        void AddCandidates()
        {
            StyleRoomSet styles = new AORebirth.DungeonGenerator.DungeonGenerator(_data.RootPath).LoadStyle(_layout.Generator.Style);
            var shapes = styles.All.ToDictionary(shape => shape.RoomId);
            var transitions = _layout.FloorTransitions.SelectMany(link => new[] { link.From.RoomIndex, link.To.RoomIndex }).ToHashSet();
            foreach (DungeonRoomPlacement placed in _dungeon.Placements)
            {
                int room = placed.Index;
                if (!shapes.TryGetValue(placed.Template.InstanceId, out RoomShape? shape)
                    || shape.Role is RoomRole.Entrance or RoomRole.Start || styles.IsMainHall(shape)
                    || transitions.Contains(room) || _policy.ExcludedRoomNameParts.Any(part =>
                        shape.Name.Contains(part, StringComparison.OrdinalIgnoreCase)))
                    continue;
                float referenceY = placed.Placed.Y;
                if (_rooms.TryGetValue(room, out List<int>? anchors) && anchors.Count > 0)
                {
                    float[] heights = anchors.Select(index => _nodes[index].Point.Y).Order().ToArray();
                    referenceY = heights[heights.Length / 2];
                }
                Wall[] walls = Walls(room, referenceY + _chest.CenterY).ToArray();
                _random.Shuffle(walls);
                if (walls.Length == 0)
                    continue;
                float wallLength = walls.Sum(wall => Vector3.Distance(wall.A, wall.B));
                int added = 0;
                for (int attempt = 0; attempt < _policy.CandidateAttemptsPerRoom && added < _policy.CandidatesPerRoom; attempt++)
                {
                    float roll = (float)_random.NextDouble() * wallLength;
                    Wall wall = walls[^1];
                    foreach (Wall section in walls)
                    {
                        roll -= Vector3.Distance(section.A, section.B);
                        if (roll > 0)
                            continue;
                        wall = section;
                        break;
                    }
                    float fraction = (float)_random.NextDouble();
                    // Bias towards section ends (corners), with collision/side clearance deciding suitability.
                    fraction = attempt % 3 == 0 ? fraction : (attempt % 2 == 0 ? fraction * fraction : 1 - fraction * fraction);
                    Vector3 contact = Vector3.Lerp(wall.A, wall.B, fraction);
                    foreach (Vector3 inward in new[] { wall.Inward, -wall.Inward })
                    {
                        float offset = _chest.Radius + _policy.CollisionPadding * (1 + (float)_random.NextDouble());
                        Vector3 point = new(contact.X, referenceY, contact.Z);
                        point += inward * offset;
                        if (!Project(point, out point) || !OwnFloor(room, point) || !Allowed(point)
                            || !Occupies(point, _chest, exact: true) || !WallClear(room, point) || !BoundaryWall(room, point, inward))
                            continue;
                        float approachDistance = _chest.Radius + _player.Radius + 2 * _policy.CollisionPadding;
                        float heading = MathF.Round(MathF.Atan2(inward.X, inward.Z) * 180 / MathF.PI) * MathF.PI / 180;
                        Vector3 facing = new(MathF.Sin(heading), 0, MathF.Cos(heading));
                        Vector3 approach = point + facing * approachDistance;
                        if (!Project(approach, out approach) || MathF.Abs(approach.Y - point.Y) > _policy.MaxWalkStep)
                            continue;
                        Vector3 tangent = new(facing.Z, 0, -facing.X);
                        float width = MathF.Max(_policy.DoorClearance, _chest.Radius + _player.Radius + _policy.CollisionPadding);
                        // Require an open room-side pocket, rather than a narrow strip beside a passage wall.
                        if (!Pocket(approach, point.Y) || !Pocket(approach + facing * width, point.Y)
                            || !Pocket(approach + tangent * width, point.Y) || !Pocket(approach - tangent * width, point.Y))
                            continue;
                        int front = Add(room, approach);
                        if (front < 0)
                            continue;
                        bool corner = walls.Any(other => MathF.Abs(Vector3.Dot(other.Inward, inward)) < 0.7f
                            && DistanceToSegment(point, other.A, other.B) <= 3 * offset
                            && BoundaryWall(room, point, Vector3.Dot(point - other.A, other.Inward) >= 0
                                ? other.Inward : -other.Inward, 3 * offset));
                        if (Add(room, point, candidate: true, facing, front, corner) < 0)
                            continue;
                        added++;
                        break;

                        bool Pocket(Vector3 probe, float height)
                            => Project(probe, out Vector3 supported) && MathF.Abs(supported.Y - height) <= _policy.MaxWalkStep
                                && OwnFloor(room, supported) && Occupies(supported, _player)
                                && CanWalkPoints(approach, supported);
                    }
                }
            }
        }

        bool OwnFloor(int room, Vector3 point, bool checkHeight = true)
            => TryOwnFloor(room, point, out _, checkHeight);

        bool TryOwnFloor(int room, Vector3 point, out Vector3 floor, bool checkHeight = true)
        {
            floor = default;
            // The generated ground triangles follow DCGA's occupied tiles, including nonrectangular rooms.
            foreach (CollisionTriangleMesh mesh in _dungeon.Collision.SurfaceMeshes.Where(mesh =>
                         mesh.CellId == room && mesh.Source?.StartsWith("ground-room", StringComparison.Ordinal) == true))
                foreach (CollisionTriangle triangle in mesh.Triangles)
                {
                    Vector3 a = mesh.Vertices[triangle.A], b = mesh.Vertices[triangle.B], c = mesh.Vertices[triangle.C];
                    Vector2 ab = new(b.X - a.X, b.Z - a.Z), ac = new(c.X - a.X, c.Z - a.Z);
                    Vector2 delta = new(point.X - a.X, point.Z - a.Z);
                    float determinant = ab.X * ac.Y - ab.Y * ac.X;
                    if (MathF.Abs(determinant) < 1e-8f)
                        continue;
                    float u = (delta.X * ac.Y - delta.Y * ac.X) / determinant;
                    float v = (ab.X * delta.Y - ab.Y * delta.X) / determinant;
                    if (u >= -0.001f && v >= -0.001f && u + v <= 1.001f
                        && (!checkHeight || MathF.Abs(point.Y - (a.Y + u * (b.Y - a.Y) + v * (c.Y - a.Y))) <= 0.01f))
                    {
                        floor = new Vector3(point.X, a.Y + u * (b.Y - a.Y) + v * (c.Y - a.Y), point.Z);
                        return true;
                    }
                }
            return false;
        }

        IEnumerable<Wall> Walls(int room, float height)
        {
            foreach (CollisionTriangleMesh mesh in _dungeon.Collision.SurfaceMeshes.Where(mesh => mesh.CellId == room))
                foreach (CollisionTriangle triangle in mesh.Triangles)
                {
                    Vector3 a = mesh.Vertices[triangle.A], b = mesh.Vertices[triangle.B], c = mesh.Vertices[triangle.C];
                    Vector3 normal = Vector3.Cross(b - a, c - a);
                    if (normal.LengthSquared() < 1e-8f || MathF.Abs(Vector3.Normalize(normal).Y) > 0.3f)
                        continue;
                    var cuts = new List<Vector3>(3);
                    Cut(a, b); Cut(b, c); Cut(c, a);
                    if (cuts.Count < 2 || Vector3.DistanceSquared(cuts[0], cuts[1]) < 0.04f)
                        continue;
                    Vector3 inward = Vector3.Normalize(new Vector3(normal.X, 0, normal.Z));
                    yield return new Wall(cuts[0], cuts[1], inward);

                    void Cut(Vector3 first, Vector3 second)
                    {
                        if ((first.Y < height && second.Y < height) || (first.Y > height && second.Y > height)
                            || MathF.Abs(second.Y - first.Y) < 1e-6f)
                            return;
                        Vector3 cut = Vector3.Lerp(first, second, (height - first.Y) / (second.Y - first.Y));
                        if (!cuts.Any(point => Vector3.DistanceSquared(point, cut) < 1e-8f))
                        {
                            cuts.Add(cut);
                        }
                    }
                }
        }

        bool WallClear(int room, Vector3 point)
        {
            Wall[] walls = Walls(room, point.Y + _chest.CenterY).ToArray();
            float distance = walls.Length == 0 ? float.PositiveInfinity : walls.Min(wall => DistanceToSegment(point, wall.A, wall.B));
            return distance >= _chest.Radius + _policy.CollisionPadding - 0.001f
                && distance <= _chest.Radius + 2 * _policy.CollisionPadding + 0.001f;
        }

        bool BoundaryWall(int room, Vector3 point, Vector3 inward, float? maximumDistance = null)
        {
            // A prop's vertical side is not a room wall: require a standing-height continuation and
            // a boundary of this room's occupied floor on the back side. Interior floor remains ineligible.
            float height = point.Y + MathF.Max(2 * _player.CenterY, _chest.CenterY + _chest.Radius);
            if (!Walls(room, height).Any(wall => MathF.Abs(Vector3.Dot(wall.Inward, inward)) > 0.9f
                    && DistanceToSegment(point, wall.A, wall.B) <= (maximumDistance ?? _chest.Radius + 2 * _policy.CollisionPadding + 0.03f)))
                return false;
            float stride = _policy.WalkSampleSpacing;
            float wallDistance = Walls(room, point.Y + _chest.CenterY)
                .Where(wall => MathF.Abs(Vector3.Dot(wall.Inward, inward)) > 0.9f)
                .Select(wall => DistanceToSegment(point, wall.A, wall.B)).DefaultIfEmpty(float.PositiveInfinity).Min();
            if (!float.IsFinite(wallDistance))
                return false;
            DungeonRoomBounds bounds = _dungeon.Rooms[room];
            float span = new Vector2(bounds.Max.X - bounds.Min.X, bounds.Max.Z - bounds.Min.Z).Length();
            if (!TryGetWalkSampleCount(span, stride, out int steps))
                return false;
            // Continue to the actual occupied-floor perimeter. A single blocked point behind a cabinet
            // is insufficient: any usable room floor before the perimeter identifies an interior prop.
            for (int step = 0; step <= steps; step++)
            {
                Vector3 back = point - inward * (wallDistance + _policy.CollisionPadding + stride * step);
                if (!TryOwnFloor(room, back, out Vector3 supported, checkHeight: false))
                    return true;
                if (Occupies(supported, _player))
                    return false;
            }
            return false;
        }

        static float DistanceToSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector2 start = new(a.X, a.Z), segment = new(b.X - a.X, b.Z - a.Z), delta = new(point.X - a.X, point.Z - a.Z);
            float t = segment.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(delta, segment) / segment.LengthSquared(), 0, 1) : 0;
            return (delta - segment * t).Length();
        }

        bool Allowed(Vector3 point)
            => !Near(point, ToNumerics(_layout.Spawn), _policy.EntryClearance)
                && !_exclusions.Any(other => Near(point, other, _policy.ObjectiveClearance))
                && !_doorPoints.Any(other => Near(point, other, _policy.DoorClearance));

        int Add(int room, Vector3 point, bool candidate = false, Vector3 inward = default, int approach = -1, bool corner = false)
        {
            if (room < 0 || room >= _dungeon.Rooms.Count || !Project(point, out point)
                || !_dungeon.Rooms[room].Contains(point) || (!candidate && !Occupies(point, _player)))
                return -1;
            int id = _nodes.Count;
            _nodes.Add(new Node(room, point, candidate, inward, approach, corner));
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
                foreach (var travel in _floorTravel)
                    if (Vector3.Distance(_nodes[current].Point, travel.Source) <= LootableDynel.OpenRange
                        && _world.HasLineOfSight(ToAo(_nodes[current].Point + Vector3.UnitY * _player.CenterY),
                            ToAo(travel.Source + Vector3.UnitY * _player.CenterY)))
                    {
                        Visit(travel.Landing, transition: true);
                    }

                void Visit(int next, bool transition)
                {
                    if (visited[next] || _nodes[next].Candidate || Blocked(_nodes[next].Point))
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
            result = CanWalkPoints(from, to);
            _walks.Add(key, result);
            return result;
        }

        bool CanWalkPoints(Vector3 from, Vector3 to)
        {
            if (!TryGetWalkSampleCount(Vector3.Distance(from, to), _policy.WalkSampleSpacing, out int steps))
                return false;
            Vector3 previous = from;
            for (int step = 1; step <= steps; step++)
            {
                Vector3 expected = Vector3.Lerp(from, to, step / (float)steps);
                // Follow supported terrain step by step; a curved floor need not follow the endpoint chord's Y.
                expected.Y = previous.Y;
                if (!Project(expected, out Vector3 point) || MathF.Abs(point.Y - previous.Y) > _policy.MaxWalkStep
                    || !ClearWalkSegment(previous, point))
                    return false;
                previous = point;
            }
            return MathF.Abs(previous.Y - to.Y) <= _policy.MaxWalkStep;
        }


        bool HasApproach(int candidate, bool[] reachable)
        {
            Node node = _nodes[candidate];
            Vector3 chest = node.Point;
            Vector3 centre = chest + Vector3.UnitY * _chest.CenterY;
            Vector3 point = _nodes[node.Approach].Point;
            return reachable[node.Approach] && Vector3.Distance(point, chest) <= LootableDynel.OpenRange
                && _world.HasLineOfSight(ToAo(point + Vector3.UnitY * _player.CenterY), ToAo(centre));
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

        bool Occupies(Vector3 feet, CollisionSphereData sphere, bool exact = false)
        {
            float radius = sphere.Radius + _policy.CollisionPadding;
            Vector3 centre = feet + Vector3.UnitY * (sphere.CenterY + 2 * _policy.CollisionPadding);
            // Probe the occupied sphere itself; sweeping its centre by another radius would double wall clearance.
            foreach (Vector3 axis in new[] { Vector3.UnitX, Vector3.UnitZ })
                if (!CastClear(centre - axis * _policy.CollisionPadding,
                        centre + axis * MathF.Max(0.001f, _policy.CollisionPadding), radius))
                    return false;
            foreach (Vector3 offset in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ })
                if (!Project(feet + offset * sphere.Radius, out Vector3 support) || MathF.Abs(support.Y - feet.Y) > _policy.MaxWalkStep)
                    return false;
            return (!exact || StaticSphereClear(feet + Vector3.UnitY * sphere.CenterY, sphere.Radius))
                && StaticSphereClear(centre, radius);
        }

        bool StaticSphereClear(Vector3 centre, float radius)
        {
            foreach (CollisionTriangleMesh mesh in _dungeon.Collision.SurfaceMeshes)
            {
                var bounds = _meshBounds[mesh];
                if (centre.X < bounds.Min.X - radius || centre.X > bounds.Max.X + radius
                    || centre.Y < bounds.Min.Y - radius || centre.Y > bounds.Max.Y + radius
                    || centre.Z < bounds.Min.Z - radius || centre.Z > bounds.Max.Z + radius)
                    continue;
                foreach (CollisionTriangle triangle in mesh.Triangles)
                {
                    Vector3 a = mesh.Vertices[triangle.A], b = mesh.Vertices[triangle.B], c = mesh.Vertices[triangle.C];
                    Vector3 min = Vector3.Min(a, Vector3.Min(b, c)) - new Vector3(radius);
                    Vector3 max = Vector3.Max(a, Vector3.Max(b, c)) + new Vector3(radius);
                    if (centre.X < min.X || centre.X > max.X || centre.Y < min.Y || centre.Y > max.Y
                        || centre.Z < min.Z || centre.Z > max.Z)
                        continue;
                    float toleranceRadius = MathF.Max(0, radius - 0.001f); // Stable tangency at upper-floor float coordinates.
                    if (Vector3.DistanceSquared(centre, ClosestTrianglePoint(centre, a, b, c)) < toleranceRadius * toleranceRadius)
                        return false;
                }
            }
            return true;
        }

        static Vector3 ClosestTrianglePoint(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = point - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0)
                return a;
            Vector3 bp = point - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3)
                return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0)
                return a + ab * (d1 / (d1 - d3));
            Vector3 cp = point - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6)
                return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0)
                return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
                return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
            float sum = va + vb + vc;
            return MathF.Abs(sum) > 1e-12f ? a + ab * (vb / sum) + ac * (vc / sum) : a;
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

        sealed record Node(int Room, Vector3 Point, bool Candidate, Vector3 Inward, int Approach, bool Corner);
        sealed record Wall(Vector3 A, Vector3 B, Vector3 Inward);
    }

    static AoVector3 ToAo(Vector3 point) => new(point.X, point.Y, point.Z);
    static Vector3 ToNumerics(AoVector3 point) => new(point.xf, point.yf, point.zf);
}
