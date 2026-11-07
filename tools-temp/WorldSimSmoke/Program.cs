using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

using AODB.Common.RDBObjects;

using AORebirth.Core.GameData;

using ZoneEngine_New.Core.GameData;
using ZoneEngine_New.Core.Logging;
using ZoneEngine_New.Core.WorldSimulation;

using AoVector3 = AORebirth.Core.Vector.Vector3;

sealed class ConsoleZoneLogger : IZoneLogger
{
    public void Info(string message) => Console.WriteLine("INFO " + message);
    public void Warn(string message) => Console.WriteLine("WARN " + message);
    public void Error(string message) => Console.WriteLine("ERROR " + message);
    public void Error(Exception exception, string message) =>
        Console.WriteLine("ERROR " + message + " :: " + exception.Message);
    public void Debug(string message)
    {
    }

    public IZoneLogger CreateForPlayfield(int playfieldId) => this;
}

readonly struct Pose
{
    public Pose(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public float X { get; }

    public float Y { get; }

    public float Z { get; }

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "({0:F3},{1:F3},{2:F3})", X, Y, Z);
}

static class Program
{
    static readonly Dictionary<int, Pose[]> ClientPoses = new()
    {
        // Live CharDCMove client coordinates (error≈0 samples) from ZoneEngine_NewLog.txt.
        [800] = new[]
        {
            new Pose(668.262f, 72.810f, 544.054f),
            new Pose(673.220f, 73.718f, 520.553f),
            new Pose(661.894f, 67.973f, 586.812f),
            new Pose(655.982f, 66.810f, 640.890f),
            new Pose(660.035f, 66.810f, 611.843f),
            new Pose(662.797f, 70.342f, 578.559f),
        },
        [4310] = new[]
        {
            new Pose(852.290f, 34.405f, 965.133f),
            new Pose(1038.263f, 53.033f, 1034.475f),
        },
    };

    static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        int playfieldId = args.Length > 0
            ? int.Parse(args[0], CultureInfo.InvariantCulture)
            : 800;

        var logger = new ConsoleZoneLogger();
        var store = new GameDataStore(logger);
        DestinationsCatalog.Instance.ConfigureRoot(store.RootPath);

        PlayfieldMetaData? meta = store.GetPlayfieldMetaData(playfieldId);
        PlayfieldGeometryData geometry = store.GetPlayfieldGeometry(playfieldId);

        Console.WriteLine("=== playfield " + playfieldId + " ===");
        Console.WriteLine("root=" + store.RootPath);
        Console.WriteLine(
            "meta=" + (meta != null)
            + " walls=" + (geometry.Walls != null)
            + " dynels=" + (geometry.Dynels != null)
            + " doors=" + (geometry.Doors != null)
            + " tilemap=" + (geometry.Tilemap != null)
            + " surface=" + (geometry.Surface != null));

