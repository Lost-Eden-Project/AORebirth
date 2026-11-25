namespace ZoneEngine_New.Core.Nanos
{
    using System;
    using System.Collections.Generic;

    using AORebirth.Enums;

    using LostEden.Protocol.GameData;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;

    /// <summary>
    /// A nano program read out of an <see cref="ItemTemplate"/>.
    /// On nano templates some <see cref="CharacterStat"/> ids mean something other than their character name:
    /// Level is the NCU cost, MetaType the nano strain, MonsterLevelsKilled the local cooldown (see the enum).
    /// </summary>
    public class NanoSpell : ItemTemplate
    {
        protected NanoSpell(ItemTemplate template)
            : base(template)
        {
            NanoStrain = ReadStat(CharacterStat.MetaType);
            _stackingLines = ReadStackingLines();
            StackingOrder = ReadStat(CharacterStat.StackingOrder);
            NcuCost = Math.Max(0, ReadStat(CharacterStat.Level));
            NanoPointCost = Math.Max(0, ReadStat(CharacterStat.NanoPoints));
            int timeExist = ReadStat(CharacterStat.TimeExist);
            DurationCentiseconds = Math.Max(0, timeExist);
            RunsUntilRemoved = timeExist < 0;
            AttackDelayCentiseconds = Math.Max(0, ReadStat(CharacterStat.AttackDelay));
            AttackDelayCapCentiseconds = Math.Max(0, ReadStat(CharacterStat.AttackDelayCap));
            RechargeDelayCentiseconds = Math.Max(0, ReadStat(CharacterStat.RechargeDelay));
            RechargeDelayCapCentiseconds = Math.Max(0, ReadStat(CharacterStat.RechargeDelayCap));
            NanoLineLockCentiseconds = Math.Max(0, Math.Max(ReadStat(CharacterStat.MonsterLevelsKilled), ReadStat(CharacterStat.NanoLineCooldown)));

            CanFlags can = (CanFlags)(uint)ReadStat(CharacterStat.Can);
            NanoFlags nanoFlags = (NanoFlags)Flags;
            IsHostile = (nanoFlags & NanoFlags.IsHostile) != 0
                || ((can & CanFlags.ApplyOnHostile) != 0
                    && (can & (CanFlags.ApplyOnFriendly | CanFlags.ApplyOnSelf)) == 0);

            // Without the IsBuff flag a duration only makes an NCU entry when the nano does something that needs one. A
            // nano that just launches others (Phalanx 29245: TeamCastNano 162357, TimeExist 4 h) gives that duration to
            // what it makes; the client never lists it in NCU, only the buff it casts.
            IsBuff = (nanoFlags & NanoFlags.IsBuff) != 0 || (DurationCentiseconds > 0 && HasEffectOfItsOwn());
            CanCancel = template.CanCancel
                && !IsHostile
                && (nanoFlags & NanoFlags.NotRemovable) == 0;
            IgnoresNcu = (nanoFlags & NanoFlags.NoRemoveNoNCUFriendly) != 0;
            RemovesOnZone = (nanoFlags & NanoFlags.IsBuff) != 0
                && (nanoFlags & NanoFlags.RemoveOnZone) == NanoFlags.RemoveOnZone;
            SurvivesDeath = (nanoFlags & NanoFlags.DontRemoveOnDeath) != 0;
            _userIsRecipient = AllFunctionsApplyToCaster();
            IsSelfOnly = (can & CanFlags.ApplyOnSelf) != 0 || _userIsRecipient;
        }

        readonly bool _userIsRecipient;

        /// <summary>The lines a nano can carry beyond its strain (stats 546-550).</summary>
        static readonly CharacterStat[] ExtraStackingLineStats =
        [
            CharacterStat.StackingLine2,
            CharacterStat.StackingLine3,
            CharacterStat.StackingLine4,
            CharacterStat.StackingLine5,
            CharacterStat.StackingLine6,
        ];

        readonly int[] _stackingLines;

        /// <summary>
        /// The strain plus every StackingLine2-6, without 0 or repeats. Composite Teachings 220331 is strain 165 and
        /// lines 167, 159, 161, 157, 163: it holds each single Teachings line (MM 151767 is 157) as well as its own.
        /// </summary>
        int[] ReadStackingLines()
        {
            int count = NanoStrain > 0 ? 1 : 0;
            Span<int> lines = stackalloc int[1 + ExtraStackingLineStats.Length];
            if (count == 1)
                lines[0] = NanoStrain;

            for (int i = 0; i < ExtraStackingLineStats.Length; i++)
            {
                int line = ReadStat(ExtraStackingLineStats[i]);
                if (line > 0 && lines[..count].IndexOf(line) < 0)
                    lines[count++] = line;
            }

            return count == 0 ? [] : lines[..count].ToArray();
        }

        /// <summary>
        /// Every function names the User or Wearer: the nano acts on whoever runs it. One that a perk or another nano
        /// lands on someone else hits that character (Bleeding Wounds 227363's DoT 274379), credited to its caster.
        /// </summary>
        protected override bool UserIsRecipient => _userIsRecipient;

        /// <summary>Every function names the User or Wearer and none an opponent (see <see cref="UserIsRecipient"/>).</summary>
        public bool ActsOnlyOnRunner => _userIsRecipient;

        /// <summary>
        /// True when an OnUse function is more than a one-shot launch: anything but casting another nano (CastNano,
        /// TeamCastNano, AreaCastNano, CastChance), summoning a pet or spawning an item or monster, or one of those
        /// repeating or delayed on a timer (its ticks run from the NCU entry). A friendly nano's single Hit is a
        /// one-shot too: it is done the moment it lands. Tap Vitae's team heal 248222 (Hit Health 1200-1500, TimeExist
        /// 10 s, no IsBuff flag) carries an NCU cost of 999, so as an NCU entry it was refused on every target and never
        /// healed (bugs #390, #445). Reads <see cref="IsHostile"/>, so it runs after that is set.
        /// </summary>
        bool HasEffectOfItsOwn()
        {
            if (!SpellList.TryGetValue(EventType.OnUse, out List<ItemSpell>? spells) || spells.Count == 0)
                return true;

            foreach (ItemSpell spell in spells)
            {
                bool launches = (FunctionType)spell.FunctionType is FunctionType.CastNano or FunctionType.TeamCastNano
                    or FunctionType.AreaCastNano or FunctionType.CastChance or FunctionType.SummonPet
                    or FunctionType.SpawnItem or FunctionType.SpawnMonster2
                    || (!IsHostile && spell.Is(FunctionType.Hit));
                if (!launches || (spell.TickCount > 1 && spell.TickInterval > 0) || Buff.IsDelayedOnce(spell))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Client rule (Gamecode.dll 0x100a478a, NanoItem_t +0xBC): every function of every event applies to the
        /// User or Wearer. An empty template counts too.
        /// </summary>
        bool AllFunctionsApplyToCaster()
        {
            foreach (List<ItemSpell> spells in SpellList.Values)
            {
                for (int i = 0; i < spells.Count; i++)
                {
                    if (spells[i].Target is not ((int)ItemTarget.User or (int)ItemTarget.Wearer))
                        return false;
                }
            }

            return true;
        }

        /// <summary>Nano strain; nanos of one strain never stack. 0 means strainless.</summary>
        public int NanoStrain { get; }

        /// <summary>Every nano line this nano occupies: <see cref="NanoStrain"/> first, then StackingLine2-6. Empty when strainless.</summary>
        public IReadOnlyList<int> StackingLines => _stackingLines;

        /// <summary>True when this nano and <paramref name="other"/> hold a nano line in common, so they cannot both run.</summary>
        public bool SharesStackingLine(NanoSpell other)
        {
            ArgumentNullException.ThrowIfNull(other);
            for (int i = 0; i < _stackingLines.Length; i++)
            {
                if (Array.IndexOf(other._stackingLines, _stackingLines[i]) >= 0)
                    return true;
            }

            return false;
        }

        /// <summary>Higher wins within a strain.</summary>
        public int StackingOrder { get; }

        /// <summary>
        /// True when an OnUse function of this nano casts <paramref name="nanoId"/> (CastNano, TeamCastNano,
        /// AreaCastNano, PlayfieldNano, CastOnPets or a CastNanoIfPossible variant), on the land or from a repeating tick.
        /// Refresh Outfit (Team) 223241 launches its nano-heal 216580 this way.
        /// </summary>
        public bool LaunchesNano(int nanoId)
        {
            if (!SpellList.TryGetValue(EventType.OnUse, out List<ItemSpell>? spells))
                return false;

            for (int i = 0; i < spells.Count; i++)
            {
                ItemSpell spell = spells[i];
                if (IsNanoLaunch((FunctionType)spell.FunctionType) && spell.TryReadInt(0, out int launched) && launched == nanoId)
                    return true;
            }

            return false;
        }

        static bool IsNanoLaunch(FunctionType function)
            => function is FunctionType.CastNano or FunctionType.TeamCastNano or FunctionType.AreaCastNano
                or FunctionType.PlayfieldNano or FunctionType.CastOnPets or FunctionType.CastNanoIfPossible
                or FunctionType.NpcCastNanoIfPossible or FunctionType.CastNanoIfPossibleOnFightTarget
                or FunctionType.NpcCastNanoIfPossibleOnFightTarget;

        public int NcuCost { get; }

        /// <summary>Base nano point cost before <see cref="CharacterStat.NPCostModifier"/>.</summary>
        public int NanoPointCost { get; }

        /// <summary>Buff lifetime; 0 for instant nanos.</summary>
        public int DurationCentiseconds { get; }

        /// <summary>
        /// TimeExist below 0 (Effect of Adonis Abyss 231227: -1): as an NCU entry it runs until something ends it (a strip,
        /// death, leaving the playfield whose item cast it) instead of for a duration.
        /// </summary>
        public bool RunsUntilRemoved { get; }

        public int AttackDelayCentiseconds { get; }

        public int AttackDelayCapCentiseconds { get; }

        public int RechargeDelayCentiseconds { get; }

        public int RechargeDelayCapCentiseconds { get; }

        /// <summary>
        /// How long a cast locks this nano's strain for the caster: the longer of the local cooldown (<see cref="CharacterStat.MonsterLevelsKilled"/>) and
        /// <see cref="CharacterStat.NanoLineCooldown"/>; 0 for none.
        /// </summary>
        public int NanoLineLockCentiseconds { get; }

        /// <summary>
        /// True when the nano occupies NCU. Catalog bit is <see cref="NanoFlags.IsBuff"/>;
        /// duration covers templates that omit the bit.
        /// </summary>
        public bool IsBuff { get; }

        /// <summary>True for nanos that only land on enemies (debuffs); these bypass NCU.</summary>
        public bool IsHostile { get; }

        /// <summary>
        /// Self-cast only: it lands on the caster whatever is targeted. Set by <see cref="CanFlags.ApplyOnSelf"/>,
        /// or when every function applies to the User or Wearer (the client's own self-target rule).
        /// </summary>
        public bool IsSelfOnly { get; }

        /// <summary>True when <see cref="NanoFlags.NoRemoveNoNCUFriendly"/> skips the NCU gate.</summary>
        public bool IgnoresNcu { get; }

        /// <summary>An IsBuff nano with <see cref="NanoFlags.RemoveOnZone"/>: it ends on a playfield change.</summary>
        public bool RemovesOnZone { get; }

        /// <summary><see cref="NanoFlags.DontRemoveOnDeath"/>: the NCU entry stays when its owner dies (239825).</summary>
        public bool SurvivesDeath { get; }

        /// <summary>Friendly buffs that occupy NCU. Hostile and NoRemoveNoNCUFriendly do not.</summary>
        public bool ConsumesNcu => !IsHostile && !IgnoresNcu;

        public static NanoSpell From(ItemTemplate template)
        {
            ArgumentNullException.ThrowIfNull(template);
            return template as NanoSpell ?? new NanoSpell(template);
        }

        /// <summary>Template stats may hold the Unset sentinel; read them as 0.</summary>
        protected int ReadStat(CharacterStat stat)
            => Stats.TryGetValue(stat, out int value) ? StatCollection.Normalize(value) : 0;
    }
}
