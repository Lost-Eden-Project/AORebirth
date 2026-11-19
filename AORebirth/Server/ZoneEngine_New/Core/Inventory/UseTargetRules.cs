namespace ZoneEngine_New.Core.Inventory
{
    using System;
    using System.Collections.Generic;

    using AORebirth.Enums;

    using LostEden.Protocol.GameData;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Helpers;
    using ZoneEngine_New.Core.Network;
    using ZoneEngine_New.Core.Playfield;

    /// <summary>
    /// Who an item or perk action is used on, from its Can flags:
    /// <list type="bullet">
    /// <item><see cref="CanFlags.ApplyOnFightingTarget"/>: only the character the user is fighting right now.</item>
    /// <item><see cref="CanFlags.ApplyOnHostile"/>: any combat-enabled character the user may attack (selected, else
    /// the fighting target), without having to be fighting it; using it does not start auto-attack.</item>
    /// <item><see cref="CanFlags.ApplyOnFriendly"/>: the selected character; no selection, self selection or a
    /// non-character selection uses the user. Hostile flags retain precedence.</item>
    /// </list>
    /// Templates with none of these flags keep their own targeting.
    /// </summary>
    internal static class UseTargetRules
    {
        /// <summary>
        /// Why a delayed use was dropped when it lost its fighting target (attack stopped, target changed); answered with
        /// <see cref="ClientFeedback.RequiresFightingTarget"/> instead of the generic failure line.
        /// </summary>
        public const string LostFightingTarget = "no longer the fighting target";

        public enum Failure
        {
            None,
            NoFightingTarget,
            NoHostileTarget,
            NotAttackable,
            NoFriendlyTarget,
            OutOfRange,
            NoLineOfSight
        }

        /// <summary>
        /// The template's Can flags. Stat 371 (SkillTimeOnSelectedTarget) = 1 marks an item used on the selected
        /// character even without ApplyOnFriendly (Health and Nano Recharger 291082/291083, First Aid Syringe 55695,
        /// pet Trimmers); unless a hostile flag already aims it, it is treated as ApplyOnFriendly.
        /// </summary>
        public static CanFlags CanOf(ItemTemplate template)
        {
            CanFlags can = template.Stats.TryGetValue(CharacterStat.Can, out int value) ? (CanFlags)(uint)value : 0;
            if (!IsHostileTargeted(can)
                && template.Stats.TryGetValue(CharacterStat.SkillTimeOnSelectedTarget, out int selected) && selected == 1)
                can |= CanFlags.ApplyOnFriendly;
            return can;
        }

        /// <summary>
        /// <see cref="CanOf"/> for an inventory item use. An item whose ToUse criteria test the target (an OnTarget /
        /// OnFightingTarget selector) is used on the selected character even without ApplyOnFriendly or stat 371
        /// (Trimmer - Increase Aggressiveness 154939/154940, Android NCU Upgrade 150306/150307); unless a hostile flag
        /// already aims it, it is treated as ApplyOnFriendly. Perk actions keep <see cref="CanOf"/>: Player resolves
        /// their ToUse-OnTarget targeting itself.
        /// </summary>
        public static CanFlags ItemUseCanOf(ItemTemplate template)
        {
            CanFlags can = CanOf(template);
            if (!IsHostileTargeted(can) && ToUseCriteriaReadTarget(template))
                can |= CanFlags.ApplyOnFriendly;
            return can;
        }

        public static bool NeedsFightingTarget(CanFlags can) => (can & CanFlags.ApplyOnFightingTarget) != 0;

        public static bool AppliesToHostile(CanFlags can) => !NeedsFightingTarget(can) && (can & CanFlags.ApplyOnHostile) != 0;

        /// <summary>True when <paramref name="can"/> aims the use at a hostile (either rule above).</summary>
        public static bool IsHostileTargeted(CanFlags can) => NeedsFightingTarget(can) || AppliesToHostile(can);

        public static bool AppliesToFriendly(CanFlags can) => !IsHostileTargeted(can) && (can & CanFlags.ApplyOnFriendly) != 0;

        /// <summary>
        /// Resolves the target of a character-targeted use. False with <paramref name="failure"/> when there is no valid target.
        /// </summary>
        public static bool TryResolve(Player user, CanFlags can, out Character target, out Failure failure)
        {
            target = user;
            failure = Failure.None;

            if (AppliesToFriendly(can))
            {
                Identity selected = user.Target;
                // A selected character receives it; no selection, self or a non-character selection uses the user.
                // Friendly kits, stims and rechargers rarely carry ApplyOnSelf.
                if (selected.Instance != 0 && selected != user.Identity && user.Playfield != null
                    && user.Playfield.GetRequiredService<DynelRegistry>().TryGet(selected, out Dynel? dynel)
                    && dynel is Character chosenFriendly)
                    target = chosenFriendly;

                failure = Revalidate(user, can, target) ?? Failure.None;
                return failure == Failure.None;
            }

            Character? chosen;
            if (NeedsFightingTarget(can))
            {
                chosen = user.TryResolveFightingTarget();
                if (chosen == null)
                {
                    failure = Failure.NoFightingTarget;
                    return false;
                }
            }
            else
            {
                chosen = SelectedCharacter(user) ?? user.TryResolveFightingTarget();
                if (chosen == null)
                {
                    failure = Failure.NoHostileTarget;
                    return false;
                }
            }

            if (!CombatRules.CanAttack(user, chosen))
            {
                failure = Failure.NotAttackable;
                return false;
            }

            target = chosen;
            return true;
        }

        /// <summary>
        /// Rechecks the captured recipient when a delayed use finishes. Friendly recipients must remain present, alive
        /// and connected. Hostile recipients must remain attackable and, when required, still the one being fought.
        /// </summary>
        public static Failure? Revalidate(Player user, CanFlags can, Character target)
        {
            if (AppliesToFriendly(can))
            {
                if (target.IsDead || user.Playfield == null || !ReferenceEquals(target.Playfield, user.Playfield)
                    || (target is Player recipient && recipient.Session?.State != SessionState.InPlay))
                    return Failure.NoFriendlyTarget;
                if (!ReferenceEquals(target, user)
                    && (!user.Playfield.GetRequiredService<DynelRegistry>().TryGet(target.Identity, out Dynel? current)
                        || !ReferenceEquals(current, target)))
                    return Failure.NoFriendlyTarget;
                return null;
            }

            if (ReferenceEquals(target, user) || !IsHostileTargeted(can))
                return null;
            if (target.IsDead || !ReferenceEquals(target.Playfield, user.Playfield))
                return Failure.NoHostileTarget;
            if (NeedsFightingTarget(can) && !ReferenceEquals(user.TryResolveFightingTarget(), target))
                return Failure.NoFightingTarget;
            if (!CombatRules.CanAttack(user, target))
                return Failure.NotAttackable;
            return null;
        }

        /// <summary>Use the template's AttackRange and the same start-only reach checks as item-backed perk actions.</summary>
        public static Failure? CheckFriendlyReach(Player user, ItemTemplate template, Character target)
        {
            if (ReferenceEquals(target, user))
                return null;
            int range = template.Stats.TryGetValue(CharacterStat.AttackRange, out int value) ? StatCollection.Normalize(value) : 0;
            if (user.GetEdgeDistanceTo(target) > (range > 0 ? range : 1))
                return Failure.OutOfRange;
            if (!user.HasLineOfSightTo(target))
                return Failure.NoLineOfSight;
            return null;
        }

        public static void SendFailure(Player user, Failure failure, Character? target)
        {
            switch (failure)
            {
                case Failure.NoFightingTarget:
                    ClientFeedback.Send(user, ClientFeedback.RequiresFightingTarget);
                    break;
                case Failure.NoHostileTarget:
                    RequirementFeedback.SendText(user, "You need a hostile target to use this.");
                    break;
                case Failure.NoFriendlyTarget:
                    RequirementFeedback.SendText(user, "You need a valid friendly target to use this.");
                    break;
                case Failure.OutOfRange:
                    RequirementFeedback.SendText(user, "Your target is out of range.");
                    break;
                case Failure.NoLineOfSight:
                    RequirementFeedback.SendText(user, "Your target is not in line of sight.");
                    break;
                case Failure.NotAttackable:
                    ClientFeedback.Send(user, target != null && CombatRules.IsPvpAttackBlocked(user, target)
                        ? "Feedback_PvpNotAllowedInThisDistrict"
                        : "Feedback_StartingAttackFailed");
                    break;
            }
        }

        /// <summary>
        /// The world object an item's OnUse criteria test: when <paramref name="template"/> is not character-targeted
        /// (no hostile or friendly Can flag), an OnUse function's criteria have an OnTarget / OnFightingTarget selector,
        /// and the user has a world dynel that is not a character selected (Novictalized Notum Crystal 231431 used with
        /// an Incarnator selected). Null with <see cref="Failure.None"/> when the use keeps its usual target. False with
        /// <see cref="Failure.OutOfRange"/> when that object is beyond the reach of using it directly.
        /// </summary>
        public static bool TryResolveSelectedDynel(Player user, ItemTemplate template, CanFlags can, out Dynel? selected,
            out Failure failure)
        {
            ArgumentNullException.ThrowIfNull(user);
            ArgumentNullException.ThrowIfNull(template);

            selected = null;
            failure = Failure.None;
            Identity target = user.Target;
            if (IsHostileTargeted(can) || AppliesToFriendly(can) || target.Instance == 0 || target == user.Identity
                || user.Playfield == null || !OnUseCriteriaReadTarget(template)
                || !user.Playfield.GetRequiredService<DynelRegistry>().TryGet(target, out Dynel? dynel)
                || dynel is null or Character)
                return true;

            // Same reach as a Use on the object itself (StaticDynel.CanBeginUse).
            if (!ReferenceEquals(dynel.Playfield, user.Playfield) || dynel.GetEdgeDistanceTo(user) > LootableDynel.OpenRange)
            {
                failure = Failure.OutOfRange;
                return false;
            }

            selected = dynel;
            return true;
        }

        /// <summary>
        /// A selected world object's value for a criteria leaf: stat 702 (Item Template ID) is its item template's low id,
        /// stat 54 (Level) its QL, any other stat its own value (Unset when it has none, as a character's).
        /// </summary>
        public static int SelectedDynelStat(Dynel dynel, CharacterStat stat)
        {
            ArgumentNullException.ThrowIfNull(dynel);

            return stat switch
            {
                CharacterStat.ACGItemTemplateID when dynel is StaticDynel item => item.ItemTemplateLowId,
                CharacterStat.Level when dynel is StaticDynel item => item.ItemQuality,
                _ => dynel.Stats.Get(stat)
            };
        }

        /// <summary>True when an OnUse function's criteria aim a check at the target (an OnTarget / OnFightingTarget selector).</summary>
        static bool OnUseCriteriaReadTarget(ItemTemplate template)
            => template.SpellList.TryGetValue(EventType.OnUse, out List<ItemSpell>? spells)
                && spells.Exists(spell => spell.Requirements.Exists(requirement
                    => (Operator)requirement.Operator is Operator.OnTarget or Operator.OnFightingTarget));

        /// <summary>True when the ToUse action's criteria aim a check at the target (an OnTarget / OnFightingTarget selector).</summary>
        static bool ToUseCriteriaReadTarget(ItemTemplate template)
            => template.Actions.Exists(action => action.ActionType == (int)ActionType.ToUse
                && action.Requirements.Exists(requirement
                    => (Operator)requirement.Operator is Operator.OnTarget or Operator.OnFightingTarget));

        /// <summary>The user's selected living character other than themselves, or null.</summary>
        static Character? SelectedCharacter(Player user)
        {
            Identity selected = user.Target;
            if (selected.Instance == 0 || selected == user.Identity || user.Playfield == null)
                return null;

            DynelRegistry registry = user.Playfield.GetRequiredService<DynelRegistry>();
            if (!registry.TryGet(selected, out Dynel? dynel)
                && !registry.TryGet(new Identity { Type = IdentityType.SimpleChar, Instance = selected.Instance }, out dynel))
                return null;

            return dynel is Character character && !character.IsDead ? character : null;
        }
    }
}
