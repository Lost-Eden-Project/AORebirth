// Throwaway probe. "pick <style> <room...>": bakes each room alone (placed at its template facing, so the room-local
// offset needs no rotation) and prints the best lift spot on its navmesh. "check <style> <room> x y z": scores a given
// local offset (to calibrate against captured anchors). "verify <style...>": generates team layouts of 2-5 floors.
using System.Numerics;
using AORebirth.DungeonGenerator;
using AORebirth.World.Collision;
using AORebirth.World.Pathfinding;

string root = @"C:\Users\tyler\OneDrive\Documents\AORebirth-Scripts\GameData";
var generator = new DungeonGenerator(root);
NavMeshBuildSettings settings = NavMeshBuildSettings.Load(
    @"C:\Users\tyler\OneDrive\Documents\AORebirth\AORebirth\Built\Debug\ZoneEngine_New\Config\NavAgent.json");

switch (args[0])
{
    case "pick":
    {
        int style = int.Parse(args[1]);
        StyleRoomSet set = generator.LoadStyle(style);
        foreach (string arg in args.Skip(2))
        {
            RoomShape shape = set.All.Single(r => r.RoomId == int.Parse(arg));
            using var room = new RoomMesh(root, style, shape, settings);
            var best = room.Candidates().OrderByDescending(c => c.Score).FirstOrDefault();
            Console.WriteLine($"{style}/{shape.RoomId} {shape.Name} role={shape.Role} doors={shape.Doors.Count} blocks={shape.FootprintBlocks} " +
                (best == null ? "NO SPOT" : $"local=[{best.Local.X:0.###}, {best.Local.Y:0.###}, {best.Local.Z:0.###}] clear={best.Clearance} doorDist={best.DoorDistance:0.0} score={best.Score:0.0}"));
        }
        break;
    }
    case "check":
    {
        int style = int.Parse(args[1]);
        RoomShape shape = generator.LoadStyle(style).All.Single(r => r.RoomId == int.Parse(args[2]));
        using var room = new RoomMesh(root, style, shape, settings);
        var local = new Vector3(float.Parse(args[3]), float.Parse(args[4]), float.Parse(args[5]));
        Candidate? scored = room.Score(room.Placed + local, 0, 0f);
        Console.WriteLine($"{style}/{shape.RoomId} {shape.Name}: {scored?.ToString() ?? "REJECTED"} {room.Rejected}");
        break;
    }
    case "list":
    {
        int style = int.Parse(args[1]);
        foreach (RoomShape r in generator.LoadStyle(style).All)
            Console.WriteLine($"{r.RoomId} {r.Name} role={r.Role} doors={r.Doors.Count} blocks={r.FootprintBlocks} valid={r.Template.IsAcgSizeValid}");
        break;
    }
    case "verify":
    {
        foreach (int style in args.Skip(1).Select(int.Parse))
        {
            bool descending = generator.LoadStyle(style).Transitions?.DescendingFloors ?? false;
            int ok = 0, failed = 0;
            for (int floors = 2; floors <= 5; floors++)
            {
                for (int seed = 1; seed <= 40; seed++)
                {
                    int size = 4 + (seed % 9);
                    try
                    {
                        DungeonGenerationResult result = generator.Generate(new DungeonGenerationRequest
                        {
                            StyleId = style, Seed = seed * 7919 + floors, FloorCount = floors, FloorSize = size, FloorHeight = 64,
                            BossFloor = true, DescendingFloors = descending, UseFloorTransitions = true,
                            BuildingInstance = unchecked((int)0xC00001F9), Layout = DungeonLayoutKind.Planned
                        });
                        if (!result.Validation.IsValid || result.Transitions.Count != floors - 1 || result.BossRoomIndex == null)
                            throw new InvalidOperationException("invalid: " + result.Validation);
                        ok++;
                        if (seed == 1 && floors == 5)
                            foreach (DungeonFloorTransition t in result.Transitions)
                                Console.WriteLine($"  {style} lift {result.Rooms[t.From.RoomIndex].Shape.Name} -> {result.Rooms[t.To.RoomIndex].Shape.Name} buttons {t.From.ButtonTemplateId}/{t.To.ButtonTemplateId}");
                    }
                    catch (Exception e)
                    {
                        failed++;
                        if (failed <= 3) Console.WriteLine($"  {style} floors={floors} size={size} seed={seed}: {e.GetType().Name} {e.Message}");
                    }
                }
            }
            Console.WriteLine($"style {style}: ok={ok} failed={failed}");
        }
        break;
    }
    case "navcheck":
    {
        // "navcheck <seedsPerFloorCount> <style...>": generates whole team dungeons as the engine does, bakes the
        // engine's dungeon navmesh over them, and checks every platform spot (departure, arrival, boss) is on the
        // walkable mesh, sits on flat clear floor, and is reachable from every doorway of its room.
        int seeds = int.Parse(args[1]);
        foreach (int style in args.Skip(2).Select(int.Parse))
        {
            bool descending = generator.LoadStyle(style).Transitions?.DescendingFloors ?? false;
            int endpoints = 0, bad = 0, layouts = 0;
            var spots = new Dictionary<string, (int Ok, int Bad, float MinClear)>();
            for (int floors = 2; floors <= 5; floors++)
            for (int seed = 1; seed <= seeds; seed++)
            {
                DungeonGenerationResult result = generator.Generate(new DungeonGenerationRequest
                {
                    StyleId = style, Seed = seed * 7919 + floors, FloorCount = floors, FloorSize = 4 + (seed % 9),
                    FloorHeight = 64, BossFloor = true, DescendingFloors = descending, UseFloorTransitions = true,
                    BuildingInstance = unchecked((int)0xC00001F9), Layout = DungeonLayoutKind.Planned
                });
                DungeonWorldLayout layout = DungeonCollisionBuilder.BuildLayout(root, result.Generator);
                using NavMeshPathfinder mesh = NavMeshPathfinder.FromBytes(style,
                    NavMeshFile.ToBytes(NavMeshBaker.Bake(layout.Collision, settings)), settings);
                layouts++;
                foreach (DungeonFloorTransition t in result.Transitions)
                foreach ((DungeonTransitionEndpoint e, string role) in new[] { (t.From, "from"), (t.To, "to") })
                {
                    endpoints++;
                    PlacedRoom room = result.Rooms[e.RoomIndex];
                    string problem = Spot.Check(mesh, layout, e);
                    string key = $"{style}/{room.Shape.RoomId} {room.Shape.Name} ({role})";
                    if (!spots.TryGetValue(key, out var tally))
                        tally = (0, 0, 99f);
                    float clear = MathF.Min(tally.MinClear, Spot.Clearance);
                    spots[key] = problem == "" ? (tally.Ok + 1, tally.Bad, clear) : (tally.Ok, tally.Bad + 1, clear);
                    if (problem != "")
                    {
                        bad++;
                        if (bad <= 8)
                            Console.WriteLine($"  BAD {key} rot={room.Rotation} floors={floors} seed={seed}: {problem}");
                    }
                }
            }
            foreach (var (key, tally) in spots.OrderBy(k => k.Key))
                Console.WriteLine($"  {key}: ok={tally.Ok} bad={tally.Bad} minClear={tally.MinClear:0.0}");
            Console.WriteLine($"style {style}: layouts={layouts} platforms={endpoints} bad={bad}");
        }
        break;
    }
}

