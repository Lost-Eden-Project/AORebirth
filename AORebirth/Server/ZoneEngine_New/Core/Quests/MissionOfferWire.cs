namespace ZoneEngine_New.Core.Quests
{
    using System;
    using System.Collections.Generic;

    using SmokeLounge.AOtomation.Messaging.GameData;
    using SmokeLounge.AOtomation.Messaging.Messages.N3Messages;

    using Utility.GameData.Missions;

    using ZoneEngine_New.Core.Entities;
    using ZoneEngine_New.Core.Quests.Dungeons;

    /// <summary>
    /// Mission terminal offers: the QuestAlternative response and its QuestInfo entries. Laid out from the 13 captured
    /// retail roll responses (65 offers, all five kinds) that were GameData/Missions/RollBodies.json before b2966d0f.
    /// Every captured offer carries the same header values (version 15, reward descriptor 6, reward hash 1364414290,
    /// Unknown16 2880, Unknown20 6, Unknown26 3), the terminal in Unknown5, one quest action and empty lists. The action
    /// is the journal's per kind (DungeonGeneration.json MissionTypes: kill 1, find person 16, find item 15, repair 8)
    /// and names its objective by NpcHash four-character code: kill and find person in Unknown2, find item in Action,
    /// repair the tool in Action and the machine in Unknown1. UnknownHash15 is the client-clock Unix expiry, as in the
    /// journal; the waypoint is the entrance (playfield type 0x9C50, the world offsets, the local position).
    /// The protocol values live here; the offer's content comes from the module that rolled it.
    /// </summary>
    internal static class MissionOfferWire
    {
        /// <summary>The fixed ShortInfo field holds 31 characters and its terminator.</summary>
        const int TitleLength = 31;

        const int OfferVersion = 15;

        const int RewardDescriptorVersion = 6;

        /// <summary>Captured on every offer; its meaning is unknown.</summary>
        const int RewardHash = 1364414290;

        /// <summary>Captured on every offer (Unknown16): 48 hours in minutes, the captured offers' stated time limit.</summary>
        const int OfferMinutes = 2880;

        const byte ResponseVersion = 4;

        /// <summary>Captured on every response (Unknown5); its meaning is unknown.</summary>
        const byte ResponseUnknown5 = 3;

        /// <summary>
        /// Unknown22 per kind: one captured value each (retail's ranged 3002-3036 repair, 3052-3081 find item, 3102-3134
        /// find person, 3141-3195 kill). Its meaning is unknown; a value from the kind's own range is sent.
        /// </summary>
        static int CapturedUnknown22(MissionRollType type) => type switch
        {
            MissionRollType.KillPerson => 3141,
            MissionRollType.FindPerson => 3102,
            MissionRollType.FindItem => 3052,
            _ => 3002
        };

        /// <summary>One offer, everything already checked and resolved by the caller.</summary>
        internal static QuestInfo Offer(Player player, int instance, Identity terminal, MissionRollType type, string title,
            string description, int quality, int credits, int experience, IReadOnlyList<QuestRewardItem> rewardItems, int iconId,
            QuestObjective objective, MissionEntrancePlacement entrance, MissionDestinationWorldPosition? worldPosition,
            DateTime expiresUtc)
        {
            var items = new QuestItemShort[rewardItems.Count];
            for (int i = 0; i < items.Length; i++)
                items[i] = new QuestItemShort
                {
                    LowId = rewardItems[i].Id, HighId = rewardItems[i].HighId ?? rewardItems[i].Id, Quality = Math.Max(1, rewardItems[i].Quality)
                };

            return new QuestInfo
            {
                QuestIdentity = new Identity { Type = IdentityType.Mission, Instance = instance },
                Unknown1 = OfferVersion,
                // Padded with spaces, not NULs: the client reads the title up to its terminator, so an early NUL would
                // shift every field after it.
                ShortInfo = QuestJournal.ShortName(title).PadRight(TitleLength),
                // Retail's description length counts its terminator; the serializer writes the string as given.
                Info = description.EndsWith('\0') ? description : description + '\0',
                Unknown5 = Copy(terminal),
                RewardDescriptorVersion = RewardDescriptorVersion,
                CashReward = credits,
                ExperienceReward = experience,
                UnknownIdentities1 = [],
                UnknownIdentities2 = [],
                ItemRewards = items,
                UnknownHash = RewardHash,
                Quality = quality,
                Unknown14 = new Identity(),
                MissionIconId = iconId,
                Unknown16 = OfferMinutes,
                QuestActions = [Action(player, type, objective, entrance, worldPosition, expiresUtc)],
                Unknown17 = [],
                Unknown18 = [],
                Unknown19 = [],
                CharInfos = [],
                Unknown20 = 6,
                UnknownIdentities20 = [],
                Unknown22 = CapturedUnknown22(type),
                Unknown23 = new Identity(),
                QuestIdentities = [],
                Unknown26 = 3,
                FactionInfo = []
            };
        }

        /// <summary>The roll response: the request's sliders and terminal back, with the offers.</summary>
        internal static QuestAlternativeMessage Response(Player player, QuestAlternativeMessage request, QuestInfo[] offers)
            => new()
            {
                Identity = Copy(player.Identity),
                VersionId = ResponseVersion,
                LevelSlider = request.LevelSlider,
                GoodBadSlider = request.GoodBadSlider,
                OrderChaosSlider = request.OrderChaosSlider,
                OpenHiddenSlider = request.OpenHiddenSlider,
                PhysicalMysticalSlider = request.PhysicalMysticalSlider,
                HeadOnStealthSlider = request.HeadOnStealthSlider,
                MoneyExperienceSlider = request.MoneyExperienceSlider,
                // Retail sends a different value per roll here; its meaning is unknown.
                Unknown4 = System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue),
                Unknown5 = ResponseUnknown5,
                MissionTerminalIdentity = Copy(request.MissionTerminalIdentity),
                QuestInfos = offers
            };

        static QuestActionList Action(Player player, MissionRollType type, QuestObjective objective, MissionEntrancePlacement entrance,
            MissionDestinationWorldPosition? worldPosition, DateTime expiresUtc)
        {
            Identity action = new(), slot1 = new(), slot2 = new();
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

            return new QuestActionList
            {
                Version = DungeonMissionTypes.Action(type),
                Action = action,
                Unknown1 = slot1,
                Unknown2 = slot2,
                Unknown3 = new Identity(),
                Unknown4 = new Identity(),
                Unknown9 = new Identity(),
                Unknown14 = new Identity(),
                UnknownHash15 = unchecked((int)QuestJournal.ClientExpiry(player, expiresUtc, DateTime.UtcNow)),
                Unknown17 = new Identity(),
                // An entrance no terminal offer was ever captured at has no WorldPos; the journal sends the same fallback.
                Playfield = new Identity
                {
                    Type = (IdentityType)(worldPosition == null ? QuestJournal.WaypointPlayfieldType : unchecked((int)worldPosition.PlayfieldIdentityType)),
                    Instance = entrance.PlayfieldId
                },
                Unknown18 = worldPosition?.WorldOffsetX ?? QuestJournal.WaypointUnknown,
                Unknown19 = worldPosition?.WorldOffsetZ ?? QuestJournal.WaypointUnknown,
                X = entrance.LocalX,
                Y = entrance.LocalY,
                Z = entrance.LocalZ
            };
        }

        /// <summary>An objective hash as retail names it in an offer; none when the objective is chosen at acceptance.</summary>
        static Identity Hash(string? hash)
            => string.IsNullOrEmpty(hash) ? new Identity() : new Identity { Type = IdentityType.NpcHash, Instance = QuestJournal.FourCc(hash) };

        static Identity Copy(Identity value) => new() { Type = value.Type, Instance = value.Instance };
    }
}