        if (meta != null)
        {
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "metadata: w={0} h={1} tileSize={2} heightScale={3} chunkSize={4} gridWidth={5} bits={6} format={7}",
                meta.Width,
                meta.Height,
                meta.TileSize,
                meta.HeightScale,
                meta.ChunkSize,
                meta.GridWidth,
                meta.BitsPerSample,
                meta.TilemapFormat));
        }

        DumpTilemapRecord(geometry.Tilemap);
        DumpSurfaceCoverage(store.RootPath, playfieldId, geometry.Surface);
        VerifyChunkOrder(store.RootPath, playfieldId, geometry.Tilemap);

        Pose[] poses = ClientPoses.TryGetValue(playfieldId, out Pose[]? p) ? p : Array.Empty<Pose>();
        FitHeightModel(geometry.Tilemap, meta, poses);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using PlayfieldWorldSimulation world = PlayfieldWorldSimulation.Create(
            playfieldId,
            geometry,
            meta,
            DestinationsCatalog.Instance,
            store,
            logger);
        sw.Stop();
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "BuildOK elapsedMs={0} statics={1} wallTriggers={2} portalTriggers={3}",
            sw.ElapsedMilliseconds,
            world.HardStaticCount,
            world.WallTriggerCount,
            world.PortalTriggerCount));

        ProbeGround(world, poses);
        ProbeCapsuleMovement(world, poses);
        SimulateWalk(world, poses);
        DumpWallSegments(geometry.Walls, playfieldId);
        ProbeWallCrossings(world, geometry.Walls);
        ProbePortalDoors(world, geometry.Dynels);
        ProbeExitProxy(world, geometry.Dynels, args);
        return 0;
    }

    /// <summary>
    /// Registers a door as an exit proxy and walks into it carrying a return, which is the only way
    /// such a door can be exercised offline: it is not in Dynels.dat and has no static destination.
    /// Usage: WorldSimSmoke &lt;playfield&gt; &lt;doorHex&gt; &lt;returnPlayfield&gt; &lt;returnDoorHex&gt;
    /// </summary>
    static void ProbeExitProxy(PlayfieldWorldSimulation world, PlayfieldDynels? dynels, string[] args)
    {
        if (args.Length < 4
            || !int.TryParse(args[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int doorInstance)
            || !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int returnPf)
            || !int.TryParse(args[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int returnDoor))
            return;

        Console.WriteLine("--- exit proxy walk-in (0.4u steps, 12u approach) ---");
        world.RegisterExitProxyDoor(doorInstance);
        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "  registered door={0:X8} exitTriggers={1}",
            doorInstance,
            world.ExitTriggerCount));

        PlayfieldDynel? door = null;
        for (int i = 0; dynels?.Dynels != null && i < dynels.Dynels.Count; i++)
        {
            if (dynels.Dynels[i].IdentityInstance == doorInstance)
            {
                door = dynels.Dynels[i];
                break;
            }
        }

        if (door == null)
        {
            Console.WriteLine("  door not present in this playfield's dynels");
            return;
        }

        float startX = door.Position.X;
        float y = door.Position.Y;
        float z = door.Position.Z;
        var returnTo = new ProxyReturn { PlayfieldId = returnPf, DoorInstance = returnDoor };
        foreach (bool withReturn in new[] { true, false })
        {
            var overlapping = new HashSet<int>();
            var landing = new AoVector3(0, 0, 0);
            int fires = 0;
            int destination = 0;
            float prevX = startX - 12f;
            for (int s = 1; s <= 60; s++)
            {
                var position = new AoVector3(startX - 12f + (s * 0.4f), y, z);
                if (world.TryResolveZoneCrossing(
                        prevX,
                        z,
                        position,
                        overlapping,
                        withReturn ? returnTo : default,
                        out ZoneCrossing hit))
                {
                    fires++;
                    if (destination == 0)
                    {
                        destination = hit.DestPlayfieldId;
                        landing = hit.Landing;
                    }
                }

                prevX = (float)position.x;
            }

            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "  return={0} fires={1} firedPf={2} landing=({3:F3},{4:F3},{5:F3})",
                withReturn ? returnPf.ToString(CultureInfo.InvariantCulture) + ":" + returnDoor.ToString("X8", CultureInfo.InvariantCulture) : "none",
                fires,
                destination,
                landing.x,
                landing.y,
                landing.z));
        }
    }

    /// <summary>
    /// Walks into every zoning door dynel from 12u out and reports whether the portal fires and
    /// where it lands, which is the door equivalent of <see cref="ProbeWallCrossings"/>.
    /// </summary>
    static void ProbePortalDoors(PlayfieldWorldSimulation world, PlayfieldDynels? dynels)
    {
        if (dynels?.Dynels == null)
            return;

        Console.WriteLine("--- portal door walk-in (0.4u steps, 12u approach) ---");
        for (int i = 0; i < dynels.Dynels.Count; i++)
        {
            PlayfieldDynel dynel = dynels.Dynels[i];
            if (!PortalDoorLandingResolver.TryReadPortal(dynel, out PortalDestination portal))
                continue;

            float cx = dynel.Position.X;
            float cy = dynel.Position.Y;
            float cz = dynel.Position.Z;

            var overlapping = new HashSet<int>();
            float prevX = cx - 12f;
            float prevZ = cz;
            int fires = 0;
            int fireStep = -1;
            int firedDestination = 0;
            var landing = new AoVector3(0, 0, 0);
            for (int s = 1; s <= 60; s++)
            {
                float x = cx - 12f + (s * 0.4f);
                var position = new AoVector3(x, cy, cz);
                if (world.TryResolveZoneCrossing(prevX, prevZ, position, overlapping, default, out ZoneCrossing hit))
                {
                    fires++;
                    if (fireStep < 0)
                    {
                        fireStep = s;
                        firedDestination = hit.DestPlayfieldId;
                        landing = hit.Landing;
                    }
                }

                prevX = x;
                prevZ = cz;
            }

            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "  door={0:X8} at=({1:F1},{2:F1},{3:F1}) destPf={4} via={5} fires={6} firstAtStep={7} firedPf={8} landing=({9:F3},{10:F3},{11:F3})",
                dynel.IdentityInstance,
                cx,
                cy,
                cz,
                portal.PlayfieldId,
                portal.Kind == PortalLandingKind.DestinationLine
                    ? "line:" + portal.DestinationIndex
                    : "door:" + portal.DoorInstance.ToString("X8", CultureInfo.InvariantCulture)
                      + "+" + portal.DoorClearance.ToString("F1", CultureInfo.InvariantCulture),
                fires,
                fireStep,
                firedDestination,
                landing.x,
                landing.y,
                landing.z));
        }
    }

    /// <summary>
    /// Reproduces CharacterMotor's ground resolution: probe from feet+lift, accept support within
    /// tolerance. A "supported=False" here is what makes a character fall.
    /// </summary>
    static void ProbeGround(PlayfieldWorldSimulation world, Pose[] poses)
    {
        if (poses.Length == 0)
            return;

        const float lift = 0.5f;
        const float tolerance = 2.5f;
        Console.WriteLine("--- ground support (motor probe: lift=0.5 tolerance=2.5) ---");
        foreach (Pose pose in poses)
        {
            var origin = new AoVector3(pose.X, pose.Y + lift, pose.Z);
            bool supported = world.TryRaycastDown(origin, lift + tolerance, out AoVector3 ground);
            bool anyBelow = world.TryRaycastDown(origin, 4096f, out AoVector3 far);
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "  {0} supported={1} groundY={2:F3} anyGeometryBelow={3} nearestBelowY={4:F3}",
                pose,
                supported,
                supported ? ground.y : double.NaN,
                anyBelow,
                anyBelow ? far.y : double.NaN));
        }
    }

    /// <summary>
    /// Mirrors CharacterMotor.Tick (support probe → gravity → capsule sweep) for a straight run,
    /// which is the loop that has to keep a character on the ground without the no-fall hack.
    /// </summary>
    static void SimulateWalk(PlayfieldWorldSimulation world, Pose[] poses)
    {
        if (poses.Length == 0)
            return;

        const float radius = 0.4f;
        const float halfHeight = 0.9f;
        const float stepHeight = 0.5f;
        const float centerLift = radius + halfHeight + stepHeight;
        const float skin = 0.02f;
        const float tolerance = 2.5f;
        const float gravity = -20f;
        const float dt = 1f / 32f;
        const float speed = 6f;
        const int ticks = 320;

        Console.WriteLine("--- motor loop walk (+x at 6u/s, 320 ticks at 32Hz) ---");
        foreach (Pose pose in poses)
        {
            var position = new AoVector3(pose.X, pose.Y, pose.Z);
            float verticalVelocity = 0f;
            int unsupportedTicks = 0;
            int blockedTicks = 0;
            double lowestY = position.y;
            for (int i = 0; i < ticks; i++)
            {
                var probe = new AoVector3(position.x, position.y + stepHeight, position.z);
                bool supported = verticalVelocity <= 0f
                    && world.TryRaycastDown(probe, stepHeight + tolerance, out AoVector3 ground);
                if (supported)
                {
                    world.TryRaycastDown(probe, stepHeight + tolerance, out AoVector3 groundAgain);
                    verticalVelocity = 0f;
                    position = new AoVector3(position.x, groundAgain.y, position.z);
                }
                else
                {
                    unsupportedTicks++;
                    verticalVelocity = Math.Max(-50f, verticalVelocity + (gravity * dt));
                }

                var target = new AoVector3(
                    position.x + (speed * dt),
                    position.y + (verticalVelocity * dt),
                    position.z);
                if (world.TryMoveCapsule(
                        position, target, radius, halfHeight, centerLift, skin,
                        out AoVector3 resolved, out AoVector3 normal))
                {
                    blockedTicks++;
                    position = resolved;
                    if (Math.Abs(normal.y) > 0.5)
                        verticalVelocity = 0f;
                }
                else
                {
                    position = target;
                }

                lowestY = Math.Min(lowestY, position.y);
            }

            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "  {0} -> ({1:F3},{2:F3},{3:F3}) travelledX={4:F2} lowestY={5:F3} unsupportedTicks={6} blockedTicks={7}",
                pose,
                position.x,
                position.y,
                position.z,
                position.x - pose.X,
                lowestY,
                unsupportedTicks,
                blockedTicks));
        }
    }

    /// <summary>
    /// A capsule centered on the feet starts buried in the terrain, which makes every sweep report
    /// zero travel and freezes the character. Verify a plain horizontal step is not blocked.
    /// </summary>
    static void ProbeCapsuleMovement(PlayfieldWorldSimulation world, Pose[] poses)
    {
        if (poses.Length == 0)
            return;

        const float radius = 0.4f;
        const float halfHeight = 0.9f;
        const float stepHeight = 0.5f;
        const float centerLift = radius + halfHeight + stepHeight;
        const float skin = 0.02f;
        const float step = 0.4f;
        Console.WriteLine("--- capsule step (0.4u +x from each pose; step-offset vs flush) ---");
        foreach (Pose pose in poses)
        {
            var start = new AoVector3(pose.X, pose.Y, pose.Z);
            var end = new AoVector3(pose.X + step, pose.Y, pose.Z);

            bool steppedBlocked = world.TryMoveCapsule(
                start, end, radius, halfHeight, centerLift, skin, out AoVector3 stepped, out _);
            bool flushBlocked = world.TryMoveCapsule(
                start, end, radius, halfHeight, radius + halfHeight, skin, out AoVector3 flush, out _);

            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "  {0} stepOffset: blocked={1} travel={2:F3} | flush: blocked={3} travel={4:F3}",
                pose,
                steppedBlocked,
                stepped.x - pose.X,
                flushBlocked,
                flush.x - pose.X));
        }
    }

    /// <summary>
    /// Walks a straight line through each zone-line segment in 0.4u steps (a running character's
    /// per-tick distance) and reports whether the crossing fires and resolves a landing.
    /// </summary>
    static void ProbeWallCrossings(PlayfieldWorldSimulation world, PlayfieldWalls? walls)
    {
        if (Get(walls, "WallGroups") is not IList groups)
            return;

        Console.WriteLine("--- zone line crossing walk (0.4u steps, 12u approach) ---");
        for (int g = 0; g < groups.Count; g++)
        {
            if (Get(groups[g]!, "Walls") is not IList members || members.Count < 2)
                continue;

            for (int i = 0; i < members.Count; i++)
            {
                object b = members[(i + 1) % members.Count]!;
                if (ToInt(Get(b, "DestinationPlayfield")) <= 0)
                    continue;

                object a = members[i]!;
                float ax = ToFloat(Get(a, "X"));
                float az = ToFloat(Get(a, "Z"));
                float bx = ToFloat(Get(b, "X"));
                float bz = ToFloat(Get(b, "Z"));

                // Approach the segment midpoint along its perpendicular.
                float midX = (ax + bx) * 0.5f;
                float midZ = (az + bz) * 0.5f;
                float dx = bx - ax;
                float dz = bz - az;
                float len = MathF.Sqrt((dx * dx) + (dz * dz));
                if (len < 1e-3f)
                    continue;

                float nx = -dz / len;
                float nz = dx / len;
                float y = ResolveWalkHeight(world, midX, midZ);

                var overlapping = new HashSet<int>();
                float prevX = midX - (nx * 12f);
                float prevZ = midZ - (nz * 12f);
                int fires = 0;
                int fireStep = -1;
                int destination = 0;
                var landing = new AoVector3(0, 0, 0);
                for (int s = 1; s <= 60; s++)
                {
                    float t = -12f + (s * 0.4f);
                    float x = midX + (nx * t);
                    float z = midZ + (nz * t);
                    var position = new AoVector3(x, y, z);
                    if (world.TryResolveZoneCrossing(prevX, prevZ, position, overlapping, default, out ZoneCrossing hit))
                    {
                        fires++;
                        if (fireStep < 0)
                        {
                            fireStep = s;
                            destination = hit.DestPlayfieldId;
                            landing = hit.Landing;
                        }
                    }


                    prevX = x;
                    prevZ = z;
                }

                Console.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "  group={0} seg={1} mid=({2:F1},{3:F1}) fires={4} firstAtStep={5} destPf={6} landing=({7:F3},{8:F3},{9:F3})",
                    g,
                    i,
                    midX,
                    midZ,
                    fires,
                    fireStep,
                    destination,
                    landing.x,
                    landing.y,
                    landing.z));
            }
        }
    }

    static float ResolveWalkHeight(PlayfieldWorldSimulation world, float x, float z)
    {
        var origin = new AoVector3(x, 4096f, z);
        return world.TryRaycastDown(origin, 8192f, out AoVector3 ground) ? (float)ground.y : 0f;
    }

    static void DumpTilemapRecord(Tilemap? tilemap)
    {
        if (tilemap == null)
        {
            Console.WriteLine("tilemap record: null");
            return;
        }

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "tilemap record: MapWidth={0} MapHeight={1} MapScale={2} HeightMod={3} ChunkSize={4} GridWidth={5} Bits={6}",
            Get(tilemap, "MapWidth"),
            Get(tilemap, "MapHeight"),
            Get(tilemap, "MapScale"),
            Get(tilemap, "HeightMod"),
            Get(tilemap, "ChunkSize"),
            Get(tilemap, "GridWidth"),
            Get(tilemap, "BitsPerSample")));

        if (Get(tilemap, "Heightmap") is IList list)
        {
            string dims = list.Count > 0 && list[0] is ushort[,] first
                ? first.GetLength(0) + "x" + first.GetLength(1)
                : "n/a";
            Console.WriteLine("Heightmap chunks=" + list.Count + " chunkDim=" + dims);
        }
    }

    static void DumpSurfaceCoverage(string root, int playfieldId, SurfaceResource? surface)
    {
        int surfaceMeshes = surface?.Surfaces?.Count ?? 0;
        Console.WriteLine("surface(playfield-id record): meshes=" + surfaceMeshes);

        string dir = Path.Combine(root, GameDataPaths.PlayfieldRelativeDirectory(playfieldId));
        if (!Directory.Exists(dir))
            return;

        string[] files = Directory.GetFiles(dir);
        var names = new List<string>();
        foreach (string file in files)
            names.Add(Path.GetFileName(file));

        names.Sort(StringComparer.OrdinalIgnoreCase);
        Console.WriteLine("playfield files: " + string.Join(", ", names));
    }

    /// <summary>
    /// The baker maps Heightmap[i] to grid (i%gridWidth, i/gridWidth). Cross-check that against
    /// the GridX/GridZ each chunk actually declares in the CHGA payload.
    /// </summary>
    static void VerifyChunkOrder(string root, int playfieldId, Tilemap? tilemap)
    {
        if (tilemap == null)
            return;

        string collisionPath = Path.Combine(root, GameDataPaths.PlayfieldCollisionRelativePath(playfieldId));
        if (!File.Exists(collisionPath))
            return;

        PlayfieldCollisionDat.Parse(File.ReadAllBytes(collisionPath), out byte[] tilemapPayload, out _);
        Type? chunkedType = typeof(Tilemap).GetNestedType(
            "ChunkedGround",
            BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo? tryParse = chunkedType?.GetMethod("TryParse", BindingFlags.Public | BindingFlags.Static);
        if (chunkedType == null || tryParse == null)
        {
            Console.WriteLine("chunk order: ChunkedGround.TryParse unavailable");
            return;
        }

        object? parsed = null;
        foreach (int offset in new[] { 12, 8, 0 })
        {
            if (offset >= tilemapPayload.Length)
                continue;

            byte[] slice = new byte[tilemapPayload.Length - offset];
            Buffer.BlockCopy(tilemapPayload, offset, slice, 0, slice.Length);
            parsed = tryParse.Invoke(null, new object[] { slice });
            if (parsed != null)
                break;
        }

        if (parsed == null)
        {
            Console.WriteLine("chunk order: payload did not parse");
            return;
        }

        int gridWidth = (int)chunkedType.GetProperty("GridWidth")!.GetValue(parsed)!;
        Type? chunkType = chunkedType.GetNestedType("GroundChunk", BindingFlags.Public | BindingFlags.NonPublic);
        FieldInfo? gridXField = chunkType?.GetField("GridX");
        FieldInfo? gridZField = chunkType?.GetField("GridZ");
        FieldInfo? heightsField = chunkType?.GetField("Heights");
        if (gridXField == null || gridZField == null || heightsField == null)
        {
            Console.WriteLine("chunk order: GroundChunk fields unavailable");
            return;
        }

        IList bakerList = (IList)Get(tilemap, "Heightmap")!;
        int index = 0;
        int gridMismatch = 0;
        int contentMismatch = 0;
        foreach (object chunk in (IEnumerable)chunkedType.GetProperty("Chunks")!.GetValue(parsed)!)
        {
            int gridX = (int)gridXField.GetValue(chunk)!;
            int gridZ = (int)gridZField.GetValue(chunk)!;
            if (gridX != index % gridWidth || gridZ != index / gridWidth)
                gridMismatch++;

            if (heightsField.GetValue(chunk) is ushort[,] chunkHeights
                && index < bakerList.Count
                && bakerList[index] is ushort[,] bakerHeights
                && !SameGrid(chunkHeights, bakerHeights))
            {
                contentMismatch++;
            }

            index++;
        }

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "chunk order: chunks={0} gridWidth={1} sequentialGridMismatch={2} heightmapContentMismatch={3}",
            index,
            gridWidth,
            gridMismatch,
            contentMismatch));
    }

    static bool SameGrid(ushort[,] a, ushort[,] b)
    {
        if (a.GetLength(0) != b.GetLength(0) || a.GetLength(1) != b.GetLength(1))
            return false;

        for (int i = 0; i < a.GetLength(0); i++)
        {
            for (int j = 0; j < a.GetLength(1); j++)
            {
                if (a[i, j] != b[i, j])
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Bilinear-samples the chunk grid at each live client pose under competing interpretations
    /// (metadata heightScale vs record HeightMod, heights[x,z] vs heights[z,x]) and reports which
    /// one reproduces the client's Y.
    /// </summary>
    static void FitHeightModel(Tilemap? tilemap, PlayfieldMetaData? meta, Pose[] poses)
    {
        if (tilemap == null || poses.Length == 0)
            return;

        if (Get(tilemap, "Heightmap") is not IList chunks || chunks.Count == 0)
            return;

        float metaScale = meta?.HeightScale ?? 0f;
        float recordScale = Get(tilemap, "HeightMod") is float hm ? hm : 0f;
        float tileSize = meta?.TileSize > 0 ? meta.TileSize : (Get(tilemap, "MapScale") is float ms ? ms : 1f);
        int chunkSize = meta?.ChunkSize ?? (Get(tilemap, "ChunkSize") is int cs ? cs : 0);
        int gridWidth = meta?.GridWidth ?? (Get(tilemap, "GridWidth") is int gw ? gw : 0);
        if (chunkSize < 2 || gridWidth <= 0 || tileSize <= 0f)
            return;

        Console.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "--- height fit (tileSize={0} chunkSize={1} gridWidth={2} metaScale={3} recordHeightMod={4}) ---",
            tileSize,
            chunkSize,
            gridWidth,
            metaScale,
            recordScale));

        foreach (bool transpose in new[] { false, true })
        {
            double sumAbsMeta = 0;
            double sumAbsRecord = 0;
            int samples = 0;
            var detail = new StringBuilder();
            foreach (Pose pose in poses)
            {
                if (!TrySampleRaw(chunks, chunkSize, gridWidth, tileSize, transpose, pose.X, pose.Z, out float raw))
                    continue;

                samples++;
                double metaY = raw * metaScale;
                double recordY = raw * recordScale;
                sumAbsMeta += Math.Abs(metaY - pose.Y);
                sumAbsRecord += Math.Abs(recordY - pose.Y);
                detail.Append(string.Format(
                    CultureInfo.InvariantCulture,
                    "    {0} clientY={1:F3} raw={2:F3} metaY={3:F3} recordY={4:F3} impliedScale={5:F5}\n",
                    pose,
                    pose.Y,
                    raw,
                    metaY,
                    recordY,
                    raw > 0 ? pose.Y / raw : 0));
            }

            if (samples == 0)
                continue;

            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "  transpose={0} samples={1} meanAbsErr(metaScale)={2:F3} meanAbsErr(recordHeightMod)={3:F3}",
                transpose,
                samples,
                sumAbsMeta / samples,
                sumAbsRecord / samples));
            Console.Write(detail.ToString());
        }
    }

    static bool TrySampleRaw(
        IList chunks,
        int chunkSize,
        int gridWidth,
        float tileSize,
        bool transpose,
        float worldX,
        float worldZ,
        out float raw)
    {
        raw = 0f;
        float span = (chunkSize - 1) * tileSize;
        int gridX = (int)MathF.Floor(worldX / span);
        int gridZ = (int)MathF.Floor(worldZ / span);
        int index = (gridZ * gridWidth) + gridX;
        if (gridX < 0 || gridZ < 0 || gridX >= gridWidth || index < 0 || index >= chunks.Count)
            return false;

        if (chunks[index] is not ushort[,] heights)
            return false;

        float localX = (worldX - (gridX * span)) / tileSize;
        float localZ = (worldZ - (gridZ * span)) / tileSize;
        int x0 = (int)MathF.Floor(localX);
        int z0 = (int)MathF.Floor(localZ);
        float fx = localX - x0;
        float fz = localZ - z0;
        int max = heights.GetLength(0) - 1;
        int x1 = Math.Min(x0 + 1, max);
        int z1 = Math.Min(z0 + 1, max);
        if (x0 < 0 || z0 < 0 || x0 > max || z0 > max)
            return false;

        float h00 = transpose ? heights[z0, x0] : heights[x0, z0];
        float h10 = transpose ? heights[z0, x1] : heights[x1, z0];
        float h01 = transpose ? heights[z1, x0] : heights[x0, z1];
        float h11 = transpose ? heights[z1, x1] : heights[x1, z1];
        raw = ((h00 * (1 - fx)) + (h10 * fx)) * (1 - fz)
            + ((h01 * (1 - fx)) + (h11 * fx)) * fz;
        return true;
    }

    static void DumpWallSegments(PlayfieldWalls? walls, int playfieldId)
    {
        if (Get(walls, "WallGroups") is not IList groups)
        {
            Console.WriteLine("walls: no WallGroups");
            return;
        }

        Console.WriteLine("--- wall segments with a destination playfield ---");
        int zoning = 0;
        for (int g = 0; g < groups.Count; g++)
        {
            if (Get(groups[g]!, "Walls") is not IList members || members.Count < 2)
                continue;

            for (int i = 0; i < members.Count; i++)
            {
                object b = members[(i + 1) % members.Count]!;
                int dest = ToInt(Get(b, "DestinationPlayfield"));
                if (dest <= 0)
                    continue;

                object a = members[i]!;
                zoning++;
                Console.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "  group={0} seg={1}->{2} a=({3:F3},{4:F3}) b=({5:F3},{6:F3}) destPf={7} destIdx={8}",
                    g,
                    i,
                    (i + 1) % members.Count,
                    ToFloat(Get(a, "X")),
                    ToFloat(Get(a, "Z")),
                    ToFloat(Get(b, "X")),
                    ToFloat(Get(b, "Z")),
                    dest,
                    ToInt(Get(b, "DestinationIndex"))));

                byte destIndex = (byte)ToInt(Get(b, "DestinationIndex"));
                bool haveLanding = DestinationsCatalog.Instance.TryGetDestination(
                    dest,
                    destIndex,
                    out PlayfieldDestination? landing);
                Console.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "      landing(destPf={0} idx={1}) found={2}{3}",
                    dest,
                    destIndex,
                    haveLanding,
                    haveLanding && landing != null
                        ? string.Format(
                            CultureInfo.InvariantCulture,
                            " start=({0:F3},{1:F3},{2:F3}) end=({3:F3},{4:F3},{5:F3})",
                            landing.StartX,
                            landing.StartY,
                            landing.StartZ,
                            landing.EndX,
                            landing.EndY,
                            landing.EndZ)
                        : string.Empty));
            }
        }

        Console.WriteLine("zoning segments=" + zoning + " playfield=" + playfieldId);
    }

    static object? Get(object? obj, string name)
    {
        if (obj == null)
            return null;

        Type t = obj.GetType();
        return t.GetProperty(name)?.GetValue(obj) ?? t.GetField(name)?.GetValue(obj);
    }

    static float ToFloat(object? v) => v switch
    {
        float f => f,
        double d => (float)d,
        _ => 0f
    };

    static int ToInt(object? v) => v switch
    {
        int i => i,
        short s => s,
        ushort us => us,
        byte b => b,
        _ => 0
    };
}
