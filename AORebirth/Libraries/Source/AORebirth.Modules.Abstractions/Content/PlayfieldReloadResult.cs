namespace AORebirth.Modules.Content
{
    using System.Collections.Generic;

    /// <summary>What a <see cref="IZoneGameData.ReloadPlayfields"/> did (module API 1.20).</summary>
    public sealed class PlayfieldReloadResult
    {
        public PlayfieldReloadResult(bool ok, int released, IReadOnlyList<PlayfieldSpawnReload> playfields,
            IReadOnlyList<string> problems)
        {
            Ok = ok;
            Released = released;
            Playfields = playfields;
            Problems = problems;
        }

        /// <summary>
        /// True when every file read and every running playfield that was asked applied its spawn points. A playfield
        /// that is not running here, or is a dungeon, is listed as not applied without making this false.
        /// </summary>
        public bool Ok { get; }

        /// <summary>How many playfields that are not running had their cached files dropped (read again when they load).</summary>
        public int Released { get; }

        /// <summary>One entry per running playfield reloaded, and per playfield id asked for.</summary>
        public IReadOnlyList<PlayfieldSpawnReload> Playfields { get; }

        /// <summary>Problems that belong to no one playfield; empty on a clean reload.</summary>
        public IReadOnlyList<string> Problems { get; }
    }

    /// <summary>
    /// One running playfield's spawn point reload (module API 1.20). Points are Spawns.json entries and Districts.json
    /// spawn table entries; a district's points count together, since each load spreads them over its locations anew.
    /// </summary>
    public sealed class PlayfieldSpawnReload
    {
        public PlayfieldSpawnReload(int playfieldId, bool applied, int kept, int added, int changed, int removed,
            int despawned, IReadOnlyList<string> problems)
        {
            PlayfieldId = playfieldId;
            Applied = applied;
            Kept = kept;
            Added = added;
            Changed = changed;
            Removed = removed;
            Despawned = despawned;
            Problems = problems;
        }

        public int PlayfieldId { get; }

        /// <summary>True when the new spawn points are in play; false keeps the old ones (see <see cref="Problems"/>).</summary>
        public bool Applied { get; }

        /// <summary>Points unchanged, kept with their NPCs, kills and timers.</summary>
        public int Kept { get; }

        /// <summary>New points; they spawn on their normal rules (respawn chance, cell awake).</summary>
        public int Added { get; }

        /// <summary>Spawns.json points at the same hash and position whose entry changed: the old NPCs went, the new point spawns.</summary>
        public int Changed { get; }

        /// <summary>Points no longer in the files, or (districts) replaced by a changed spawn table.</summary>
        public int Removed { get; }

        /// <summary>NPCs and statics of removed and changed points taken out of the world (no corpse or loot).</summary>
        public int Despawned { get; }

        /// <summary>
        /// Why it was not applied, or entries the new files hold that cannot spawn here and were not reported before (for
        /// example "skipped DORY at (2745.8, 17.3, 514.5): no NpcTemplates entry that can spawn at level 115").
        /// </summary>
        public IReadOnlyList<string> Problems { get; }
    }
}
