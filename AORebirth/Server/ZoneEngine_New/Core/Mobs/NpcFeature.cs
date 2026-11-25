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

        /// <summary>
        /// Leaves an Exit Device where it dies: runs the NpcScripts program bound to this feature
        /// (<c>GameData/NpcScripts/Mothership_ExitDevice.json</c>) beside whatever program the NPC's own binding gives it.
        /// The feature only names the NPC; the item and its lifetime are the script's.
        /// </summary>
        SpawnsExitDevice,

        /// <summary>
        /// Leaves an Exit Portal where it dies: runs the NpcScripts program bound to this feature
        /// (<c>GameData/NpcScripts/Mission_ExitPortal.json</c>) beside whatever program the NPC's own binding gives it.
        /// Given to a mission's objective NPC by its objective block (Dungeons/Objectives.json "Features": the Rubi-Ka
        /// terminal missions' boss), so the same template elsewhere leaves none. The item and its lifetime are the script's.
        /// </summary>
        SpawnsExitPortal,

        /// <summary>
        /// Its kill gives no XP at all: no regular kill XP, not even the grey 1 XP, and no alien XP
        /// (<see cref="Entities.Character"/> kill rewards). Loot, quest kill credit and the rest of the kill are unchanged.
        /// Pets never give kill XP, with or without it.
        /// </summary>
        NoXp,

        /// <summary>
        /// Its friendly nanos (heals and buffs) go only on the NPC that spawned it (a Regeneration Conduit healing
        /// Master of Biological Metamorphoses): never on itself or any other NPC. With that NPC dead or gone it casts
        /// none of them. Hostile nanos and summons are unchanged.
        /// </summary>
        HealsSpawner,

        /// <summary>
        /// Attacks players on sight whatever their side: its own Side (a Clan or Omni soldier, a Neutral civilian) does
        /// not choose whom it goes for, and a guard's opposing-faction rule does not apply. It still needs aggressiveness
        /// of its own (BreedHostility above 0), and <see cref="Passive"/> wins over it. Listed for a hash, or for every
        /// monster of a dungeon population (Dungeons/Population.json "Features": Rubi-Ka mission humanoids).
        /// </summary>
        AggroesAllSides,

        /// <summary>
        /// Never starts a fight with a player: no aggro on sight from its own aggressiveness (BreedHostility) or, as a
        /// guard, from the opposing-faction rule (an Omni guard that leaves Clans alone). It still fights back when
        /// attacked and answers its kind's calls for help, and a guard still defends players against NPCs and attacks
        /// the families it Hates.
        /// </summary>
        Passive,

        /// <summary>
        /// Always in the world, like a scripted NPC: its spawn point's cell stays Cold from playfield load (ticks once a
        /// second with no player near), so it spawns as soon as the playfield loads, without a player waking the cell,
        /// and is never despawned by a cell going to sleep. A living one keeps whatever cell it stands in Cold, so it
        /// keeps ticking (its nanos and buffs keep their timers) after it walks off its point. After a death it respawns
        /// on its own Spawns.json respawnTime and respawnChance, with no player needed. For world NPCs players wait on.
        /// </summary>
        Persistent,

        /// <summary>
        /// Fights although its template is blue-named: a blue name (Flags HasBlueName) otherwise makes an NPC
        /// <see cref="NoCombat"/> (NpcTemplateCatalog). With it the NPC keeps its blue name but can be attacked, gets a
        /// combat brain and fights like any other monster. An explicitly listed <see cref="NoCombat"/> still wins, and the
        /// unresolved placeholder stays NoCombat.
        /// </summary>
        Combat,
    }
}