static class Spot
{
    public static float Clearance;

    /// <summary>Empty when the spot is usable, else what is wrong with it.</summary>
    public static string Check(NavMeshPathfinder mesh, DungeonWorldLayout layout, DungeonTransitionEndpoint e)
    {
        Vector3 at = e.Position;
        if (!mesh.TrySnapDown(at + new Vector3(0, 1.5f, 0), 3f, out Vector3 snapped))
            return "off mesh (nothing below)";
        float drift = Vector2.Distance(new(at.X, at.Z), new(snapped.X, snapped.Z));
        if (MathF.Abs(snapped.Y - at.Y) > 0.5f || drift > 0.25f)
            return $"off mesh (snapped {drift:0.00} away, dy={snapped.Y - at.Y:0.00})";
        if (!mesh.IsDestinationOnMesh(snapped))
            return "not a walkable destination";
        // Platform footprint: radius of flat walkable floor all round, in half units up to 3.
        Clearance = 0f;
        for (float radius = 0.5f; radius <= 3f; radius += 0.5f)
        {
            bool flat = true;
            for (int k = 0; k < 16 && flat; k++)
            {
                double a = k * Math.PI / 8;
                var p = new Vector3(snapped.X + radius * (float)Math.Cos(a), snapped.Y + 1.5f, snapped.Z + radius * (float)Math.Sin(a));
                flat = mesh.TrySnapDown(p, 3f, out Vector3 s) && MathF.Abs(s.Y - snapped.Y) <= 0.35f;
            }
            if (!flat)
                break;
            Clearance = radius;
        }
        if (Clearance < 1f)
            return $"cramped: flat floor only {Clearance:0.0} round";
        // Reachable on foot from every doorway of the room (both sides of each door are tried; one must reach).
        var doors = layout.Doors.Where(d => d.Room == e.RoomIndex || d.LinkedRoom == e.RoomIndex && !d.IsExit
            || d.IsExit && e.RoomIndex == 0).ToList();
        if (doors.Count == 0)
            return layout.Doors.Count > 0 ? "" : "layout has no doors"; // the boss floor's room stands alone
        int reached = 0;
        foreach (DungeonDoorPlacement door in doors)
        {
            (float dx, float dz) = (MathF.Sin(door.YawDegrees * MathF.PI / 180f), MathF.Cos(door.YawDegrees * MathF.PI / 180f));
            bool ok = false;
            foreach (float side in new[] { 2f, -2f })
            {
                var probe = new Vector3(door.Position.X + dx * side, door.Position.Y + 1.5f, door.Position.Z + dz * side);
                if (mesh.TrySnapDown(probe, 4f, out Vector3 start) && mesh.TryCanReach(start, snapped))
                {
                    ok = true;
                    break;
                }
            }
            if (ok)
                reached++;
        }
        return reached == doors.Count ? "" : $"reached from {reached}/{doors.Count} doors";
    }
}

