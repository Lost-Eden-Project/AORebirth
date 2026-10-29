namespace SmokeLounge.AOtomation.Messaging.Messages.N3Messages
{
    using SmokeLounge.AOtomation.Messaging.Serialization.MappingAttributes;

    /// <summary>
    /// Damage an absorb layer took instead of health: Identity is the character absorbing, <see cref="Amount"/> the
    /// points absorbed, <see cref="DamageType"/> the hit's damage type (the AC stat id, 90-97). Live sends it after the
    /// ReflectAttack and before the AttackInfo, which then carries only what got through (2026-10-29T14:17:17Z: 332
    /// melee absorbed, AttackInfo 172, from a 504 hit that emptied the layer).
    /// </summary>
    [AoContract((int)N3MessageType.Absorb)]
    public class AbsorbMessage : N3Message
    {
        public AbsorbMessage()
        {
            this.N3MessageType = N3MessageType.Absorb;
        }

        [AoMember(1)]
        public int Amount { get; set; }

        [AoMember(2)]
        public int DamageType { get; set; }
    }
}
