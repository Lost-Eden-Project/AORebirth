namespace ZoneEngine_New.Core.Ai
{
    using System;
    using System.Collections.Generic;

    using ZoneEngine_New.Core.Configuration;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Playfield.Locality;

    /// <summary>
    /// Healing draws aggro (Gameplay.json threat.healThreatPercent; live: 2 points of healing make 1 point of threat).
    /// Every NPC near the healed one that has it on its hate list adds that much threat for the healer, each the full
    /// amount. Only health the heal actually restored counts, so an overheal draws nothing past the top-up. A pet's
    /// brain is left out: a pet picks its fights from its owner.
    /// </summary>
    internal static class HealThreat
    {
        // Tick thread only, like the locality's own scratch; never handed out.
        [ThreadStatic]
        static List<NpcCharacter>? _nearby;

        internal static void Apply(Character healer, Character healed, int restored)
        {
            ArgumentNullException.ThrowIfNull(healer);
            ArgumentNullException.ThrowIfNull(healed);
            if (restored <= 0 || healer.IsDead || healed.IsDead
                || healed.Playfield is not Playfield.Playfield playfield || !ReferenceEquals(healer.Playfield, playfield)
                || playfield.GetService<GameplaySettings>()?.Threat is not { HealThreatPercent: > 0 } rules
                || playfield.GetService<PlayfieldLocality>() is not PlayfieldLocality locality)
                return;

            float threat = restored * rules.HealThreatPercent / 100f;
            if (!float.IsFinite(threat) || threat <= 0f)
                return;

            List<NpcCharacter> nearby = _nearby ??= new List<NpcCharacter>();
            // An NPC fighting the healed one is within its engage range of them, which the locality ring covers.
            locality.CollectNearby(healed, NpcAiRules.EngageRange(isCurrentTarget: false, leashRange: 0f), nearby);
            try
            {
                for (int i = 0; i < nearby.Count; i++)
                {
                    NpcCharacter npc = nearby[i];
                    if (ReferenceEquals(npc, healer) || ReferenceEquals(npc, healed) || npc.IsDead
                        || npc.Brain is not NpcBrain { Pet: null } brain || !brain.Hate.Contains(healed.Identity))
                        continue;

                    brain.AddThreatFrom(healer, threat);
                }
            }
            finally
            {
                nearby.Clear();
            }
        }
    }
}
