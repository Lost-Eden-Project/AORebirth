namespace AORebirth.Modules.Content
{
    using System.Collections.Generic;

    /// <summary>
    /// The zone's Knubot dialog scripts. Scripts load from GameData/Knubot, with the folder named by the
    /// AO_REBIRTH_KNUBOT_PATH environment variable laid over it: a script there replaces the GameData script with the
    /// same id (its path under the folder, without ".json", '/'-separated) and takes its NPCs from any other.
    /// </summary>
    public interface IKnubotScripts
    {
        /// <summary>The AO_REBIRTH_KNUBOT_PATH folder, or null when the variable is not set.</summary>
        string? OverrideDirectory { get; }

        /// <summary>
        /// The errors <paramref name="json"/> would be rejected with as script <paramref name="scriptId"/>; empty when it
        /// would load. Nothing is installed.
        /// </summary>
        IReadOnlyList<string> Check(string scriptId, string json);

        /// <summary>
        /// Loads every script again and swaps the result in. Chats already open keep the script they started with. Safe
        /// to call from any thread.
        /// </summary>
        KnubotReloadResult Reload();
    }

    /// <summary>What a <see cref="IKnubotScripts.Reload"/> installed, and one "file: reasons" line per script it left out.</summary>
    public sealed class KnubotReloadResult
    {
        public KnubotReloadResult(int loaded, int npcs, IReadOnlyList<string> problems)
        {
            Loaded = loaded;
            Npcs = npcs;
            Problems = problems;
        }

        public int Loaded { get; }

        public int Npcs { get; }

        public IReadOnlyList<string> Problems { get; }
    }
}
