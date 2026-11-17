namespace ZoneEngine_New.Core.Quests
{
    using System;
    using System.Collections.Generic;

    using LostEden.Protocol.GameData;
    using WireQuestReward = LostEden.Protocol.GameData.QuestReward;
    using LostEden.Protocol.Messages.N3Messages;

    using Utility.GameData.Missions;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Quests.Dungeons;

    /// <summary>
    /// Mission terminal offers: the QuestAlternative response and its mission entries. Laid out from the 13 captured
    /// retail roll responses (65 offers, all five kinds) that were GameData/Missions/RollBodies.json before b2966d0f.
    /// Every captured offer carries the same header values (quest version 15, reward version 6, reward Unknown0C
    /// 1364414290, Unknown74 2880, UnknownA8 6, UnknownD4 3), the terminal as Identity38, one quest action and empty
    /// lists. The action is the journal's per kind (DungeonGeneration.json MissionTypes: kill 1, find person 16, find
    /// item 15, repair 8) and names its objective by MobHash four-character code: kill and find person in Identity14,
    /// find item in Identity04, repair the tool in Identity04 and the machine in Identity0C. ExpiryTime is the
    /// client-clock Unix expiry, as in the journal; the waypoint is the entrance (playfield type 0x9C50, the world
    /// offsets, the local position). The protocol values live here; the offer's content comes from the module that
    /// rolled it.
    /// </summary>
    internal static class MissionOfferWire
    {
        /// <summary>The fixed name field holds 31 characters and its terminator.</summary>
        const int TitleLength = 31;

        const int OfferVersion = 15;

        const int RewardDescriptorVersion = 6;

        /// <summary>Captured on every offer (the reward's Unknown0C); its meaning is unknown.</summary>
        const int RewardHash = 1364414290;

        /// <summary>Captured on every offer (Unknown74): 48 hours in minutes, the captured offers' stated time limit.</summary>
        const int OfferMinutes = 2880;

        const byte ResponseVersion = 4;

        /// <summary>Captured on every response (the originator byte); its meaning is unknown.</summary>
        const MissionScope ResponseScope = (MissionScope)3;

        /// <summary>
        /// UnknownB0 per kind: one captured value each (retail's ranged 3002-3036 repair, 3052-3081 find item, 3102-3134
        /// find person, 3141-3195 kill). Its meaning is unknown; a value from the kind's own range is sent.
        /// </summary>
        static int CapturedUnknownB0(MissionRollType type) => type switch
        {
            MissionRollType.KillPerson => 3141,
            MissionRollType.FindPerson => 3102,
            MissionRollType.FindItem => 3052,
            _ => 3002
        };

        /// <summary>One offer, everything already checked and resolved by the caller.</summary>
        internal static MissionInfo Offer(Player player, int instance, Identity terminal, MissionRollType type, string title,
            string description, int quality, int credits, int experience, IReadOnlyList<QuestRewardItem> rewardItems, int iconId,
            QuestObjective objective, MissionEntrancePlacement entrance, MissionDestinationWorldPosition? worldPosition,
            DateTime expiresUtc)
        {
            var items = new List<AcgItem>(rewardItems.Count);
            for (int i = 0; i < rewardItems.Count; i++)
                items.Add(new AcgItem
                {
                    LowId = rewardItems[i].Id, HighId = rewardItems[i].HighId ?? rewardItems[i].Id, Quality = Math.Max(1, rewardItems[i].Quality)
                });

            var quest = new Quest
            {
                Identity = new Identity(IdentityType.Mission, instance),
                Version = OfferVersion,
                // Padded with spaces, not NULs: the client reads the title up to its terminator, so an early NUL would
                // shift every field after it.
                Name = QuestJournal.ShortName(title).PadRight(TitleLength),
                // Retail's description length counts its terminator; the codec writes the string as given.
                Description = description.EndsWith('\0') ? description : description + '\0',
                Identity38 = terminal,
                Reward = new WireQuestReward
                {
                    Version = RewardDescriptorVersion,
                    Cash = credits,
                    Experience = experience,
                    Items = items,
                    Unknown0C = RewardHash,
                    Unknown10 = quality
                },
                Icon = iconId,
                Unknown74 = OfferMinutes,
                Actions = [Action(player, type, objective, entrance, worldPosition, expiresUtc)],
                UnknownA8 = 6,
                UnknownB0 = CapturedUnknownB0(type),
                UnknownD4 = 3,
                IdentitiesD8 = []
            };
            return new MissionInfo { Quest = quest };
        }

        /// <summary>The roll response: the request's sliders and terminal back, with the offers.</summary>
        internal static QuestAlternativeMessage Response(Player player, QuestAlternativeMessage request, MissionInfo[] offers)
            => new()
            {
                Identity = player.Identity,
                Version = ResponseVersion,
                MissionSliders = new MissionSliders
                {
                    Difficulty = request.MissionSliders.Difficulty,
                    GoodBad = request.MissionSliders.GoodBad,
                    OrderChaos = request.MissionSliders.OrderChaos,
                    OpenHidden = request.MissionSliders.OpenHidden,
                    PhysicalMystical = request.MissionSliders.PhysicalMystical,
                    HeadonStealth = request.MissionSliders.HeadonStealth,
                    CreditsXp = request.MissionSliders.CreditsXp
                },
                // Retail sends a different value per roll here; its meaning is unknown.
                Seed = System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue),
                Scope = ResponseScope,
                Terminal = request.Terminal,
                MissionDetails = offers
            };

        static QuestActionInfo Action(Player player, MissionRollType type, QuestObjective objective, MissionEntrancePlacement entrance,
            MissionDestinationWorldPosition? worldPosition, DateTime expiresUtc)
        {
            Identity action = default, slot1 = default, slot2 = default;
            switch (type)
            {
                case MissionRollType.KillPerson:
                case MissionRollType.FindPerson:
                    slot2 = Hash(objective.Npc);
                    break;
                case MissionRollType.FindItem:
                    action = Hash(objective.WorldItem);
                    break;
                case MissionRollType.RepairMachine:
                    action = Hash(objective.SpecItem);
                    slot1 = Hash(objective.WorldItem);
                    break;
            }

            return new QuestActionInfo
            {
                Type = DungeonMissionTypes.Action(type),
                Identity04 = action,
                Identity0C = slot1,
                Identity14 = slot2,
                ExpiryTime = QuestJournal.ClientExpiry(player, expiresUtc, DateTime.UtcNow),
                WorldPos = new QuestWorldPos
                {
                    // An entrance no terminal offer was ever captured at has no WorldPos; the journal sends the same fallback.
                    Playfield = new Identity(
                        (IdentityType)(worldPosition == null ? QuestJournal.WaypointPlayfieldType : unchecked((int)worldPosition.PlayfieldIdentityType)),
                        entrance.PlayfieldId),
                    X = worldPosition?.WorldOffsetX ?? QuestJournal.WaypointUnknown,
                    Z = worldPosition?.WorldOffsetZ ?? QuestJournal.WaypointUnknown,
                    Offset = new Vector3(entrance.LocalX, entrance.LocalY, entrance.LocalZ)
                }
            };
        }

        /// <summary>An objective hash as retail names it in an offer; none when the objective is chosen at acceptance.</summary>
        static Identity Hash(string? hash)
            => string.IsNullOrEmpty(hash) ? default : new Identity(IdentityType.MobHash, QuestJournal.FourCc(hash));
    }
}
