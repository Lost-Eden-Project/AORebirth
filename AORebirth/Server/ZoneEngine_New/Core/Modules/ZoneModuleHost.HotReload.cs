namespace ZoneEngine_New.Core.Modules
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Reflection.Metadata;
    using System.Reflection.PortableExecutable;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;

    using AORebirth.Modules;
    using AORebirth.Modules.Commands;

    public enum ModuleReloadOutcome
    {
        Ok,
        DryRun,
        Refused,
        Leaked,
        Failed
    }

    /// <summary>
    /// What a hot reload did. <see cref="Describe"/> is the one-line form the request file's result and the GM command
    /// report: <c>ok …</c>, <c>ok-dryrun …</c>, <c>refused:&lt;code&gt; …</c>, <c>leaked …</c> (the new version runs, the old
    /// one stayed in memory) or <c>failed:&lt;code&gt; restored=yes|no …</c>.
    /// </summary>
    public sealed record ModuleReloadResult(
        ModuleReloadOutcome Outcome,
        string ModuleId,
        string? Code = null,
        string? Detail = null,
        string? Version = null,
        int Generation = 0,
        long Milliseconds = 0,
        string State = "none",
        bool Restored = false,
        int Leaks = 0)
    {
        public string Describe()
        {
            string detail = string.IsNullOrWhiteSpace(Detail) ? string.Empty : " " + Detail.Replace('\r', ' ').Replace('\n', ' ');
            return Outcome switch
            {
                ModuleReloadOutcome.Ok => string.Format(CultureInfo.InvariantCulture, "ok id={0} version={1} gen={2} ms={3} state={4}",
                    ModuleId, Version, Generation, Milliseconds, State),
                ModuleReloadOutcome.DryRun => "ok-dryrun id=" + ModuleId,
                ModuleReloadOutcome.Leaked => string.Format(CultureInfo.InvariantCulture,
                    "leaked id={0} version={1} gen={2} ms={3} state={4} leaks={5}", ModuleId, Version, Generation, Milliseconds, State, Leaks),
                ModuleReloadOutcome.Refused => "refused:" + Code + detail,
                _ => "failed:" + Code + " restored=" + (Restored ? "yes" : "no") + detail
            };
        }
    }

    /// <summary>One loaded module as <c>.module list</c> shows it.</summary>
    public sealed record ModuleStatus(string Id, string Version, string Sha256, int Generation, bool HotReloadable);

    /// <summary>
    /// Module hot reload. A reload stops dispatch to the module, waits for every playfield to finish the tick it is on,
    /// exports its state, shuts it down, unloads its collectible context and checks with a weak reference that the code
    /// really left memory, then loads the new version from a fresh staged copy. Refused (nothing changes) when the
    /// preflight fails; a new version that fails to load is replaced by the old one again.
    /// </summary>
    public sealed partial class ZoneModuleHost
    {
        private static readonly TimeSpan QuiesceTimeout = TimeSpan.FromSeconds(5);

        private const int CollectRounds = 10;

        private readonly Func<Playfield.PlayfieldManager> _playfields;
        private readonly object _sync = new();
        private readonly SemaphoreSlim _reloadGate = new(1, 1);
        private HotReloadSettings _hotReload = new();
        private string _modulesDirectory = string.Empty;
        private string? _stagingRoot;
        private volatile bool _reloading;
        private volatile bool _shuttingDown;
        private int _leaks;
        private Func<string, IReadOnlyList<ModuleGmCommand>, string?>? _gmBinder;

        /// <summary>Modules.json HotReload; Enabled is false unless the file turns it on.</summary>
        public HotReloadSettings HotReload => _hotReload;

        /// <summary>
        /// The GM command dispatcher's hook for replacing one module's commands (empty to remove them). It answers null,
        /// or why the commands were refused (a name another command already has).
        /// </summary>
        internal void AttachGmCommandBinder(Func<string, IReadOnlyList<ModuleGmCommand>, string?> binder)
            => _gmBinder = binder ?? throw new ArgumentNullException(nameof(binder));

        public IReadOnlyList<ModuleStatus> ListModules()
        {
            lock (_sync)
                return _modules.Select(m => new ModuleStatus(m.Id, m.Version, m.Sha256, m.Generation, m.Context != null)).ToList();
        }

        /// <summary>Starts a reload off the caller's thread (never on a playfield tick, which the reload waits for).</summary>
        public Task<ModuleReloadResult> ReloadAsync(string moduleId, string? sourceDirectory, bool dryRun, string requestedBy)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
            return Task.Run(() => ReloadGatedAsync(moduleId.Trim(), sourceDirectory, dryRun, requestedBy ?? "unknown"));
        }

        private async Task<ModuleReloadResult> ReloadGatedAsync(string moduleId, string? sourceDirectory, bool dryRun, string requestedBy)
        {
            if (!_reloadGate.Wait(0))
                return Refused(moduleId, "busy", "another module reload is running");
            try
            {
                ModuleReloadResult result = await ReloadLockedAsync(moduleId, sourceDirectory, dryRun, requestedBy).ConfigureAwait(false);
                string line = "Module hot reload by " + requestedBy + ": " + result.Describe();
                if (result.Outcome is ModuleReloadOutcome.Ok or ModuleReloadOutcome.DryRun)
                    _logger.Info(line);
                else
                    _logger.Warn(line);
                return result;
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Module hot reload of " + moduleId + " failed unexpectedly.");
                return new ModuleReloadResult(ModuleReloadOutcome.Failed, moduleId, "unexpected", exception.GetBaseException().Message);
            }
            finally
            {
                _reloadGate.Release();
            }
        }

        private async Task<ModuleReloadResult> ReloadLockedAsync(string requestedId, string? source, bool dryRun, string requestedBy)
        {
            var clock = Stopwatch.StartNew();
            string? refusal = Preflight(requestedId, source, out string id, out string moduleDirectory, out string sourceDirectory,
                out string detail);
            if (refusal != null)
                return Refused(id, refusal, detail);
            if (dryRun)
                return new ModuleReloadResult(ModuleReloadOutcome.DryRun, id);

            _logger.Warn("Module hot reload id=" + id + " source=" + sourceDirectory + " by " + requestedBy + ": stopping the running version.");

            // 1. Nothing new reaches the old version: events, observers, handlers, reloadables, GM commands, hooks.
            Forget(id);
            _gmBinder?.Invoke(id, Array.Empty<ModuleGmCommand>());

            // 2. Calls already under way finish: every playfield completes the tick it is on.
            await QuiesceAsync().ConfigureAwait(false);

            // 3. Export, Shutdown, unload; nothing of the old version is referenced past this call.
            DetachedModule detached = Detach(id);

            // 4. Did the code leave memory? Background collections, so the zone does not stall on a large heap.
            int rounds = await CollectAsync(detached.Context).ConfigureAwait(false);
            bool leaked = rounds < 0;
            if (leaked)
            {
                int leaks = Interlocked.Increment(ref _leaks);
                _logger.Error(string.Format(CultureInfo.InvariantCulture,
                    "Module {0} generation {1} did not unload: something still references its code (leaks in this process: {2}).",
                    id, detached.Generation, leaks));
            }
            else
            {
                _logger.Info(string.Format(CultureInfo.InvariantCulture, "Module {0} generation {1} unloaded after {2} collection(s).",
                    id, detached.Generation, rounds));
            }

            if (!detached.Stopped)
                _logger.Error("Module " + id + " did not finish Shutdown within " + _hotReload.ShutdownTimeoutSeconds.ToString(CultureInfo.InvariantCulture)
                    + "s; loading the new version anyway.");

            // 5. The new version.
            int generation = detached.Generation + 1;
            try
            {
                LoadedModule loaded = LoadAndBind(id, moduleDirectory, sourceDirectory, generation, detached.Index);
                string state = Import(loaded, detached.State);
                clock.Stop();
                // The old copy is only kept to fall back to; a leaked one is still mapped and stays.
                if (!leaked)
                    TryDelete(detached.StagedDirectory);
                return new ModuleReloadResult(leaked ? ModuleReloadOutcome.Leaked : ModuleReloadOutcome.Ok, id, Version: loaded.Version,
                    Generation: generation, Milliseconds: clock.ElapsedMilliseconds, State: state, Leaks: Volatile.Read(ref _leaks));
            }
            catch (Exception exception)
            {
                string reason = exception.GetBaseException().Message;
                _logger.Error(exception, "Module " + id + " new version failed to load; loading the previous version again.");
                bool restored = false;
                if (detached.StagedDirectory != null && Directory.Exists(detached.StagedDirectory))
                {
                    try
                    {
                        LoadedModule previous = LoadAndBind(id, moduleDirectory, detached.StagedDirectory, generation + 1, detached.Index);
                        Import(previous, detached.State);
                        restored = true;
                    }
                    catch (Exception restoreFailure)
                    {
                        _logger.Error(restoreFailure, "Module " + id + " previous version failed to load again; the module is not running.");
                    }
                }

                return new ModuleReloadResult(ModuleReloadOutcome.Failed, id, "load", reason, Restored: restored,
                    Leaks: Volatile.Read(ref _leaks));
            }
        }

        /// <summary>Why a reload of <paramref name="requestedId"/> cannot run now, or null. Changes nothing.</summary>
        private string? Preflight(string requestedId, string? source, out string id, out string moduleDirectory,
            out string sourceDirectory, out string detail)
        {
            id = requestedId;
            moduleDirectory = string.Empty;
            sourceDirectory = string.Empty;
            detail = string.Empty;
            if (!_hotReload.Enabled)
                return "hot_reload_disabled";
            if (_shuttingDown)
                return "shutting_down";
            int leaks = Volatile.Read(ref _leaks);
            if (leaks >= Math.Max(1, _hotReload.MaxLeaks))
            {
                detail = leaks.ToString(CultureInfo.InvariantCulture) + " unload(s) leaked in this process; restart the zone";
                return "leak_limit";
            }

            LoadedModule? current;
            lock (_sync)
                current = _modules.FirstOrDefault(m => string.Equals(m.Id, requestedId, StringComparison.OrdinalIgnoreCase));
            if (current == null)
                return "not_loaded";
            id = current.Id;
            if (current.Context == null)
            {
                detail = "its module.json has no SupportsHotReload, or it loaded before HotReload was enabled";
                return "not_hot_reloadable";
            }

            if (_required.Contains(id))
                return "required";
            if (string.Equals(_connectionFilterOwner, id, StringComparison.OrdinalIgnoreCase))
                return "owns_connection_filter";
            if (string.Equals(_handoffHookOwner, id, StringComparison.OrdinalIgnoreCase))
                return "owns_handoff_hook";

            // Old and new code are both in memory for a moment, and a leak keeps the old one for good.
            GCMemoryInfo memory = GC.GetGCMemoryInfo();
            if (memory.HighMemoryLoadThresholdBytes > 0 && memory.MemoryLoadBytes >= memory.HighMemoryLoadThresholdBytes)
            {
                detail = string.Format(CultureInfo.InvariantCulture, "memory load {0} MB of {1} MB threshold",
                    memory.MemoryLoadBytes / 1048576, memory.HighMemoryLoadThresholdBytes / 1048576);
                return "memory_headroom";
            }

            moduleDirectory = Path.GetFullPath(Path.Combine(_modulesDirectory, id));
            sourceDirectory = string.IsNullOrWhiteSpace(source) ? moduleDirectory : Path.GetFullPath(source.Trim());
            if (!Directory.Exists(sourceDirectory))
            {
                detail = sourceDirectory;
                return "source_missing";
            }

            try
            {
                (ModuleManifest manifest, string assemblyPath) = ReadManifest(id, sourceDirectory);
                if (!manifest.SupportsHotReload)
                    return "new_version_not_hot_reloadable";
                int major = ReferencedApiMajor(assemblyPath);
                if (major != ApiMajorVersion)
                {
                    detail = string.Format(CultureInfo.InvariantCulture, "built against module API {0}; this engine provides {1}", major, ApiMajorVersion);
                    return "api_major";
                }

                _capabilities.TryGetValue(id, out List<string>? granted);
                var gate = new CapabilityGate(id, granted, _logger);
                CheckOperations(manifest, gate);
                ModuleDatabase.RequireCurrent(id, sourceDirectory, gate, _databaseConnection);
            }
            catch (Exception exception)
            {
                detail = exception.GetBaseException().Message;
                return "invalid_new_version";
            }

            return null;
        }

        /// <summary>The module API major the assembly was built against, read from its metadata without loading it; -1 for none.</summary>
        private static int ReferencedApiMajor(string assemblyPath)
        {
            using FileStream stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            MetadataReader metadata = pe.GetMetadataReader();
            foreach (AssemblyReferenceHandle handle in metadata.AssemblyReferences)
            {
                AssemblyReference reference = metadata.GetAssemblyReference(handle);
                if (string.Equals(metadata.GetString(reference.Name), ModuleLoadContext.SharedAssemblyName, StringComparison.OrdinalIgnoreCase))
                    return reference.Version.Major;
            }

            return -1;
        }

        /// <summary>Waits until every running playfield has finished the tick it was on when the module was taken out.</summary>
        private async Task QuiesceAsync()
        {
            IReadOnlyList<Playfield.Playfield> playfields;
            try
            {
                playfields = _playfields().SnapshotPlayfields();
            }
            catch (Exception exception)
            {
                _logger.Warn("Module hot reload: playfields unavailable (" + exception.GetType().Name + "); waiting a moment instead.");
                playfields = Array.Empty<Playfield.Playfield>();
            }

            var ticks = new List<Task>(playfields.Count);
            foreach (Playfield.Playfield playfield in playfields)
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (playfield.Dispatch(() => done.TrySetResult()))
                    ticks.Add(done.Task);
            }

            try
            {
                await Task.WhenAll(ticks).WaitAsync(QuiesceTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.Warn("Module hot reload: not every playfield finished its tick within " + QuiesceTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + "s.");
            }

            // Calls from network and World threads, which no tick covers.
            await Task.Delay(250).ConfigureAwait(false);
        }

        /// <summary>What is left of the old version once it is detached: no reference to any of its objects or types.</summary>
        private readonly record struct DetachedModule(int Index, int Generation, string? StagedDirectory,
            Dictionary<string, string> State, WeakReference Context, bool Stopped);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private DetachedModule Detach(string id)
        {
            LoadedModule old;
            int index;
            lock (_sync)
            {
                index = _modules.FindIndex(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
                old = _modules[index];
            }

            var state = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (IZoneModule entry in old.Entries)
            {
                if (entry is not IZoneModuleStateTransfer transfer)
                    continue;
                try
                {
                    string? text = transfer.ExportState();
                    if (text != null)
                        state[entry.GetType().FullName ?? entry.GetType().Name] = text;
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, "Module " + id + " failed to export its state; it is lost.");
                }
            }

            TimeSpan limit = TimeSpan.FromSeconds(Math.Max(1, _hotReload.ShutdownTimeoutSeconds));
            bool stopped = Task.Run(() => ShutdownModule(old)).Wait(limit);
            old.Gate?.Revoke();
            lock (_sync)
                _modules.RemoveAt(index);

            ModuleLoadContext context = old.Context!;
            var weak = new WeakReference(context, trackResurrection: false);
            old.Context = null;
            old.Entries.Clear();
            context.Unload();
            return new DetachedModule(index, old.Generation, old.StagedDirectory, state, weak, stopped);
        }

        /// <summary>Collections until the context is gone: the rounds it took, or -1 when it stayed (leaked).</summary>
        private static async Task<int> CollectAsync(WeakReference context)
        {
            for (int round = 1; round <= CollectRounds; round++)
            {
                // System.Text.Json keeps the property accessors it generated in one process-wide cache. An entry expires a
                // second after its last use but is only evicted when the cache is next added to, so until then the
                // unloaded module's config types stay referenced. One throwaway deserialization adds to it.
                if (round == 1)
                    await Task.Delay(TimeSpan.FromMilliseconds(1200)).ConfigureAwait(false);
                EvictJsonAccessorCache();
                // Not blocking: a background collection, so a large heap does not stall every playfield.
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
                await Task.Delay(500).ConfigureAwait(false);
                GC.WaitForPendingFinalizers();
                if (!context.IsAlive)
                    return round;
            }

            return -1;
        }

        private static void EvictJsonAccessorCache()
        {
            try
            {
                // A resolver of its own builds the metadata afresh, which asks the accessor cache (and so evicts) every time.
                var options = new System.Text.Json.JsonSerializerOptions
                {
                    TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
                };
                System.Text.Json.JsonSerializer.Deserialize<JsonCacheProbe>("{\"Value\":1}", options);
            }
            catch (Exception)
            {
                // Only a nudge; the leak check reports whatever stays.
            }
        }

        private sealed class JsonCacheProbe
        {
            public int Value { get; set; }
        }

        /// <summary>Loads a version and gives it its GM commands; on any failure nothing of it stays registered.</summary>
        private LoadedModule LoadAndBind(string id, string moduleDirectory, string sourceDirectory, int generation, int index)
        {
            LoadedModule loaded;
            _reloading = true;
            try
            {
                loaded = LoadModule(id, moduleDirectory, sourceDirectory, generation);
            }
            catch
            {
                Forget(id);
                throw;
            }
            finally
            {
                _reloading = false;
            }

            List<ModuleGmCommand> commands = _gmCommands
                .Where(x => string.Equals(x.ModuleId, id, StringComparison.OrdinalIgnoreCase)).Select(x => x.Command).ToList();
            string? clash = _gmBinder?.Invoke(id, commands);
            if (clash != null)
            {
                Discard(loaded);
                throw new InvalidOperationException(clash);
            }

            foreach (ModuleGmCommand command in commands)
                _logger.Info(string.Format(CultureInfo.InvariantCulture, "Module {0} GM command .{1} gmLevel={2}",
                    id, command.Name, command.RequiredGmLevel));
            lock (_sync)
                _modules.Insert(Math.Clamp(index, 0, _modules.Count), loaded);
            return loaded;
        }

        private void Discard(LoadedModule module)
        {
            ShutdownModule(module);
            module.Gate?.Revoke();
            Forget(module.Id);
            _gmBinder?.Invoke(module.Id, Array.Empty<ModuleGmCommand>());
            module.Entries.Clear();
            module.Context?.Unload();
            module.Context = null;
        }

        /// <summary>Hands each new entry what the entry of the same type exported: imported, partial, lost or none.</summary>
        private string Import(LoadedModule module, Dictionary<string, string> state)
        {
            if (state.Count == 0)
                return "none";
            int imported = 0;
            foreach (IZoneModule entry in module.Entries)
            {
                if (entry is not IZoneModuleStateTransfer transfer
                    || !state.TryGetValue(entry.GetType().FullName ?? entry.GetType().Name, out string? text))
                    continue;
                try
                {
                    transfer.ImportState(text);
                    imported++;
                }
                catch (Exception exception)
                {
                    _logger.Error(exception, "Module " + module.Id + " failed to import its state.");
                }
            }

            return imported == state.Count ? "imported" : imported == 0 ? "lost" : "partial";
        }

        /// <summary>This process's own staging folder; leftovers of processes that are gone are removed.</summary>
        private void PrepareStaging()
        {
            string root = _hotReload.ResolveStagingDirectory();
            string own = "p" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-"
                + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            _stagingRoot = Path.Combine(root, own);
            try
            {
                Directory.CreateDirectory(_stagingRoot);
                foreach (string directory in Directory.GetDirectories(root, "p*"))
                {
                    string name = Path.GetFileName(directory);
                    int dash = name.IndexOf('-');
                    if (dash < 2 || string.Equals(name, own, StringComparison.Ordinal)
                        || !int.TryParse(name.AsSpan(1, dash - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
                        || IsRunning(pid))
                        continue;
                    TryDelete(directory);
                }
            }
            catch (Exception exception)
            {
                _logger.Warn("Modules: hot reload staging " + root + ": " + exception.Message);
            }

            _logger.Info(string.Format(CultureInfo.InvariantCulture, "Modules: hot reload enabled staging={0} maxLeaks={1} shutdownTimeout={2}s",
                _stagingRoot, _hotReload.MaxLeaks, _hotReload.ShutdownTimeoutSeconds));
        }

        private static bool IsRunning(int processId)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Copies a module's code (never its data) into a new generation folder, which is what gets loaded.</summary>
        private string Stage(string id, string sourceDirectory, int generation)
        {
            if (_stagingRoot == null)
                throw new InvalidOperationException("Hot reload staging is not prepared.");
            string target = Path.Combine(_stagingRoot, id, ".gen-" + generation.ToString(CultureInfo.InvariantCulture));
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            Directory.CreateDirectory(target);
            foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(sourceDirectory, file);
                if (!IsCode(relative))
                    continue;
                string destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }

            return target;
        }

        private static bool IsCode(string relative)
        {
            string name = Path.GetFileName(relative);
            string extension = Path.GetExtension(name);
            if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) || extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".so", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dylib", StringComparison.OrdinalIgnoreCase))
                return true;
            bool topLevel = string.IsNullOrEmpty(Path.GetDirectoryName(relative));
            return topLevel && (name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, ModuleManifest.FileName, StringComparison.OrdinalIgnoreCase));
        }

        private void TryDelete(string? directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return;
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception)
            {
                _logger.Warn("Modules: could not remove " + directory + ": " + exception.GetType().Name);
            }
        }

        private static ModuleReloadResult Refused(string id, string code, string? detail)
            => new(ModuleReloadOutcome.Refused, id, code, detail);
    }
}
