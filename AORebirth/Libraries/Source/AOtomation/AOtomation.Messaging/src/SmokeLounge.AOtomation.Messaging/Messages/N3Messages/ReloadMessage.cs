namespace SmokeLounge.AOtomation.Messaging.Messages.N3Messages
{
    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Serialization.MappingAttributes;

    /// <summary>
    /// Server→client weapon reload (Gamecode.dll ReloadIIR_t, read 10076a08, apply 10076aa7 → 1006959a). Retail capture
    /// 2026-10-23: N3 Unknown 1, ammo {Inventory, 92}, the weapon instance, 49650 left in the stack, clip 50, 0. The
    /// client sets the slot's stack count (or drops the slot when <see cref="RemoveAmmoStack"/> is set), sets the
    /// weapon's Energy (26) to <see cref="ClipCount"/>, shows "Reloading" and runs its reload timer from the weapon's
    /// RechargeDelay (210).
    /// </summary>
    [AoContract((int)N3MessageType.Reload)]
    public class ReloadMessage : N3Message
    {
        public ReloadMessage()
        {
            this.N3MessageType = N3MessageType.Reload;
        }

        /// <summary>The ammo stack's slot ({Inventory, placement}).</summary>
        [AoMember(0)]
        public Identity AmmoSlot { get; set; }

        /// <summary>The weapon reloaded (its instance identity).</summary>
        [AoMember(1)]
        public Identity Weapon { get; set; }

        /// <summary>Rounds left in the ammo stack after the reload.</summary>
        [AoMember(2)]
        public int AmmoStackCount { get; set; }

        /// <summary>The weapon's clip after the reload.</summary>
        [AoMember(3)]
        public int ClipCount { get; set; }

        /// <summary>1 when the reload used up the stack: the client drops the slot. A count of 0 alone does not.</summary>
        [AoMember(4)]
        public int RemoveAmmoStack { get; set; }
    }
}
