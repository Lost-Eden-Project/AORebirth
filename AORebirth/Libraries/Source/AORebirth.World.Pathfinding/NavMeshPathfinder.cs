namespace AORebirth.World.Pathfinding
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Numerics;

    using AORebirth.Core.GameData;
    using AORebirth.World.Collision;

    using DotRecast.Core.Numerics;
    using DotRecast.Detour;

    /// <summary>
    /// Tick-thread Detour query over a GameData <c>Navmesh.dat</c>. One instance per playfield.
    /// Tiles stream in from the file around each query and out again once idle (<see cref="NavMeshTileStreamer"/>),
    /// so a playfield's resident navmesh is the part being walked on, not the whole bake.
    /// </summary>
    public sealed class NavMeshPathfinder : IDisposable
    {
        const int MaxPathPolys = 256;
        const int MaxStraightPath = 256;
        // DotRecast's default node pool grows without bound. Native Detour stops at maxNodes.
        const int MaxSearchIters = 4096;
        const float StartSkipEpsilon = 0.05f;
        const float PathQueryHorizontal = 8f;
        const float OnMeshHorizontal = 2f;

        /// <summary>Search radius used when placing an NPC onto the mesh at spawn.</summary>
        public const float SpawnSnapExtent = 64f;

        /// <summary>XZ half-width of the column <see cref="TrySnapDown"/> gathers polygons from.</summary>
        const float ColumnHalfWidth = 0.05f;

        /// <summary>Stacked floors a column can hold before the rest are ignored.</summary>
        const int MaxColumnPolys = 64;

        /// <summary>
        /// Tiles loaded for a path search form a corridor along the start→end segment. Its half-width is half
        /// the distance, held between these bounds, so a route can swing around an obstacle without loading the
        /// square a diagonal bounding box would.
        /// </summary>
        const float CorridorMarginMin = 32f;

        const float CorridorMarginMax = 128f;

        /// <summary>
        /// A search that ran out of loaded mesh (Partial) is retried once with a wider corridor: one and a half
        /// times the distance, held between these bounds. A Partial after that is a detour longer than the search
        /// could have explored under its iteration cap anyway.
        /// </summary>
        const float RetryMarginMin = 96f;

        const float RetryMarginMax = 320f;

        /// <summary>Share of the per-request tile budget a corridor may plan for; the rest absorbs tile-grid rounding.</summary>
        const float CorridorBudgetFill = 0.85f;

        /// <summary>Half-width of the box kept loaded around each connected player (<see cref="Prefetch"/>).</summary>
        public const float PrefetchExtent = 64f;

        readonly NavMeshTileStreamer _streamer;
        readonly DtNavMeshQuery _query;
        readonly DtQueryDefaultFilter _filter;
        readonly RcVec3f _extents;
        readonly NavMeshIslands? _islands;
        readonly long[] _columnPolys = new long[MaxColumnPolys];

        // Reach-map flood scratch, grown on demand.
        long[] _reachRefs = [];
        long[] _reachParents = [];
        float[] _reachCosts = [];

        NavMeshPathfinder(int playfieldId, NavMeshTileStreamer streamer, NavMeshIslands? islands, NavMeshBuildSettings settings)
        {
            PlayfieldId = playfieldId;
            Settings = settings;
            _streamer = streamer;
            _query = new DtNavMeshQuery(streamer.Mesh);
            _filter = new DtQueryDefaultFilter();
            float pathHoriz = MathF.Max(settings.AgentRadius, PathQueryHorizontal);
            float pathVert = settings.AgentHeight + settings.AgentMaxClimb + PathQueryHorizontal;
            _extents = new RcVec3f(pathHoriz, pathVert, pathHoriz);
            _islands = islands;
        }

        /// <summary>Separate connected regions of this navmesh; 0 when the pre-check is off (<see cref="IslandCheckEnabled"/>).</summary>
        public int IslandCount => _islands?.Count ?? 0;

        /// <summary>False for a mesh with off-mesh connections: every search then runs rather than being pre-answered.</summary>
        public bool IslandCheckEnabled => _islands != null;

        public int PlayfieldId { get; }

        public NavMeshBuildSettings Settings { get; }

        /// <summary>How the most recent <see cref="TryFindPath"/> ended. Diagnostics only; tick-thread state.</summary>
        public PathSearchOutcome LastOutcome { get; private set; }

        /// <summary>Corridor search iterations the most recent <see cref="TryFindPath"/> used (cap <see cref="SearchIterationCap"/>).</summary>
        public int LastIterations { get; private set; }

        public static int SearchIterationCap => MaxSearchIters;

        /// <summary>What the tile window holds right now.</summary>
        public NavMeshResidency Residency => new(
            _streamer.Index.Count,
            _streamer.LoadedCount,
            _streamer.Capacity,
            _streamer.TileLoads,
            _streamer.TileUnloads,
            _streamer.LoadMilliseconds,
            _streamer.Index.TotalPolys);

        public static bool TryLoad(string gameDataRoot, int playfieldId, out NavMeshPathfinder? pathfinder)
        {
            return TryLoad(gameDataRoot, playfieldId, out pathfinder, out _);
        }

        /// <param name="failure">Why a present mesh was rejected; null when it loaded or there is none.</param>
        public static bool TryLoad(
            string gameDataRoot,
            int playfieldId,
            out NavMeshPathfinder? pathfinder,
            out string? failure)
        {
            pathfinder = null;
            failure = null;
            if (string.IsNullOrWhiteSpace(gameDataRoot) || playfieldId <= 0)
                return false;
            if (DungeonPlayfieldKinds.IsStyleTemplate(gameDataRoot, playfieldId))
                return false;

            string meshPath = Path.Combine(gameDataRoot, GameDataPaths.PlayfieldNavMeshRelativePath(playfieldId));
            if (!File.Exists(meshPath))
                return false;

            NavMeshBuildSettings settings;
            try
            {
                settings = NavMeshBuildSettings.Load(NavMeshBuildSettings.ResolvePath(gameDataRoot));
            }
            catch (Exception exception)
            {
                failure = "settings: " + exception.Message;
                return false;
            }

            NavMeshTileStreamer? streamer = null;
            try
            {
                NavMeshTileIndex index = NavMeshTileIndex.Build(meshPath);
                streamer = new NavMeshTileStreamer(index, meshPath);
                NavMeshIslands? islands = NavMeshIslands.Build(index, streamer.File);
                pathfinder = new NavMeshPathfinder(playfieldId, streamer, islands, settings);
                return true;
            }
            catch (Exception exception)
            {
                streamer?.Dispose();
                failure = "mesh: " + exception.Message;
                return false;
            }
        }

        /// <summary>Releases tiles no query has touched lately. Call once per playfield tick.</summary>
        public void Tick() => _streamer.Sweep(Now());

        /// <summary>
        /// Keeps the tiles around a player resident and loads new ones a row at a time as they move, so the first
        /// NPC to path near them pays nothing. Skipped when the window is busy, leaving it to demand loading.
        /// </summary>
        public void Prefetch(Vector3 at)
        {
            if (_streamer.LoadedCount >= NavMeshTileStreamer.PrefetchOccupancyLimit)
                return;
            EnsureAround(at, PrefetchExtent);
        }

        /// <summary>
        /// Whether a tile has been unloaded since <paramref name="map"/> was flooded. Its polygon references may
        /// then be dead (a reloaded tile gets fresh references), so the owner should rebuild it.
        /// </summary>
        public bool IsStale(ReachMap map)
        {
            ArgumentNullException.ThrowIfNull(map);
            return map.BuiltStamp < _streamer.LastUnloadStamp;
        }

        public bool TrySnap(Vector3 position, out Vector3 snapped)
            => TrySnap(position, MathF.Max(Settings.AgentRadius, PathQueryHorizontal), out snapped);

        public bool TrySnap(Vector3 position, float extent, out Vector3 snapped)
        {
            snapped = default;
            if (extent <= 0f)
                return false;

            EnsureAround(position, extent);
            float vert = MathF.Max(extent, Settings.AgentHeight + Settings.AgentMaxClimb);
            DtStatus status = _query.FindNearestPoly(
                position,
                new RcVec3f(extent, vert, extent),
                _filter,
                out long polyRef,
                out RcVec3f nearest,
                out _);
            if (status.Failed() || polyRef == 0)
                return false;

            snapped = new Vector3(nearest.X, nearest.Y, nearest.Z);
            return true;
        }

        /// <summary>
        /// Snaps straight down: the highest polygon under (<paramref name="position"/>.X, .Z) whose surface is at
        /// or below the position (plus <see cref="NavMeshBuildSettings.AgentMaxClimb"/> for feet placed slightly
        /// under the floor), searching at most <paramref name="maxDrop"/> down. Taking the first surface below,
        /// not the nearest in 3D, keeps a placement over a pit or stairwell on the floor under it rather than on
        /// the lip beside it; a bridge overhead is ignored and a bridge underneath wins over the ground below it.
        /// </summary>
        public bool TrySnapDown(Vector3 position, float maxDrop, out Vector3 snapped)
        {
            snapped = default;
            if (maxDrop <= 0f)
                return false;

            EnsureAround(position, ColumnHalfWidth);
            float above = Settings.AgentMaxClimb;
            float half = (maxDrop + above) * 0.5f;
            var center = new RcVec3f(position.X, position.Y + above - half, position.Z);
            var extents = new RcVec3f(ColumnHalfWidth, half, ColumnHalfWidth);

            long[] polys = _columnPolys;
            var collect = new DtCollectPolysQuery(polys, polys.Length);
            if (_query.QueryPolygons(center, extents, _filter, ref collect).Failed())
                return false;

            bool found = false;
            float best = float.NegativeInfinity;
            var at = new RcVec3f(position.X, position.Y, position.Z);
            for (int i = 0; i < collect.NumCollected(); i++)
            {
                // GetPolyHeight only succeeds when the XZ point lies inside the polygon.
                if (_query.GetPolyHeight(polys[i], at, out float height).Failed())
                    continue;
                if (height > position.Y + above || height < position.Y - maxDrop)
                    continue;
                if (height <= best)
                    continue;

                best = height;
                found = true;
            }

            if (!found)
                return false;

            snapped = new Vector3(position.X, best, position.Z);
            return true;
        }

        /// <summary>
        /// A random navmesh point within <paramref name="radius"/> (XZ) of <paramref name="centre"/>, on polygons
        /// connected to the one under the centre, so the point can be walked to from there. False when the centre is
        /// not near the mesh.
        /// </summary>
        public bool TryFindRandomPointWithin(Vector3 centre, float radius, Random random, out Vector3 point)
        {
            ArgumentNullException.ThrowIfNull(random);
            point = default;
            if (radius <= 0f)
                return false;

            EnsureAround(centre, radius + _extents.X);
            var at = new RcVec3f(centre.X, centre.Y, centre.Z);
            if (_query.FindNearestPoly(at, _extents, _filter, out long startRef, out RcVec3f nearest, out _).Failed()
                || startRef == 0)
                return false;

            if (_query.FindRandomPointWithinCircle(startRef, nearest, radius, _filter, new DotRecast.Core.RcRand(random),
                    out long randomRef, out RcVec3f randomPt).Failed()
                || randomRef == 0)
                return false;

            point = new Vector3(randomPt.X, randomPt.Y, randomPt.Z);
            return true;
        }

        public bool TryCanReach(Vector3 start, Vector3 end)
        {
            EnsurePathRegion(start, end, widest: false);
            if (!TryResolveEnds(start, end, out PolyEnds ends))
                return false;
            if (!IsOnMesh(end, ends.EndPt))
                return false;

            Span<long> path = stackalloc long[MaxPathPolys];
            return TrySearchCorridor(start, end, ends, path, out int pathCount)
                && pathCount > 0
                && path[pathCount - 1] == ends.EndRef;
        }

        public bool TryFindPath(Vector3 start, Vector3 end, List<Vector3> waypoints)
        {
            ArgumentNullException.ThrowIfNull(waypoints);
            waypoints.Clear();
            LastIterations = 0;

            EnsurePathRegion(start, end, widest: false);
            if (!TryResolveEnds(start, end, out PolyEnds ends))
            {
                LastOutcome = PathSearchOutcome.NoEnds;
                return false;
            }
            if (!IsOnMesh(end, ends.EndPt))
            {
                LastOutcome = PathSearchOutcome.EndOffMesh;
                return false;
            }

            // No route exists between separate islands; skip the search that would run to its cap proving it.
            if (_islands != null && _islands.AreDisconnected(_streamer.GlobalPolyId(ends.StartRef), _streamer.GlobalPolyId(ends.EndRef)))
            {
                LastOutcome = PathSearchOutcome.Disconnected;
                return false;
            }

            Span<long> path = stackalloc long[MaxPathPolys];
            if (!TrySearchCorridor(start, end, ends, path, out int pathCount) || pathCount <= 0)
                return false;

            Span<DtStraightPath> straight = stackalloc DtStraightPath[MaxStraightPath];
            DtStatus straightStatus = _query.FindStraightPath(
                ends.StartPt,
                ends.EndPt,
                path,
                pathCount,
                straight,
                out int straightCount,
                straight.Length,
                0);
            if (straightStatus.Failed() || straightCount <= 0)
            {
                LastOutcome = PathSearchOutcome.NoStraightPath;
                return false;
            }

            int first = 0;
            if (straightCount > 1 && IsNear(straight[0].pos, start))
                first = 1;

            for (int i = first; i < straightCount; i++)
                waypoints.Add(straight[i].pos);

            LastOutcome = waypoints.Count > 0 ? PathSearchOutcome.Found : PathSearchOutcome.NoStraightPath;
            return waypoints.Count > 0;
        }

        /// <summary>
        /// Floods outward from <paramref name="target"/> over polygons whose route stays within
        /// <paramref name="radius"/> of it, so walkers can test reachability with <see cref="Reaches"/>. The end is
        /// resolved exactly as <see cref="TryFindPath"/> resolves it. More than <paramref name="maxPolys"/> polygons
        /// gives <see cref="ReachMapKind.TooLarge"/>. Tick-thread state; not thread safe.
        /// </summary>
        public ReachMap BuildReachMap(Vector3 target, float radius, int maxPolys)
        {
            EnsureAround(target, radius + _extents.X);
            if (!TrySnap(target, out _))
                return new ReachMap(ReachMapKind.TargetNoMesh, null, target, 0, _streamer.Stamp);

            DtStatus status = _query.FindNearestPoly(target, _extents, _filter, out long endRef, out RcVec3f endPt, out _);
            if (status.Failed() || endRef == 0)
                return new ReachMap(ReachMapKind.TargetNoMesh, null, target, 0, _streamer.Stamp);

            var end = new Vector3(endPt.X, endPt.Y, endPt.Z);
            if (!IsOnMesh(target, endPt))
                return new ReachMap(ReachMapKind.TargetOffMesh, null, end, 0, _streamer.Stamp);

            // Stamped right before the flood: only an unload after this can retire polygons the map holds.
            long stamp = _streamer.Stamp;

            // One slot past the limit tells "exactly full" from "ran out of room".
            int capacity = maxPolys + 1;
            if (_reachRefs.Length < capacity)
            {
                _reachRefs = new long[capacity];
                _reachParents = new long[capacity];
                _reachCosts = new float[capacity];
            }

            DtStatus flood = _query.FindPolysAroundCircle(
                endRef,
                endPt,
                radius,
                _filter,
                _reachRefs.AsSpan(0, capacity),
                _reachParents.AsSpan(0, capacity),
                _reachCosts.AsSpan(0, capacity),
                out int count,
                capacity);
            if (flood.Failed() || count > maxPolys)
                return new ReachMap(ReachMapKind.TooLarge, null, end, count, stamp);

            var polys = new HashSet<long>(count);
            for (int i = 0; i < count; i++)
                polys.Add(_reachRefs[i]);
            return new ReachMap(ReachMapKind.Flooded, polys, end, count, stamp);
        }

        /// <summary>
        /// Whether a walker at <paramref name="start"/> reaches <paramref name="map"/>'s target. A walker off the mesh
        /// counts as reaching (holes and off-mesh links are not walls), as does any walker when the target is nowhere
        /// near the mesh.
        /// </summary>
        public ReachAnswer Reaches(ReachMap map, Vector3 start)
        {
            ArgumentNullException.ThrowIfNull(map);
            if (!TrySnap(start, out _))
                return ReachAnswer.Reachable;

            switch (map.Kind)
            {
                case ReachMapKind.TargetNoMesh:
                    return ReachAnswer.Reachable;
                case ReachMapKind.TargetOffMesh:
                    return ReachAnswer.Unreachable;
                case ReachMapKind.TooLarge:
                    return ReachAnswer.TooLarge;
            }

            DtStatus status = _query.FindNearestPoly(start, _extents, _filter, out long startRef, out _, out _);
            if (status.Failed() || startRef == 0)
                return ReachAnswer.Reachable;

            return map.Contains(startRef) ? ReachAnswer.Reachable : ReachAnswer.Unreachable;
        }

        public void Dispose()
        {
            _streamer.Dispose();
        }

        static long Now() => Environment.TickCount64;

        /// <summary>Loads the tiles under an XZ box of half-width <paramref name="extent"/> around <paramref name="at"/>.</summary>
        void EnsureAround(Vector3 at, float extent)
            => _streamer.EnsureRegion(at.X - extent, at.Z - extent, at.X + extent, at.Z + extent, Now());

        /// <summary>Loads the corridor a route between the two points may cross. Returns how many tiles were newly loaded.</summary>
        int EnsurePathRegion(Vector3 start, Vector3 end, bool widest)
        {
            float dx = end.X - start.X;
            float dz = end.Z - start.Z;
            float distance = MathF.Sqrt((dx * dx) + (dz * dz));
            float margin = widest
                ? Math.Clamp(distance * 1.5f, RetryMarginMin, RetryMarginMax)
                : Math.Clamp(distance * 0.5f, CorridorMarginMin, CorridorMarginMax);

            // The corridor must fit the window's corridor budget or its far end is cut off. The streamer claims
            // every tile whose centre is within the half-width plus half a tile diagonal, a capsule of area
            // 2·r·d + π·r², so the widest affordable reach solves that for r. A route too long even for the
            // minimum margin is loaded as far as the budget reaches and fails like any out-of-reach route (the
            // search's iteration cap bounded such routes before streaming too).
            DtNavMeshParams tiles = _streamer.Index.Params;
            float halfDiagonal = 0.5f * MathF.Sqrt((tiles.tileWidth * tiles.tileWidth) + (tiles.tileHeight * tiles.tileHeight));
            float budgetArea = NavMeshTileStreamer.MaxCorridorTiles * tiles.tileWidth * tiles.tileHeight * CorridorBudgetFill;
            float affordableReach = (-distance + MathF.Sqrt((distance * distance) + (MathF.PI * budgetArea))) / MathF.PI;
            float reach = margin + _extents.X + halfDiagonal;
            if (reach > affordableReach)
                margin = MathF.Max(CorridorMarginMin, affordableReach - _extents.X - halfDiagonal);
            long now = Now();
            int loaded = _streamer.EnsureCorridor(start.X, start.Z, end.X, end.Z, margin + _extents.X, now);

            // A corridor past the window's budget is cut short; the ends must still resolve.
            loaded += _streamer.EnsureRegion(start.X - _extents.X, start.Z - _extents.X, start.X + _extents.X, start.Z + _extents.X, now);
            loaded += _streamer.EnsureRegion(end.X - _extents.X, end.Z - _extents.X, end.X + _extents.X, end.Z + _extents.X, now);
            return loaded;
        }

        /// <summary>
        /// The corridor search. On a full mesh two polygons on one island always connect, so a Partial result
        /// means the route left the loaded tiles: a wider corridor is loaded once and the search repeated.
        /// </summary>
        bool TrySearchCorridor(Vector3 start, Vector3 end, in PolyEnds ends, Span<long> path, out int pathCount)
        {
            if (SearchCorridor(ends, path, out pathCount))
                return true;
            if (LastOutcome != PathSearchOutcome.Partial || EnsurePathRegion(start, end, widest: true) == 0)
                return false;

            return SearchCorridor(ends, path, out pathCount);
        }

        bool SearchCorridor(in PolyEnds ends, Span<long> path, out int pathCount)
        {
            pathCount = 0;
            DtStatus init = _query.InitSlicedFindPath(
                ends.StartRef,
                ends.EndRef,
                ends.StartPt,
                ends.EndPt,
                _filter,
                0);
            if (init.Failed())
            {
                LastOutcome = PathSearchOutcome.SearchFailed;
                return false;
            }

            DtStatus update = _query.UpdateSlicedFindPath(MaxSearchIters, out int iterations);
            LastIterations = iterations;
            if (update.InProgress())
            {
                LastOutcome = PathSearchOutcome.IterationCap;
                return false;
            }
            if (update.Failed() || update.IsPartial() || !update.Succeeded())
            {
                LastOutcome = update.IsPartial() ? PathSearchOutcome.Partial : PathSearchOutcome.SearchFailed;
                return false;
            }

            DtStatus finalized = _query.FinalizeSlicedFindPath(path, out pathCount, path.Length);
            if (finalized.Failed() || finalized.IsPartial() || pathCount <= 0)
            {
                LastOutcome = finalized.IsPartial() ? PathSearchOutcome.Partial : PathSearchOutcome.SearchFailed;
                return false;
            }

            return true;
        }

        bool TryResolveEnds(Vector3 start, Vector3 end, out PolyEnds ends)
        {
            ends = default;
            RcVec3f startPos = start;
            RcVec3f endPos = end;
            DtStatus startStatus = _query.FindNearestPoly(
                startPos,
                _extents,
                _filter,
                out long startRef,
                out RcVec3f startPt,
                out _);
            if (startStatus.Failed() || startRef == 0)
                return false;

            DtStatus endStatus = _query.FindNearestPoly(
                endPos,
                _extents,
                _filter,
                out long endRef,
                out RcVec3f endPt,
                out _);
            if (endStatus.Failed() || endRef == 0)
                return false;

            ends = new PolyEnds(startRef, endRef, startPt, endPt);
            return true;
        }

        bool IsOnMesh(Vector3 destination, RcVec3f snapped)
        {
            float dx = snapped.X - destination.X;
            float dz = snapped.Z - destination.Z;
            float horiz = MathF.Sqrt((dx * dx) + (dz * dz));
            if (horiz > OnMeshHorizontal)
                return false;

            float vertSlack = Settings.AgentHeight + Settings.AgentMaxClimb;
            return MathF.Abs(snapped.Y - destination.Y) <= vertSlack;
        }

        static bool IsNear(RcVec3f a, RcVec3f b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            float dz = a.Z - b.Z;
            return (dx * dx) + (dy * dy) + (dz * dz) <= StartSkipEpsilon * StartSkipEpsilon;
        }

        readonly struct PolyEnds
        {
            public PolyEnds(long startRef, long endRef, RcVec3f startPt, RcVec3f endPt)
            {
                StartRef = startRef;
                EndRef = endRef;
                StartPt = startPt;
                EndPt = endPt;
            }

            public long StartRef { get; }

            public long EndRef { get; }

            public RcVec3f StartPt { get; }

            public RcVec3f EndPt { get; }
        }
    }

    /// <summary>What a pathfinder's tile window holds: baked tiles in the file versus tiles resident now.</summary>
    public readonly record struct NavMeshResidency(
        int IndexedTiles,
        int LoadedTiles,
        int WindowCapacity,
        long TileLoads,
        long TileUnloads,
        double LoadMilliseconds,
        int TotalPolys);

    /// <summary>How a <see cref="NavMeshPathfinder.TryFindPath"/> call ended.</summary>
    public enum PathSearchOutcome
    {
        Found = 0,

        /// <summary>Start or end had no navmesh polygon nearby.</summary>
        NoEnds,

        /// <summary>The end snapped to a polygon too far from the requested point.</summary>
        EndOffMesh,

        /// <summary>The corridor search failed outright.</summary>
        SearchFailed,

        /// <summary>The corridor search ran out of iterations before reaching the end.</summary>
        IterationCap,

        /// <summary>The corridor search only reached part of the way (end unreachable).</summary>
        Partial,

        /// <summary>A corridor was found but no straight path could be built along it.</summary>
        NoStraightPath,

        /// <summary>Start and end are on separate navmesh islands; answered without searching.</summary>
        Disconnected
    }
}
