namespace AORebirth.World.Pathfinding
{
    using System;
    using System.IO;
    using Microsoft.Win32.SafeHandles;

    using DotRecast.Core;
    using DotRecast.Core.Numerics;
    using DotRecast.Detour;
    using DotRecast.Detour.Io;

    /// <summary>
    /// Where every tile of a <c>Navmesh.dat</c> sits in the file, built by one sequential pass over the tile
    /// records without materialising their polygons. A tile is then read on its own by offset, so a playfield
    /// keeps only the tiles something is walking on rather than the whole mesh: a large outdoor mesh is hundreds
    /// of thousands of 6 m tiles that cost close to 1 GB resident when loaded whole.
    /// </summary>
    internal sealed class NavMeshTileIndex
    {
        /// <summary>Bytes of a tile's DtMeshHeader: 15 ints, 3 floats, bmin, bmax, bvQuantFactor.</summary>
        public const int TileHeaderBytes = 25 * 4;

        // Header prefix read per tile during indexing: through offMeshConCount (int 13).
        const int TileHeaderPrefixBytes = 14 * sizeof(int);
        const int TileRecordHeaderBytes = sizeof(long) + sizeof(int);

        readonly int[] _entryByCell;
        readonly int[] _offset;
        readonly int[] _size;
        readonly int[] _polyBase;

        NavMeshTileIndex(
            in DtNavMeshParams meshParams,
            int maxVertsPerPoly,
            int minX,
            int minY,
            int width,
            int height,
            int[] entryByCell,
            int[] offset,
            int[] size,
            int[] polyBase,
            int totalPolys,
            int largestTileBytes,
            bool hasOffMeshConnections)
        {
            Params = meshParams;
            MaxVertsPerPoly = maxVertsPerPoly;
            MinX = minX;
            MinY = minY;
            Width = width;
            Height = height;
            _entryByCell = entryByCell;
            _offset = offset;
            _size = size;
            _polyBase = polyBase;
            TotalPolys = totalPolys;
            LargestTileBytes = largestTileBytes;
            HasOffMeshConnections = hasOffMeshConnections;
        }

        /// <summary>Detour parameters the mesh was baked with; <c>maxTiles</c> is the baked tile count.</summary>
        public DtNavMeshParams Params { get; }

        public int MaxVertsPerPoly { get; }

        /// <summary>Tile grid bounds (inclusive min, <see cref="Width"/> columns, <see cref="Height"/> rows).</summary>
        public int MinX { get; }

        public int MinY { get; }

        public int Width { get; }

        public int Height { get; }

        public int Count => _offset.Length;

        /// <summary>Polygons across all tiles; each tile's polygons are numbered from its <see cref="PolyBase"/>.</summary>
        public int TotalPolys { get; }

        public int LargestTileBytes { get; }

        /// <summary>Any tile carries off-mesh connections (our baker emits none).</summary>
        public bool HasOffMeshConnections { get; }

        public int PolyBase(int entry) => _polyBase[entry];

        public int PolyCount(int entry) => (entry + 1 < _polyBase.Length ? _polyBase[entry + 1] : TotalPolys) - _polyBase[entry];

        /// <summary>Tile at grid cell (<paramref name="tx"/>, <paramref name="ty"/>), or -1 when the bake left it empty.</summary>
        public int EntryAt(int tx, int ty)
        {
            if (tx < MinX || ty < MinY || tx >= MinX + Width || ty >= MinY + Height)
                return -1;
            return _entryByCell[((ty - MinY) * Width) + (tx - MinX)];
        }

        /// <summary>Tile column/row under a world position (may fall outside the grid).</summary>
        public void TileLocation(float x, float z, out int tx, out int ty)
        {
            tx = (int)MathF.Floor((x - Params.orig.X) / Params.tileWidth);
            ty = (int)MathF.Floor((z - Params.orig.Z) / Params.tileHeight);
        }

        /// <summary>Reads one tile's raw record into <paramref name="scratch"/> (grown to the largest tile) and returns its size.</summary>
        public int ReadTileBytes(SafeFileHandle file, int entry, ref byte[] scratch)
        {
            int size = _size[entry];
            if (scratch.Length < size)
                scratch = new byte[Math.Max(size, LargestTileBytes)];

            long offset = _offset[entry];
            int read = 0;
            while (read < size)
            {
                int n = RandomAccess.Read(file, scratch.AsSpan(read, size - read), offset + read);
                if (n <= 0)
                    throw new EndOfStreamException("Navmesh.dat ended inside tile " + entry + ".");
                read += n;
            }

            return size;
        }

        /// <summary>Reads and parses one tile into Detour's layout.</summary>
        public DtMeshData ReadTile(SafeFileHandle file, int entry, ref byte[] scratch, DtMeshDataReader reader)
        {
            ReadTileBytes(file, entry, ref scratch);
            // The reader consumes exactly the tile's fields; bytes past them in the scratch array are ignored.
            return reader.Read(new RcByteBuffer(scratch), MaxVertsPerPoly, false);
        }

