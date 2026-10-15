namespace ZoneEngine_New.Core.Helpers
{
    using System;
    using System.Collections.Generic;

    using SmokeLounge.AOtomation.Messaging.GameData;

    public enum AwardCredit
    {
        Shared,
        Full
    }

    /// <summary>
    /// Fight-scoped player HP-removed ledger. Today one player is one bucket;
    /// a later team id can group records without changing death or loot stamping.
    /// </summary>
    public sealed class KillRewardResolver
    {
        public const int MinimumContributionPercent = 20;

        readonly Dictionary<int, int> _damage = new();
        readonly Dictionary<int, Identity> _identities = new();
        readonly Dictionary<int, int> _firstCreditOrder = new();
        int _nextOrder;

        public void Record(Identity identity, int hpRemoved)
        {
            int instance = identity.Instance;
            if (instance == 0 || hpRemoved <= 0)
                return;

            if (!_damage.TryGetValue(instance, out int current))
            {
                _identities[instance] = identity;
                _firstCreditOrder[instance] = _nextOrder++;
                _damage[instance] = hpRemoved;
                return;
            }

            _damage[instance] = current + hpRemoved;
        }

        public void Clear()
        {
            _damage.Clear();
            _identities.Clear();
            _firstCreditOrder.Clear();
            _nextOrder = 0;
        }

        public bool TryGetLootWinner(out Identity identity)
        {
            identity = Identity.None;
            int bestDamage = 0;
            int bestOrder = int.MaxValue;
            int winner = 0;

            foreach (KeyValuePair<int, int> pair in _damage)
            {
                int order = _firstCreditOrder[pair.Key];
                if (pair.Value < bestDamage)
                    continue;
                if (pair.Value == bestDamage && order >= bestOrder)
                    continue;

                bestDamage = pair.Value;
                bestOrder = order;
                winner = pair.Key;
            }

            if (winner == 0)
                return false;

            identity = _identities[winner];
            return true;
        }

        public List<int> GetQualifyingPlayerInstances()
        {
            var qualifiers = new List<int>();
            if (_damage.Count == 0)
                return qualifiers;

            long total = 0;
            foreach (int damage in _damage.Values)
                total += damage;

            if (total <= 0)
                return qualifiers;

            foreach (KeyValuePair<int, int> pair in _damage)
            {
                if (pair.Value * 100L >= total * MinimumContributionPercent)
                    qualifiers.Add(pair.Key);
            }

            if (qualifiers.Count > 0)
                return qualifiers;

            int max = 0;
            foreach (int damage in _damage.Values)
            {
                if (damage > max)
                    max = damage;
            }

            foreach (KeyValuePair<int, int> pair in _damage)
            {
                if (pair.Value == max)
                    qualifiers.Add(pair.Key);
            }

            return qualifiers;
        }

        public bool TryGetIdentity(int playerInstance, out Identity identity)
            => _identities.TryGetValue(playerInstance, out identity);

        /// <summary>Every player with recorded damage.</summary>
        public List<int> GetDamagerInstances() => new(_damage.Keys);

        /// <summary>
        /// Splits <paramref name="pool"/> between award groups: a team pools its members' damage, a player outside a team
        /// is a group of one (<paramref name="groupOf"/> gives the key). Only <paramref name="present"/> damagers count
        /// toward their group, but the <see cref="MinimumContributionPercent"/> test is against all damage dealt. With no
        /// group over it, the highest group takes the pool. Each group is named by its highest damager; the caller
        /// splits a group's amount between its members, so a teammate's own damage does not change their cut.
        /// </summary>
        public List<GroupShare> DivideByGroup(int pool, IReadOnlyList<int> present, Func<int, long> groupOf)
        {
            ArgumentNullException.ThrowIfNull(present);
            ArgumentNullException.ThrowIfNull(groupOf);

            var shares = new List<GroupShare>();
            if (pool <= 0 || present.Count == 0)
                return shares;

            long total = 0;
            foreach (int damage in _damage.Values)
                total += damage;
            if (total <= 0)
                return shares;

            // Group key -> pooled damage and its highest damager (ties to the earliest).
            var groups = new Dictionary<long, (long Damage, int Lead, int LeadDamage, int LeadOrder)>();
            for (int i = 0; i < present.Count; i++)
            {
                int instance = present[i];
                if (!_damage.TryGetValue(instance, out int damage) || damage <= 0)
                    continue;

                int order = _firstCreditOrder.TryGetValue(instance, out int recorded) ? recorded : int.MaxValue;
                long key = groupOf(instance);
                if (!groups.TryGetValue(key, out var group))
                {
                    groups[key] = (damage, instance, damage, order);
                    continue;
                }

                bool leads = damage > group.LeadDamage || (damage == group.LeadDamage && order < group.LeadOrder);
                groups[key] = leads
                    ? (group.Damage + damage, instance, damage, order)
                    : (group.Damage + damage, group.Lead, group.LeadDamage, group.LeadOrder);
            }

            if (groups.Count == 0)
                return shares;

            long highestKey = 0;
            long highestDamage = -1;
            foreach (KeyValuePair<long, (long Damage, int Lead, int LeadDamage, int LeadOrder)> pair in groups)
            {
                if (pair.Value.Damage > highestDamage)
                {
                    highestDamage = pair.Value.Damage;
                    highestKey = pair.Key;
                }
            }

            var qualifying = new List<long>();
            long qualifyingDamage = 0;
            foreach (KeyValuePair<long, (long Damage, int Lead, int LeadDamage, int LeadOrder)> pair in groups)
            {
                if (pair.Value.Damage * 100 < total * MinimumContributionPercent)
                    continue;
                qualifying.Add(pair.Key);
                qualifyingDamage += pair.Value.Damage;
            }

            if (qualifying.Count == 0)
            {
                qualifying.Add(highestKey);
                qualifyingDamage = highestDamage;
            }

            int assigned = 0;
            int highestIndex = -1;
            for (int i = 0; i < qualifying.Count; i++)
            {
                var group = groups[qualifying[i]];
                int amount = (int)(pool * group.Damage / qualifyingDamage);
                if (qualifying[i] == highestKey)
                    highestIndex = shares.Count;
                shares.Add(new GroupShare(group.Lead, amount));
                assigned += amount;
            }

            int leftover = pool - assigned;
            if (leftover > 0)
            {
                int index = highestIndex >= 0 ? highestIndex : 0;
                shares[index] = new GroupShare(shares[index].LeadInstance, shares[index].Amount + leftover);
            }

            return shares;
        }

        public List<AwardShare> Divide(int pool, IReadOnlyList<int> recipients, AwardCredit credit)
        {
            if (credit == AwardCredit.Full)
                return FullAward(pool, recipients);

            return SplitAward(pool, recipients);
        }

        public List<AwardShare> SplitAward(int pool, IReadOnlyList<int> recipients)
        {
            var shares = new List<AwardShare>();
            if (pool <= 0 || recipients.Count == 0)
                return shares;

            long qualifyingDamage = 0;
            int highestInstance = 0;
            int highestDamage = -1;
            int highestOrder = int.MaxValue;

            for (int i = 0; i < recipients.Count; i++)
            {
                int instance = recipients[i];
                if (!_damage.TryGetValue(instance, out int damage) || damage <= 0)
                    continue;

                qualifyingDamage += damage;
                int order = _firstCreditOrder.TryGetValue(instance, out int recorded) ? recorded : int.MaxValue;
                if (damage < highestDamage)
                    continue;
                if (damage == highestDamage && order >= highestOrder)
                    continue;

                highestDamage = damage;
                highestOrder = order;
                highestInstance = instance;
            }

            if (qualifyingDamage <= 0)
                return shares;

            int assigned = 0;
            for (int i = 0; i < recipients.Count; i++)
            {
                int instance = recipients[i];
                if (!_damage.TryGetValue(instance, out int damage) || damage <= 0)
                    continue;

                int amount = (int)(pool * (long)damage / qualifyingDamage);
                shares.Add(new AwardShare(instance, amount));
                assigned += amount;
            }

            int leftover = pool - assigned;
            if (leftover <= 0 || highestInstance == 0)
                return shares;

            for (int i = 0; i < shares.Count; i++)
            {
                if (shares[i].PlayerInstance != highestInstance)
                    continue;

                shares[i] = new AwardShare(highestInstance, shares[i].Amount + leftover);
                break;
            }

            return shares;
        }

        List<AwardShare> FullAward(int pool, IReadOnlyList<int> recipients)
        {
            var shares = new List<AwardShare>();
            if (pool <= 0 || recipients.Count == 0)
                return shares;

            for (int i = 0; i < recipients.Count; i++)
            {
                int instance = recipients[i];
                if (!_damage.TryGetValue(instance, out int damage) || damage <= 0)
                    continue;

                shares.Add(new AwardShare(instance, pool));
            }

            return shares;
        }
    }

    /// <summary>An award group's amount, named by the group's highest damager.</summary>
    public readonly struct GroupShare
    {
        public GroupShare(int leadInstance, int amount)
        {
            LeadInstance = leadInstance;
            Amount = amount;
        }

        public int LeadInstance { get; }

        public int Amount { get; }
    }

    public readonly struct AwardShare
    {
        public AwardShare(int playerInstance, int amount)
        {
            PlayerInstance = playerInstance;
            Amount = amount;
        }

        public int PlayerInstance { get; }

        public int Amount { get; }
    }
}
