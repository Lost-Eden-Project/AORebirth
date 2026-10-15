namespace ZoneEngine_New.Core.Modules
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Reflection;
    using System.Security.Cryptography;
    using System.Text.Json;

    using AORebirth.Modules;
    using AORebirth.Modules.Events;
    using AORebirth.Modules.Network;

    using ZoneEngine_New.Core.Logging;

    /// <summary>
    /// Loads the external modules named in Config/Modules.json once at startup, each into its own load context, and
    /// holds what they registered. With no Modules.json nothing is loaded and every hook point is a no-op.
    /// Modules are trusted code running in the zone process; there is no unloading or hot reload.
    /// </summary>
    public sealed class ZoneModuleHost
    {
        private static readonly int ApiMajorVersion = typeof(IZoneModule).Assembly.GetName().Version?.Major ?? 0;

        private readonly IZoneLogger _logger;
        private readonly List<LoadedModule> _modules = new();
        private IConnectionFilterProvider? _connectionFilter;
        private string? _connectionFilterOwner;

        private ZoneModuleHost(IZoneLogger logger)
        {
            _logger = logger;
            Events = new ZoneModuleEvents(logger);
        }

        public ZoneModuleEvents Events { get; }

        public int Count => _modules.Count;

        public static ZoneModuleHost Load(string gameDataRoot, IZoneLogger logger)
        {
            ArgumentNullException.ThrowIfNull(logger);
            var host = new ZoneModuleHost(logger);
            string? settingsPath = ModulesSettings.ResolvePath(gameDataRoot);
            if (settingsPath == null)
            {
                logger.Info("Modules: no " + ModulesSettings.ConfigFileName + "; no modules loaded.");
                return host;
            }

            ModulesSettings settings = ModulesSettings.Load(settingsPath);
            string modulesDirectory = settings.ResolveModulesDirectory(settingsPath);
            var required = new HashSet<string>(settings.Required, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            logger.Info($"Modules: config={settingsPath} directory={modulesDirectory} enabled={settings.Enabled.Count}");

            foreach (string id in settings.Enabled)
            {
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                    continue;

                try
                {
                    host.LoadModule(id, modulesDirectory);
                }
                catch (Exception exception)
                {
                    host.Events.RemoveModule(id);
                    if (string.Equals(host._connectionFilterOwner, id, StringComparison.OrdinalIgnoreCase))
                    {
                        host._connectionFilter = null;
                        host._connectionFilterOwner = null;
                    }

                    logger.Error(exception, "Module " + id + " failed to load.");
                    if (required.Contains(id))
                        throw new InvalidOperationException("Required module " + id + " failed to load.", exception);
                }
            }

            return host;
        }

        /// <summary>The registered connection filter for a new zone connection, or null when no module filters traffic.</summary>
        public IConnectionFilter? CreateConnectionFilter(Guid sessionId, EndPoint? remoteEndPoint)
        {
            IConnectionFilterProvider? provider = _connectionFilter;
            return provider?.Create(new ConnectionInfo(sessionId, remoteEndPoint));
        }

        public void Shutdown()
        {
            for (int i = _modules.Count - 1; i >= 0; i--)
            {
                LoadedModule module = _modules[i];
                try
                {
                    foreach (IZoneModule entry in module.Entries)
                        entry.Shutdown();
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, "Module " + module.Id + " failed to shut down.");
                }
            }

            _modules.Clear();
        }

        private void LoadModule(string id, string modulesDirectory)
        {
            string moduleDirectory = Path.GetFullPath(Path.Combine(modulesDirectory, id));
            string manifestPath = Path.Combine(moduleDirectory, ModuleManifest.FileName);
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("Module manifest not found.", manifestPath);

            ModuleManifest manifest;
            using (FileStream stream = File.OpenRead(manifestPath))
                manifest = JsonSerializer.Deserialize<ModuleManifest>(stream, ModulesSettings.JsonOptions)
                    ?? throw new InvalidDataException("Empty module manifest: " + manifestPath);
            if (!string.Equals(manifest.Id, id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Module manifest id '{manifest.Id}' does not match enabled id '{id}'.");
            if (string.IsNullOrWhiteSpace(manifest.Assembly))
                throw new InvalidDataException("Module manifest names no assembly: " + manifestPath);

            string assemblyPath = Path.GetFullPath(Path.Combine(moduleDirectory, manifest.Assembly));
            if (!assemblyPath.StartsWith(moduleDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Module assembly must be inside the module folder: " + manifest.Assembly);
            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException("Module assembly not found.", assemblyPath);

            var context = new ModuleLoadContext(id, assemblyPath);
            Assembly assembly = context.LoadFromAssemblyPath(assemblyPath);
            CheckApiVersion(assembly);

            Type[] entryTypes = assembly.GetExportedTypes()
                .Where(type => type.IsClass && !type.IsAbstract && typeof(IZoneModule).IsAssignableFrom(type))
                .ToArray();
            if (entryTypes.Length == 0)
                throw new InvalidDataException("Module assembly has no public IZoneModule: " + assemblyPath);

            var moduleContext = new ZoneModuleContext(this, manifest.Id, moduleDirectory);
            var entries = new List<IZoneModule>(entryTypes.Length);
            foreach (Type type in entryTypes)
            {
                var entry = (IZoneModule)(Activator.CreateInstance(type)
                    ?? throw new InvalidOperationException("Could not create " + type.FullName));
                entry.Initialize(moduleContext);
                entries.Add(entry);
            }

            _modules.Add(new LoadedModule(manifest.Id, entries));
            _logger.Info(string.Format(CultureInfo.InvariantCulture,
                "Module loaded id={0} version={1} assembly={2} sha256={3} entries={4} connectionFilter={5}",
                manifest.Id,
                string.IsNullOrWhiteSpace(manifest.Version) ? "?" : manifest.Version,
                Path.GetFileName(assemblyPath),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assemblyPath))),
                entries.Count,
                string.Equals(_connectionFilterOwner, manifest.Id, StringComparison.OrdinalIgnoreCase) ? "yes" : "no"));
        }

        private static void CheckApiVersion(Assembly assembly)
        {
            AssemblyName? api = assembly.GetReferencedAssemblies().FirstOrDefault(reference =>
                string.Equals(reference.Name, ModuleLoadContext.SharedAssemblyName, StringComparison.OrdinalIgnoreCase));
            if (api == null)
                throw new InvalidDataException("Module does not reference " + ModuleLoadContext.SharedAssemblyName + ".");
            if ((api.Version?.Major ?? -1) != ApiMajorVersion)
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture,
                    "Module was built against module API {0}; this engine provides {1}.", api.Version, ApiMajorVersion));
        }

        private void RegisterConnectionFilter(string moduleId, IConnectionFilterProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (_connectionFilter != null)
                throw new InvalidOperationException(
                    "Module " + _connectionFilterOwner + " already registered the connection filter; only one is allowed.");
            _connectionFilter = provider;
            _connectionFilterOwner = moduleId;
        }

        private sealed record LoadedModule(string Id, List<IZoneModule> Entries);

        private sealed class ZoneModuleContext : IZoneModuleContext
        {
            private readonly ZoneModuleHost _host;

            public ZoneModuleContext(ZoneModuleHost host, string moduleId, string moduleDirectory)
            {
                _host = host;
                ModuleId = moduleId;
                ModuleDirectory = moduleDirectory;
                Log = new ModuleLogger(host._logger, moduleId);
                Events = new ModuleEventsView(host.Events, moduleId);
            }

            public string ModuleId { get; }

            public string ModuleDirectory { get; }

            public IModuleLogger Log { get; }

            public IZoneEvents Events { get; }

            public T? GetConfig<T>() where T : class
            {
                string path = Path.Combine(ModuleDirectory, "config.json");
                if (!File.Exists(path))
                    return null;
                using FileStream stream = File.OpenRead(path);
                return JsonSerializer.Deserialize<T>(stream, ModulesSettings.JsonOptions);
            }

            public void RegisterConnectionFilter(IConnectionFilterProvider provider)
                => _host.RegisterConnectionFilter(ModuleId, provider);
        }

        private sealed class ModuleEventsView : IZoneEvents
        {
            private readonly ZoneModuleEvents _events;
            private readonly string _moduleId;

            public ModuleEventsView(ZoneModuleEvents events, string moduleId)
            {
                _events = events;
                _moduleId = moduleId;
            }

            public event Action<NpcKilledEvent> NpcKilled
            {
                add => _events.NpcKilled.Add(_moduleId, value);
                remove => _events.NpcKilled.Remove(_moduleId, value);
            }

            public event Action<PlayerDiedEvent> PlayerDied
            {
                add => _events.PlayerDied.Add(_moduleId, value);
                remove => _events.PlayerDied.Remove(_moduleId, value);
            }
        }

        private sealed class ModuleLogger : IModuleLogger
        {
            private readonly IZoneLogger _logger;
            private readonly string _prefix;

            public ModuleLogger(IZoneLogger logger, string moduleId)
            {
                _logger = logger;
                _prefix = "[" + moduleId + "] ";
            }

            public void Debug(string message) => _logger.Debug(_prefix + message);

            public void Info(string message) => _logger.Info(_prefix + message);

            public void Warn(string message) => _logger.Warn(_prefix + message);

            public void Error(string message) => _logger.Error(_prefix + message);

            public void Error(Exception exception, string message) => _logger.Error(exception, _prefix + message);
        }
    }
}
