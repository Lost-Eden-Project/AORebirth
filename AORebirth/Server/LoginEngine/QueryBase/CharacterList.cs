#region License

// Copyright (c) 2005-2014, CellAO Team
// 
// 
// All rights reserved.
// 
// 
// Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:
// 
// 
//     * Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
//     * Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
//     * Neither the name of the CellAO Team nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.
// 
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR
// CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
// EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
// PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
// PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
// LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
// NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// 

#endregion

namespace LoginEngine.QueryBase
{
    #region Usings ...

    using System;
    using System.Collections.Generic;
    using System.Linq;

    using AORebirth.Database.Dao;
    using AORebirth.Interfaces.Persistence.Characters;

    using LoginEngine.Packets;

    using SmokeLounge.AOtomation.Messaging.GameData;

    using Utility;

    #endregion

    /// <summary>
    /// </summary>
    public static class CharacterList
    {
        #region Public Methods and Operators

        /// <summary>
        /// </summary>
        /// <param name="accountName">
        /// </param>
        /// <returns>
        /// </returns>
        public static List<CharacterEntry> LoadCharacters(string accountName)
        {
            return LoadCharacters(
                AORebirth.Database.DatabaseDaoFactory.CreateCharacterDao().ListForAccount(accountName),
                characterId => StatDao.Instance.GetAll(new { Type = (int)IdentityType.CanbeAffected, Instance = characterId }));
        }

        internal static List<CharacterEntry> LoadCharacters(
            IEnumerable<CharacterDirectoryData> directory,
            Func<int, IEnumerable<DBStats>> loadStats)
        {
            var characters = new List<CharacterEntry>();

            foreach (var ch in directory)
            {
                // GetById fabricates a zero-valued row when a stat is absent. The
                // character list must distinguish that from a stored zero Sex value.
                var stats = loadStats(ch.CharacterId).ToList();
                DBStats level = stats.FirstOrDefault(stat => stat.StatId == (int)CharacterStat.Level);
                DBStats breed = stats.FirstOrDefault(stat => stat.StatId == (int)CharacterStat.Breed);
                DBStats gender = stats.FirstOrDefault(stat => stat.StatId == (int)CharacterStat.Sex);
                DBStats profession = stats.FirstOrDefault(stat => stat.StatId == (int)CharacterStat.Profession);

                string incompleteField = level == null || level.StatValue <= 0 ? "Level"
                    : breed == null || breed.StatValue <= 0 ? "Breed"
                    : gender == null ? "Sex"
                    : profession == null || profession.StatValue <= 0 ? "Profession"
                    : ch.Playfield <= 0 ? "Playfield"
                    : null;
                if (incompleteField != null)
                {
                    LogUtil.WarnException(
                        "Character list skipped incomplete character id={0}: missing or invalid {1}.",
                        ch.CharacterId,
                        incompleteField);
                    continue;
                }

                characters.Add(new CharacterEntry
                {
                    Id = ch.CharacterId,
                    Name = ch.Name,
                    Playfield = ch.Playfield,
                    Level = level.StatValue,
                    Breed = breed.StatValue,
                    Gender = gender.StatValue,
                    Profession = profession.StatValue
                });
            }

            return characters;
        }

        #endregion
    }
}
