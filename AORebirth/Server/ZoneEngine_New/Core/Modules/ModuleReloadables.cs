namespace ZoneEngine_New.Core.Modules
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Threading;

    using AORebirth.Modules.Content;

    using ZoneEngine_New.Core.GameData;

    /// <summary>
    /// The modules' own reloadable data (module API 1.13), each listed as <c>&lt;module id&gt;/&lt;name&gt;</c> by the
    /// zone's <see cref="GameDataReloadRegistry"/> after its GameData catalogs. It depends on nothing, so the module host
    /// fills it while modules initialize and the registry reads it whenever it is built.
    /// </summary>
    public sealed class ModuleReloadables
    {
        public const char Separator = '/';

        readonly Lock _lock = new();
        readonly Dictionary<string, Entry> _byName = new(StringComparer.OrdinalIgnoreCase);
        string[] _names = [];

        /// <summary>The full names, in registration order.</summary>
        public IReadOnlyList<string> Names => Volatile.Read(ref _names);

        public bool TryGet(string name, [MaybeNullWhen(false)] out IReloadableGameData reload)
        {
            lock (_lock)
            {
                bool found = _byName.TryGetValue(name, out Entry? entry);
                reload = entry;
                return found;
            }
        }

        internal void Add(string moduleId, IModuleReloadable reloadable)
        {
            ArgumentNullException.ThrowIfNull(reloadable);
            string name = reloadable.Name ?? string.Empty;
            if (name.Length == 0 || name.Length > 32 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                throw new ArgumentException("A reloadable name is 1-32 letters, digits, '-' or '_': '" + name + "'.");

            string fullName = moduleId + Separator + name;
            lock (_lock)
            {
                if (!_byName.TryAdd(fullName, new Entry(moduleId, fullName, reloadable)))
                    throw new InvalidOperationException("Module " + moduleId + " already registered the reloadable " + name + ".");
                Volatile.Write(ref _names, [.. _names, fullName]);
            }
        }

        internal void RemoveModule(string moduleId)
        {
            lock (_lock)
            {
                foreach (Entry entry in _byName.Values.Where(x => string.Equals(x.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase)).ToArray())
                    _byName.Remove(entry.Name);
                Volatile.Write(ref _names, _names.Where(_byName.ContainsKey).ToArray());
            }
        }

        sealed class Entry(string moduleId, string name, IModuleReloadable reloadable) : IReloadableGameData
        {
            public string ModuleId { get; } = moduleId;

            public string Name { get; } = name;

            public IReadOnlyList<string> Files => reloadable.Files ?? [];

            public GameDataReloadResult Reload()
            {
                GameDataReloadResult? result = reloadable.Reload();
                return result == null
                    ? new GameDataReloadResult(Name, false, 0, ["the module's reload returned no result"])
                    : new GameDataReloadResult(Name, result.Ok, result.Entries, result.Problems ?? []);
            }
        }
    }
}
