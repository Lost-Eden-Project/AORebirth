namespace AORebirth.World.Pathfinding
{
    using System;
    using System.Buffers.Binary;
    using System.Collections.Generic;
    using Microsoft.Win32.SafeHandles;

    using DotRecast.Detour;

    /// <summary>
    /// Connected regions ("islands") of a navmesh, built once at load. Two polygons on different islands have no
    /// route between them in either direction, so a path query between them can fail without searching: an
    /// unreachable target otherwise costs a search that runs to its iteration cap.
    /// Built straight from the tile bytes, one row of tiles at a time and without allocating per tile: polygon
    /// neighbours inside a tile come from the baked neighbour table, and portal edges on facing tile borders
    /// are matched with Detour's slab-overlap rule made slightly more permissive. The pass can therefore only
    /// merge what Detour would link, never split it: "different islands" is always a true "no path".
    /// Polygons are numbered mesh-wide by <see cref="NavMeshTileIndex.PolyBase"/>.
    /// </summary>
    internal sealed class NavMeshIslands
    {
        // Detour shrinks portal slabs by 0.01 so touching endpoints do not connect; growing them instead keeps this a superset.
        const float SlabHorizontalSlack = 0.05f;
        const float SlabVerticalSlack = 0.25f;

        // Header offsets (bytes): polyCount int 6, vertCount int 7, walkableClimb float 17.
        const int PolyCountOffset = 6 * 4;
        const int VertCountOffset = 7 * 4;
        const int WalkableClimbOffset = 17 * 4;

        readonly ushort[]? _small;
        readonly int[]? _large;

        NavMeshIslands(ushort[]? small, int[]? large, int count)
        {
            _small = small;
            _large = large;
            Count = count;
        }

        /// <summary>Number of separate islands.</summary>
        public int Count { get; }

        /// <summary>
        /// Null when the mesh carries off-mesh connections: those link polygons by position, which this pass
        /// does not resolve, and a missed link could split an island, so the pre-check is disabled instead.
        /// </summary>
        public static NavMeshIslands? Build(NavMeshTileIndex index, SafeFileHandle file)
        {
            ArgumentNullException.ThrowIfNull(index);
            ArgumentNullException.ThrowIfNull(file);
            if (index.HasOffMeshConnections)
                return null;

            int total = index.TotalPolys;
            var parent = new int[total];
            for (int i = 0; i < total; i++)
                parent[i] = i;

            int nvp = index.MaxVertsPerPoly;
            int polyStride = (4 * nvp) + 4;
            var buffer = new byte[Math.Max(index.LargestTileBytes, 1024)];

            // Portal edges still waiting for their neighbour: the previous row's +Z edges per column, and the
            // previous tile's +X edges in this row. A missing tile clears them so nothing matches across a gap.
            var above = new List<Edge>[index.Width];
            for (int i = 0; i < above.Length; i++)
                above[i] = new List<Edge>();
            var left = new List<Edge>();
            var mineEast = new List<Edge>();
            var mineNorth = new List<Edge>();
            var mineWest = new List<Edge>();
            var mineSouth = new List<Edge>();

            for (int ty = index.MinY; ty < index.MinY + index.Height; ty++)
            {
                left.Clear();
                for (int col = 0; col < index.Width; col++)
                {
                    int entry = index.EntryAt(index.MinX + col, ty);
                    if (entry < 0)
                    {
                        above[col].Clear();
                        left.Clear();
                        continue;
                    }

                    index.ReadTileBytes(file, entry, ref buffer);
                    ReadOnlySpan<byte> tile = buffer;
                    int polyCount = BinaryPrimitives.ReadInt32LittleEndian(tile.Slice(PolyCountOffset));
                    int vertCount = BinaryPrimitives.ReadInt32LittleEndian(tile.Slice(VertCountOffset));
                    float climb = BinaryPrimitives.ReadSingleLittleEndian(tile.Slice(WalkableClimbOffset));
                    if (polyCount != index.PolyCount(entry))
                        throw new InvalidOperationException("Navmesh tile " + entry + " polygon count changed between index and read.");

                    int vertsOffset = NavMeshTileIndex.TileHeaderBytes;
                    int polysOffset = vertsOffset + (vertCount * 12);
                    int polyBase = index.PolyBase(entry);
                    mineEast.Clear();
                    mineNorth.Clear();
                    mineWest.Clear();
                    mineSouth.Clear();

                    for (int p = 0; p < polyCount; p++)
                    {
                        int poly = polysOffset + (p * polyStride);
                        int vc = tile[poly + (4 * nvp) + 2];
                        int global = polyBase + p;
                        for (int j = 0; j < vc; j++)
                        {
                            int nei = BinaryPrimitives.ReadUInt16LittleEndian(tile.Slice(poly + (2 * nvp) + (2 * j)));
                            if (nei == 0)
                                continue;

                            if ((nei & DtDetour.DT_EXT_LINK) == 0)
                            {
                                Union(parent, global, polyBase + nei - 1);
                                continue;
                            }

                            int side = nei & 0xff;
                            List<Edge>? bucket = side switch
                            {
                                0 => mineEast,
                                2 => mineNorth,
                                4 => mineWest,
                                6 => mineSouth,
                                _ => null
                            };
                            if (bucket == null)
                                continue;

                            int va = vertsOffset + (BinaryPrimitives.ReadUInt16LittleEndian(tile.Slice(poly + (2 * j))) * 12);
                            int vb = vertsOffset + (BinaryPrimitives.ReadUInt16LittleEndian(tile.Slice(poly + (2 * ((j + 1) % vc)))) * 12);
                            // Along the border: Z for the X-facing sides, X for the Z-facing sides.
                            int along = side == 0 || side == 4 ? 8 : 0;
                            bucket.Add(new Edge(
                                global,
                                BinaryPrimitives.ReadSingleLittleEndian(tile.Slice(va + along)),
                                BinaryPrimitives.ReadSingleLittleEndian(tile.Slice(va + 4)),
                                BinaryPrimitives.ReadSingleLittleEndian(tile.Slice(vb + along)),
                                BinaryPrimitives.ReadSingleLittleEndian(tile.Slice(vb + 4))));
                        }
                    }

                    MatchBorders(parent, mineWest, left, climb);
                    MatchBorders(parent, mineSouth, above[col], climb);

                    (left, mineEast) = (mineEast, left);
                    (above[col], mineNorth) = (mineNorth, above[col]);
                }
            }

            // Compact roots to small island ids.
            var idByRoot = new int[total];
            int count = 0;
            for (int i = 0; i < total; i++)
            {
                int root = Find(parent, i);
                if (idByRoot[root] == 0)
                    idByRoot[root] = ++count;
            }

            if (count <= ushort.MaxValue)
            {
                var small = new ushort[total];
                for (int i = 0; i < total; i++)
                    small[i] = (ushort)idByRoot[Find(parent, i)];
                return new NavMeshIslands(small, null, count);
            }

            var large = new int[total];
            for (int i = 0; i < total; i++)
                large[i] = idByRoot[Find(parent, i)];
            return new NavMeshIslands(null, large, count);
        }

        /// <summary>The island of a mesh-wide polygon number; 0 when unknown.</summary>
        public int IslandOf(int globalPolyId)
        {
            if (globalPolyId < 0)
                return 0;
            if (_small != null)
                return globalPolyId < _small.Length ? _small[globalPolyId] : 0;
            return globalPolyId < _large!.Length ? _large[globalPolyId] : 0;
        }

        /// <summary>True only when both polygons are known and on different islands.</summary>
        public bool AreDisconnected(int globalA, int globalB)
        {
            int islandA = IslandOf(globalA);
            int islandB = IslandOf(globalB);
            return islandA != 0 && islandB != 0 && islandA != islandB;
        }

        static void MatchBorders(int[] parent, List<Edge> mine, List<Edge> theirs, float climb)
        {
            if (mine.Count == 0 || theirs.Count == 0)
                return;

            float vertical = climb + SlabVerticalSlack;
            for (int i = 0; i < mine.Count; i++)
            {
                Edge a = mine[i];
                for (int j = 0; j < theirs.Count; j++)
                {
                    Edge b = theirs[j];
                    if (OverlapSlabs(a, b, vertical))
                        Union(parent, a.Poly, b.Poly);
                }
            }
        }

        /// <summary>Detour's dtOverlapSlabs with the slabs grown rather than shrunk and extra vertical tolerance.</summary>
        static bool OverlapSlabs(in Edge a, in Edge b, float py)
        {
            float minx = MathF.Max(a.A0 - SlabHorizontalSlack, b.A0 - SlabHorizontalSlack);
            float maxx = MathF.Min(a.A1 + SlabHorizontalSlack, b.A1 + SlabHorizontalSlack);
            if (minx > maxx)
                return false;

            float ad = (a.Y1 - a.Y0) / MathF.Max(a.A1 - a.A0, 1e-6f);
            float ak = a.Y0 - (ad * a.A0);
            float bd = (b.Y1 - b.Y0) / MathF.Max(b.A1 - b.A0, 1e-6f);
            float bk = b.Y0 - (bd * b.A0);
            float aminy = (ad * minx) + ak;
            float amaxy = (ad * maxx) + ak;
            float bminy = (bd * minx) + bk;
            float bmaxy = (bd * maxx) + bk;
            float dmin = bminy - aminy;
            float dmax = bmaxy - amaxy;
            if (dmin * dmax < 0f)
                return true;

            float thr = (py * 2f) * (py * 2f);
            return dmin * dmin <= thr || dmax * dmax <= thr;
        }

        static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        static void Union(int[] parent, int a, int b)
        {
            int rootA = Find(parent, a);
            int rootB = Find(parent, b);
            if (rootA != rootB)
                parent[rootB] = rootA;
        }

        /// <summary>A portal edge as a slab along a tile border: ordered along-axis coordinates with their heights.</summary>
        readonly struct Edge
        {
            public Edge(int poly, float a0, float y0, float a1, float y1)
            {
                Poly = poly;
                if (a0 <= a1)
                {
                    A0 = a0; Y0 = y0; A1 = a1; Y1 = y1;
                }
                else
                {
                    A0 = a1; Y0 = y1; A1 = a0; Y1 = y0;
                }
            }

            public int Poly { get; }

            public float A0 { get; }

            public float Y0 { get; }

            public float A1 { get; }

            public float Y1 { get; }
        }
    }
}
