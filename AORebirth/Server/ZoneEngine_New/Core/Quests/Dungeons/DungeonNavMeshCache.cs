namespace ZoneEngine_New.Core.Quests.Dungeons;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Tasks;

using AORebirth.World.Collision;
using AORebirth.World.Pathfinding;

using ZoneEngine_New.Core.Logging;

/// <summary>
/// Navmeshes for generated dungeons, baked with Recast off the tick thread and kept in memory in the
/// <c>Navmesh.dat</c> layout. A mesh is keyed by the layout's generator payload, so every build of the same
/// seed (a released dungeon rebuilt on re-entry, a teammate's instance) shares one bake. The bake starts when a
/// mission is accepted (<see cref="Prewarm"/>), long before anyone reaches the entrance; the dungeon's own build,
/// which already runs off the tick, waits for it only when the player got there first. A solo dungeon bakes in
/// well under a second and a five-floor team dungeon in about two seconds on a desktop core; Recast's own thread
/// option does not help on these meshes, so a bake is one thread.
/// <para>
/// Faster still would be reassembling pre-baked per-room tiles (bake each style room template once per facing,
/// translate its Detour tiles into place at build time: milliseconds, no Recast at runtime). Rooms sit on the
/// 10-unit block grid and floors stack as Detour tile layers, so the geometry allows it. It is deferred because it
/// needs (1) a dungeon tile size that divides 10 units, separate from the outdoor NavAgent.json settings;
/// (2) doorway floor stubs during each room's bake so agent-radius erosion does not close the door, with the
/// neighbour room's floor height at every door within the climb tolerance so Detour links the two tiles; and
/// (3) a per-style room tile set carried as generated runtime GameData through the manifest chain. When that
/// lands, it replaces <see cref="Bake"/> here; the rest of this cache and <see cref="NavMeshPathfinder.FromBytes"/>
/// stay as they are.
/// </para>
/// </summary>
public sealed class DungeonNavMeshCache
{
    /// <summary>Meshes kept; the least recently requested go first. The largest team dungeons are about 1.5 MB.</summary>
    const int MaxEntries = 48;

    /// <summary>A mesh nobody has asked for this long is dropped and baked again on demand.</summary>
    static readonly TimeSpan Retention = TimeSpan.FromHours(2);

    /// <summary>How long a dungeon build waits for its bake before going without a navmesh.</summary>
    public static readonly TimeSpan BuildWait = TimeSpan.FromSeconds(30);

    readonly string _gameDataRoot;
    readonly IZoneLogger _logger;
    readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    readonly Lazy<NavMeshBuildSettings?> _settings;
    readonly object _trim = new();

