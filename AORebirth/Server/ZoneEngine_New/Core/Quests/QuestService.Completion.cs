namespace ZoneEngine_New.Core.Quests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AORebirth.Enums;
using AORebirth.Interfaces.Persistence.Missions;
using SmokeLounge.AOtomation.Messaging.GameData;
using ZoneEngine_New.Core.Data;
using ZoneEngine_New.Core.Entities;
using ZoneEngine_New.Core.Helpers;
using ZoneEngine_New.Core.Inventory;
using ZoneEngine_New.Core.Missions;
using ZoneEngine_New.Core.Network;
using ZoneEngine_New.Core.Quests.Dungeons;

public sealed partial class QuestService
{
    /// <summary>Only the registered owner-bound world objective can call this collection path.</summary>
    internal bool TryCollectFindItem(Player player, MissionObjectiveItem objective)
    {
        lock (player.PersistenceGate)
        {
            if (!CanDeliver(player) || _completionDao == null
                || objective.Playfield is not ZoneEngine_New.Core.Playfield.QuestDungeonPlayfield world
                || !ReferenceEquals(player.Playfield, world)
                || player.Identity.Instance != objective.Binding.OwnerId
                || !GetLog(player).Quests.TryGetValue(objective.Binding.QuestId, out PlayerQuest? quest)
                || !quest.IsActive || quest.IsRewardPending || !quest.Template.IsFindItem
                || !QuestDungeonParameters.TryParse(quest.AcgBuildingGeneratorJson, out var parameters)
                || parameters.DungeonPlayfield != world.Identity.Instance
                || parameters.ObjectiveItem?.SameAs(objective.Binding) != true
                || player.GetEdgeDistanceTo(objective) > LootableDynel.OpenRange
                || !world.GetRequiredService<ZoneEngine_New.Core.Playfield.DynelRegistry>().TryGet(objective.Identity, out var current)
                || !ReferenceEquals(current, objective))
                return false;

            bool committed = false;
            try
            {
                Item item = _items.Create(objective.LowId, objective.HighId, objective.Quality,
                    ItemSource.Quest, 1, objective.Identity.Instance, objective.Identity);
                if (!InventoryGrantPlan.TryCreate(player, [item], out var grant))
                {
                    ClientFeedback.Send(player, "Make inventory space before picking up the mission item.");
                    return false;
                }
                _flush.HardFlush(player);
                var result = _completionDao.Execute(player.Identity.Instance, null!, tx =>
                {
                    var completion = RequireCompletion(tx);
                    var stored = completion.ReadCharacterQuest(quest.QuestId);
                    RequireCurrent(stored, quest, player);
                    if (stored.Source != (int)QuestSource.Generated || stored.OwnerId != player.Identity.Instance
                        || !QuestDungeonParameters.TryParse(stored.AcgBuildingGeneratorJson, out var durable)
                        || durable.ObjectiveItem?.SameAs(objective.Binding) != true
                        || durable.DungeonPlayfield != world.Identity.Instance)
                        throw new InvalidOperationException("Objective does not match the accepted mission.");
                    return completion.TryEarnCharacterQuest(new()
                    {
                        QuestId = quest.QuestId, AssignedAtUtcTicks = stored.AssignedAtUtcTicks,
                        ExpectedDefinitionJson = stored.DefinitionJson,
                        ExpectedAcgBuildingGeneratorJson = stored.AcgBuildingGeneratorJson,
                        NowUtcTicks = _now().Ticks, Grants = grant.Rows.Select(ToMissionItem).ToArray()
                    });
                });
                if (result.Status != CharacterQuestCompletionStatus.Earned)
                    return false;
                committed = true;
                grant.PublishAfterCommit(notify: false);
                quest.Progress = quest.RequiredCount;
                quest.NextRewardRetryUtcTicks = 0;
                // The objective dynel publishes the captured pickup acknowledgement first.
                // The player's next tick then delivers the independent terminal rewards.
                return true;
            }
            catch (Exception exception)
            {
                CompletionFailure(player, exception, committed);
                return false;
            }
        }
    }

    public void RetryPendingRewards(Player player, bool force = false)
    {
        if (!CanDeliver(player) || _completionDao == null)
            return;
        var log = player.QuestLog ??= Load(player);
        long now = _now().Ticks;
        foreach (var quest in log.Quests.Values.Where(q => q.IsRewardPending
            && (force || q.NextRewardRetryUtcTicks <= now)).ToArray())
            Complete(player, quest);
    }

    static bool CanDeliver(Player player) => !player.IsDead && !player.IsPersistenceQuarantined
        && player.Inventory.IsHydrated && player.Playfield != null
        && player.Session is { State: SessionState.InPlay } session && ReferenceEquals(session.Player, player);

