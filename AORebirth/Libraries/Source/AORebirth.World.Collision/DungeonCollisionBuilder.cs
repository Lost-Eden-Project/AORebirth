namespace AORebirth.World.Collision
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    using AODB.Common.RDBObjects;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using Vector3 = System.Numerics.Vector3;

    /// <summary>
    /// Assembles dungeon collision from a style playfield and an ACG room list.
    /// </summary>
    public static class DungeonCollisionBuilder
    {
        public static PlayfieldCollisionSet Build(string gameDataRoot, AcgBuildingGeneratorData generator)
            => BuildLayout(gameDataRoot, generator).Collision;

        public static PlayfieldCollisionSet Build(
            string gameDataRoot,
            int style,
            IReadOnlyList<DungeonRoomSpec> rooms,
            int widthBlocks,
            int heightBlocks,
            int floorHeight)
            => BuildLayout(
                DungeonStyleCatalog.Load(gameDataRoot, style),
                rooms,
                widthBlocks,
                heightBlocks,
                floorHeight,
                playfieldId: 0).Collision;

        public static PlayfieldCollisionSet Build(
            DungeonStyleCatalog catalog,
            IReadOnlyList<DungeonRoomSpec> rooms,
            int widthBlocks,
            int heightBlocks,
            int floorHeight)
            => BuildLayout(catalog, rooms, widthBlocks, heightBlocks, floorHeight, playfieldId: 0).Collision;

        public static bool TryBuildStatic(
            string gameDataRoot,
            int playfieldId,
            out DungeonWorldLayout? layout)
        {
            layout = null;
            if (!DungeonStyleCatalog.TryLoad(gameDataRoot, playfieldId, out DungeonStyleCatalog? catalog)
                || catalog == null)
                return false;

            layout = BuildStatic(catalog, playfieldId);
            return layout.Rooms.Count > 0;
        }

        public static DungeonWorldLayout BuildLayout(string gameDataRoot, AcgBuildingGeneratorData generator)
        {
            ArgumentNullException.ThrowIfNull(generator);
            return BuildLayout(
                DungeonStyleCatalog.Load(gameDataRoot, generator.Style),
                MapRooms(generator.Rooms),
                generator.Width,
                generator.Height,
                generator.RoomsPerFloor,
                playfieldId: 0);
        }

        public static DungeonWorldLayout BuildStatic(DungeonStyleCatalog catalog, int playfieldId = 0)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            var rooms = new List<DungeonRoomBounds>(catalog.Rooms.Count);
            var meshes = new List<CollisionTriangleMesh>();
            for (int i = 0; i < catalog.Rooms.Count; i++)
            {
                StyleRoomTemplate template = catalog.Rooms[i];
                int facing = template.TemplateFacing & 3;
                AppendPlacedRoom(
                    catalog,
                    template,
                    template.TemplatePos,
                    facing,
                    floorHeight: 0,
                    i,
                    template.InstanceId,
                    meshes,
                    rooms);
            }

            int id = playfieldId > 0 ? playfieldId : catalog.StyleId;
            var listed = new List<KeyValuePair<int, IReadOnlyList<int>>>(catalog.Rooms.Count);
            for (int i = 0; i < catalog.Rooms.Count; i++)
                listed.Add(new KeyValuePair<int, IReadOnlyList<int>>(catalog.Rooms[i].InstanceId, catalog.Rooms[i].LinkedRooms));
            return new DungeonWorldLayout(
                new PlayfieldCollisionSet(id, meshes, terrain: null),
                rooms,
                roomLinks: DungeonWorldLayout.SymmetricLinks(listed));
        }

        public static DungeonWorldLayout BuildLayout(
            DungeonStyleCatalog catalog,
            IReadOnlyList<DungeonRoomSpec> rooms,
            int widthBlocks,
            int heightBlocks,
            int floorHeight,
            int playfieldId)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            ArgumentNullException.ThrowIfNull(rooms);
            if (widthBlocks < 0 || heightBlocks < 0)
                throw new ArgumentOutOfRangeException(nameof(widthBlocks));

            var meshes = new List<CollisionTriangleMesh>();
            var bounds = new List<DungeonRoomBounds>(rooms.Count);
            var templates = new List<StyleRoomTemplate>(rooms.Count);
            var placements = new List<DungeonRoomPlacement>(rooms.Count);
            int minFloor = DungeonRoomPlacer.MinFloor(rooms);
            for (int i = 0; i < rooms.Count; i++)
            {
                DungeonRoomSpec spec = rooms[i] ?? throw new ArgumentNullException(nameof(rooms));
                StyleRoomTemplate template = LookupTemplate(catalog, spec.RoomId);
                templates.Add(template);
                if (!template.IsAcgSizeValid)
                {
                    throw new InvalidDataException(
                        "Style room "
                        + spec.RoomId.ToString(CultureInfo.InvariantCulture)
                        + " has invalid ACG size "
                        + template.NumTilesX.ToString(CultureInfo.InvariantCulture)
                        + "x"
                        + template.NumTilesZ.ToString(CultureInfo.InvariantCulture)
                        + ".");
                }

                Vector3 placed = DungeonRoomPlacer.Place(
                    template,
                    spec,
                    catalog.TileSize,
                    widthBlocks,
                    heightBlocks,
                    floorHeight,
                    minFloor);
                placements.Add(new DungeonRoomPlacement(i, template, placed, spec.Facing & 3));
                AppendPlacedRoom(
                    catalog,
                    template,
                    placed,
                    spec.Facing & 3,
                    floorHeight,
                    i,
                    i,
                    meshes,
                    bounds);
            }

            int id = playfieldId > 0 ? playfieldId : catalog.StyleId;
            return new DungeonWorldLayout(
                new PlayfieldCollisionSet(id, meshes, terrain: null),
                bounds,
                PlaceDoors(rooms, templates, bounds, placements, catalog),
                placements)
            {
                SafePosition = placements.Count > 0 && catalog.Gnda.Length > 0 && catalog.Dcga.Length > 0
                    ? DungeonSafePosition.Compute(catalog, placements[0].Template, placements[0].Placed, placements[0].Facing)
                    : null
            };
        }

        /// <summary>Two door connections closer than this (units) are the same doorway.</summary>
        const float DoorMatchDistance = 0.3f;

        /// <summary>
        /// Two connections more than this (units) apart vertically are on different floors. A floor's rooms share one
        /// placed height; generated floors are stacked at least a floor height (64) apart.
        /// </summary>
        const float DoorMatchHeight = 16f;

        /// <summary>
        /// Live doors sit this far (units) off the shared room edge along their heading, just inside the room that owns
        /// them (e.g. 10.001, 19.999, 60.001). Exactly on the edge the client's Door_t::LinkDoorToRooms may find no room
        /// for the door and remove it.
        /// </summary>
        const float DoorEdgeOffset = 0.001f;

        static Vector3 OffsetAlongYaw(float x, float y, float z, int yawDegrees)
        {
            double radians = yawDegrees * Math.PI / 180.0;
            return new Vector3(x + (float)(Math.Sin(radians) * DoorEdgeOffset), y, z + (float)(Math.Cos(radians) * DoorEdgeOffset));
        }

        readonly struct DoorPoint
        {
            public DoorPoint(int room, float x, float z, float y, float floorY, int yaw)
            {
                Room = room;
                X = x;
                Z = z;
                Y = y;
                FloorY = floorY;
                Yaw = yaw;
            }

            public int Room { get; }

            public float X { get; }

            public float Z { get; }

            /// <summary>The room's placed height: which floor the doorway is on (<see cref="Meets"/>).</summary>
            public float Y { get; }

            /// <summary>The door's height: the floor under the doorway (<see cref="DoorFloorY"/>).</summary>
            public float FloorY { get; }

            public int Yaw { get; }

            // Same floor too: stacked floors put rooms with identical footprints directly above each other.
            public bool Meets(in DoorPoint other)
                => MathF.Abs(X - other.X) < DoorMatchDistance && MathF.Abs(Z - other.Z) < DoorMatchDistance
                    && MathF.Abs(Y - other.Y) < DoorMatchHeight;
        }

        /// <summary>
        /// Doors of an ACG layout, in the live order: the exit (the entrance room's connection that meets no other
        /// room), then for each room in generator order, each of its connections (template order) that meets a
        /// connection of an earlier room. A connection sits on the middle of its tile's edge, rotated into place by the
        /// room's facing; unpaired connections other than the exit stay walls.
        /// Verified against every door of the five captured live dungeons (90 of 90, none extra).
        /// </summary>
        static IReadOnlyList<DungeonDoorPlacement> PlaceDoors(
            IReadOnlyList<DungeonRoomSpec> rooms,
            IReadOnlyList<StyleRoomTemplate> templates,
            IReadOnlyList<DungeonRoomBounds> bounds,
            IReadOnlyList<DungeonRoomPlacement> placements,
            DungeonStyleCatalog catalog)
        {
            float tileSize = catalog.TileSize;
            bool heights = catalog.Gnda.Length > 0 && catalog.Dcga.Length > 0;
            var byRoom = new List<DoorPoint>[rooms.Count];
            for (int i = 0; i < rooms.Count; i++)
            {
                StyleRoomTemplate template = templates[i];
                int facing = rooms[i].Facing & 3;
                int w = template.NumTilesX;
                int h = template.NumTilesZ;
                float placedY = placements[i].Placed.Y;
                float minHeight = heights
                    ? DungeonRoomPlacer.ComputeYOffset(catalog.Gnda, catalog.Dcga, catalog.MapWidth, catalog.MapHeight, template, catalog.HeightScale)
                    : 0f;
                var points = new List<DoorPoint>(template.DoorPosRots.Count);
                foreach (int posRot in template.DoorPosRots)
                {
                    int tile = posRot >> 2;
                    int rotation = posRot & 3;
                    int cx = w > 0 ? tile % w : 0;
                    int cz = w > 0 ? tile / w : 0;
                    (float u, float v) = rotation switch
                    {
                        0 => (cx + 0.5f, cz + 1f),
                        1 => (cx + 1f, cz + 0.5f),
                        2 => (cx + 0.5f, (float)cz),
                        _ => ((float)cx, cz + 0.5f)
                    };
                    (float x, float z) = facing switch
                    {
                        0 => (u, v),
                        1 => (v, w - u),
                        2 => (w - u, h - v),
                        _ => (h - v, u)
                    };
                    int yaw = (180 + (90 * ((rotation + facing) & 3))) % 360;
                    float floorY = heights ? DoorFloorY(catalog, template, cx, cz, rotation, minHeight, placedY) : placedY;
                    points.Add(new DoorPoint(i, bounds[i].Min.X + (x * tileSize), bounds[i].Min.Z + (z * tileSize), bounds[i].Min.Y + 1f, floorY, yaw));
                }

                byRoom[i] = points;
            }

            var doors = new List<DungeonDoorPlacement>();
            if (rooms.Count == 0)
                return doors;

            foreach (DoorPoint point in byRoom[0])
            {
                if (!MeetsAnyOther(byRoom, point))
                {
                    doors.Add(new DungeonDoorPlacement(OffsetAlongYaw(point.X, point.FloorY, point.Z, point.Yaw), point.Yaw, room: -1, linkedRoom: 0));
                    break;
                }
            }

            for (int i = 1; i < rooms.Count; i++)
            {
                foreach (DoorPoint point in byRoom[i])
                {
                    for (int j = 0; j < i; j++)
                    {
                        bool paired = false;
                        foreach (DoorPoint other in byRoom[j])
                        {
                            if (!point.Meets(other))
                                continue;

                            doors.Add(new DungeonDoorPlacement(OffsetAlongYaw(point.X, point.FloorY, point.Z, point.Yaw), point.Yaw, room: i, linkedRoom: j));
                            paired = true;
                            break;
                        }

                        if (paired)
                            break;
                    }
                }
            }

            return doors;
        }

        /// <summary>
        /// The door's height, N3 <c>n3Room_t::GetDoorPosRot</c> (10010acf): the doorway point in the template's tile rect
        /// (tile column / row times the tile size plus 1, moved 0.999 toward its side: 0 +z, 1 +x, 2 -z, 3 -x), the
        /// tilemap's floor there (FUN_10017800) less the room's lowest floor, plus the room's placed height. The client
        /// links a door to the room whose floor band holds it (<c>Door_t::LinkDoorToRooms</c>, 100800cb); at the room's
        /// placed height a doorway on a raised floor is below that band and the door is removed.
        /// </summary>
        static float DoorFloorY(DungeonStyleCatalog catalog, StyleRoomTemplate template, int column, int row, int side,
            float minHeight, float placedY)
        {
            float s = catalog.TileSize;
            float x = (s * column) + 1f;
            float z = (s * row) + 1f;
            switch (side)
            {
                case 0: z += 0.999f; break;
                case 1: x += 0.999f; break;
                case 2: z -= 0.999f; break;
                default: x -= 0.999f; break;
            }

            return TileFloor(catalog, (s * template.TileMinX) + x, (s * template.TileMinZ) + z) - minHeight + placedY;
        }

        /// <summary>
        /// N3 FUN_10017800, indoor branch: 0 outside the map; the tile <c>(int)(x / s)</c>, <c>(int)(z / s)</c>; corners
        /// from the floors at (x-1, z-1), (x, z-1), (x, z), (x-1, z), each index below 1 taken as 0; the height on the
        /// plane of corners 0, 2, 3 when the point's x offset from corner 0 is at most its z offset, else 0, 1, 2.
        /// </summary>
        static float TileFloor(DungeonStyleCatalog catalog, float x, float z)
        {
            float s = catalog.TileSize;
            int w = catalog.MapWidth, h = catalog.MapHeight;
            if (x < 0f || s * w <= x || z < 0f || s * h <= z)
                return 0f;

            int tx = (int)(x / s), tz = (int)(z / s);
            int xm = tx - 1 < 1 ? 0 : tx - 1;
            int zm = tz - 1 < 1 ? 0 : tz - 1;
            var c0 = new Vector3(tx * s, GroundFloor(catalog, xm, zm), tz * s);
            var c1 = new Vector3(s + (tx * s), GroundFloor(catalog, tx, zm), tz * s);
            var c2 = new Vector3(s + (tx * s), GroundFloor(catalog, tx, tz), s + (tz * s));
            var c3 = new Vector3(tx * s, GroundFloor(catalog, xm, tz), s + (tz * s));

            float dx = x - c0.X, dz = z - c0.Z;
            Vector3 n = dx <= dz ? Vector3.Cross(c0 - c3, c0 - c2) : Vector3.Cross(c0 - c2, c0 - c1);
            return (-((dx * n.X) + (dz * n.Z)) / n.Y) + c0.Y;
        }

        /// <summary>The tile's floor byte times the height scale (N3 FUN_10016454).</summary>
        static float GroundFloor(DungeonStyleCatalog catalog, int x, int z)
        {
            int i = (z * catalog.MapWidth) + x;
            return (uint)i < (uint)catalog.Gnda.Length ? catalog.Gnda[i] * catalog.HeightScale : 0f;
        }

        static bool MeetsAnyOther(List<DoorPoint>[] byRoom, in DoorPoint point)
        {
            for (int j = 0; j < byRoom.Length; j++)
            {
                if (j == point.Room)
                    continue;
                foreach (DoorPoint other in byRoom[j])
                {
                    if (point.Meets(other))
                        return true;
                }
            }

            return false;
        }

        static void AppendPlacedRoom(
            DungeonStyleCatalog catalog,
            StyleRoomTemplate template,
            Vector3 placed,
            int facing,
            float floorHeight,
            int meshIndex,
            int cellId,
            List<CollisionTriangleMesh> meshes,
            List<DungeonRoomBounds> rooms)
        {
            rooms.Add(
                DungeonRoomBounds.FromPlaced(
                    cellId,
                    template,
                    placed,
                    facing,
                    catalog.TileSize,
                    floorHeight));

            if (catalog.Gnda.Length == 0 || catalog.Dcga.Length == 0)
                return;

            float yOffset = DungeonRoomPlacer.ComputeYOffset(
                catalog.Gnda,
                catalog.Dcga,
                catalog.MapWidth,
                catalog.MapHeight,
                template,
                catalog.HeightScale);
            CollisionTriangleMesh? ground = DungeonGroundMesher.TryBuild(
                catalog.Gnda,
                catalog.Dcga,
                catalog.MapWidth,
                catalog.MapHeight,
                template,
                placed,
                facing,
                catalog.TileSize,
                catalog.HeightScale,
                yOffset,
                meshIndex);
            if (ground != null)
                meshes.Add(ground);

            AppendRoomSurfaces(catalog, template, placed, facing, meshIndex, meshes);
        }

        static StyleRoomTemplate LookupTemplate(DungeonStyleCatalog catalog, ushort roomId)
        {
            for (int i = 0; i < catalog.Rooms.Count; i++)
            {
                if (catalog.Rooms[i].InstanceId == roomId)
                    return catalog.Rooms[i];
            }

            if (roomId < catalog.Rooms.Count)
                return catalog.Rooms[roomId];

            throw new InvalidDataException(
                "RoomId "
                + roomId.ToString(CultureInfo.InvariantCulture)
                + " is outside style catalog size "
                + catalog.Rooms.Count.ToString(CultureInfo.InvariantCulture)
                + ".");
        }

        static void AppendRoomSurfaces(
            DungeonStyleCatalog catalog,
            StyleRoomTemplate template,
            Vector3 placed,
            int facing,
            int roomIndex,
            List<CollisionTriangleMesh> meshes)
        {
            if (!catalog.SurfacePayloads.TryGetValue(template.InstanceId, out byte[]? payload)
                || payload == null
                || payload.Length == 0)
                return;

            var raw = new List<CollisionTriangleMesh>();
            try
            {
                SurfaceResource surface = RdbObjectDeserializer.Deserialize<SurfaceResource>(
                    payload,
                    "Surfaces.dat#room" + template.InstanceId.ToString(CultureInfo.InvariantCulture));
                SurfaceResourceNormalizer.AppendMeshes(surface, template.InstanceId, raw);
            }
            catch
            {
                return;
            }

            string source = "surface-room" + roomIndex.ToString(CultureInfo.InvariantCulture);
            for (int i = 0; i < raw.Count; i++)
            {
                Vector3[] verts = raw[i].Vertices;
                var transformed = new Vector3[verts.Length];
                for (int v = 0; v < verts.Length; v++)
                {
                    transformed[v] = DungeonRoomPlacer.TransformSurfaceVertex(
                        verts[v],
                        template,
                        placed,
                        facing);
                }

                meshes.Add(new CollisionTriangleMesh(transformed, raw[i].Triangles, roomIndex, source));
            }
        }

        static DungeonRoomSpec[] MapRooms(BuildingRoomInfo[]? source)
        {
            if (source == null || source.Length == 0)
                return Array.Empty<DungeonRoomSpec>();

            var rooms = new DungeonRoomSpec[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                BuildingRoomInfo room = source[i] ?? new BuildingRoomInfo();
                rooms[i] = new DungeonRoomSpec
                {
                    RoomId = room.RoomId,
                    Floor = room.Floor,
                    GridX = room.GridX,
                    GridZ = room.GridZ,
                    Facing = room.Facing
                };
            }

            return rooms;
        }
    }
}
