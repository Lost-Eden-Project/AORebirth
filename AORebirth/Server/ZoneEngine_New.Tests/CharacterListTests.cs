using System.Collections.Generic;
using System.IO;
using System.Linq;

using AORebirth.Database.Dao;
using AORebirth.Interfaces.Persistence.Characters;
using LoginEngine.QueryBase;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SmokeLounge.AOtomation.Messaging.GameData;

namespace ZoneEngine_New.Tests
{
    [TestClass]
    public sealed class CharacterListTests
    {
        [TestMethod]
        public void IncompleteHistoricalRowDoesNotHideOtherCharactersOrInventStats()
        {
            CharacterDirectoryData incomplete = Character(10);
            CharacterDirectoryData complete = Character(11);
            var reads = new List<int>();
            var stored = new Dictionary<int, List<DBStats>>
            {
                [incomplete.CharacterId] = new List<DBStats>(),
                [complete.CharacterId] = Stats(complete.CharacterId)
            };

            var entries = CharacterList.LoadCharacters(new[] { incomplete, complete }, id =>
            {
                reads.Add(id);
                return stored[id];
            });

            CollectionAssert.AreEqual(new[] { complete.CharacterId }, entries.Select(entry => entry.Id).ToArray());
            CollectionAssert.AreEqual(new[] { incomplete.CharacterId, complete.CharacterId }, reads);
            Assert.AreEqual(0, stored[incomplete.CharacterId].Count);
            Assert.AreEqual(4, stored[complete.CharacterId].Count);
        }

        [TestMethod]
        [DataRow(CharacterStat.Level)]
        [DataRow(CharacterStat.Breed)]
        [DataRow(CharacterStat.Sex)]
        [DataRow(CharacterStat.Profession)]
        public void MissingMandatoryStatExcludesTheCharacter(CharacterStat missing)
        {
            CharacterDirectoryData character = Character(10);
            List<DBStats> stats = Stats(character.CharacterId);
            stats.RemoveAll(stat => stat.StatId == (int)missing);

            var entries = CharacterList.LoadCharacters(new[] { character }, _ => stats);

            Assert.AreEqual(0, entries.Count);
            Assert.AreEqual(3, stats.Count);
        }

        [TestMethod]
        [DataRow(CharacterStat.Level, 0)]
        [DataRow(CharacterStat.Level, -1)]
        [DataRow(CharacterStat.Breed, 0)]
        [DataRow(CharacterStat.Breed, -1)]
        [DataRow(CharacterStat.Profession, 0)]
        [DataRow(CharacterStat.Profession, -1)]
        public void InvalidMandatoryStatExcludesTheCharacter(CharacterStat invalid, int value)
        {
            CharacterDirectoryData character = Character(10);
            List<DBStats> stats = Stats(character.CharacterId);
            stats.Single(stat => stat.StatId == (int)invalid).StatValue = value;

            var entries = CharacterList.LoadCharacters(new[] { character }, _ => stats);

            Assert.AreEqual(0, entries.Count);
            Assert.AreEqual(value, stats.Single(stat => stat.StatId == (int)invalid).StatValue);
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(-1)]
        public void MissingStartPlayfieldExcludesTheCharacter(int playfield)
        {
            CharacterDirectoryData character = Character(10);
            character.Playfield = playfield;

            var entries = CharacterList.LoadCharacters(new[] { character }, Stats);

            Assert.AreEqual(0, entries.Count);
            Assert.AreEqual(playfield, character.Playfield);
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        public void StoredGenderIncludingZeroIsPreserved(int gender)
        {
            CharacterDirectoryData character = Character(10);
            List<DBStats> stats = Stats(character.CharacterId);
            stats.Single(stat => stat.StatId == (int)CharacterStat.Sex).StatValue = gender;

            var entries = CharacterList.LoadCharacters(new[] { character }, _ => stats);

            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual(character.CharacterId, entries[0].Id);
            Assert.AreEqual(character.Name, entries[0].Name);
            Assert.AreEqual(character.Playfield, entries[0].Playfield);
            Assert.AreEqual(1, entries[0].Level);
            Assert.AreEqual((int)Breed.Solitus, entries[0].Breed);
            Assert.AreEqual((int)Profession.Soldier, entries[0].Profession);
            Assert.AreEqual(gender, entries[0].Gender);
        }

        [TestMethod]
        public void HistoricalPositiveStatsAreNotRestrictedToCurrentCreationChoices()
        {
            CharacterDirectoryData character = Character(10);
            List<DBStats> stats = Stats(character.CharacterId);
            stats.Single(stat => stat.StatId == (int)CharacterStat.Level).StatValue = 220;
            stats.Single(stat => stat.StatId == (int)CharacterStat.Breed).StatValue = (int)Breed.Special;
            stats.Single(stat => stat.StatId == (int)CharacterStat.Profession).StatValue = (int)Profession.Monster;

            var entries = CharacterList.LoadCharacters(new[] { character }, _ => stats);

            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual(220, entries[0].Level);
            Assert.AreEqual((int)Breed.Special, entries[0].Breed);
            Assert.AreEqual((int)Profession.Monster, entries[0].Profession);
        }

        [TestMethod]
        public void StatReadFailureIsNotTreatedAsAnIncompleteCharacter()
        {
            var failure = new IOException("Simulated stat read failure.");

            IOException observed = Assert.ThrowsExactly<IOException>(() =>
                CharacterList.LoadCharacters(new[] { Character(10) }, _ => throw failure));

            Assert.AreSame(failure, observed);
        }

        private static CharacterDirectoryData Character(int id)
        {
            return new CharacterDirectoryData
            {
                CharacterId = id,
                AccountUsername = "fixture-account",
                Name = "Character" + id,
                Playfield = 4582
            };
        }

        private static List<DBStats> Stats(int id)
        {
            return new List<DBStats>
            {
                Stat(id, CharacterStat.Level, 1),
                Stat(id, CharacterStat.Breed, (int)Breed.Solitus),
                Stat(id, CharacterStat.Sex, (int)Gender.Male),
                Stat(id, CharacterStat.Profession, (int)Profession.Soldier)
            };
        }

        private static DBStats Stat(int id, CharacterStat stat, int value)
        {
            return new DBStats
            {
                Type = (int)IdentityType.CanbeAffected,
                Instance = id,
                StatId = (int)stat,
                StatValue = value
            };
        }
    }
}