        public static NavMeshTileIndex Build(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            using var reader = new BinaryReader(stream);
            long fileLength = stream.Length;
            if (fileLength > int.MaxValue)
                throw new InvalidDataException("Navmesh.dat is larger than 2 GB.");
            NavMeshFile.ReadHeader(reader);

            int magic = reader.ReadInt32();
            if (magic != NavMeshSetHeader.NAVMESHSET_MAGIC)
                throw new InvalidDataException("Navmesh.dat tile set magic is invalid.");
            int version = reader.ReadInt32();
            if (version != NavMeshSetHeader.NAVMESHSET_VERSION_RECAST4J)
                throw new InvalidDataException("Navmesh.dat tile set version " + version + " is not the DotRecast layout.");

            int numTiles = reader.ReadInt32();
            var meshParams = new DtNavMeshParams
            {
                orig = new RcVec3f(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                tileWidth = reader.ReadSingle(),
                tileHeight = reader.ReadSingle(),
                maxTiles = reader.ReadInt32(),
                maxPolys = reader.ReadInt32()
            };
            int maxVertsPerPoly = reader.ReadInt32();
            if (numTiles <= 0 || maxVertsPerPoly < 3 || meshParams.tileWidth <= 0f || meshParams.tileHeight <= 0f)
                throw new InvalidDataException("Navmesh.dat tile set header is invalid.");

            var offset = new int[numTiles];
            var size = new int[numTiles];
            var polyCount = new int[numTiles];
            var xs = new int[numTiles];
            var ys = new int[numTiles];
            int count = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            int largest = 0;
            bool offMesh = false;
            for (int i = 0; i < numTiles; i++)
            {
                long recordStart = stream.Position;
                if (recordStart + TileRecordHeaderBytes > fileLength)
                    break;
                long tileRef = reader.ReadInt64();
                int dataSize = reader.ReadInt32();
                if (tileRef == 0 || dataSize <= 0)
                    break;

                long dataStart = recordStart + TileRecordHeaderBytes;
                if (dataSize < TileHeaderBytes || dataStart + dataSize > fileLength)
                    throw new InvalidDataException("Navmesh.dat tile " + i + " runs past the end of the file.");

                int tileMagic = reader.ReadInt32();
                int tileVersion = reader.ReadInt32();
                int x = reader.ReadInt32();
                int y = reader.ReadInt32();
                int layer = reader.ReadInt32();
                reader.ReadInt32(); // userId
                int polys = reader.ReadInt32();
                reader.ReadInt32(); // vertCount
                reader.ReadInt32(); // maxLinkCount
                reader.ReadInt32(); // detailMeshCount
                reader.ReadInt32(); // detailVertCount
                reader.ReadInt32(); // detailTriCount
                reader.ReadInt32(); // bvNodeCount
                int offMeshConCount = reader.ReadInt32();
                if (tileMagic != DtDetour.DT_NAVMESH_MAGIC
                    || tileVersion < DtDetour.DT_NAVMESH_VERSION_RECAST4J_FIRST
                    || tileVersion > DtDetour.DT_NAVMESH_VERSION_RECAST4J_LAST)
                    throw new InvalidDataException("Navmesh.dat tile " + i + " has an unsupported tile header.");
                if (layer != 0)
                    throw new InvalidDataException("Navmesh.dat tile " + i + " uses layer " + layer + "; layered tiles are not supported.");

                offset[count] = (int)dataStart;
                size[count] = dataSize;
                polyCount[count] = polys;
                xs[count] = x;
                ys[count] = y;
                count++;
                offMesh |= offMeshConCount > 0;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                if (dataSize > largest) largest = dataSize;

                stream.Seek(dataStart + dataSize, SeekOrigin.Begin);
            }

            if (count == 0)
                throw new InvalidDataException("Navmesh.dat holds no tiles.");

            int width = maxX - minX + 1;
            int height = maxY - minY + 1;
            var entryByCell = new int[checked(width * height)];
            Array.Fill(entryByCell, -1);
            var polyBase = new int[count];
            int total = 0;
            for (int e = 0; e < count; e++)
            {
                int cell = ((ys[e] - minY) * width) + (xs[e] - minX);
                if (entryByCell[cell] != -1)
                    throw new InvalidDataException("Navmesh.dat has two tiles at " + xs[e] + "," + ys[e] + ".");
                entryByCell[cell] = e;
                polyBase[e] = total;
                total = checked(total + polyCount[e]);
            }

            if (count != numTiles)
            {
                Array.Resize(ref offset, count);
                Array.Resize(ref size, count);
            }

            return new NavMeshTileIndex(meshParams, maxVertsPerPoly, minX, minY, width, height, entryByCell, offset, size, polyBase, total, largest, offMesh);
        }
    }
}