    void Complete(Player player, PlayerQuest quest)
    {
        lock (player.PersistenceGate)
        {
            if (!CanDeliver(player) || _completionDao == null || !quest.IsActive)
                return;
            quest.NextRewardRetryUtcTicks = _now().AddSeconds(5).Ticks;
            bool committed = false;
            try
            {
                _flush.HardFlush(player);
                // Earning is durable even when capacity, content availability or delivery fails later.
                var earned = _completionDao.Execute(player.Identity.Instance, null!, tx =>
                {
                    var completion = RequireCompletion(tx);
                    var stored = completion.ReadCharacterQuest(quest.QuestId);
                    RequireCurrent(stored, quest, player);
                    if (quest.Source == QuestSource.Generated && quest.Template.IsFindItem && stored.Progress < stored.RequiredCount)
                        throw new InvalidOperationException("Find Item completion requires committed world collection.");
                    return completion.TryEarnCharacterQuest(new()
                    {
                        QuestId = quest.QuestId, AssignedAtUtcTicks = stored.AssignedAtUtcTicks,
                        ExpectedDefinitionJson = stored.DefinitionJson,
                        ExpectedAcgBuildingGeneratorJson = stored.AcgBuildingGeneratorJson, NowUtcTicks = _now().Ticks
                    });
                });
                if (earned.Status == CharacterQuestCompletionStatus.AlreadyApplied)
                {
                    // A different/uncertain publication already paid; never overwrite its durable stats with this actor.
                    player.QuarantinePersistence();
                    player.Session?.Close();
                    return;
                }
                if (earned.Status is not (CharacterQuestCompletionStatus.Earned or CharacterQuestCompletionStatus.AlreadyEarned))
                    return;
                quest.Progress = quest.RequiredCount;
                if (_snapshot == null)
                    throw new InvalidOperationException("Character snapshot service is unavailable; reward remains pending.");
                _snapshot.CommitOnline(player);
                QuestTemplate rewardTemplate = quest.Source == QuestSource.Generated
                    ? JsonSerializer.Deserialize<QuestTemplate>(earned.Quest.DefinitionJson, QuestTemplate.JsonOptions)
                        ?? throw new InvalidOperationException("Stored reward definition is unavailable.")
                    : quest.Template;
                var reward = rewardTemplate.Reward;
                var definitions = SelectRewardRecord(quest, reward);
                var items = new List<Item>();
                foreach (var definition in definitions)
                {
                    if (definition.Id <= 0 || definition.HighId is <= 0 || definition.Quality <= 0)
                        throw new InvalidOperationException("Reward item definition is invalid; delivery remains pending.");
                    var item = _items.CreateWithNewInstance(definition.Id, definition.HighId ?? definition.Id,
                        definition.Quality, ItemSource.Quest);
                    if (ZoneEngine_New.Core.Trade.TradeRules.IsUnique(item) && (ZoneEngine_New.Core.Trade.TradeRules.WouldDuplicateUnique(player, item.LowId, item.HighId)
                        || items.Any(existing => existing.LowId == item.LowId || existing.HighId == item.HighId)))
                    {
                        NotifyPending(player, quest);
                        return;
                    }
                    items.Add(item);
                }
                if (!InventoryGrantPlan.TryCreate(player, items, out var grant))
                {
                    NotifyPending(player, quest);
                    return;
                }
                var xp = DirectXpRewardPlan.CreateQuestReward(player, Math.Max(0, reward?.Xp ?? 0));
                var values = xp.Stats.ToDictionary(pair => pair.Key, pair => pair.Value);
                if (reward?.Credits > 0)
                    values[CharacterStat.Cash] = (int)Math.Min(int.MaxValue,
                        (long)player.Stats.GetOrZero(CharacterStat.Cash, StatDetail.Base) + reward.Credits);
                if (rewardTemplate.MissionBit is int bit && MissionBits.IsValid(bit))
                {
                    var (stat, mask) = MissionBits.Locate(bit);
                    values[stat] = player.Stats.GetOrZero(stat, StatDetail.Base) | mask;
                }
                var expected = values.Keys.Select(stat => new MissionStatValueData
                {
                    StatIdentityType = (int)player.Identity.Type, StatId = (int)stat,
                    Value = player.Stats.GetOrZero(stat, StatDetail.Base)
                }).ToArray();
                var result = _completionDao.Execute(player.Identity.Instance, null!, tx => RequireCompletion(tx).TryCompleteCharacterQuest(new()
                {
                    QuestId = quest.QuestId, AssignedAtUtcTicks = earned.Quest.AssignedAtUtcTicks,
                    ExpectedDefinitionJson = earned.Quest.DefinitionJson,
                    ExpectedAcgBuildingGeneratorJson = earned.Quest.AcgBuildingGeneratorJson, NowUtcTicks = _now().Ticks,
                    Grants = grant.Rows.Select(ToMissionItem).ToArray(), ExpectedStatValues = expected,
                    StatMutations = values.Select(pair => new MissionStatMutationData
                    {
                        StatIdentityType = (int)player.Identity.Type, StatId = (int)pair.Key,
                        Kind = MissionStatMutationKind.Set, Value = pair.Value,
                        MinimumValue = int.MinValue, MaximumValue = int.MaxValue
                    }).ToArray()
                }));
                if (result.Status != CharacterQuestCompletionStatus.Applied)
                {
                    if (result.Status == CharacterQuestCompletionStatus.AlreadyApplied)
                    {
                        player.QuarantinePersistence();
                        player.Session?.Close();
                    }
                    return;
                }
                committed = true;
                // Retire any queued pre-reward checkpoint before publishing, so it cannot overwrite paid stats.
                player.SaveState.MarkCommitted(player.SaveState.NextSequence(), values.Select(pair => new StatRecord
                {
                    StatId = (int)pair.Key, StatValue = pair.Value
                }).ToArray(), location: null);
                quest.State = QuestState.Completed;
                // Durable lifecycle cleanup must not depend on successful socket publication.
                QuestEnded?.Invoke(player, quest);
                grant.PublishAfterCommit();
                xp.PublishAfterCommit(player);
                foreach (var pair in values.Where(pair => !xp.Stats.ContainsKey(pair.Key)))
                    player.Stats.Set(pair.Key, pair.Value, StatDetail.Base, dirty: true);
                player.FlushDirtyStats();
                QuestJournal.Delete(player, quest);
                ClientFeedback.Send(player, "Feedback_MissionAccomplished");
                _logger.Info("Quest completed and rewards committed char=" + player.Identity.Instance + " quest=" + quest.QuestId);
                if (!string.IsNullOrEmpty(rewardTemplate.NextStep) && _catalog.TryGet(rewardTemplate.NextStep, out _))
                    TryAssignTemplate(player, rewardTemplate.NextStep, out _);
            }
            catch (Exception exception)
            {
                CompletionFailure(player, exception, committed);
                if (quest.IsRewardPending && !player.IsPersistenceQuarantined)
                    NotifyPending(player, quest);
            }
        }
    }

