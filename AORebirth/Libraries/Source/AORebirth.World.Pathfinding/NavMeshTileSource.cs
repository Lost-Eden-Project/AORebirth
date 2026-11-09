namespace AORebirth.World.Pathfinding
{
    using System;
    using System.IO;
    using Microsoft.Win32.SafeHandles;

    /// <summary>
    /// Random-access bytes of a navmesh in the <c>Navmesh.dat</c> layout: a GameData file for a baked playfield, or
    /// the bytes a dungeon bake produced in memory. The index, streamer and island pass read tiles through this so
    /// both kinds of mesh share one loading path.
    /// </summary>
    internal sealed class NavMeshTileSource : IDisposable
    {
        readonly SafeFileHandle? _file;
        readonly string? _path;
        readonly byte[]? _bytes;
        bool _disposed;

        NavMeshTileSource(SafeFileHandle? file, string? path, byte[]? bytes, long length)
        {
            _file = file;
            _path = path;
            _bytes = bytes;
            Length = length;
        }

        public long Length { get; }

        /// <summary>The whole mesh is held in memory (a generated dungeon's bake) rather than read from a file.</summary>
        public bool InMemory => _bytes != null;

        public static NavMeshTileSource OpenFile(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            SafeFileHandle file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
            try
            {
                return new NavMeshTileSource(file, path, null, RandomAccess.GetLength(file));
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        public static NavMeshTileSource FromBytes(byte[] bytes)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            return new NavMeshTileSource(null, null, bytes, bytes.Length);
        }

        /// <summary>
        /// A sequential view from the start, for indexing; the caller disposes it. A file gets its own handle: a
        /// FileStream over the source's handle would own it and close it on dispose, failing every later tile read.
        /// </summary>
        public Stream OpenSequential()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_bytes != null)
                return new MemoryStream(_bytes, writable: false);
            return new FileStream(_path!, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        }

        /// <summary>Fills <paramref name="destination"/> from <paramref name="offset"/>; throws when the source ends first.</summary>
        public void ReadExactly(Span<byte> destination, long offset)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (offset < 0 || offset + destination.Length > Length)
                throw new EndOfStreamException("Navmesh tile read runs past the end of the mesh.");

            if (_bytes != null)
            {
                _bytes.AsSpan((int)offset, destination.Length).CopyTo(destination);
                return;
            }

            int read = 0;
            while (read < destination.Length)
            {
                int n = RandomAccess.Read(_file!, destination.Slice(read), offset + read);
                if (n <= 0)
                    throw new EndOfStreamException("Navmesh tile read ended early.");
                read += n;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _file?.Dispose();
        }
    }
}
