namespace AORebirth.World.Pathfinding
{
    using System;
    using System.Collections.Generic;
    using System.IO;

    using AORebirth.Core.GameData;
    using AORebirth.World.Collision;

    using DotRecast.Recast;
    using DotRecast.Recast.Geom;
    using DotRecast.Detour;

    using LostEden.Protocol.GameData;

    public static class NavMeshBaker
    {
        const int WalkablePolyFlags = 1;
        const int MaxPolysPerTile = 32768;

        /// <summary>
        /// Bakes <paramref name="collision"/>. Walkable ground under any of <paramref name="water"/>'s surfaces becomes
        /// <see cref="NavMeshAreas.Water"/>, so searches price it by <see cref="NavMeshBuildSettings.WaterAreaCost"/>.
        /// Tiles build on <paramref name="threads"/> workers; 1 or less builds them on the calling thread, which is what
        /// the engine's own bakes use so they never compete with the tick for cores.
        /// </summary>
        public static NavMeshBakeResult Bake(
            PlayfieldCollisionSet collision,
            NavMeshBuildSettings? settings = null,
            IReadOnlyList<PlayfieldWaterEntry>? water = null,
            int threads = 1)
        {
            ArgumentNullException.ThrowIfNull(collision);
            settings ??= NavMeshBuildSettings.CreateDefault();
            settings.Validate();

            CollisionMeshFlattener.Flatten(collision, out float[] vertices, out int[] triangles, out int triangleCount);
            if (triangleCount <= 0)
            {
                throw new InvalidDataException(
                    "Playfield "
                    + collision.PlayfieldId
                    + " has no collision triangles to bake.");
            }

            vertices = PadVerticalBounds(vertices, settings);
            var geom = new RcSampleInputGeomProvider(vertices, triangles);
            if (water != null)
                AddWaterVolumes(geom, water);
            RcConfig config = CreateConfig(settings);
            var builder = new RcBuilder();
            List<RcBuilderResult> tiles = builder.BuildTiles(geom, config, keepInterResults: false, buildAll: true, threads: threads);

            var navParams = new DtNavMeshParams
            {
                orig = geom.GetMeshBoundsMin(),
                tileWidth = settings.TileSizeVoxels * settings.CellSize,
                tileHeight = settings.TileSizeVoxels * settings.CellSize,
                maxTiles = Math.Max(tiles.Count, 1),
                maxPolys = MaxPolysPerTile
            };

            var mesh = new DtNavMesh();
            mesh.Init(navParams, settings.VertsPerPoly);

            int added = 0;
            int tilesWithPolys = 0;
            for (int i = 0; i < tiles.Count; i++)
            {
                RcPolyMesh? polyMesh = tiles[i].Mesh;
                if (polyMesh != null && polyMesh.npolys > 0)
                    tilesWithPolys++;

                DtMeshData? data = CreateTile(tiles[i], config, settings);
                if (data == null)
                    continue;

                DtStatus status = mesh.AddTile(data, 0, 0, out _);
                if (status.Failed())
                {
                    throw new InvalidDataException(
                        "Detour rejected navmesh tile "
                        + tiles[i].TileX
                        + ","
                        + tiles[i].TileZ
                        + " for playfield "
                        + collision.PlayfieldId
                        + ".");
                }

                added++;
            }

            if (added == 0)
            {
                throw new InvalidDataException(
                    "Recast produced no walkable tiles for playfield "
                    + collision.PlayfieldId
                    + " (sourceTriangles="
                    + triangleCount
                    + " recastTiles="
                    + tiles.Count
                    + " tilesWithPolys="
                    + tilesWithPolys
                    + ").");
            }

            return new NavMeshBakeResult(collision.PlayfieldId, triangleCount, added, mesh, settings);
        }

        public static NavMeshBakeResult Bake(string gameDataRoot, int playfieldId, NavMeshBuildSettings? settings = null)
            => Bake(
                PlayfieldCollisionResolver.Resolve(gameDataRoot, playfieldId),
                settings,
                NavMeshAreas.LoadPlayfieldWater(gameDataRoot, playfieldId));

        public static NavMeshBakeResult Bake(
            string gameDataRoot,
            AcgBuildingGenerator generator,
            NavMeshBuildSettings? settings = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(gameDataRoot);
            ArgumentNullException.ThrowIfNull(generator);
            return Bake(DungeonCollisionBuilder.Build(gameDataRoot, generator), settings);
        }