sealed record Candidate(Vector3 Local, int Clearance, float DoorDistance, double Score);

sealed class RoomMesh : IDisposable
{
    const int Margin = 1;
    readonly NavMeshPathfinder _mesh;
    readonly List<Vector3> _doorInsides = [];
    readonly List<Vector3> _doors = [];
    readonly IReadOnlyList<(int X, int Z)> _cells;
    readonly float _floorY;

    public RoomMesh(string root, int style, RoomShape shape, NavMeshBuildSettings settings)
    {
        int rotation = shape.Template.TemplateFacing & 3;
        (int bx, int bz) = Geometry.RotatedBlocks(shape, rotation);
        int width = bx + 2 * Margin, height = bz + 2 * Margin;
        var spec = new DungeonRoomSpec
        {
            RoomId = (ushort)shape.RoomId, Floor = 0, GridX = Margin,
            GridZ = (byte)(height - Margin - bz), Facing = (byte)rotation
        };
        Placed = DungeonRoomPlacer.Place(shape.Template, spec, 2f, width, height, 0, 0);
        PlayfieldCollisionSet collision = DungeonCollisionBuilder.Build(root, style, [spec], width, height, 64);
        _mesh = NavMeshPathfinder.FromBytes(style, NavMeshFile.ToBytes(NavMeshBaker.Bake(collision, settings)), settings);
        _cells = Geometry.PlaceFootprint(shape, rotation, Margin, Margin);
        foreach (PlacedDoor door in Geometry.PlaceDoors(shape, rotation, Margin, Margin))
        {
            (float dx, float dz) = door.Facing switch { 0 => (0f, 1f), 1 => (1f, 0f), 2 => (0f, -1f), _ => (-1f, 0f) };
            _doors.Add(new Vector3(door.X, Placed.Y, door.Z));
            // From just above the floor band: a lone room's bake also walks its roof.
            if (_mesh.TrySnapDown(new Vector3(door.X - dx * 2.5f, Placed.Y + FloorBand, door.Z - dz * 2.5f), FloorBand + 3f, out Vector3 inside))
                _doorInsides.Add(inside);
        }
        // The room's main floor: the most common door-inside height, else the placed height.
        _floorY = _doorInsides.Count > 0
            ? _doorInsides.GroupBy(p => MathF.Round(p.Y * 2f) / 2f).OrderByDescending(g => g.Count()).First().Average(p => p.Y)
            : Placed.Y;
    }

