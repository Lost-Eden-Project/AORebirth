namespace ZoneEngine_New.Core.Factions
{
    using System;
    using System.Collections.Generic;

    using ZoneEngine_New.Core.Configuration;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Helpers;

    /// <summary>
    /// Shadowlands kill faction (Gameplay.json factions). Killing an Unredeemed monster raises Faction with The Redeemed
    /// and lowers Faction with The Unredeemed, and the other way round (client text: "If you kill an Unredeemed monster,
    /// you need Redeemed faction and vice versa"). The monster's own faction stat is the most standing it can give.
    /// </summary>
    public static class KillFaction
    {
        /// <summary>
        /// The first <see cref="GameplayFactions.Kills"/> rule whose mob stat <paramref name="victim"/> carries above 0, and
        /// that value. False for a monster of no faction.
        /// </summary>
        public static bool TryGetRule(GameplayFactions rules, Character victim, out GameplayFactionKillRule rule, out int mobValue)
        {
            IReadOnlyList<GameplayFactionKillRule> kills = rules.Kills;
            for (int i = 0; i < kills.Count; i++)
            {
                int value = victim.Stats.GetOrZero(kills[i].MobCharacterStat);
                if (value > 0 && !StatCollection.IsUnset(value))
                {
                    rule = kills[i];
                    mobValue = value;
                    return true;
                }
            }

            rule = null!;
            mobValue = 0;
            return false;
        }

        /// <summary>
        /// Points one kill adds to <paramref name="standing"/>: <see cref="GameplayFactions.LerpPercent"/> of the distance
        /// to <paramref name="mobValue"/>, at least <see cref="GameplayFactions.MinGainPercent"/> of the value and 1 point,
        /// never past the value or <see cref="GameplayFactions.MaxStanding"/>. 0 when the standing is already there.
        /// </summary>
        public static int Gain(GameplayFactions rules, int mobValue, int standing)
        {
            long distance = (long)mobValue - standing;
            if (mobValue <= 0 || distance <= 0)
                return 0;

            long lerp = (long)Math.Round(distance * rules.LerpPercent / 100, MidpointRounding.AwayFromZero);
            long floor = (long)Math.Ceiling(mobValue * rules.MinGainPercent / 100);
            long gain = Math.Max(1, Math.Max(lerp, floor));
            gain = Math.Min(gain, distance);
            gain = Math.Min(gain, (long)rules.MaxStanding - standing);
            return gain <= 0 ? 0 : (int)gain;
        }

        /// <summary>Points the same kill takes from the lose standing: <see cref="GameplayFactions.LossPercent"/> of <paramref name="gain"/>.</summary>
        public static int Loss(GameplayFactions rules, int gain)
            => gain <= 0 ? 0 : (int)Math.Min(int.MaxValue, Math.Round(gain * rules.LossPercent / 100, MidpointRounding.AwayFromZero));

        /// <summary>Index of the highest rank whose Min is at or below <paramref name="standing"/>; -1 below them all.</summary>
        public static int RankIndex(IReadOnlyList<GameplayFactionRank> ranks, int standing)
        {
            int index = -1;
            for (int i = 0; i < ranks.Count && ranks[i].Min <= standing; i++)
                index = i;
            return index;
        }

        /// <summary>
        /// Applies one kill of a <paramref name="rule"/> monster worth <paramref name="mobValue"/> to <paramref name="player"/>:
        /// the gain, then the loss proportional to it, each with its client line and any rank change.
        /// </summary>
        public static void Apply(Player player, GameplayFactions rules, GameplayFactionKillRule rule, int mobValue)
        {
            int gainBefore = player.Stats.GetOrZero(rule.GainStat, StatDetail.Base);
            int gain = Gain(rules, mobValue, gainBefore);
            if (gain <= 0)
                return;

            // Standings go to the player's own client only, not to everyone nearby (no dirty flag, so no broadcast);
            // saving follows the base change.
            int gainAfter = gainBefore + gain;
            player.Stats.Set(rule.GainStat, gainAfter, StatDetail.Base);
            player.SendOwnStat(rule.GainStat, gainAfter);

            // A rule without a lose standing (Guardian of Shadow) only goes up.
            int loseBefore = rule.HasLoss ? player.Stats.GetOrZero(rule.LoseStat, StatDetail.Base) : 0;
            // A standing already under the floor (set by hand) is not raised by a loss.
            int loseAfter = rule.HasLoss
                ? (int)Math.Min(loseBefore, Math.Max(rules.MinStanding, (long)loseBefore - Loss(rules, gain)))
                : loseBefore;
            if (loseAfter != loseBefore)
            {
                player.Stats.Set(rule.LoseStat, loseAfter, StatDetail.Base);
                player.SendOwnStat(rule.LoseStat, loseAfter);
            }

            ClientFeedback.SendFormattedInCategory(player, ClientFeedback.FactionCategoryId, ClientFeedback.FactionGained,
                FactionName(rule.Gain), gain);
            TellRankChange(player, rules.Ranks, rule.Gain, gainBefore, gainAfter);
            if (loseAfter == loseBefore)
                return;

            ClientFeedback.SendFormattedInCategory(player, ClientFeedback.FactionCategoryId, ClientFeedback.FactionLost,
                FactionName(rule.Lose), loseBefore - loseAfter);
            TellRankChange(player, rules.Ranks, rule.Lose, loseBefore, loseAfter);
        }

        static ClientFeedback.TextReference FactionName(int stat) => new(ClientFeedback.FactionNameCategoryId, stat);

        static void TellRankChange(Player player, IReadOnlyList<GameplayFactionRank> ranks, int stat, int before, int after)
        {
            int from = RankIndex(ranks, before);
            int to = RankIndex(ranks, after);
            if (to == from || to < 0)
                return;

            bool raised = to > from;
            ClientFeedback.SendFormattedInCategory(player, ClientFeedback.FactionCategoryId,
                raised ? ClientFeedback.FactionRankRaised : ClientFeedback.FactionRankLowered,
                FactionName(stat), raised ? ranks[to].Raised : ranks[to].Lowered);
        }
    }
}
