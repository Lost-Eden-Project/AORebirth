namespace ZoneEngine_New.Tests
{
    using AORebirth.Enums;

    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using ZoneEngine_New.Core.Characters;
    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Helpers;
    using ZoneEngine_New.Core.Nanos;

    /// <summary>
    /// The hostile-nano land roll every cast, area child and proc goes through (NanoRuntime.RollCastResist,
    /// NanoCastFunctions.ResistsArea). Formula measured on live by Reimagine:
    /// https://anarchaeology.github.io/theory/nano-chance.html
    /// </summary>
    [TestClass]
    public sealed class NanoLandChanceTests
    {
        const int HostileId = 21104;

        [TestMethod]
        [DataRow(0, 1000, -100, 4)]     // no skill against a fully defensive target: only the 4% floor
        [DataRow(0, 100000, -100, 4)]   // however much Nano Resist, a nano still lands about 4% of the time
        [DataRow(0, 1000, 0, 24)]       // no skill, neutral bar: 50% - 50% + 20% + 4%
        [DataRow(1000, 1000, 0, 74)]    // equal ratings, neutral bar: 50% + 0 + 20% + 4%
        [DataRow(1000, 1000, 100, 94)]  // equal ratings, fully aggressive target
        [DataRow(1000, 1000, -100, 54)] // equal ratings, fully defensive target
        [DataRow(1500, 1000, 0, 100)]   // past 100% is held to 100%
        public void HostileNanoLandsByTheMeasuredFormula(int nanoSkill, int nanoResist, int targetAggDef, int expected)
        {
            // Mind Control 50% + Time and Space 50% against Nano Resist 100%, as on 28602.
            NanoSpell nano = Hostile();
            nano.Attack.Add(CharacterStat.PsychologicalModification, 50);
            nano.Attack.Add(CharacterStat.SpaceTime, 50);
            nano.Defend.Add(CharacterStat.NanoAC, 100);
            Player caster = Caster();
            caster.Stats.Set(CharacterStat.PsychologicalModification, nanoSkill);
            caster.Stats.Set(CharacterStat.SpaceTime, nanoSkill);
            Player target = Target(nanoResist, targetAggDef);

            DamageCalculator.ResolveNanoLand(caster, target, nano, out int attack, out int defense, out int chance);
            Assert.AreEqual(nanoSkill, attack);
            Assert.AreEqual(nanoResist, defense);
            Assert.AreEqual(expected, chance);
        }

        [TestMethod]
        public void DefenseWeightScalesTheTargetsNanoResist()
        {
            // 45879 counts only 75% of the target's Nano Resist.
            NanoSpell nano = Hostile();
            nano.Attack.Add(CharacterStat.PsychologicalModification, 100);
            nano.Defend.Add(CharacterStat.NanoAC, 75);
            Player caster = Caster();
            caster.Stats.Set(CharacterStat.PsychologicalModification, 750);
            Player target = Target(nanoResist: 1000, aggDef: 0);

            DamageCalculator.ResolveNanoLand(caster, target, nano, out _, out int defense, out int chance);
            Assert.AreEqual(750, defense);
            Assert.AreEqual(74, chance);
        }

        [TestMethod]
        public void UnskilledCasterStillLandsAgainstHugeNanoResist()
        {
            // A fully defensive target with any amount of Nano Resist is still landed on about 4% of the time.
            NanoSpell nano = Hostile();
            nano.Attack.Add(CharacterStat.PsychologicalModification, 100);
            nano.Defend.Add(CharacterStat.NanoAC, 100);
            Player caster = Caster();
            caster.Stats.Set(CharacterStat.PsychologicalModification, 0);
            Player target = Target(nanoResist: 100000, aggDef: -100);

            const int Rolls = 20000;
            int landed = 0;
            for (int i = 0; i < Rolls; i++)
            {
                if (DamageCalculator.ResolveNanoLand(caster, target, nano, out _, out _, out _))
                    landed++;
            }

            // 4% of 20000 = 800; the band is far wider than the roll's spread (about +-28).
            Assert.IsTrue(landed is > 600 and < 1000, $"landed {landed} of {Rolls}");
        }

        [TestMethod]
        public void PerkActionContestDoesNotTakeTheNanoFloorOrTargetBar()
        {
            NanoSpell action = Hostile();
            action.Attack.Add(CharacterStat.PsychologicalModification, 100);
            action.Defend.Add(CharacterStat.NanoAC, 100);
            Player caster = Caster();
            caster.Stats.Set(CharacterStat.PsychologicalModification, 0);
            Player target = Target(nanoResist: 1000, aggDef: -100);

            DamageCalculator.ResolveNanoLand(caster, target, action, out _, out _, out int chance, nanoLandBonus: false);
            Assert.AreEqual(20, chance);
        }

        static NanoSpell Hostile() => TestNanos.Create(HostileId, flags: NanoFlags.IsHostile);

        static Player Caster()
        {
            Player caster = TestWorld.CreatePlayer(1);
            caster.Stats.Set(CharacterStat.AMSModifier, 0);
            return caster;
        }

        static Player Target(int nanoResist, int aggDef)
        {
            Player target = TestWorld.CreatePlayer(2);
            target.Stats.Set(CharacterStat.NanoAC, nanoResist);
            target.Stats.Set(CharacterStat.DMSModifier, 0);
            target.Stats.Set(CharacterStat.AggDef, aggDef);
            return target;
        }
    }
}
