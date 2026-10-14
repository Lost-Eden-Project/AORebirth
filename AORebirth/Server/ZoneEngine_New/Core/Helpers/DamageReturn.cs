namespace ZoneEngine_New.Core.Helpers
{
    using System;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

    using ZoneEngine_New.Core.Entities;

    /// <summary>
    /// Damage a defender sends back at whoever hit it.
    /// <list type="bullet">
    /// <item><b>Reflect</b> (70300: Reflect Melee AC 75, Max Reflected Melee Damage 136): the defender's Reflect AC for the
    /// hit's damage type is the share of the damage it does not take, 0-100 (below 0 adds nothing, 100 and above takes
    /// none). What was turned away hits the attacker, at most the defender's Max Reflected Damage for that type.</item>
    /// <item><b>Damage shield</b> (55751: Shield ACs 60): every hit of a type costs the attacker the defender's Shield AC
    /// for that type, whatever the hit's size. The defender still takes the whole hit.</item>
    /// </list>
    /// Live (capture 2026-10-14T20:31:51Z, a melee hit of 668): ReflectAttack 49 then ShieldAttack 38, both naming the
    /// attacker with the defender as Target, go out before the hit's AttackInfo; the attacker's Health then drops by 87 in
    /// an ordinary Stat update. Returned damage is never reflected or shielded again.
    /// </summary>
    internal static class DamageReturn
    {
        /// <summary>What a hit sends back: the reflected and the shield damage the attacker takes.</summary>
        public readonly record struct Returned(int Reflected, int Shield)
        {
            public int Total => Reflected + Shield;
        }

        /// <summary>
        /// The damage <paramref name="target"/> takes from <paramref name="attacker"/>'s hit of <paramref name="damageType"/>
        /// after its reflect, and what goes back. <paramref name="shield"/> is false for nano damage, which only reflects.
        /// </summary>
        public static int Resolve(Character attacker, Character target, int damage, int damageType, bool shield, out Returned returned)
        {
            returned = default;
            if (damage <= 0 || ReferenceEquals(attacker, target))
                return damage;

            int reflectedBack = 0;
            if (DamageCalculator.TryGetReflectStats(damageType, out CharacterStat reflectStat, out CharacterStat maxReflectedStat))
            {
                int percent = Math.Clamp(Normalized(target, reflectStat), 0, 100);
                int turnedAway = (int)((long)damage * percent / 100);
                damage -= turnedAway;
                reflectedBack = Math.Min(turnedAway, Math.Max(0, Normalized(target, maxReflectedStat)));
            }

            int shieldBack = shield && DamageCalculator.TryGetShieldStat(damageType, out CharacterStat shieldStat)
                ? Math.Max(0, Normalized(target, shieldStat))
                : 0;

            returned = new Returned(reflectedBack, shieldBack);
            return damage;
        }

        /// <summary>ReflectAttack and ShieldAttack for <paramref name="returned"/>, to everyone who sees the attacker. Before the hit is shown.</summary>
        public static void Announce(Character attacker, Character target, Returned returned)
        {
            if (returned.Reflected > 0)
                attacker.Cell?.Announce(new ReflectAttackMessage
                {
                    Identity = attacker.Identity,
                    Unknown1 = returned.Reflected,
                    Target = target.Identity,
                    Unknown2 = 0
                });

            if (returned.Shield > 0)
                attacker.Cell?.Announce(new ShieldAttackMessage
                {
                    Identity = attacker.Identity,
                    Unknown1 = returned.Shield,
                    Target = target.Identity,
                    Unknown2 = 0
                });
        }

        /// <summary>Takes <paramref name="returned"/> off the attacker's health, credited to <paramref name="target"/>. After the hit lands.</summary>
        public static void Apply(Character attacker, Character target, Returned returned)
        {
            if (returned.Total > 0 && !attacker.IsDead)
                attacker.ApplyDamage(target, returned.Total, HitType.Normal);
        }

        static int Normalized(Character character, CharacterStat stat) => StatCollection.Normalize(character.Stats.GetOrZero(stat));
    }
}