    public DungeonNavMeshCache(string gameDataRoot, IZoneLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDataRoot);
        ArgumentNullException.ThrowIfNull(logger);
        _gameDataRoot = gameDataRoot;
        _logger = logger;
        _settings = new Lazy<NavMeshBuildSettings?>(LoadSettings, isThreadSafe: true);
    }

    /// <summary>Meshes held or baking right now.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Generates the dungeon's layout and starts its bake on the thread pool, unless that mesh is already held.
    /// Fire-and-forget: failures are logged, never thrown.
    /// </summary>
    public void Prewarm(QuestDungeonParameters parameters, DungeonLayoutGenerator layouts)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(layouts);
        _ = Task.Run(() =>
        {
            try
            {
                if (layouts.TryGenerate(parameters, out DungeonLayout layout))
                    _ = GetOrStart(layout);
            }
            catch (Exception exception)
            {
                _logger.Warn("Dungeon navmesh prewarm failed seed=" + parameters.Seed.ToString(CultureInfo.InvariantCulture)
                    + ": " + exception.Message);
            }
        });
    }

    /// <summary>The mesh bytes for <paramref name="layout"/>, baking them if nobody has; null when the bake failed.</summary>
    public Task<byte[]?> GetOrStart(DungeonLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        string key = KeyOf(layout);
        long now = Environment.TickCount64;
        Entry entry = _entries.GetOrAdd(key, k => new Entry(
            new Lazy<Task<byte[]?>>(() => Task.Run(() => Bake(k, layout)), isThreadSafe: true), now));
        entry.TouchedMs = now;
        Trim(now, keep: key);
        return entry.Bake.Value;
    }

    /// <summary>
    /// A pathfinder for <paramref name="layout"/>, waiting up to <see cref="BuildWait"/> for its bake. Meant for a
    /// dungeon build already off the tick thread. Null, with the reason logged, when there is no usable mesh.
    /// </summary>
    public NavMeshPathfinder? TryCreatePathfinder(int playfieldId, DungeonLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        NavMeshBuildSettings? settings = _settings.Value;
        if (settings == null)
            return null;

        Task<byte[]?> bake = GetOrStart(layout);
        byte[]? bytes;
        try
        {
            if (!bake.Wait(BuildWait))
            {
                _logger.Warn("Dungeon navmesh not ready in time playfield=" + playfieldId.ToString(CultureInfo.InvariantCulture)
                    + " seed=" + layout.Seed.ToString(CultureInfo.InvariantCulture) + "; NPCs path without a navmesh.");
                return null;
            }

            bytes = bake.Result;
        }
        catch (AggregateException exception)
        {
            _logger.Warn("Dungeon navmesh bake faulted playfield=" + playfieldId.ToString(CultureInfo.InvariantCulture)
                + ": " + (exception.InnerException ?? exception).Message);
            return null;
        }

        if (bytes == null)
            return null;

        try
        {
            return NavMeshPathfinder.FromBytes(playfieldId, bytes, settings);
        }
        catch (Exception exception)
        {
            _logger.Warn("Dungeon navmesh rejected playfield=" + playfieldId.ToString(CultureInfo.InvariantCulture)
                + ": " + exception.Message);
            return null;
        }
    }

    byte[]? Bake(string key, DungeonLayout layout)
    {
        NavMeshBuildSettings? settings = _settings.Value;
        if (settings == null)
            return null;

        long started = Stopwatch.GetTimestamp();
        try
        {
            PlayfieldCollisionSet collision = DungeonCollisionBuilder.Build(_gameDataRoot, layout.Generator);
            NavMeshBakeResult result = NavMeshBaker.Bake(collision, settings);
            byte[] bytes = NavMeshFile.ToBytes(result);
            _logger.Info(string.Format(CultureInfo.InvariantCulture,
                "Dungeon navmesh baked seed={0} rooms={1} triangles={2} tiles={3} bytes={4} elapsedMs={5} key={6}",
                layout.Seed, layout.Generator.Rooms?.Length ?? 0, result.SourceTriangles, result.TileCount, bytes.Length,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture), key[..12]));
            return bytes;
        }
        catch (Exception exception)
        {
            _logger.Warn(string.Format(CultureInfo.InvariantCulture,
                "Dungeon navmesh bake failed seed={0} elapsedMs={1}: {2}",
                layout.Seed, Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture),
                exception.Message));
            return null;
        }
    }

    NavMeshBuildSettings? LoadSettings()
    {
        if (NavMeshPathfinder.TryLoadSettings(_gameDataRoot, out NavMeshBuildSettings? settings, out string? failure) && settings != null)
            return settings;

        _logger.Warn("Dungeon navmeshes disabled: " + (failure ?? "no bake settings"));
        return null;
    }

    /// <summary>Drops meshes past <see cref="Retention"/>, then the least recently requested past <see cref="MaxEntries"/>.</summary>
    void Trim(long nowMs, string keep)
    {
        // Requests are rare (one per dungeon build or mission accept) and the table is small: a plain lock.
        lock (_trim)
        {
            List<KeyValuePair<string, Entry>>? stale = null;
            foreach (KeyValuePair<string, Entry> pair in _entries)
            {
                if (pair.Key != keep && nowMs - pair.Value.TouchedMs > Retention.TotalMilliseconds)
                    (stale ??= []).Add(pair);
            }

            if (stale != null)
            {
                foreach (KeyValuePair<string, Entry> pair in stale)
                    _entries.TryRemove(pair);
            }

            int excess = _entries.Count - MaxEntries;
            if (excess <= 0)
                return;

            var oldest = new List<KeyValuePair<string, Entry>>(_entries);
            oldest.Sort(static (a, b) => a.Value.TouchedMs.CompareTo(b.Value.TouchedMs));
            for (int i = 0; i < oldest.Count && excess > 0; i++)
            {
                if (oldest[i].Key == keep)
                    continue;
                if (_entries.TryRemove(oldest[i]))
                    excess--;
            }
        }
    }

    static string KeyOf(DungeonLayout layout)
        => Convert.ToHexString(SHA256.HashData(layout.GeneratorPayload ?? []));

    sealed class Entry(Lazy<Task<byte[]?>> bake, long touchedMs)
    {
        public Lazy<Task<byte[]?>> Bake { get; } = bake;

        /// <summary>Last request, <see cref="Environment.TickCount64"/>; written without a lock, a near value is fine.</summary>
        public long TouchedMs { get; set; } = touchedMs;
    }
}