    /// <summary>Floors sit at most this far above the room's placed height (captured raised boss floor: 2).</summary>
    const float FloorBand = 4f;

    public Vector3 Placed { get; }

    public string? Rejected { get; private set; }

    public IEnumerable<Candidate> Candidates()
    {
        foreach ((int cx, int cz) in _cells)
            for (float x = cx * 10 + 0.5f; x < cx * 10 + 10; x += 1f)
                for (float z = cz * 10 + 0.5f; z < cz * 10 + 10; z += 1f)
                    if (Score(new Vector3(x, Placed.Y, z)) is Candidate c)
                        yield return c;
    }

    public Candidate? Score(Vector3 probe) => Score(probe, MinClearance, MinDoorDistance);

    public static int MinClearance = 2;
    public static float MinDoorDistance = 6f;

    public Candidate? Score(Vector3 probe, int minClearance, float minDoorDistance)
    {
        Rejected = null;
        if (!_mesh.TrySnapDown(probe with { Y = Placed.Y + FloorBand }, FloorBand + 3f, out Vector3 at))
        {
            Rejected = "off mesh";
            return null;
        }
        // Flat walkable ring: the platform needs room around it.
        int clearance = 0;
        for (int radius = 1; radius <= 5; radius++)
        {
            bool flat = true;
            for (int k = 0; k < 16 && flat; k++)
            {
                double a = k * Math.PI / 8;
                var p = new Vector3(at.X + radius * (float)Math.Cos(a), at.Y + 1.5f, at.Z + radius * (float)Math.Sin(a));
                flat = InCells(p) && _mesh.TrySnapDown(p, 3f, out Vector3 s) && MathF.Abs(s.Y - at.Y) < 0.35f;
            }
            if (!flat) break;
            clearance = radius;
        }
        float doorDistance = _doors.Count == 0 ? 99f : _doors.Min(d => Vector2.Distance(new(d.X, d.Z), new(at.X, at.Z)));
        int reached = _doorInsides.Count(inside => _mesh.TryCanReach(at, inside));
        Rejected = $"clear={clearance} doorDist={doorDistance:0.0} reach={reached}/{_doorInsides.Count}/{_doors.Count} y={at.Y - Placed.Y:0.00}";
        if (clearance < minClearance)
            return null;
        // Every doorway walkable from the spot: it stands on the room's main floor, not a ledge or a closed corner.
        if (reached < _doorInsides.Count || _doorInsides.Count < _doors.Count)
            return null;
        if (doorDistance < minDoorDistance)
            return null;
        Rejected = null;
        var centre = new Vector2((float)_cells.Average(c => c.X * 10 + 5), (float)_cells.Average(c => c.Z * 10 + 5));
        double score = Math.Min(clearance, 4) * 10 + Math.Min(doorDistance, 12) + (MathF.Abs(at.Y - _floorY) < 0.5f ? 5 : 0)
            - 0.15 * Vector2.Distance(centre, new(at.X, at.Z));
        var local = at - Placed;
        return new Candidate(local with { Y = local.Y + 0.01f }, clearance, doorDistance, score);
    }

    bool InCells(Vector3 p) => _cells.Contains(((int)MathF.Floor(p.X / 10f), (int)MathF.Floor(p.Z / 10f)));

    public void Dispose() => _mesh.Dispose();
}
