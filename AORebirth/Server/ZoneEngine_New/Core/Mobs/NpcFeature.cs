namespace ZoneEngine_New.Core.Mobs
{
    /// <summary>
    /// Behaviour switches a mob hash lists in <c>MonsterCombatRules.json</c> ("NEBG": { "Features": ["Guard"] }).
    /// </summary>
    public enum NpcFeature
    {
        /// <summary>Cannot be attacked and gets no combat brain (never aggroes or fights back).</summary>
        NoCombat,

        NoMove,

        Wander,

        /// <summary>
        /// Defends players: attacks any NPC within its aggro range (ProximityRangeOutdoors/Indoors) that is fighting a
        /// player or a player's pet. Otherwise an ordinary NPC (it can be attacked, and fights back). Which NPC families an
        /// NPC attacks on its own is MonsterCombatRules.json "Hates".
        /// </summary>
        Guard,
    }
}
