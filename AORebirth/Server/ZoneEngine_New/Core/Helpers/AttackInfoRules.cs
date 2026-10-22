namespace ZoneEngine_New.Core.Helpers
{
    using AORebirth.Enums;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;

    /// <summary>
    /// AttackInfo field selection for auto-attack hits.
    /// Player synthetic fists must use the unarmed shape (ammo -1, slot 0, instance 0) — never RH/LH
    /// without WIFU. NPC tag-backed weapons use SAW tags in Unknown6.
    /// </summary>
    internal static class AttackInfoRules
    {
        /// <summary>Player equipped melee / player unarmed ammo (0xFFFFFFFF).</summary>
        public const int PlayerMeleeAmmoCount = -1;

        /// <summary>NPC ranged swing ammo (AOEmu PlayfieldEngine).</summary>
        public const int NpcRangedAmmoCount = 1;

        /// <summary>Legacy/private-server equipped ranged placeholder ammo.</summary>
        public const int RangedAmmoCount = 40;

        /// <summary>AOEmu treats AmmoType 10 like melee for AttackInfo ammo.</summary>
        const int MeleeStyleAmmoType = 10;

        public static bool IsUnarmedPresentation(CharacterWeapon? armed, Item? weapon)
        {
            if (armed != null && armed.IsSyntheticFist)
                return true;

            if (weapon == null)
                return true;

            // PhysicalInit / Unarmed style.
            if ((weapon.GetWeaponFlags() & WeaponFlags.Unarmed) != 0)
                return true;

            // Synthetic MA fist from ArmMartialArtsFist uses Create() → InstanceId 0.
            return weapon.InstanceId <= 0;
        }

        public static bool IsUnarmedPresentation(Item? weapon)
            => IsUnarmedPresentation(armed: null, weapon);

        public static bool UsesMeleeAmmo(Item? weapon)
        {
            if (weapon == null)
                return true;

            WeaponFlags flags = weapon.GetWeaponFlags();
            if ((flags & (WeaponFlags.Melee | WeaponFlags.Unarmed)) != 0)
                return true;

            return weapon.GetStat(CharacterStat.AmmoType) == MeleeStyleAmmoType;
        }

        /// <summary>NPC swing shown as its ranged hand weapon (retail ICC Shuttle Guard, capture 2026-10-22T19:18Z).</summary>
        public const int NpcHandRangedAmmoCount = 0;

        public static int ResolveAmmoCount(CharacterWeapon? armed, Item? weapon, bool attackerIsPlayer)
        {
            if (!attackerIsPlayer)
            {
                // Bound to the visible hand weapon: melee -1, ranged 0 (retail slot-6 hits carry ammo 0).
                if (armed?.VisualHand is Item hand)
                    return UsesMeleeAmmo(hand) ? PlayerMeleeAmmoCount : NpcHandRangedAmmoCount;
                // A tagged monster weapon swings as itself: retail sends -1 for every one (guards' martial arts
                // and the aliens' natural weapons alike, capture 2026-10-22T19:18Z).
                if (armed != null && armed.SawTag != 0)
                    return PlayerMeleeAmmoCount;
                return UsesMeleeAmmo(armed?.DamageItem ?? weapon) ? PlayerMeleeAmmoCount : NpcRangedAmmoCount;
            }

            // Player unarmed and player melee both use -1 (private-server / AOEmu melee rows).
            if (IsUnarmedPresentation(armed, weapon) || UsesMeleeAmmo(weapon))
                return PlayerMeleeAmmoCount;

            return RangedAmmoCount;
        }

        public static int ResolveAmmoCount(Item? weapon)
            => ResolveAmmoCount(armed: null, weapon, attackerIsPlayer: true);

        public static int ResolveWeaponSlot(
            CharacterWeapon? armed,
            WeaponSlot logicalSlot,
            Item? weapon,
            bool attackerIsPlayer = true)
        {
            // NPC: right hand for the swing bound to the visible hand weapon, else the monster weapon's own wire slot
            // (retail: a guard's martial arts weapons hit from slots 1..4, a multi-weapon alien from 2 and 3, a
            // single-weapon alien from 0; capture 2026-10-22T19:18Z).
            if (!attackerIsPlayer)
            {
                if (armed?.VisualHand != null)
                    return (int)WeaponSlots.Righthand;
                return armed != null && armed.WireSlot > 0 ? armed.WireSlot : 0;
            }

            // Only the server-made fists swing from slot 0. A real martial arts weapon (PhysicalInit, e.g. X1-R4 Viral
            // Force-Blades) swings from its hand like any weapon: live combined-MA weapons hit from slots 6/8 (capture
            // 2026-10-05T13:34:38Z..13:34:45Z).
            if (IsServerFist(armed, weapon))
                return 0;

            return logicalSlot switch
            {
                WeaponSlot.OffHand => (int)WeaponSlots.LeftHand,
                _ => (int)WeaponSlots.Righthand
            };
        }

        public static int ResolveWeaponSlot(WeaponSlot slot, Item? weapon)
            => ResolveWeaponSlot(armed: null, slot, weapon, attackerIsPlayer: true);

        /// <summary>AttackInfo Unknown6 for the combined-MA fist hit (live, two combined-MA weapons).</summary>
        public const int CombinedMartialArtsInstance = 100;

        public static int ResolveWeaponInstance(CharacterWeapon? armed, Item? weapon, bool attackerIsPlayer)
        {
            // NPC monster weapons: AttackInfo Unknown6 must equal SAW SpecialAttack.Unknown3,
            // unless a visual weapon overrides the swing.
            if (armed != null && armed.SawTag != 0 && armed.VisualHand == null)
                return armed.SawTag;

            // The combined-MA fist that swings beside combined-MA weapons: slot 0, Unknown6 100. The weapons' own hits
            // keep slot 6/8 and 0 (capture 2026-10-05T13:34:38Z..13:34:45Z).
            if (attackerIsPlayer && armed != null && armed.IsSyntheticFist && armed.LogicalSlot == WeaponSlot.CombinedMA)
                return CombinedMartialArtsInstance;

            // Private-server player unarmed rows use instance 0 (not AOEmu's 100).
            _ = weapon;
            return 0;
        }

        public static int ResolveWeaponInstance(Item? weapon, bool attackerIsPlayer)
            => ResolveWeaponInstance(armed: null, weapon, attackerIsPlayer);

        /// <summary>
        /// True when observers should receive WeaponItemFullUpdate for this hand item.
        /// Only a visible weapon (non-zero weapon mesh) gets an instance.
        /// Fists/unarmed and NPC tag-driven natural weapons stay AttackInfo/SAW-only.
        /// </summary>
        public static bool ShouldAnnounceWeaponItemFullUpdate(Item? item, CharacterWeapon? armed = null)
        {
            if (item == null || item.InstanceId <= 0 || !item.IsWieldableCombatWeapon())
                return false;
            if (!HasVisibleWeaponMesh(item))
                return false;

            // Tag-backed NPC weapons use SAW + AttackInfo instance=tag, not WIFU.
            if (armed != null && armed.SawTag != 0)
                return false;

            // Server-made fists stay AttackInfo-only. Every real weapon in a hand gets its WIFU, a martial arts one
            // (PhysicalInit) included: the client builds a hand's attack timer from it, so without one it shows a single
            // martial arts bar and treats the character as unarmed. Live sends a WIFU for every carried weapon
            // (capture 2026-10-06T21:00:38Z).
            return !IsServerFist(armed, item);
        }

        /// <summary>A fist the server arms itself (no weapon, or the combined-MA fist): no inventory item behind it.</summary>
        static bool IsServerFist(CharacterWeapon? armed, Item? weapon)
            => (armed != null && armed.IsSyntheticFist) || weapon == null || weapon.InstanceId <= 0;

        /// <summary>True when the item carries a non-zero weapon mesh on either hand or the shared stat.</summary>
        public static bool HasVisibleWeaponMesh(Item? item)
        {
            if (item == null)
                return false;

            return VisualMesh(item, CharacterStat.WeaponMesh) > 0
                || VisualMesh(item, CharacterStat.WeaponMeshRight) > 0
                || VisualMesh(item, CharacterStat.WeaponMeshLeft) > 0;
        }

        /// <summary>
        /// True when this equipment slot would draw a weapon mesh.
        /// Shared <see cref="CharacterStat.WeaponMesh"/> fills either hand. Hand stats fill only their hand.
        /// </summary>
        public static bool HasVisibleWeaponMesh(Item? item, int equipmentSlot)
        {
            if (item == null)
                return false;
            if (VisualMesh(item, CharacterStat.WeaponMesh) > 0)
                return true;
            if (equipmentSlot == (int)WeaponSlots.Righthand)
                return VisualMesh(item, CharacterStat.WeaponMeshRight) > 0;
            if (equipmentSlot == (int)WeaponSlots.LeftHand)
                return VisualMesh(item, CharacterStat.WeaponMeshLeft) > 0;
            return false;
        }

        static int VisualMesh(Item item, CharacterStat stat)
        {
            int value = StatCollection.Normalize(item.GetStat(stat));
            return value <= 0 ? 0 : value;
        }
    }
}
