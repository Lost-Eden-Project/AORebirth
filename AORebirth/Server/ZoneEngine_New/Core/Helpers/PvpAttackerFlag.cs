namespace ZoneEngine_New.Core.Helpers
{
    using System;

    using ZoneEngine_New.Core.Configuration;
    using ZoneEngine_New.Core.Data;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;
    using ZoneEngine_New.Core.Nanos;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// A player who starts a fight with a player, or aims a hostile action at one (a hostile nano cast, perk action or item
    /// use), gets Gameplay.json pvp.attackerFlagNano (214879, the PvP flag: ChangeActionRestriction PvPEnabled for ten
    /// minutes). So does one who helps a PvP-enabled player: a friendly nano (team buffs and auras included), perk action
    /// or item use on them. It is never refreshed while it runs: only an attack or action after it has run out lands it
    /// again. A player's pet counts as that player.
    /// </summary>
    internal static class PvpAttackerFlag
    {
        public static void OnHostileAction(Player attacker, Character target)
        {
            ArgumentNullException.ThrowIfNull(attacker);
            ArgumentNullException.ThrowIfNull(target);

            if (OtherPlayerSide(attacker, target) is not null)
                Flag(attacker);
        }

        /// <summary><paramref name="helper"/> lands or starts a friendly effect on <paramref name="target"/>.</summary>
        public static void OnFriendlyAction(Player helper, Character target)
        {
            ArgumentNullException.ThrowIfNull(helper);
            ArgumentNullException.ThrowIfNull(target);

            if (OtherPlayerSide(helper, target) is Player helped && CombatRules.IsPvpEnabled(helped))
                Flag(helper);
        }

        /// <summary>The player <paramref name="target"/> belongs to (itself, or a pet's owner) when that is not <paramref name="actor"/>.</summary>
        static Player? OtherPlayerSide(Player actor, Character target)
        {
            Character side = target is NpcCharacter { PetOwner: Player owner } ? owner : target;
            return side is Player player && !ReferenceEquals(player, actor) ? player : null;
        }

        static void Flag(Player attacker)
        {
            if (attacker.IsDead)
                return;

            Playfield? playfield = attacker.Playfield;
            int nanoId = playfield?.GetService<GameplaySettings>()?.Pvp.AttackerFlagNano ?? 0;
            if (playfield == null || nanoId <= 0 || attacker.TryGetBuff(nanoId, out _))
                return;

            // The flag is a hostile-flagged buff on its own holder, which only a GM landing allows.
            NanoRuntime.TryApplyImmediate(
                attacker,
                attacker,
                nanoId,
                playfield.GetRequiredService<IItemBuilder>(),
                playfield.GetRequiredService<IInventoryRepository>(),
                DateTime.UtcNow,
                gmApply: true);
        }
    }
}
