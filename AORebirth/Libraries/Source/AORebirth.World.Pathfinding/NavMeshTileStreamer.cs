namespace AORebirth.World.Pathfinding
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using Microsoft.Win32.SafeHandles;

    using DotRecast.Detour;
    using DotRecast.Detour.Io;

    /// <summary>
    /// A Detour mesh holding only the tiles recent queries touched, read from <c>Navmesh.dat</c> on demand through
    /// a <see cref="NavMeshTileIndex"/>. Queries declare the world box they will look at with
    /// <see cref="EnsureRegion"/>; tiles nothing has touched for <see cref="KeepMs"/> are dropped by
    /// <see cref="Sweep"/>, and the oldest go first when the window is full. Tick-thread state.
    /// </summary>
    internal sealed class NavMeshTileStreamer : IDisposable
    {
        /// <summary>
        /// Tile slots in the window: about 3 MB of fixed bookkeeping per playfield, and up to ~48 MB of tiles when
        /// a zone is packed (the largest baked tiles cost about 1.5 KB each resident). Sized so thirty players
        /// spread across one zone, each pinning a prefetch box, fit without evicting each other.
        /// </summary>
        public const int WindowCapacity = 32768;

        /// <summary>A tile untouched this long is released by the next sweep.</summary>
        public const int KeepMs = 30_000;

        /// <summary>One box region may claim at most this many tiles; a larger box is clamped around its centre.</summary>
        public const int MaxRegionTiles = WindowCapacity / 4;

        /// <summary>A route corridor may claim up to half the window: long routes on dense meshes need the room.</summary>
        public const int MaxCorridorTiles = WindowCapacity / 2;

        /// <summary>Prefetching stops above this occupancy so demand loading keeps the window for live queries.</summary>
        public const int PrefetchOccupancyLimit = WindowCapacity * 3 / 4;

        const int SweepIntervalMs = 1000;

        readonly NavMeshTileIndex _index;
        readonly SafeFileHandle _file;
        readonly DtNavMesh _mesh;
        readonly DtMeshDataReader _reader = new();
        readonly int _capacity;

        // Per window slot (Detour tile index): the file entry resident there and when a query last touched it.
        readonly int[] _slotEntry;
        readonly long[] _slotTouchMs;
        readonly long[] _slotStamp;
        readonly long[] _slotRef;

        // Per file entry: the slot holding it, or -1.
        readonly int[] _entrySlot;

        readonly List<(long TouchMs, int Slot)> _evictScratch = new();
        byte[] _scratch;
        long _lastSweepMs;
        int _busy;
        bool _disposed;

        public NavMeshTileStreamer(NavMeshTileIndex index, string path)
        {
            ArgumentNullException.ThrowIfNull(index);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            _index = index;
            _capacity = Math.Min(WindowCapacity, index.Count);
            _file = System.IO.File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);

            DtNavMeshParams meshParams = index.Params;
            meshParams.maxTiles = _capacity;
            _mesh = new DtNavMesh();
            DtStatus status = _mesh.Init(meshParams, index.MaxVertsPerPoly);
            if (status.Failed())
            {
                _file.Dispose();
                throw new InvalidDataException("Detour rejected the navmesh parameters.");
            }

            _slotEntry = new int[_capacity];
            _slotTouchMs = new long[_capacity];
            _slotStamp = new long[_capacity];
            _slotRef = new long[_capacity];
            Array.Fill(_slotEntry, -1);
            _entrySlot = new int[index.Count];
            Array.Fill(_entrySlot, -1);
            _scratch = new byte[Math.Max(index.LargestTileBytes, 1024)];
        }

        public DtNavMesh Mesh => _mesh;

        public NavMeshTileIndex Index => _index;

        /// <summary>The open <c>Navmesh.dat</c>; the streamer owns it.</summary>
        public SafeFileHandle File => _file;

        public int LoadedCount { get; private set; }

        public int Capacity => _capacity;

        public long TileLoads { get; private set; }

        public long TileUnloads { get; private set; }

        /// <summary>Time spent reading and parsing tiles, in Stopwatch ticks.</summary>
        public long LoadTicks { get; private set; }

        public double LoadMilliseconds => LoadTicks * 1000.0 / Stopwatch.Frequency;

        /// <summary>Counts <see cref="EnsureRegion"/> calls; a cheap "when" for cached polygon references.</summary>
        public long Stamp { get; private set; }

        /// <summary>The <see cref="Stamp"/> current when a tile was last removed; references older than it may be dead.</summary>
        public long LastUnloadStamp { get; private set; }

        /// <summary>
        /// Loads every tile overlapping the XZ box (missing tiles stay missing) and marks them touched.
        /// Returns how many tiles were newly loaded.
        /// </summary>
        public int EnsureRegion(float minX, float minZ, float maxX, float maxZ, long nowMs)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Enter();
            try
            {
                Stamp++;
                _index.TileLocation(minX, minZ, out int tx0, out int ty0);
                _index.TileLocation(maxX, maxZ, out int tx1, out int ty1);

                // Cap around the request's own centre first, then cut to the grid: a route near the map edge keeps
                // its box centred on itself rather than drifting toward the middle of the map.
                ClampSpan(ref tx0, ref tx1);
                ClampSpan(ref ty0, ref ty1);
                tx0 = Math.Max(tx0, _index.MinX);
                ty0 = Math.Max(ty0, _index.MinY);
                tx1 = Math.Min(tx1, _index.MinX + _index.Width - 1);
                ty1 = Math.Min(ty1, _index.MinY + _index.Height - 1);
                if (tx1 < tx0 || ty1 < ty0)
                    return 0;

                int loaded = 0;
                for (int ty = ty0; ty <= ty1; ty++)
                {
                    for (int tx = tx0; tx <= tx1; tx++)
                    {
                        int entry = _index.EntryAt(tx, ty);
                        if (entry < 0)
                            continue;

                        int slot = _entrySlot[entry];
                        if (slot >= 0)
                        {
                            _slotTouchMs[slot] = nowMs;
                            _slotStamp[slot] = Stamp;
                            continue;
                        }

                        if (TryLoad(entry, nowMs))
                            loaded++;
                    }
                }

                return loaded;
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>
        /// Loads the tiles within <paramref name="margin"/> of the segment A→B and marks them touched: the shape a
        /// route search explores, without the square a diagonal bounding box would cost. Stops at
        /// <see cref="MaxCorridorTiles"/>. Returns how many tiles were newly loaded.
        /// </summary>
        public int EnsureCorridor(float ax, float az, float bx, float bz, float margin, long nowMs)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Enter();
            try
            {
                Stamp++;
                _index.TileLocation(MathF.Min(ax, bx) - margin, MathF.Min(az, bz) - margin, out int tx0, out int ty0);
                _index.TileLocation(MathF.Max(ax, bx) + margin, MathF.Max(az, bz) + margin, out int tx1, out int ty1);
                tx0 = Math.Max(tx0, _index.MinX);
                ty0 = Math.Max(ty0, _index.MinY);
                tx1 = Math.Min(tx1, _index.MinX + _index.Width - 1);
                ty1 = Math.Min(ty1, _index.MinY + _index.Height - 1);
                if (tx1 < tx0 || ty1 < ty0)
                    return 0;

                // A tile is in the corridor when its centre lies within margin plus half its diagonal of the segment.
                float tw = _index.Params.tileWidth;
                float th = _index.Params.tileHeight;
                float reach = margin + (0.5f * MathF.Sqrt((tw * tw) + (th * th)));
                float reachSq = reach * reach;
                float dx = bx - ax;
                float dz = bz - az;
                float lengthSq = (dx * dx) + (dz * dz);

                int loaded = 0;
                int claimed = 0;
                for (int ty = ty0; ty <= ty1 && claimed < MaxCorridorTiles; ty++)
                {
                    float cz = _index.Params.orig.Z + ((ty + 0.5f) * th);
                    for (int tx = tx0; tx <= tx1 && claimed < MaxCorridorTiles; tx++)
                    {
                        int entry = _index.EntryAt(tx, ty);
                        if (entry < 0)
                            continue;

                        float cx = _index.Params.orig.X + ((tx + 0.5f) * tw);
                        float t = lengthSq > 0f ? Math.Clamp((((cx - ax) * dx) + ((cz - az) * dz)) / lengthSq, 0f, 1f) : 0f;
                        float px = ax + (t * dx) - cx;
                        float pz = az + (t * dz) - cz;
                        if ((px * px) + (pz * pz) > reachSq)
                            continue;

                        claimed++;
                        int slot = _entrySlot[entry];
                        if (slot >= 0)
                        {
                            _slotTouchMs[slot] = nowMs;
                            _slotStamp[slot] = Stamp;
                            continue;
                        }

                        if (TryLoad(entry, nowMs))
                            loaded++;
                    }
                }

                return loaded;
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>Releases tiles untouched for <see cref="KeepMs"/>. Cheap to call every tick; runs once a second.</summary>
        public void Sweep(long nowMs)
        {
            if (_disposed || LoadedCount == 0 || nowMs - _lastSweepMs < SweepIntervalMs)
                return;

            Enter();
            try
            {
                _lastSweepMs = nowMs;
                for (int slot = 0; slot < _capacity; slot++)
                {
                    if (_slotEntry[slot] >= 0 && nowMs - _slotTouchMs[slot] > KeepMs)
                        Unload(slot);
                }
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>The file entry a polygon reference lives in, or -1 when its tile is not resident (or the reference is stale).</summary>
        public int EntryOf(long polyRef)
        {
            if (polyRef == 0 || _mesh.GetTileByRef(polyRef) == null)
                return -1;
            int slot = DtDetour.DecodePolyIdTile(polyRef);
            return slot >= 0 && slot < _capacity ? _slotEntry[slot] : -1;
        }

        /// <summary>Mesh-wide polygon number (see <see cref="NavMeshTileIndex.PolyBase"/>), or -1 when not resident.</summary>
        public int GlobalPolyId(long polyRef)
        {
            int entry = EntryOf(polyRef);
            if (entry < 0)
                return -1;
            int poly = DtDetour.DecodePolyIdPoly(polyRef);
            return poly < _index.PolyCount(entry) ? _index.PolyBase(entry) + poly : -1;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _file.Dispose();
        }

        /// <summary>
        /// Tiles are small and the mesh mutates only here and in <see cref="Sweep"/>; both run on the owning
        /// playfield's tick thread. Two callers at once would corrupt Detour's tile table, so that is refused
        /// loudly instead of silently.
        /// </summary>
        void Enter()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                throw new InvalidOperationException("NavMeshTileStreamer used from two threads at once.");
        }

        void Exit() => System.Threading.Volatile.Write(ref _busy, 0);

        static void ClampSpan(ref int lo, ref int hi)
        {
            // Square cap: a region side longer than sqrt(MaxRegionTiles) is cut around its centre. Counted in
            // tiles on purpose: smaller tiles mean denser polygons, and the search's iteration cap reaches a
            // correspondingly shorter distance, so the cap tracks what a search could explore anyway.
            const int MaxSide = 90; // 90 * 90 <= MaxRegionTiles
            long span = (long)hi - lo + 1;
            if (span <= MaxSide)
                return;
            long centre = lo + (span / 2);
            lo = (int)(centre - (MaxSide / 2));
            hi = lo + MaxSide - 1;
        }

        bool TryLoad(int entry, long nowMs)
        {
            if (LoadedCount >= _capacity && !EvictOldest())
                return false;

            long started = Stopwatch.GetTimestamp();
            DtMeshData data = _index.ReadTile(_file, entry, ref _scratch, _reader);
            DtStatus status = _mesh.AddTile(data, 0, 0, out long tileRef);
            LoadTicks += Stopwatch.GetTimestamp() - started;
            if (status.Failed())
                return false;

            int slot = DtDetour.DecodePolyIdTile(tileRef);
            _slotEntry[slot] = entry;
            _slotTouchMs[slot] = nowMs;
            _slotStamp[slot] = Stamp;
            _slotRef[slot] = tileRef;
            _entrySlot[entry] = slot;
            LoadedCount++;
            TileLoads++;
            return true;
        }

        void Unload(int slot)
        {
            int entry = _slotEntry[slot];
            _mesh.RemoveTile(_slotRef[slot]);
            _slotEntry[slot] = -1;
            _slotRef[slot] = 0;
            _entrySlot[entry] = -1;
            LoadedCount--;
            TileUnloads++;
            LastUnloadStamp = Stamp;
        }

        /// <summary>Window full: drops the least recently touched quarter, never a tile the current region claimed.</summary>
        bool EvictOldest()
        {
            List<(long TouchMs, int Slot)> candidates = _evictScratch;
            candidates.Clear();
            for (int slot = 0; slot < _capacity; slot++)
            {
                if (_slotEntry[slot] >= 0 && _slotStamp[slot] != Stamp)
                    candidates.Add((_slotTouchMs[slot], slot));
            }

            if (candidates.Count == 0)
                return false;

            candidates.Sort(static (a, b) => a.TouchMs.CompareTo(b.TouchMs));
            int evict = Math.Min(candidates.Count, Math.Max(1, _capacity / 4));
            for (int i = 0; i < evict; i++)
                Unload(candidates[i].Slot);
            candidates.Clear();
            return true;
        }
    }
}
