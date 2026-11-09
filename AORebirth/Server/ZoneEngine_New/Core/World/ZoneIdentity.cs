namespace ZoneEngine_New.Core.World
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading.Tasks;

    using AORebirth.Core.GameData;

    using ZoneEngine_New.Core.Logging;

    /// <summary>
    /// What this zone reports so World can refuse a new instance that would behave differently from the running ones:
    /// its build and the content fingerprint of its GameData. World compares them across zone instances only, so they
    /// need to be equal for equal inputs, not to match any release manifest.
    /// </summary>
    public static class ZoneIdentity
    {
        /// <summary>
        /// Engine version plus a hash of the module id of every assembly this engine loads from beside itself. The
        /// engine assembly alone would not change when only a library it uses did. A deterministic build of the same
        /// source gives the same value on every host.
        /// </summary>
        public static string Build { get; } = ComputeBuild();

        static string ComputeBuild()
        {
            Assembly engine = typeof(ZoneIdentity).Assembly;
            string baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            var modules = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { engine.GetName().Name ?? string.Empty };
            var pending = new Queue<Assembly>();
            pending.Enqueue(engine);
            while (pending.Count > 0)
            {
                Assembly assembly = pending.Dequeue();
                modules[assembly.GetName().Name ?? string.Empty] = assembly.ManifestModule.ModuleVersionId.ToString("N");
                foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
                {
                    string? name = reference.Name;
                    if (name == null || !seen.Add(name)) continue;
                    // What ships with the engine; the shared framework belongs to the installed runtime.
                    if (!File.Exists(Path.Combine(baseDirectory, name + ".dll"))) continue;
                    try { pending.Enqueue(Assembly.Load(reference)); }
                    catch (Exception) { modules[name] = "unloadable"; }
                }
            }

            var rows = new StringBuilder();
            foreach (KeyValuePair<string, string> module in modules) rows.Append(module.Key).Append(':').Append(module.Value).Append('\n');
            string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rows.ToString()))).ToLowerInvariant();
            return (engine.GetName().Version?.ToString() ?? "0") + "+" + digest.Substring(0, 16);
        }
    }

    /// <summary>
    /// SHA-256 over every file of the GameData this zone reads (path, size and content hash per file), plus the
    /// override folders laid over it. The same directories the release manifest leaves out (.git, caches, logs and
    /// the like) are skipped. Per-file hashes are cached by size and modification time outside GameData, so only the
    /// first start after a content change reads the whole tree.
    /// </summary>
    public static class GameDataFingerprint
    {
        static readonly HashSet<string> NonRuntimeDirectories = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".local", "cache", "caches", "tmp", "temp", "temporary", "bin", "obj", "build", "build-verify", "logs",
            "reports", "screenshots", "backups", "research", "exports"
        };

        const string CacheHeader = "aorebirth-gamedata-fingerprint v1";

        public static Task<string> StartAsync(IZoneLogger logger)
            => Task.Factory.StartNew(() => Compute(logger), TaskCreationOptions.LongRunning);

        public static string Compute(IZoneLogger logger)
        {
            ArgumentNullException.ThrowIfNull(logger);
            var roots = new List<(string Label, string? Path)> { ("gamedata", GameDataPaths.ResolveRuntimeRoot()) };
            string? overrideRoot = GameDataPaths.ResolveOverrideRoot();
            if (overrideRoot != null) roots.Add(("override", overrideRoot));
            string? knubot = Environment.GetEnvironmentVariable(Knubot.KnubotCatalog.OverrideVariable);
            if (!string.IsNullOrWhiteSpace(knubot)) roots.Add(("knubot", Path.GetFullPath(knubot.Trim())));

            var watch = Stopwatch.StartNew();
            var combined = new StringBuilder();
            int files = 0;
            int hashed = 0;
            long hashedBytes = 0;
            foreach ((string label, string? path) in roots)
            {
                RootResult result = HashRoot(path!, logger);
                files += result.Files;
                hashed += result.Hashed;
                hashedBytes += result.HashedBytes;
                combined.Append(label).Append('\0').Append(result.Digest).Append('\n');
                logger.Info(string.Format(CultureInfo.InvariantCulture, "GameData fingerprint {0}={1} root={2} files={3}", label,
                    result.Digest.Substring(0, Math.Min(16, result.Digest.Length)), path, result.Files));
            }

            string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(combined.ToString()))).ToLowerInvariant();
            logger.Info(string.Format(CultureInfo.InvariantCulture,
                "GameData fingerprint {0} files={1} rehashed={2} ({3} MB) in {4} ms", fingerprint.Substring(0, 16), files, hashed,
                hashedBytes / (1024 * 1024), watch.ElapsedMilliseconds));
            return fingerprint;
        }

        readonly record struct RootResult(string Digest, int Files, int Hashed, long HashedBytes);

        readonly record struct Entry(long Size, long Ticks, string Sha256);

        static RootResult HashRoot(string root, IZoneLogger logger)
        {
            if (!Directory.Exists(root)) return new RootResult("absent", 0, 0, 0);
            List<(string Relative, FileInfo Info)> files = Enumerate(root);
            string cachePath = CachePath(root);
            Dictionary<string, Entry> cache = LoadCache(cachePath);
            var results = new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);
            var misses = new ConcurrentBag<(string Relative, FileInfo Info)>();
            foreach ((string relative, FileInfo info) in files)
            {
                if (cache.TryGetValue(relative, out Entry cached) && cached.Size == info.Length && cached.Ticks == info.LastWriteTimeUtc.Ticks)
                    results[relative] = cached;
                else
                    misses.Add((relative, info));
            }

            long hashedBytes = 0;
            // Bounded so a first start does not starve the GameData load running beside it.
            var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) };
            Parallel.ForEach(misses, parallel, file =>
            {
                long size = file.Info.Length;
                long ticks = file.Info.LastWriteTimeUtc.Ticks;
                string digest;
                using (var stream = new FileStream(file.Info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
                    FileOptions.SequentialScan))
                    digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                file.Info.Refresh();
                // Changed while it was read: the digest stands for this run, but is not remembered.
                bool stable = file.Info.Exists && file.Info.Length == size && file.Info.LastWriteTimeUtc.Ticks == ticks;
                results[file.Relative] = new Entry(stable ? size : -1, ticks, digest);
                System.Threading.Interlocked.Add(ref hashedBytes, size);
            });

            var rows = new StringBuilder();
            foreach ((string relative, FileInfo info) in files.OrderBy(f => f.Relative, StringComparer.Ordinal))
                rows.Append(relative).Append('\0').Append(info.Length.ToString(CultureInfo.InvariantCulture)).Append('\0')
                    .Append(results[relative].Sha256).Append('\n');
            string rootDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rows.ToString()))).ToLowerInvariant();

            if (!misses.IsEmpty || cache.Count != files.Count) SaveCache(cachePath, results, logger);
            return new RootResult(rootDigest, files.Count, misses.Count, hashedBytes);
        }

        static List<(string Relative, FileInfo Info)> Enumerate(string root)
        {
            var files = new List<(string, FileInfo)>();
            var directories = new Stack<DirectoryInfo>();
            directories.Push(new DirectoryInfo(root));
            while (directories.Count > 0)
            {
                DirectoryInfo directory = directories.Pop();
                foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (entry is DirectoryInfo child)
                    {
                        if (!NonRuntimeDirectories.Contains(child.Name)) directories.Push(child);
                    }
                    else if (entry is FileInfo file)
                    {
                        files.Add((Path.GetRelativePath(root, file.FullName).Replace('\\', '/'), file));
                    }
                }
            }

            return files;
        }

        /// <summary>
        /// One cache per GameData root, outside it: AO_REBIRTH_ZONE_STATE_DIR when set, else the temp folder. Zone
        /// instances on one host share it.
        /// </summary>
        static string CachePath(string root)
        {
            string? state = Environment.GetEnvironmentVariable("AO_REBIRTH_ZONE_STATE_DIR");
            string directory = string.IsNullOrWhiteSpace(state) ? Path.Combine(Path.GetTempPath(), "AORebirth") : state.Trim();
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                OperatingSystem.IsWindows() ? Path.GetFullPath(root).ToUpperInvariant() : Path.GetFullPath(root)))).Substring(0, 16).ToLowerInvariant();
            return Path.Combine(directory, "gamedata-fingerprint-" + key + ".cache");
        }

        static Dictionary<string, Entry> LoadCache(string path)
        {
            var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(path)) return entries;
                using var reader = new StreamReader(path, Encoding.UTF8);
                if (reader.ReadLine() != CacheHeader) return entries;
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] parts = line.Split('\t', 4);
                    if (parts.Length == 4 && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long size)
                        && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks) && parts[2].Length == 64)
                        entries[parts[3]] = new Entry(size, ticks, parts[2]);
                }
            }
            catch (Exception)
            {
                entries.Clear();
            }

            return entries;
        }

        static void SaveCache(string path, IReadOnlyDictionary<string, Entry> entries, IZoneLogger logger)
        {
            string temporary = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
                {
                    writer.WriteLine(CacheHeader);
                    foreach (KeyValuePair<string, Entry> entry in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
                    {
                        if (entry.Value.Size < 0) continue;
                        writer.Write(entry.Value.Size.ToString(CultureInfo.InvariantCulture));
                        writer.Write('\t');
                        writer.Write(entry.Value.Ticks.ToString(CultureInfo.InvariantCulture));
                        writer.Write('\t');
                        writer.Write(entry.Value.Sha256);
                        writer.Write('\t');
                        writer.WriteLine(entry.Key);
                    }
                }

                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception exception)
            {
                logger.Warn("GameData fingerprint cache not saved (" + exception.GetType().Name + "): the next start hashes everything again");
                try { File.Delete(temporary); }
                catch (Exception) { }
            }
        }
    }
}
