namespace ZoneEngine_New.Core.Mobs
{
    /// <summary>
    /// Behaviour switches a mob hash lists in <c>MonsterCombatRules.json</c> ("NEBG": { "Features": ["Guard"] }).
    /// </summary>
    public enum NpcFeature
    {
        /// <summary>Cannot be attacked and gets no combat brain (never aggroes or fights back).</summary>
        NoCombat,

        /// <summary>
        /// Stands where it spawned: never paths, wanders or chases, and fights from there (NpcCharacter.IsImmobile),
        /// even though its MonsterData body can move. For stationary bosses whose body record is a mobile one.
        /// </summary>
        NoMove,

        Wander,

        /// <summary>
        /// Defends players: attacks any NPC within its aggro range (ProximityRangeOutdoors/Indoors) that is fighting a
        /// player or a player's pet. Otherwise an ordinary NPC (it can be attacked, and fights back). Which NPC families an
        /// NPC attacks on its own is MonsterCombatRules.json "Hates".
        /// </summary>
        Guard,

        /// <summary>
        /// Its corpse lasts 30 minutes instead of the usual 3 (<see cref="Entities.Corpse"/>). Set on its own for a
        /// template whose Flags has the boss bit (NpcTemplateCatalog), and can be listed for any other hash.
        /// </summary>
        Boss,

        /// <summary>
        /// Answers its kind's calls for help (Gameplay.json socialAggro) even when it has no aggressiveness of its own
        /// (BreedHostility 0). Without it, only aggressive NPCs and guards join a fight they were not part of.
        /// </summary>
        Social,

        /// <summary>
        /// Pulls players to it in a fight: runs the NpcScripts program bound to this feature
        /// (<c>GameData/NpcScripts/Summoner.json</c>, a CastNano on a random hostile beyond its minimum distance with
        /// a warp to the NPC's feet) beside whatever program the NPC's own binding gives it. The feature only names
        /// the NPC; the nano, cadence and distance are the script's.
        /// </summary>
        Summoner,
    }
}
