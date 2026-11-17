namespace ZoneEngine_New.Core.Entities
{
    using System;

    using LostEden.Protocol.GameData;
    using LostEden.Protocol.Messages.N3Messages;

    /// <summary>
    /// Finishes a SimpleCharFullUpdate for the codec. The codec writes each optional block, and each field's size, by
    /// the message's Flags, so the flags are derived here from what the message holds, the way the engine's own
    /// serializer did: a block is announced when it is there, a field is sent small when its value fits.
    /// </summary>
    internal static class ScfuWire
    {
        /// <summary>The flags that say which blocks follow and how wide the fields are: what the content decides.</summary>
        const SimpleCharFullUpdateFlags Structural =
            SimpleCharFullUpdateFlags.HasPlayfieldId | SimpleCharFullUpdateFlags.HasFightingTarget | SimpleCharFullUpdateFlags.HasHeading
            | SimpleCharFullUpdateFlags.IsNpc | SimpleCharFullUpdateFlags.HasSmallNpcFamily | SimpleCharFullUpdateFlags.HasSmallNpcLosHeight
            | SimpleCharFullUpdateFlags.UnknownDataFlag | SimpleCharFullUpdateFlags.HasOrgName | SimpleCharFullUpdateFlags.HasExtendedLevel
            | SimpleCharFullUpdateFlags.HasSmallHealth | SimpleCharFullUpdateFlags.HasSmallHealthDamage | SimpleCharFullUpdateFlags.HasHeadMesh
            | SimpleCharFullUpdateFlags.HasExtendedRunSpeed | SimpleCharFullUpdateFlags.IsUnderAttack | SimpleCharFullUpdateFlags.HasExtendedTextures
            | SimpleCharFullUpdateFlags.HasWaypoints | SimpleCharFullUpdateFlags.HasNoWeaponPairs;

        /// <summary>
        /// Sets the flags from the content, with <paramref name="additional"/> added and <paramref name="suppressed"/>
        /// taken away, and the health damage field: a byte of damage when it fits (HasSmallHealthDamage), else the field
        /// carries the current health itself, as the client reads it.
        /// </summary>
        public static void Finalize(SimpleCharFullUpdateMessage scfu, int healthDamage,
            SimpleCharFullUpdateFlags additional, SimpleCharFullUpdateFlags suppressed)
        {
            SimpleCharFullUpdateFlags flags = (Derive(scfu, additional) | additional) & ~suppressed;
            scfu.Flags = Sized(scfu, flags, healthDamage);
        }

        /// <summary>
        /// After the content changed: derives the block flags again, keeps the behaviour flags the message has, and
        /// takes the behaviour flags of <paramref name="captured"/> when a captured flag word is given.
        /// </summary>
        public static void Refresh(SimpleCharFullUpdateMessage scfu, SimpleCharFullUpdateFlags? captured = null)
        {
            bool smallDamage = (scfu.Flags & SimpleCharFullUpdateFlags.HasSmallHealthDamage) != 0;
            int healthDamage = smallDamage ? scfu.HealthDamage : scfu.Health - scfu.HealthDamage;
            SimpleCharFullUpdateFlags behaviour = (captured ?? scfu.Flags) & ~Structural;
            SimpleCharFullUpdateFlags flags = Derive(scfu, scfu.Flags) | behaviour;
            scfu.Flags = Sized(scfu, flags, healthDamage);
        }

        static SimpleCharFullUpdateFlags Derive(SimpleCharFullUpdateMessage scfu, SimpleCharFullUpdateFlags hints)
        {
            var flags = SimpleCharFullUpdateFlags.HasHeading;
            if (scfu.PlayfieldId.HasValue)
                flags |= SimpleCharFullUpdateFlags.HasPlayfieldId;
            if (scfu.CharacterInfo is SimpleCharInfo.NPCInfo npc)
            {
                // Live NPC updates carry these two; the NPC data block goes with its own flag.
                flags |= SimpleCharFullUpdateFlags.IsNpc | SimpleCharFullUpdateFlags.UnknownFlag | SimpleCharFullUpdateFlags.UnknownFlag2;
                if (npc.Family is >= 0 and <= byte.MaxValue)
                    flags |= SimpleCharFullUpdateFlags.HasSmallNpcFamily;
                if (npc.LosHeight is >= 0 and <= byte.MaxValue)
                    flags |= SimpleCharFullUpdateFlags.HasSmallNpcLosHeight;
                if (npc.Unknown1 != 0 || npc.Unknown2 != 0 || (hints & SimpleCharFullUpdateFlags.UnknownDataFlag) != 0)
                    flags |= SimpleCharFullUpdateFlags.UnknownDataFlag;
            }
            else if (scfu.CharacterInfo is SimpleCharInfo.PlayerInfo player
                && (!string.IsNullOrWhiteSpace(player.OrgName) || player.OrgId != 0))
            {
                flags |= SimpleCharFullUpdateFlags.HasOrgName;
            }

            if (scfu.FightingTarget.HasValue)
                flags |= SimpleCharFullUpdateFlags.IsUnderAttack;
            if (scfu.TextureOverrides is { Length: > 0 })
                flags |= SimpleCharFullUpdateFlags.HasExtendedTextures;
            if (scfu.Waypoints is { Count: > 0 })
            {
                flags |= SimpleCharFullUpdateFlags.HasWaypoints;
                scfu.WaypointTarget = scfu.Identity;
            }

            if (scfu.WeaponPairs is { Length: > 0 })
                flags |= SimpleCharFullUpdateFlags.HasNoWeaponPairs;
            return flags;
        }

        /// <summary>The field-size flags from the values, and the health damage field for them.</summary>
        static SimpleCharFullUpdateFlags Sized(SimpleCharFullUpdateMessage scfu, SimpleCharFullUpdateFlags flags, int healthDamage)
        {
            flags = Set(flags, SimpleCharFullUpdateFlags.HasExtendedLevel, scfu.Level > byte.MaxValue);
            flags = Set(flags, SimpleCharFullUpdateFlags.HasSmallHealth, scfu.Health is >= 0 and <= ushort.MaxValue);
            flags = Set(flags, SimpleCharFullUpdateFlags.HasExtendedRunSpeed, scfu.RunSpeedBase > byte.MaxValue);
            flags = Set(flags, SimpleCharFullUpdateFlags.HasHeadMesh, scfu.HeadMesh.HasValue);
            if (healthDamage is >= 0 and <= byte.MaxValue)
            {
                flags |= SimpleCharFullUpdateFlags.HasSmallHealthDamage;
                scfu.HealthDamage = healthDamage;
            }
            else
            {
                // Without the small-damage flag the field is the current health (the client subtracts nothing).
                flags &= ~SimpleCharFullUpdateFlags.HasSmallHealthDamage;
                scfu.HealthDamage = Math.Clamp(scfu.Health - healthDamage, 0, Math.Max(0, scfu.Health));
            }

            return flags;
        }

        static SimpleCharFullUpdateFlags Set(SimpleCharFullUpdateFlags flags, SimpleCharFullUpdateFlags flag, bool on)
            => on ? flags | flag : flags & ~flag;
    }
}