    static IReadOnlyList<QuestRewardItem> SelectRewardRecord(PlayerQuest quest, QuestReward? reward)
    {
        if (reward?.Records is not { Count: > 0 }) return [];
        // Stable per accepted run: reconnects and failed deliveries cannot reroll an alternative.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(quest.QuestId + ":" + quest.AssignedAtUtc.Ticks));
        int index = (int)(BitConverter.ToUInt32(hash, 0) % (uint)reward.Records.Count);
        return reward.Records[index];
    }

    static ICharacterQuestCompletionTransaction RequireCompletion(IMissionDaoTransaction transaction)
        => transaction as ICharacterQuestCompletionTransaction
            ?? throw new InvalidOperationException("Mission DAO does not support atomic character quest rewards.");

    static void NotifyPending(Player player, PlayerQuest quest)
    {
        if (quest.RewardPendingNotified) return;
        quest.RewardPendingNotified = true;
        ClientFeedback.Send(player, "Mission objective complete. Your reward is saved and pending delivery. Make inventory space; delivery retries automatically and after reconnect.");
    }

    static void RequireCurrent(CharacterQuestCompletionSnapshotData stored, PlayerQuest quest, Player player)
    {
        if (stored == null || stored.CharacterId != player.Identity.Instance || stored.Source != (int)quest.Source
            || stored.AssignedAtUtcTicks != quest.AssignedAtUtc.Ticks
            || (quest.Source == QuestSource.Generated && (stored.OwnerType != (int)QuestOwnerType.Character
                || stored.OwnerId != player.Identity.Instance || stored.AcgBuildingGeneratorJson != quest.AcgBuildingGeneratorJson)))
            throw new InvalidOperationException("Accepted quest authority changed.");
    }

    static MissionItemInstanceData ToMissionItem(ItemInstanceRecord row) => new()
    {
        InstanceId = row.InstanceId, ContainerType = row.ContainerType, ContainerInstance = row.ContainerInstance,
        ContainerPlacement = row.ContainerPlacement, ItemType = row.ItemType, LowId = row.LowId, HighId = row.HighId,
        Quality = row.Quality, StackCount = row.StackCount, Source = (byte)row.Source
    };

    void CompletionFailure(Player player, Exception exception, bool committed)
    {
        if (committed || exception is MissionCommitOutcomeUnknownException or DatabaseCommitOutcomeUnknownException
            || player.IsPersistenceQuarantined)
        {
            player.QuarantinePersistence();
            player.Session?.Close();
        }
        _logger.Error(exception, "Quest collection/reward delivery failed; no uncommitted success published and earned rewards remain recoverable.");
    }
}
