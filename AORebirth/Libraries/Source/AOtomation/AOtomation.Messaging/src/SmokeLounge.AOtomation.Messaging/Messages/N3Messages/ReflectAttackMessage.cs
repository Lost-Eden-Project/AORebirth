namespace SmokeLounge.AOtomation.Messaging.Messages.N3Messages
{
    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Serialization.MappingAttributes;

    /// <summary>
    /// Reflected damage: Identity is the attacker that takes it, Unknown1 the amount, Target the character that reflected
    /// it. Same layout as <see cref="ShieldAttackMessage"/> (live 2026-10-14T20:31:51Z: 49 back from a 668 melee hit).
    /// </summary>
    [AoContract((int)N3MessageType.ReflectAttack)]
    public class ReflectAttackMessage : N3Message
    {
        public ReflectAttackMessage()
        {
            this.N3MessageType = N3MessageType.ReflectAttack;
        }

        [AoMember(1)]
        public int Unknown1 { get; set; }

        [AoMember(2)]
        public Identity Target { get; set; }

        [AoMember(3)]
        public int Unknown2 { get; set; }
    }
}