        /// <summary>
        /// One convex volume per water triangle, from below the whole mesh up to that triangle's surface: every walkable
        /// span under it is marked water, while a bridge or shore above the surface stays ground.
        /// </summary>
        static void AddWaterVolumes(RcSampleInputGeomProvider geom, IReadOnlyList<PlayfieldWaterEntry> water)
        {
            float floor = geom.GetMeshBoundsMin().Y - 1f;
            var area = new RcAreaModification(NavMeshAreas.Water);
            for (int w = 0; w < water.Count; w++)
            {
                float[]? v = water[w].Vertices;
                short[]? tri = water[w].Triangles;
                if (v == null || tri == null)
                    continue;

                for (int i = 0; i + 2 < tri.Length; i += 3)
                {
                    int a = tri[i] * 3, b = tri[i + 1] * 3, c = tri[i + 2] * 3;
                    if (a < 0 || b < 0 || c < 0 || a + 2 >= v.Length || b + 2 >= v.Length || c + 2 >= v.Length)
                        continue;

                    // Edge-on to the ground (a wall of the water volume) covers no column.
                    float cross = ((v[b] - v[a]) * (v[c + 2] - v[a + 2])) - ((v[b + 2] - v[a + 2]) * (v[c] - v[a]));
                    if (MathF.Abs(cross) < 1e-4f)
                        continue;

                    float surface = MathF.Max(v[a + 1], MathF.Max(v[b + 1], v[c + 1]));
                    if (surface <= floor)
                        continue;

                    geom.AddConvexVolume(
                        [v[a], v[a + 1], v[a + 2], v[b], v[b + 1], v[b + 2], v[c], v[c + 1], v[c + 2]],
                        floor,
                        surface,
                        area);
                }
            }
        }

        static float[] PadVerticalBounds(float[] vertices, NavMeshBuildSettings settings)
        {
            if (vertices.Length < 3)
                return vertices;

            float maxY = vertices[1];
            for (int i = 1; i < vertices.Length; i += 3)
                maxY = MathF.Max(maxY, vertices[i]);

            float pad = settings.AgentHeight + settings.AgentMaxClimb + (settings.CellHeight * 4f);
            var padded = new float[vertices.Length + 3];
            Array.Copy(vertices, padded, vertices.Length);
            padded[vertices.Length] = vertices[0];
            padded[vertices.Length + 1] = maxY + pad;
            padded[vertices.Length + 2] = vertices[2];
            return padded;
        }

        static RcConfig CreateConfig(NavMeshBuildSettings settings)
        {
            float cell = settings.CellSize;
            float minRegion = settings.RegionMinSize * settings.RegionMinSize * cell * cell;
            float mergeRegion = settings.RegionMergeSize * settings.RegionMergeSize * cell * cell;
            return new RcConfig(
                useTiles: true,
                tileSizeX: settings.TileSizeVoxels,
                tileSizeZ: settings.TileSizeVoxels,
                borderSize: RcConfig.CalcBorder(settings.AgentRadius, cell),
                partition: RcPartition.WATERSHED,
                cellSize: cell,
                cellHeight: settings.CellHeight,
                agentMaxSlope: settings.AgentMaxSlope,
                agentHeight: settings.AgentHeight,
                agentRadius: settings.AgentRadius,
                agentMaxClimb: settings.AgentMaxClimb,
                minRegionArea: minRegion,
                mergeRegionArea: mergeRegion,
                edgeMaxLen: settings.EdgeMaxLen,
                edgeMaxError: settings.EdgeMaxError,
                vertsPerPoly: settings.VertsPerPoly,
                detailSampleDist: settings.DetailSampleDist,
                detailSampleMaxError: settings.DetailSampleMaxError,
                filterLowHangingObstacles: true,
                filterLedgeSpans: true,
                filterWalkableLowHeightSpans: true,
                walkableAreaMod: new RcAreaModification(RcRecast.RC_WALKABLE_AREA),
                buildMeshDetail: true);
        }

        static DtMeshData? CreateTile(RcBuilderResult tile, RcConfig config, NavMeshBuildSettings settings)
        {
            RcPolyMesh? poly = tile.Mesh;
            if (poly == null || poly.nverts <= 0 || poly.npolys <= 0)
                return null;

            if (poly.flags == null || poly.flags.Length < poly.npolys)
                poly.flags = new int[poly.npolys];
            for (int i = 0; i < poly.npolys; i++)
                poly.flags[i] = WalkablePolyFlags;

            RcPolyMeshDetail? detail = tile.MeshDetail;
            var create = new DtNavMeshCreateParams
            {
                verts = poly.verts,
                vertCount = poly.nverts,
                polys = poly.polys,
                polyAreas = poly.areas,
                polyFlags = poly.flags,
                polyCount = poly.npolys,
                nvp = poly.nvp,
                detailMeshes = detail?.meshes,
                detailVerts = detail?.verts,
                detailVertsCount = detail?.nverts ?? 0,
                detailTris = detail?.tris,
                detailTriCount = detail?.ntris ?? 0,
                walkableHeight = settings.AgentHeight,
                walkableRadius = settings.AgentRadius,
                walkableClimb = settings.AgentMaxClimb,
                bmin = poly.bmin,
                bmax = poly.bmax,
                cs = config.Cs,
                ch = config.Ch,
                buildBvTree = true,
                tileX = tile.TileX,
                tileZ = tile.TileZ
            };

            return DtNavMeshBuilder.CreateNavMeshData(create);
        }
    }
}
