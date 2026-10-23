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
    using AORebirth.Modules.Commands;
    using AORebirth.Modules.Content;
    using AORebirth.Modules.Events;
    using AORebirth.Modules.Network;
    using AORebirth.Modules.World;

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
        private readonly IKnubotScripts _knubot;
        private readonly IZoneGameData _gameData;
        private readonly List<LoadedModule> _modules = new();
        private readonly IZoneActions _actions;
        private Dictionary<string, List<string>> _capabilities = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string ModuleId, ModuleGmCommand Command)> _gmCommands = new();
        private readonly HashSet<string> _required = new(StringComparer.OrdinalIgnoreCase);
        private string? _initializing;
        private IConnectionFilterProvider? _connectionFilter;
        private string? _connectionFilterOwner;

        private ZoneModuleHost(IZoneLogger logger, IKnubotScripts knubot, IZoneGameData gameData,
            Func<Playfield.PlayfieldManager> playfields)
        {
            _logger = logger;
            _knubot = knubot;
            _gameData = gameData;
            Events = new ZoneModuleEvents(logger);
            World = new ZoneModuleWorld(playfields);
            _actions = new ZoneModuleActions(playfields);
        }

        public ZoneModuleEvents Events { get; }

        /// <summary>The world view every playfield tick publishes into while a module reads it.</summary>
        internal ZoneModuleWorld World { get; }

        public int Count => _modules.Count;

        public static ZoneModuleHost Load(
            string gameDataRoot,
            IZoneLogger logger,
            Func<Knubot.KnubotCatalog> knubot,
            Func<Playfield.PlayfieldManager> playfields,
            Func<ZoneEngine_New.Core.GameData.GameDataReloadRegistry>? gameData = null)
        {
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(knubot);
            ArgumentNullException.ThrowIfNull(playfields);
            var host = new ZoneModuleHost(logger, new ModuleKnubotScripts(knubot),
                new ModuleGameData(gameData ?? (() => new ZoneEngine_New.Core.GameData.GameDataReloadRegistry([]))), playfields);
            string? settingsPath = ModulesSettings.ResolvePath(gameDataRoot);
            if (settingsPath == null)
            {
                logger.Info("Modules: no " + ModulesSettings.ConfigFileName + "; no modules loaded.");
                return host;
            }

            ModulesSettings settings = ModulesSettings.Load(settingsPath);
            string modulesDirectory = settings.ResolveModulesDirectory(settingsPath);
            HashSet<string> required = host._required;
            required.UnionWith(settings.Required);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            host._capabilities = LoadCapabilities(settings, logger);
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
                    host.Forget(id);
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

        /// <summary>
        /// The module GM commands, checked against the built-in names when the GM command dispatcher is built (before the
        /// zone accepts connections). A module whose command clashes is shut down and unloaded, as if it had failed to
        /// load; when it is Required, zone startup fails.
        /// </summary>
        internal IReadOnlyList<(string ModuleId, ModuleGmCommand Command)> BindGmCommands(IEnumerable<string> builtInNames)
        {
            var builtIns = new HashSet<string>(builtInNames, StringComparer.OrdinalIgnoreCase);
            foreach (string moduleId in _gmCommands.Where(x => builtIns.Contains(x.Command.Name)).Select(x => x.ModuleId)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            {
                string names = string.Join(", ", _gmCommands
                    .Where(x => string.Equals(x.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase) && builtIns.Contains(x.Command.Name))
                    .Select(x => "." + x.Command.Name));
                var failure = new InvalidOperationException("Module " + moduleId + " GM command clashes with a built-in: " + names);
                _logger.Error(failure, "Module " + moduleId + " failed to start; unloading it.");
                int index = _modules.FindIndex(x => string.Equals(x.Id, moduleId, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    ShutdownModule(_modules[index]);
                    _modules.RemoveAt(index);
                }

                Forget(moduleId);
                if (_required.Contains(moduleId))
                    throw new InvalidOperationException("Required module " + moduleId + " failed to start.", failure);
            }

            foreach ((string moduleId, ModuleGmCommand command) in _gmCommands)
                _logger.Info(string.Format(CultureInfo.InvariantCulture, "Module {0} GM command .{1} gmLevel={2}",
                    moduleId, command.Name, command.RequiredGmLevel));
            return _gmCommands.ToArray();
        }

        /// <summary>Drops everything <paramref name="moduleId"/> registered.</summary>
        private void Forget(string moduleId)
        {
            Events.RemoveModule(moduleId);
            _gmCommands.RemoveAll(x => string.Equals(x.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(_connectionFilterOwner, moduleId, StringComparison.OrdinalIgnoreCase))
            {
                _connectionFilter = null;
                _connectionFilterOwner = null;
            }
        }

        private void ShutdownModule(LoadedModule module)
        {
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

        public void Shutdown()
        {
            for (int i = _modules.Count - 1; i >= 0; i--)
                ShutdownModule(_modules[i]);

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

            _capabilities.TryGetValue(manifest.Id, out List<string>? granted);
            var gate = new CapabilityGate(manifest.Id, granted, _logger);
            var moduleContext = new ZoneModuleContext(this, manifest.Id, moduleDirectory, gate);
            var entries = new List<IZoneModule>(entryTypes.Length);
            _initializing = manifest.Id;
            try
            {
                foreach (Type type in entryTypes)
                {
                    var entry = (IZoneModule)(Activator.CreateInstance(type)
                        ?? throw new InvalidOperationException("Could not create " + type.FullName));
                    entry.Initialize(moduleContext);
                    entries.Add(entry);
                }
            }
            finally
            {
                _initializing = null;
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

        private static Dictionary<string, List<string>> LoadCapabilities(ModulesSettings settings, IZoneLogger logger)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ModuleCapabilities.ReadWorld, ModuleCapabilities.RefreshNpc, ModuleCapabilities.SendMessages,
                ModuleCapabilities.GameDataReload, ModuleCapabilities.RespawnNpc
            };
            var capabilities = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach ((string id, List<string>? names) in settings.Capabilities ?? new Dictionary<string, List<string>>())
            {
                var granted = new List<string>();
                foreach (string name in names ?? new List<string>())
                {
                    if (known.Contains(name))
                        granted.Add(name);
                    else
                        logger.Warn("Modules: unknown capability '" + name + "' for module " + id + " ignored.");
                }

                capabilities[id] = granted;
                logger.Info("Modules: " + id + " capabilities=" + (granted.Count == 0 ? "none" : string.Join(",", granted)));
            }

            return capabilities;
        }

        private void RegisterGmCommand(string moduleId, ModuleGmCommand command)
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(command.Execute);
            if (!string.Equals(_initializing, moduleId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("GM commands can only be registered during Initialize.");
            string name = command.Name ?? string.Empty;
            if (name.Length == 0 || name.Length > 32 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                throw new ArgumentException("A GM command name is 1-32 letters, digits, '-' or '_', without the dot: '" + name + "'.");
            if (command.RequiredGmLevel < 0)
                throw new ArgumentException("RequiredGmLevel cannot be negative for ." + name + ".");
            (string ModuleId, ModuleGmCommand Command) owner = _gmCommands.FirstOrDefault(x =>
                string.Equals(x.Command.Name, name, StringComparison.OrdinalIgnoreCase));
            if (owner.Command != null)
                throw new InvalidOperationException("GM command ." + name + " is already registered by module " + owner.ModuleId + ".");
            _gmCommands.Add((moduleId, command));
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

            public ZoneModuleContext(ZoneModuleHost host, string moduleId, string moduleDirectory, CapabilityGate gate)
            {
                _host = host;
                ModuleId = moduleId;
                ModuleDirectory = moduleDirectory;
                Log = new ModuleLogger(host._logger, moduleId);
                Events = new ModuleEventsView(host.Events, moduleId);
                Knubot = host._knubot;
                World = new GatedWorld(host.World, gate);
                Actions = new GatedActions(host._actions, gate);
                GameData = new GatedGameData(host._gameData, gate);
            }

            public IZoneWorld World { get; }

            public IZoneGameData GameData { get; }

            public IZoneActions Actions { get; }

            public string ModuleId { get; }

            public string ModuleDirectory { get; }

            public IModuleLogger Log { get; }

            public IZoneEvents Events { get; }

            public IKnubotScripts Knubot { get; }

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

            public void RegisterGmCommand(ModuleGmCommand command) => _host.RegisterGmCommand(ModuleId, command);
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

            public event Action<PlayerPlayfieldEvent> PlayerEnteredPlayfield
            {
                add => _events.PlayerEnteredPlayfield.Add(_moduleId, value);
                remove => _events.PlayerEnteredPlayfield.Remove(_moduleId, value);
            }

            public event Action<PlayerPlayfieldEvent> PlayerLeftPlayfield
            {
                add => _events.PlayerLeftPlayfield.Add(_moduleId, value);
                remove => _events.PlayerLeftPlayfield.Remove(_moduleId, value);
            }

            public event Action<PlayerSessionEvent> PlayerLoggedIn
            {
                add => _events.PlayerLoggedIn.Add(_moduleId, value);
                remove => _events.PlayerLoggedIn.Remove(_moduleId, value);
            }

            public event Action<PlayerSessionEvent> PlayerLoggedOut
            {
                add => _events.PlayerLoggedOut.Add(_moduleId, value);
                remove => _events.PlayerLoggedOut.Remove(_moduleId, value);
            }

            public event Action<NpcSpawnEvent> NpcSpawned
            {
                add => _events.NpcSpawned.Add(_moduleId, value);
                remove => _events.NpcSpawned.Remove(_moduleId, value);
            }

            public event Action<NpcSpawnEvent> NpcDespawned
            {
                add => _events.NpcDespawned.Add(_moduleId, value);
                remove => _events.NpcDespawned.Remove(_moduleId, value);
            }

            public event Action<KnubotChatOpenedEvent> KnubotChatOpened
            {
                add => _events.KnubotChatOpened.Add(_moduleId, value);
                remove => _events.KnubotChatOpened.Remove(_moduleId, value);
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
