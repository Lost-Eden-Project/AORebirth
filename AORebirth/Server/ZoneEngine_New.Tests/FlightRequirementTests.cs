namespace ZoneEngine_New.Tests
{
    using System.Collections.Generic;

    using AORebirth.Core.GameData;
    using AORebirth.Enums;

    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Inventory;

    /// <summary>
    /// ToUse of the hoverboard 281569 (Phasefront Classic - Charon) and Sparrow Flight 82835, as items.dat encodes them,
    /// against the playfield-entry stats and FlyingAllowed that <see cref="PlayfieldEntryFlags"/> derives from
    /// metadata.json.
    /// </summary>
    [TestClass]
    public sealed class FlightRequirementTests
    {
        const int RubiKaOutdoor = 570;
        const int SparrowNanoLine = 223;

        static readonly PlayfieldMetaData OutdoorRubiKa = new() { IsIndoor = false, UnknownV9A = 0, PlayfieldTypeBits = 0 };

        // 281569 ToUse: 438 NotBitAnd 1, 139 > 80, And, 410 = 0, And, 359 = 0, And, 531 = 0, And, FlyingAllowed, And.
        static readonly ItemTemplate Hoverboard = ToUse(
            Leaf(CharacterStat.PlayfieldType, Operator.NotBitAnd, 1),
            Leaf(CharacterStat.VehicleAir, Operator.GreaterThan, 80),
            Link(Operator.And),
            Leaf(CharacterStat.NumberOfFightingOpponents, Operator.EqualTo, 0),
            Link(Operator.And),
            Leaf(CharacterStat.MonsterData, Operator.EqualTo, 0),
            Link(Operator.And),
            Leaf(CharacterStat.ExpansionPlayfield, Operator.EqualTo, 0),
            Link(Operator.And),
            State(Operator.FlyingAllowed, 0),
            Link(Operator.And));

        // 82835 ToUse: 128 > 132, 127 > 111, And, 122 > 111, And, 368 = 6, And, 438 NotBitAnd 1, And,
        // 359 = 0, HasRunningNanoLine 223, Or, And.
        static readonly ItemTemplate SparrowFlight = ToUse(
            Leaf((CharacterStat)128, Operator.GreaterThan, 132),
            Leaf((CharacterStat)127, Operator.GreaterThan, 111),
            Link(Operator.And),
            Leaf((CharacterStat)122, Operator.GreaterThan, 111),
            Link(Operator.And),
            Leaf((CharacterStat)368, Operator.EqualTo, 6),
            Link(Operator.And),
            Leaf(CharacterStat.PlayfieldType, Operator.NotBitAnd, 1),
            Link(Operator.And),
            Leaf(CharacterStat.MonsterData, Operator.EqualTo, 0),
            State(Operator.HasRunningNanoLine, SparrowNanoLine),
            Link(Operator.Or),
            Link(Operator.And));

        [TestMethod]
        public void HoverboardPassesOutdoorsOnRubiKaInNormalShapeOutOfCombat()
        {
            Assert.IsTrue(CanUse(Hoverboard, RubiKaOutdoor, OutdoorRubiKa, HoverboardPilot()));
        }

        [TestMethod]
        public void HoverboardPassesWithNoStoredMonsterData()
        {
            Dictionary<CharacterStat, int> pilot = HoverboardPilot();
            pilot.Remove(CharacterStat.MonsterData);
            Assert.IsTrue(CanUse(Hoverboard, RubiKaOutdoor, OutdoorRubiKa, pilot));
        }

        [TestMethod]
        public void HoverboardFailsIndoors()
        {
            Assert.IsFalse(CanUse(Hoverboard, 4582, new PlayfieldMetaData { IsIndoor = true }, HoverboardPilot()));
        }

        [TestMethod]
        public void HoverboardFailsInTheShadowlands()
        {
            // Shadowlands bit alone, flying otherwise allowed: only "Must be on Rubi-ka" fails.
            Assert.IsFalse(CanUse(Hoverboard, 4310, new PlayfieldMetaData { UnknownV9A = 0x2 }, HoverboardPilot()));
        }

        [TestMethod]
        public void HoverboardFailsWhereFlyingIsForbidden()
        {
            Assert.IsFalse(CanUse(Hoverboard, 4582, new PlayfieldMetaData { PlayfieldTypeBits = 0x1F80000 }, HoverboardPilot()));
        }

        [TestMethod]
        public void HoverboardFailsWithoutPlayfieldMetadata()
        {
            Assert.IsFalse(CanUse(Hoverboard, RubiKaOutdoor, null, HoverboardPilot()));
        }

        [TestMethod]
        public void HoverboardFailsWhenMorphed()
        {
            Dictionary<CharacterStat, int> pilot = HoverboardPilot();
            pilot[CharacterStat.MonsterData] = 30377;
            Assert.IsFalse(CanUse(Hoverboard, RubiKaOutdoor, OutdoorRubiKa, pilot));
        }

        [TestMethod]
        public void HoverboardFailsWithVehicleAirEighty()
        {
            Dictionary<CharacterStat, int> pilot = HoverboardPilot();
            pilot[CharacterStat.VehicleAir] = 80;
            Assert.IsFalse(CanUse(Hoverboard, RubiKaOutdoor, OutdoorRubiKa, pilot));
        }

        [TestMethod]
        public void HoverboardFailsInCombat()
        {
            Dictionary<CharacterStat, int> pilot = HoverboardPilot();
            pilot[CharacterStat.NumberOfFightingOpponents] = 1;
            Assert.IsFalse(CanUse(Hoverboard, RubiKaOutdoor, OutdoorRubiKa, pilot));
        }

        [TestMethod]
        public void BattleStationsSetTheirPlayfieldTypeBitWithoutBlockingOutdoorChecks()
        {
            Assert.AreEqual(PlayfieldEntryFlags.BattleStationBit, PlayfieldEntryFlags.PlayfieldType(4380, OutdoorRubiKa));
            Assert.AreEqual(PlayfieldEntryFlags.BattleStationBit, PlayfieldEntryFlags.PlayfieldType(6104, OutdoorRubiKa));
            Assert.AreEqual(PlayfieldEntryFlags.DungeonBit, PlayfieldEntryFlags.PlayfieldType(4389, new PlayfieldMetaData { IsIndoor = true }));
        }

        [TestMethod]
        public void SparrowFlightNeedsOutdoorsAndNormalShapeUnlessItsLineRuns()
        {
            Dictionary<CharacterStat, int> adventurer = SparrowCaster();
            Assert.IsTrue(CanUse(SparrowFlight, RubiKaOutdoor, OutdoorRubiKa, adventurer));
            Assert.IsFalse(CanUse(SparrowFlight, 4582, new PlayfieldMetaData { IsIndoor = true }, adventurer));

            adventurer[CharacterStat.MonsterData] = 30377;
            Assert.IsFalse(CanUse(SparrowFlight, RubiKaOutdoor, OutdoorRubiKa, adventurer));
            Assert.IsTrue(CanUse(SparrowFlight, RubiKaOutdoor, OutdoorRubiKa, adventurer, runningLine: SparrowNanoLine));
        }

        static Dictionary<CharacterStat, int> HoverboardPilot() => new()
        {
            [CharacterStat.VehicleAir] = 81,
            [CharacterStat.NumberOfFightingOpponents] = 0,
            [CharacterStat.MonsterData] = 0
        };

        static Dictionary<CharacterStat, int> SparrowCaster() => new()
        {
            [(CharacterStat)128] = 133,
            [(CharacterStat)127] = 112,
            [(CharacterStat)122] = 112,
            [(CharacterStat)368] = 6,
            [CharacterStat.MonsterData] = 0
        };

        /// <summary>
        /// The caster's stats as the zone engine has them after entering <paramref name="playfieldId"/>: the entry flags
        /// set, everything not given unset; FlyingAllowed and the nano-line check answered as Character does.
        /// </summary>
        static bool CanUse(ItemTemplate template, int playfieldId, PlayfieldMetaData? metaData,
            Dictionary<CharacterStat, int> stats, int runningLine = 0)
        {
            var all = new Dictionary<CharacterStat, int>(stats)
            {
                [CharacterStat.PlayfieldType] = PlayfieldEntryFlags.PlayfieldType(playfieldId, metaData),
                [CharacterStat.ExpansionPlayfield] = PlayfieldEntryFlags.ExpansionPlayfield(metaData),
                [CharacterStat.PlayfieldProxy] = playfieldId
            };

            return template.MeetsActionRequirements(
                stat => all.TryGetValue(stat, out int value) ? value : (int)CharacterStat.Unset,
                ActionType.ToUse,
                requirement => (Operator)requirement.Operator switch
                {
                    Operator.FlyingAllowed => PlayfieldEntryFlags.IsFlyingAllowed(metaData),
                    Operator.HasRunningNanoLine => runningLine != 0 && requirement.Value == runningLine,
                    _ => null
                });
        }

        static ItemTemplate ToUse(params ItemRequirement[] requirements) => new()
        {
            Actions = [new ItemAction { ActionType = (int)ActionType.ToUse, Requirements = [.. requirements] }]
        };

        static ItemRequirement Leaf(CharacterStat stat, Operator op, int value)
            => new() { StatNumber = (int)stat, Operator = (int)op, Value = value };

        static ItemRequirement State(Operator op, int value) => new() { Operator = (int)op, Value = value };

        static ItemRequirement Link(Operator op) => new() { Operator = (int)op };
    }
}
