namespace AORebirth.Modules.Content
{
    using System.Collections.Generic;
    using System.Threading.Tasks;

    /// <summary>
    /// The zone's reloadable GameData (module API 1.3). Files load from the GameData folder, with the folder named by the
    /// AO_REBIRTH_GAMEDATA_OVERRIDE_PATH environment variable laid over it: a file at a relative path there replaces the
    /// shipped file at the same relative path. A reload changes what the next lookup sees; anything already spawned keeps
    /// its state until it respawns (<see cref="World.IZoneActions.RespawnNpc"/>). Each zone instance holds its own
    /// catalogs, so a module running on every instance reloads on every instance.
    /// </summary>
    public interface IZoneGameData
    {
        /// <summary>The AO_REBIRTH_GAMEDATA_OVERRIDE_PATH folder, or null when the variable is not set.</summary>
        string? OverrideDirectory { get; }

        /// <summary>The names <see cref="Reload"/> accepts, e.g. "MonsterCombatRules".</summary>
        IReadOnlyList<string> Reloadable { get; }

        /// <summary>
        /// Reads the named catalog's files again and swaps the result in. A file that fails to read or parse keeps the
        /// catalog as it was and reports why; an unknown name reports that, never throws. Safe from any thread; the files
        /// are read on a worker, never on a playfield tick. Needs <c>gamedata-reload</c>.
        /// </summary>
        Task<GameDataReloadResult> Reload(string name);
    }

    /// <summary>What a <see cref="IZoneGameData.Reload"/> installed, or why it kept the old data.</summary>
    public sealed class GameDataReloadResult
    {
        public GameDataReloadResult(string name, bool ok, int entries, IReadOnlyList<string> problems)
        {
            Name = name;
            Ok = ok;
            Entries = entries;
            Problems = problems;
        }

        public string Name { get; }

        /// <summary>True when the new data was swapped in.</summary>
        public bool Ok { get; }

        /// <summary>How many entries the catalog holds now.</summary>
        public int Entries { get; }

        /// <summary>Why the reload failed, or what it left out; empty on a clean reload.</summary>
        public IReadOnlyList<string> Problems { get; }
    }
}
