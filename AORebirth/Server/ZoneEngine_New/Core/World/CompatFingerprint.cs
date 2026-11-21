namespace ZoneEngine_New.Core.World
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Reflection.Metadata;
    using System.Reflection.PortableExecutable;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;

    using AORebirth.Core.GameData;

    using ZoneEngine_New.Core.Logging;

    /// <summary>
    /// <c>--compat-fingerprint [--engine-root world=DIR,chat=DIR,login=DIR]</c>: one JSON line on stdout describing what a
    /// zone swap must compare between the running build and a candidate, without touching the database or World. Exit 0;
    /// on failure a non-zero exit and {"error":…} on stdout. The engine roots default to /opt/losteden/&lt;engine&gt; when
    /// that exists, else the engine folders beside this one. Values are content digests (assembly MVIDs of deterministic
    /// builds, file hashes), so equal inputs give equal output on every host.
    /// </summary>
    public static class CompatFingerprint
    {
        public const string Schema = "aor.compat/1";

        public static int Run(string? engineRoots)
        {
            try
            {
                Dictionary<string, string?> roots = ResolveRoots(engineRoots);
                string gameDataRoot = GameDataPaths.ResolveRuntimeRoot();
                var result = new Dictionary<string, object?>
                {
                    ["schema"] = Schema,
                    ["zoneBuild"] = ZoneIdentity.Build,
                    ["worldBuild"] = FolderDigest(roots["world"]),
                    ["chatBuild"] = FolderDigest(roots["chat"]),
                    ["loginBuild"] = FolderDigest(roots["login"]),
                    ["worldContract"] = "sha256:" + Hex(SHA256.HashData(
                        AORebirth.World.Contracts.V1.WorldReflection.Descriptor.SerializedData.ToByteArray())),
                    ["messaging"] = new Dictionary<string, string?>
                    {
                        ["zone"] = AssemblyIdentity(typeof(LostEden.Protocol.Messages.N3MessageType).Assembly),
                        ["login"] = AssemblyIdentity(roots["login"], "SmokeLounge.AOtomation.Messaging.dll"),
                        ["chat"] = AssemblyIdentity(roots["chat"], "SmokeLounge.AOtomation.Messaging.dll")
                    },
                    ["migrations"] = Migrations(Path.Combine(AppContext.BaseDirectory, "Migrations")),
                    ["persistenceFormat"] = Data.PersistenceFormat.Version,
                    // The persistence libraries themselves: a change there may change what is stored without anyone
                    // raising persistenceFormat, so a swap treats any difference as not viable.
                    // (A build that links the DAO sources into the engine itself also changes zoneBuild.)
                    ["persistenceCode"] = FilesDigest(AppContext.BaseDirectory, "AORebirth.Database.dll", "AORebirth.Database.Schema.dll",
                        "AORebirth.Interfaces.dll"),
                    ["modulesApi"] = typeof(AORebirth.Modules.IZoneModule).Assembly.GetName().Version?.ToString(2),
                    ["chatModulesApi"] = AssemblyVersion(roots["chat"], "AORebirth.ChatModules.Abstractions.dll"),
                    ["gameData"] = GameDataFingerprint.Compute(new StderrLogger()),
                    ["capabilities"] = new[] { "swap/1", "module-reload/1" },
                    ["modules"] = Modules.ZoneModuleHost.ProbeModules(gameDataRoot),
                    ["engineRoots"] = roots
                };
                Console.Out.WriteLine(JsonSerializer.Serialize(result));
                return 0;
            }
            catch (Exception exception)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(new { error = exception.GetBaseException().Message }));
                return 1;
            }
        }

        static Dictionary<string, string?> ResolveRoots(string? text)
        {
            bool linux = Directory.Exists("/opt/losteden");
            string beside = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
            var roots = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["world"] = linux ? "/opt/losteden/world" : Path.Combine(beside, "WorldEngine"),
                // A Windows build puts ChatEngine and LoginEngine in the shared output folder itself.
                ["chat"] = linux ? "/opt/losteden/chat" : Directory.Exists(Path.Combine(beside, "ChatEngine")) ? Path.Combine(beside, "ChatEngine") : beside,
                ["login"] = linux ? "/opt/losteden/login" : Directory.Exists(Path.Combine(beside, "LoginEngine")) ? Path.Combine(beside, "LoginEngine") : beside
            };
            foreach (string pair in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int equals = pair.IndexOf('=');
                string key = equals > 0 ? pair[..equals].Trim() : string.Empty;
                if (!roots.ContainsKey(key))
                    throw new ArgumentException("--engine-root takes world=DIR,chat=DIR,login=DIR; got '" + pair + "'.");
                roots[key] = Path.GetFullPath(pair[(equals + 1)..].Trim());
            }

            foreach (string key in roots.Keys.ToList())
                if (roots[key] is string path && !Directory.Exists(path))
                    roots[key] = null;
            return roots;
        }

        /// <summary>SHA-256 over "name:mvid" of every managed assembly at the top of the folder; null when there is no folder.</summary>
        static string? FolderDigest(string? folder)
        {
            if (folder == null)
                return null;
            var rows = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.EnumerateFiles(folder, "*.dll", SearchOption.TopDirectoryOnly))
            {
                string? mvid = Mvid(path);
                if (mvid != null)
                    rows[Path.GetFileName(path)] = mvid;
            }

            var text = new StringBuilder();
            foreach (KeyValuePair<string, string> row in rows)
                text.Append(row.Key).Append(':').Append(row.Value).Append('\n');
            return "sha256:" + Hex(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }

        static string FilesDigest(string folder, params string[] files)
        {
            var text = new StringBuilder();
            foreach (string file in files)
                text.Append(file).Append(':').Append(Mvid(Path.Combine(folder, file)) ?? "absent").Append('\n');
            return "sha256:" + Hex(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }

        static string? Mvid(string path)
        {
            if (!File.Exists(path))
                return null;
            try
            {
                using FileStream stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata)
                    return null;
                MetadataReader metadata = pe.GetMetadataReader();
                return metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("N");
            }
            catch (BadImageFormatException)
            {
                return null;
            }
        }

        static string AssemblyIdentity(Assembly assembly)
            => assembly.GetName().Name + "@" + (assembly.GetName().Version?.ToString() ?? "?") + "+" + assembly.ManifestModule.ModuleVersionId.ToString("N");

        static string? AssemblyIdentity(string? folder, string file)
        {
            if (folder == null || !File.Exists(Path.Combine(folder, file)))
                return null;
            string path = Path.Combine(folder, file);
            return Path.GetFileNameWithoutExtension(file) + "@" + (AssemblyName.GetAssemblyName(path).Version?.ToString() ?? "?") + "+" + Mvid(path);
        }

        static string? AssemblyVersion(string? folder, string file)
        {
            string? path = folder == null ? null : Path.Combine(folder, file);
            return path != null && File.Exists(path) ? AssemblyName.GetAssemblyName(path).Version?.ToString(2) : null;
        }

        static object Migrations(string folder)
        {
            var names = new List<string>();
            var text = new StringBuilder();
            if (Directory.Exists(folder))
            {
                foreach (string path in Directory.EnumerateFiles(folder, "*.sql").OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal))
                {
                    string name = Path.GetFileName(path);
                    names.Add(name);
                    text.Append(name).Append(':').Append(Hex(SHA256.HashData(File.ReadAllBytes(path)))).Append('\n');
                }
            }

            return new { set = names, sha256 = "sha256:" + Hex(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))) };
        }

        static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        /// <summary>The GameData hash's progress lines go to stderr, so stdout holds only the JSON.</summary>
        sealed class StderrLogger : IZoneLogger
        {
            public void Debug(string message)
            {
            }

            public void Info(string message) => Console.Error.WriteLine(message);

            public void Warn(string message) => Console.Error.WriteLine("WARN " + message);

            public void Error(string message) => Console.Error.WriteLine("ERROR " + message);

            public void Error(Exception exception, string message) => Console.Error.WriteLine("ERROR " + message + ": " + exception.GetBaseException().Message);

            public IZoneLogger CreateForPlayfield(int playfieldId) => this;
        }
    }
}
