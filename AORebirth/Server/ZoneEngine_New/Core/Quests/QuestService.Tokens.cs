namespace ZoneEngine_New.Core.Quests;

using System;
using System.Security.Cryptography;
using System.Text.Json;
using AORebirth.Interfaces.Persistence.Missions;
using SmokeLounge.AOtomation.Messaging.GameData;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Quests.Dungeons;

public sealed partial class QuestService
{
    internal MissionTokenProgress? CreateTokenProgress(int ownerId, string questId, long assignedAtTicks,
        MissionTokenRewardPolicy? policy)
    {
        if (policy == null)
            return null;
        if (_completionDao == null || !policy.IsValid)
            throw new InvalidOperationException("Mission token persistence or accepted policy is unavailable.");
        return new MissionTokenProgress(_completionDao, ownerId, questId, assignedAtTicks, policy);
    }

    sealed class FrozenMissionTokenReward
    {
        public FrozenMissionTokenReward() { }

        public long AssignedAtUtcTicks { get; set; }
        public int Side { get; set; }
        public int Killed { get; set; }
        public int Total { get; set; }
        public int Roll { get; set; }
        public bool Won { get; set; }
        public int ItemId { get; set; }
        public int Quality { get; set; }
    }

    static string TokenRewardFlag(long assigned) => "token-reward:" + assigned.ToString(System.Globalization.CultureInfo.InvariantCulture);

    CharacterQuestCompletionResult EarnWithToken(IMissionDaoTransaction transaction,
        ICharacterQuestCompletionTransaction completion, Player player, PlayerQuest quest, CharacterQuestEarnRequest request)
    {
        var result = completion.TryEarnCharacterQuest(request);
        if (result.Status == CharacterQuestCompletionStatus.Earned)
        {
            FreezeTokenReward(transaction, player, quest, result.Quest);
        }
        return result;
    }

    void FreezeTokenReward(IMissionDaoTransaction transaction, Player player, PlayerQuest quest,
        CharacterQuestCompletionSnapshotData stored)
    {
        if (quest.Source != QuestSource.Generated
            || !QuestDungeonParameters.TryParse(stored.AcgBuildingGeneratorJson, out var parameters)
            || parameters.TokenReward is not { } policy)
            return;
        var key = new MissionKeyData(player.Identity.Instance, quest.QuestId);
        string flagKey = TokenRewardFlag(stored.AssignedAtUtcTicks);
        if (transaction.GetFlag(key, flagKey) != null)
            throw new InvalidOperationException("An unearned mission already has a frozen token result.");
        var (killed, total) = MissionTokenProgress.ReadCounts(transaction, quest.QuestId, stored.AssignedAtUtcTicks);
        if (killed < 0 || total < 0 || killed > total)
            throw new InvalidOperationException("Mission token progress is invalid.");
        int side = player.Stats.GetOrZero(CharacterStat.Side);
        bool sided = policy.SidedItems.TryGetValue(side, out int itemId);
        // Kill ratio is the configured gameplay model, including below the first captured feedback.
        // A single draw is frozen with earning; capacity failures and reconnects cannot reroll it.
        int roll = sided && total > 0 ? RandomNumberGenerator.GetInt32(total) : -1;
        bool won = roll >= 0 && roll < killed;
        var frozen = new FrozenMissionTokenReward
        {
            AssignedAtUtcTicks = stored.AssignedAtUtcTicks, Side = side, Killed = killed, Total = total,
            Roll = roll, Won = won, ItemId = won ? itemId : 0, Quality = won ? policy.ItemQuality : 0
        };
        transaction.SaveFlag(key, new MissionFlagData
        {
            CharacterId = player.Identity.Instance, QuestId = quest.QuestId, FlagKey = flagKey,
            Value = JsonSerializer.Serialize(frozen), CreatedAtUtcTicks = _now().Ticks, UpdatedAtUtcTicks = _now().Ticks
        });
    }

    static FrozenMissionTokenReward? ReadTokenReward(IMissionDaoTransaction transaction, Player player,
        PlayerQuest quest, CharacterQuestCompletionSnapshotData stored)
    {
        if (quest.Source != QuestSource.Generated
            || !QuestDungeonParameters.TryParse(stored.AcgBuildingGeneratorJson, out var parameters)
            || parameters.TokenReward is not { } policy)
            return null;
        var flag = transaction.GetFlag(new MissionKeyData(player.Identity.Instance, quest.QuestId),
            TokenRewardFlag(stored.AssignedAtUtcTicks));
        var frozen = flag == null ? null : JsonSerializer.Deserialize<FrozenMissionTokenReward>(flag.Value);
        if (frozen == null || frozen.AssignedAtUtcTicks != stored.AssignedAtUtcTicks
            || frozen.Killed < 0 || frozen.Total < 0 || frozen.Killed > frozen.Total)
            throw new InvalidOperationException("Earned mission token result is missing or invalid; reward remains pending.");
        bool sided = policy.SidedItems.TryGetValue(frozen.Side, out int itemId);
        bool canRoll = sided && frozen.Total > 0;
        bool validRoll = canRoll ? frozen.Roll >= 0 && frozen.Roll < frozen.Total : frozen.Roll == -1;
        bool won = canRoll && frozen.Roll < frozen.Killed;
        if (!validRoll || frozen.Won != won || frozen.ItemId != (won ? itemId : 0)
            || frozen.Quality != (won ? policy.ItemQuality : 0))
            throw new InvalidOperationException("Frozen mission token award does not match its accepted policy.");
        return frozen;
    }
}
